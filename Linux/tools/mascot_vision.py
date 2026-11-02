#!/usr/bin/env python3
"""Observe explicitly allowed toplevels; publish only current mascot decisions."""
import argparse
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import re
import signal
import subprocess
import time
import threading

ROUTINES = frozenset(('unknown', 'read', 'study', 'chat', 'work', 'podcast', 'film', 'entertainment'))
ACCENTS = frozenset(('none', 'smile', 'laugh', 'surprise', 'think', 'concern'))
SENSITIVE = re.compile(r'password|passphrase|парол|код подтверждения|one.time.code|keepass|1password|bitwarden|банкинг|online.bank', re.I)
OBSERVER_ID = f'{os.getpid()}:{time.monotonic_ns()}'
NEUTRAL_TARGET = [.25, .2, .5, .6]
VIDEO_ROUTINES = frozenset(('podcast', 'film', 'entertainment'))


def neutral():
    return dict(routine='unknown', accent='none', confidence=0, target=list(NEUTRAL_TARGET))


class VisionUnavailable(RuntimeError):
    def __init__(self, stage):
        super().__init__('Vision unavailable at ' + stage)
        self.stage = stage


@dataclass
class Window:
    address: str
    pid: int
    app: str
    title: str
    x: int
    y: int
    width: int
    height: int
    xwayland: bool
    workspace: str = ''

    @property
    def identity(self):
        return f'{self.address}:{self.pid}'

    @classmethod
    def from_client(cls, value):
        if not value or not value.get('mapped', False):
            return None
        return cls(value['address'], int(value['pid']), value.get('initialClass') or value.get('class', ''),
                   value.get('title', ''), *value['at'], *value['size'], bool(value.get('xwayland')),
                   (value.get('workspace') or {}).get('name', ''))


class PrivacyPolicy:
    def __init__(self, allowed_apps, allowed_windows=(), work_contexts=()):
        self.allowed_apps = {app.casefold() for app in allowed_apps}
        self.allowed_windows = set(allowed_windows)
        self.work_contexts = tuple((app.casefold(), re.compile(title)) for app, title in work_contexts)

    def permits(self, window, *, locked=False):
        return bool(window and not locked and window.pid > 1 and window.width > 0 and window.height > 0
                    and window.app.casefold() in self.allowed_apps and not SENSITIVE.search(window.title)
                    and (not self.allowed_windows or window.address in self.allowed_windows))

    def work_hint(self, window):
        return any(app == window.app.casefold() and pattern.search(window.title)
                   for app, pattern in self.work_contexts)


class ContextGate:
    def __init__(self):
        self.token = ('', 0)
        self._context = None
        self.allowed = False

    def observe(self, window, *, allowed, lease=None):
        context = (window.identity, window.title, lease) if window else None
        if context != self._context or allowed != self.allowed:
            self.token = (window.identity if window else '', self.token[1] + 1)
            self._context, self.allowed = context, allowed
        return self.token

    def current(self, token):
        return self.allowed and token == self.token


class DecisionState:
    """Raw video motion expires frame-specific details, not the native context lease."""
    def __init__(self):
        self.clear()

    def clear(self):
        self.decision = neutral()
        self.observed = 0
        self.pixels = b''
        self.frame_revision = 0
        self.reading_carry = False
        self.carry_until = 0

    def frame(self, pixels, utc):
        changed = bool(self.pixels) and change_ratio(self.pixels, pixels) > .18
        self.pixels = pixels
        if changed:
            self.frame_revision += 1
            routine = self.decision['routine']
            if routine == 'read':
                self.reading_carry = True
                self.carry_until = utc + 8
            elif routine not in VIDEO_ROUTINES:
                self.decision = neutral()
            self.decision['accent'] = 'none'
            self.decision['target'] = list(NEUTRAL_TARGET)
        self.expire(utc)
        return changed

    def expire(self, utc):
        if utc - self.observed > 45 or self.reading_carry and utc >= self.carry_until:
            self.decision = neutral()
            self.reading_carry = False

    def accept(self, result, frame_revision, observed):
        changed = frame_revision != self.frame_revision
        if changed and result['routine'] not in VIDEO_ROUTINES:
            return False
        self.decision = dict(result)
        if changed:
            self.decision.update(accent='none', target=list(NEUTRAL_TARGET))
        self.observed = observed
        self.reading_carry = False
        return True

    def inference_due(self, elapsed, interval, *, settled, submitted_revision):
        stable_change = settled and self.frame_revision != submitted_revision and elapsed >= 2
        text_wait = self.decision['routine'] in ('read', 'study', 'chat', 'work')
        return (elapsed >= interval or stable_change) and (settled or not text_wait)


def control_lease(path, window, utc=None):
    utc = time.time() if utc is None else utc
    try:
        path = Path(path)
        info = path.lstat()
        if path.is_symlink() or path.parent.resolve() != path.parent.absolute() or info.st_uid != os.getuid() or info.st_mode & 0o077 or info.st_size > 4096:
            return None
        value = json.loads(path.read_text())
        if (value.get('version') != 1 or value.get('enabled') is not True or not window or
                value.get('identity') != window.identity or not isinstance(value.get('lease'), str) or
                not value['lease'] or type(value.get('epoch')) is not int or
                not 0 <= value.get('notBeforeUtc', float('inf')) <= utc + 1 or
                not utc < value.get('expiresUtc', 0) <= utc + 2):
            return None
        return value
    except (OSError, ValueError, TypeError):
        return None


def validate_decision(value, *, work_hint=False):
    if not isinstance(value, dict) or set(value) != {'routine', 'accent', 'confidence', 'target'}:
        raise ValueError('Invalid vision decision fields')
    routine, accent, confidence, target = (value[key] for key in ('routine', 'accent', 'confidence', 'target'))
    if routine not in ROUTINES or accent not in ACCENTS:
        raise ValueError('Unknown routine or accent')
    if type(confidence) not in (int, float) or not math.isfinite(confidence) or not 0 <= confidence <= 1:
        raise ValueError('Invalid confidence')
    if not isinstance(target, list) or len(target) != 4 or any(
            type(n) not in (int, float) or not math.isfinite(n) or not 0 <= n <= 1 for n in target):
        raise ValueError('Invalid normalized target')
    x, y, width, height = target
    if width <= 0 or height <= 0 or x + width > 1.00001 or y + height > 1.00001:
        raise ValueError('Target outside the selected window')
    if confidence < .65:
        routine = 'unknown'
    if routine == 'work' and not work_hint:
        routine = 'chat'
    if routine == 'unknown':
        accent = 'none'
    return dict(routine=routine, accent=accent, confidence=float(confidence), target=target)


def publish(path, window, gate, decision, *, sequence, now, permitted, scrolling=False,
            observed_utc=None, provider_ready=True, control=None, reading_carry=False, reason=''):
    path = Path(path)
    if path.is_symlink() or path.parent.resolve() != path.parent.absolute():
        raise ValueError('Vision state must not traverse symlinks')
    info = path.parent.stat()
    if info.st_uid != os.getuid() or info.st_mode & 0o022:
        raise ValueError('Unsafe vision state directory')
    target = decision.get('target', [.25, .2, .5, .6])
    observed = now if observed_utc is None else observed_utc
    packet = dict(version=2, observer=OBSERVER_ID, sequence=sequence, identity=window.identity if window else '',
                  lease=control['lease'] if control else '', epoch=control['epoch'] if control else 0,
                  observedUtc=observed, expiresUtc=min(observed + 45, now + 2),
                  readingCarry=bool(reading_carry),
                  permitted=bool(permitted), providerReady=bool(provider_ready), scrolling=bool(scrolling),
                  unavailableStage=reason,
                  routine=decision.get('routine', 'unknown'), accent=decision.get('accent', 'none'),
                  confidence=decision.get('confidence', 0), targetX=target[0], targetY=target[1],
                  targetWidth=target[2], targetHeight=target[3],
                  windowX=window.x if window else 0, windowY=window.y if window else 0,
                  windowWidth=window.width if window else 0, windowHeight=window.height if window else 0)
    temporary = path.with_name(path.name + '.new')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(packet, stream, separators=(',', ':'), allow_nan=False)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
    return packet


def change_ratio(previous, current):
    if not previous or len(previous) != len(current):
        return 1.0
    def background(pixels):
        counts = [0] * 256
        for value in pixels:
            counts[value] += 1
        return max(range(256), key=counts.__getitem__)
    before, after = background(previous), background(current)
    if abs(before - after) > 24:
        return 1.0
    ink = changed = 0
    for a, b in zip(previous, current):
        if abs(a - before) > 12 or abs(b - after) > 12:
            ink += 1
            changed += abs(a - b) > 12
    return changed / max(1, ink)


def command(arguments, *, data=None, seconds=4):
    result = subprocess.run(arguments, input=data, capture_output=True, timeout=seconds, check=False)
    if result.returncode:
        raise RuntimeError('Native vision dependency failed: ' + Path(arguments[0]).name)
    return result.stdout


def compositor_client(signature, address=None):
    args = ['hyprctl', '-i', signature, '-j']
    if address:
        rows = json.loads(command(args + ['clients']))
        return Window.from_client(next((row for row in rows if row.get('address') == address), None))
    return Window.from_client(json.loads(command(args + ['activewindow'])))


def session_locked():
    value = command(['loginctl', 'show-session', 'auto', '-p', 'LockedHint', '--value']).strip()
    if value not in (b'yes', b'no'):
        raise RuntimeError('Session lock state unavailable')
    return value == b'yes'


def usable_sample(pixels):
    return len(pixels) == 4096 and max(pixels) - min(pixels) >= 16


class WindowCapture:
    def __init__(self, helper):
        self.helper = str(Path(helper).resolve(strict=True))

    def capture(self, window):
        rows = [json.loads(line) for line in command([self.helper]).decode().splitlines() if line]
        candidates = [row['identifier'] for row in rows
                      if row.get('address') == window.address
                      and row.get('app_id', '').casefold() == window.app.casefold()
                      and row.get('title') == window.title]
        if len(candidates) != 1:
            raise VisionUnavailable('capture_mapping')
        image = command(['grim', '-T', candidates[0], '-t', 'png', '-'], seconds=6)
        if not image.startswith(b'\x89PNG\r\n\x1a\n') or len(image) > 24 * 1024 * 1024:
            raise VisionUnavailable('capture_image')
        pixels = command(['magick', 'png:-', '-resize', '64x64!', '-colorspace', 'gray', '-depth', '8', 'gray:-'], data=image)
        if not usable_sample(pixels):
            raise VisionUnavailable('capture_unrendered')
        return image, pixels


class ModelRequest:
    def __init__(self, image, *, app, work_hint, previous, model, control_path, control, observed_utc, agent_dir=None):
        import base64
        request = dict(image=base64.b64encode(image).decode('ascii'), app=app, workHint=bool(work_hint),
                       previous=previous, model=model, controlPath=str(control_path), control=control,
                       capturedUtc=observed_utc, agentDir=agent_dir)
        self.process = subprocess.Popen(['bun', str(Path(__file__).with_name('vision_model.mjs'))],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        self.output = None
        self.error = None
        self.work_hint = work_hint
        data = json.dumps(request).encode()

        def communicate():
            try:
                self.output, _ = self.process.communicate(data, timeout=40)
            except (OSError, subprocess.SubprocessError) as error:
                self.error = error
                if self.process.poll() is None:
                    self.process.kill()
                self.process.communicate()

        self.thread = threading.Thread(target=communicate, name='mascot-vision-request', daemon=True)
        self.thread.start()

    def done(self):
        return not self.thread.is_alive()

    def cancel(self):
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                self.process.kill()
        self.thread.join(timeout=2)
        if self.thread.is_alive():
            raise RuntimeError('Vision transport did not terminate')

    def result(self):
        self.thread.join(timeout=41)
        if self.thread.is_alive():
            self.cancel()
            raise RuntimeError('Vision provider deadline exceeded')
        if self.error:
            raise RuntimeError('Vision transport unavailable') from self.error
        response = json.loads(self.output or b'{}')
        if self.process.returncode or response.get('ok') is not True:
            stage = response.get('stage')
            if stage not in ('input', 'sdk', 'auth_storage', 'model_registry', 'credential', 'permission', 'provider', 'response_schema'):
                stage = 'unknown'
            raise VisionUnavailable('provider_' + stage)
        return validate_decision(response['decision'], work_hint=self.work_hint)


def infer(image, **kwargs):
    request = ModelRequest(image, **kwargs)
    try:
        return request.result()
    finally:
        request.cancel()


def load_config(path):
    path = Path(path).resolve(strict=True)
    info = path.stat()
    if info.st_uid != os.getuid() or info.st_mode & 0o022 or info.st_size > 65536:
        raise ValueError('Unsafe vision configuration')
    data = json.loads(path.read_text())
    if not isinstance(data.get('allowedApps'), list) or not data['allowedApps'] or any(
            not isinstance(item, str) or not item for item in data['allowedApps']):
        raise ValueError('An explicit allowedApps list is required')
    if data.get('model', 'openai-codex/gpt-6-luna') != 'openai-codex/gpt-6-luna':
        raise ValueError('This installation is configured for GPT-6 Luna; no silent model substitution')
    return data


def run(args):
    config = load_config(args.config)
    policy = PrivacyPolicy(config['allowedApps'], config.get('allowedWindows', ()), config.get('workContexts', ()))
    state = args.state.absolute()
    state.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    capture = WindowCapture(args.capture_helper)
    gate = ContextGate()
    decisions = DecisionState()
    control_path = state.with_name('vision-control.json')
    sequence = 0
    request = None
    pending_token = None
    pending_at = 0
    pending_revision = 0
    last_inference = 0
    last_submitted_revision = -1
    last_change = time.monotonic()
    last_window = None
    running = True
    provider_ready = True
    failure = ''

    def stop(_signum, _frame):
        nonlocal running
        running = False

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    end = time.monotonic() + args.seconds if args.seconds else float('inf')
    try:
        while running and time.monotonic() < end:
            tick = time.monotonic()
            image = None
            control = None
            binding = None
            stage = 'foreground'
            try:
                window = compositor_client(args.signature, args.window)
                stage = 'permission'
                control = control_lease(control_path, window)
                allowed = bool(control) and policy.permits(window, locked=session_locked())
                if args.window and window:
                    key = os.environ.get('OMP_GUI_KEY', '')
                    allowed = allowed and bool(key) and window.workspace == 'special:omp-' + key
                old = gate.token
                binding = (control['lease'], control['epoch']) if control else None
                gate.observe(window, allowed=allowed, lease=binding)
                if old != gate.token:
                    if request:
                        request.cancel()
                        request = None
                    decisions.clear()
                    last_inference = 0
                    last_submitted_revision = -1
                    last_change = tick
                    provider_ready = True
                    failure = ''
                if allowed:
                    previous_routine = decisions.decision['routine']
                    captured_at = time.time()
                    stage = 'capture'
                    image, pixels = capture.capture(window)
                    confirmed = compositor_client(args.signature, args.window)
                    current = control_lease(control_path, confirmed)
                    if (not confirmed or (confirmed.identity, confirmed.title) != (window.identity, window.title) or
                            not current or (current['lease'], current['epoch']) != binding or
                            captured_at < current['notBeforeUtc'] or session_locked()):
                        allowed = False
                        gate.observe(confirmed, allowed=False)
                        image = None
                        decisions.clear()
                        if request:
                            request.cancel()
                            request = None
                    else:
                        if decisions.frame(pixels, time.time()):
                            last_change = tick
                            if previous_routine in ('read', 'study', 'chat', 'work'):
                                last_inference = 0
                if request and request.done():
                    try:
                        result = request.result()
                        if gate.current(pending_token) and time.time() - pending_at < 40:
                            decisions.accept(result, pending_revision, pending_at)
                    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
                        if gate.current(pending_token):
                            provider_ready = False
                            decisions.clear()
                            failure = getattr(error, 'stage', 'provider')
                    request.cancel()
                    request = None
                settled = tick - last_change >= 1.2
                if (allowed and provider_ready and image is not None and request is None and
                        decisions.inference_due(tick - last_inference, args.interval, settled=settled,
                            submitted_revision=last_submitted_revision)):
                    # Permission is rechecked after capture and inside the transport immediately before upload.
                    current = control_lease(control_path, window)
                    if current and (current['lease'], current['epoch']) == binding:
                        pending_token, pending_at, pending_revision = gate.token, captured_at, decisions.frame_revision
                        request = ModelRequest(image, app=window.app, work_hint=policy.work_hint(window),
                            previous=decisions.decision['routine'], model=config.get('model', 'openai-codex/gpt-6-luna'),
                            control_path=control_path, control=current, observed_utc=captured_at,
                            agent_dir=config.get('agentDir'))
                        last_inference = tick
                        last_submitted_revision = pending_revision
                decisions.expire(time.time())
                sequence += 1
                stage = 'publish'
                active = allowed and provider_ready and time.time() - decisions.observed < 45
                publish(state, window, gate, decisions.decision, sequence=sequence, now=time.time(),
                    observed_utc=decisions.observed, permitted=active, scrolling=decisions.reading_carry,
                    provider_ready=provider_ready, control=control, reading_carry=decisions.reading_carry,
                    reason=failure if allowed else 'permission')
                last_window = window
            except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
                if request:
                    request.cancel()
                    request = None
                decisions.clear()
                gate.observe(None, allowed=False)
                sequence += 1
                publish(state, None, gate, {}, sequence=sequence, now=time.time(), permitted=False,
                    provider_ready=False, reason=getattr(error, 'stage', stage))
            # Poll control independently of the more expensive next capture to bound revocation latency.
            until = tick + args.sample_interval
            while running and time.monotonic() < until:
                current = control_lease(control_path, last_window)
                if request and (not current or (current['lease'], current['epoch']) != binding):
                    request.cancel()
                    request = None
                time.sleep(min(.05, max(0, until - time.monotonic())))
    finally:
        if request:
            request.cancel()
        gate.observe(None, allowed=False)
        publish(state, last_window, gate, {}, sequence=sequence + 1, now=time.time(), permitted=False,
            provider_ready=False, reason='stopped')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--state', type=Path, required=True)
    parser.add_argument('--capture-helper', type=Path, required=True)
    parser.add_argument('--signature', default=os.environ.get('HYPRLAND_INSTANCE_SIGNATURE'), required=not bool(os.environ.get('HYPRLAND_INSTANCE_SIGNATURE')))
    parser.add_argument('--window', help='Owned special-workspace window address for an explicitly attributed preview')
    parser.add_argument('--seconds', type=float, default=0)
    parser.add_argument('--interval', type=float, default=20)
    parser.add_argument('--sample-interval', type=float, default=.75)
    args = parser.parse_args()
    if args.seconds < 0 or args.interval < 2 or args.sample_interval < .25:
        parser.error('Invalid observer timing')
    run(args)


if __name__ == '__main__':
    main()
