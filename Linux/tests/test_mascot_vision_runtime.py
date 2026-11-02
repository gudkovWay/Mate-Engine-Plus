"""Opt-in native player rehearsal on the caller's attributed special workspace."""
import json
import os
from pathlib import Path
import select
import subprocess as sp
import sys
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Linux/tools'))
import mascot_vision as vision

FIXTURES = {
    'read': '''NEWS CHANNEL                                          12,460 subscribers

CITY SCIENCE DESK                                      Today, 12:40

A new public observatory opens this weekend

The university has finished restoring the old telescope. Visitors can
join an evening tour and attend a short introduction to astronomy.
The programme includes accessible sessions for school groups.

Registration opens on Thursday. There is no admission fee.

Views 1,203          Comments 28                        Share

-----------------------------------------------------------------------
Another post: The city library is extending its opening hours.
''',
    'study': '''DOCUMENTATION                                     Python / asyncio

Task cancellation and structured concurrency

An asyncio.Task can be cancelled. The coroutine receives CancelledError
at its next suspension point. Use try/finally to release resources.

async def fetch_all(urls):
    async with asyncio.TaskGroup() as group:
        tasks = [group.create_task(fetch(url)) for url in urls]
    return [task.result() for task in tasks]

TaskGroup waits for every child. If one child fails, the remaining
children are cancelled before the context exits.

Exercise: explain why swallowing CancelledError can break TaskGroup.
''',
    'chat': '''MESSAGES                                               Friend / online

12:00  Friend: Are we still going for a walk on Saturday?
12:01  Me: Yes! Shall we meet at the park around eleven?
12:02  Friend: Perfect. I'll bring coffee.
12:03  Me: Great, see you there!


Write a message...                                             Send
''',
    'work': '''MESSAGES                                  Project team / API deployment

10:20  Colleague: Can you review the payment API pull request today?
10:21  Me: I will check the idempotency change after the team standup.
10:22  Colleague: We need the migration reviewed before release.
10:23  Me: Please link the ticket and the rollback procedure.
10:24  Colleague: Ticket ENG-412. Deployment is scheduled for 16:00.


Write a message...                                             Send
''',
    'podcast': '''VIDEO PLAYER                                                Interviews

              HOST                      GUEST
              (o_o)                     (o_o)
             /|   |\\                   /|   |\\
                [microphone]        [microphone]

The weekly science podcast: a conversation with an astronomer
Episode 48 | Interview | Full podcast

Guest: What led me into research was curiosity about the night sky.
Host: How did your first observation change your perspective?

Playing  18:24 / 1:12:06           Pause          Volume          Captions
''',
    'film': '''VIDEO PLAYER                                                 Cinema

                 ______________________________
                |                              |
                |    THE LAST TRAIN            |
                |    A fictional feature film  |
                |                              |
                |   [station at midnight]      |
                |______________________________|

Subtitle: "If we miss this train, we'll never reach the coast."

The Last Train (2024) | Full movie | Drama
Playing  42:16 / 1:48:30           Pause          Volume          Captions
''',
    'entertainment': '''VIDEO PLAYER                                                  Comedy

                   CAT vs EMPTY CARDBOARD BOX

                      /\\_/\\
                     ( o.o )      [  BOX  ]
                      > ^ <

Funny cat fails compilation - the box is definitely winning
Short comedy clips | Pets | Entertainment

Caption: He bought a fancy bed. The cat chose the packaging.

Playing  01:12 / 08:40            Pause          Volume          Captions
''',
    'comments': '''2 Comments                                      Sort by

Add a public comment...
______________________________________________________________________

@city-observer                                      2 days ago
The old station in this film is beautiful. Does anyone know where
the final scene was filmed?

Like       Reply

1 reply
    @railway-history                                Yesterday
    The coast sequence was filmed near a restored railway museum.

Top is selected, so you will see featured comments.
''',
}


def fixture(path):
    previous = ''
    while True:
        value = path.read_text()
        if value != previous:
            item = json.loads(value)
            kind = item['routine']
            title = item.get('title', 'Work discussion' if kind == 'work' else 'Mascot vision rehearsal: ' + kind)
            text = FIXTURES[kind]
            if item.get('page'):
                text = '''NEWS CHANNEL                             SCIENCE DESK / NEXT ARTICLE

New telescope observations released for public review

The city research team has published its first images of the night sky.
Residents can examine the new star maps at the public library.
The free exhibit opens on Friday and includes a guided evening tour.

A short introduction explains how astronomers compare observations.
Teachers can reserve a group visit through the city library.

Views 862          Comments 14                              Share
'''
            print('\x1b]2;' + title + '\x07\x1b[2J\x1b[H' + text, flush=True)
            receipt = path.with_suffix('.printed.json')
            temporary = receipt.with_suffix('.new')
            temporary.write_text(value)
            temporary.replace(receipt)
            previous = value
        time.sleep(.1)


@unittest.skipUnless(os.environ.get('MATEENGINE_VISION_RUNTIME'), 'explicit owned runtime output required')
class VisionNativeRuntime(unittest.TestCase):
    def test_current_avatar_choreography_and_real_window_inference(self):
        backend = Path(os.environ['MATEENGINE_DESKTOP_SOURCE']).resolve(strict=True)
        sys.path.insert(0, str(backend))
        from remote.desktop_launch import prepare_profile, stop_player_tree
        out = Path(os.environ['MATEENGINE_VISION_RUNTIME']).absolute()
        manual = os.environ.get('MATEENGINE_VISION_MANUAL') == '1'
        deadline = time.monotonic() + 540 if manual else None
        if manual: self.assertTrue(sys.stdin.isatty(), 'manual review needs its owned controlling terminal')
        self.assertTrue(out.is_relative_to(ROOT / '.superpowers'))
        out.mkdir()
        profile = out / 'profile'
        steam_root = Path(os.environ.get('MATEENGINE_STEAM_ROOT', str(Path.home() / '.local/share/Steam')))
        steam_pid = Path(os.environ.get('MATEENGINE_STEAM_PID', str(Path.home() / '.steam/steam.pid')))
        prepare_profile(profile, steam_root, steam_pid)
        data = profile / 'player-data'; data.mkdir(mode=0o700)
        (data / 'settings.json').write_text(json.dumps(dict(windowSizeState=0, avatarSize=.500023663,
            selectedModelPath='Lazuli_VRM_Clothes', graphicsQualityLevel=0, fpsLimit=30,
            enableWindowSitting=True, allowHyprlandMonitorSitting=False, enableMouseTracking=False,
            enableDancing=True, enableLocomotion=False, enableDiscordRPC=False, enableRandomMessages=False,
            enableFeedSystem=False, tutorialDone=True, startWithWindows=False)))
        config = out / 'desktop.json'
        config.write_text(json.dumps(dict(remote_database=str(profile / 'absent.sqlite'), herdr_enabled=False,
            session_runtime_dir=str(profile / 'private-sessions'), roots=[str(profile)], actions=[], projects=[])))
        scope = Path('/proc/self/cgroup').read_text()
        self.assertIn('omp-heavy-job-', scope)
        signature = os.environ['HYPRLAND_INSTANCE_SIGNATURE']
        workspace = 'special:omp-' + os.environ['OMP_GUI_KEY']
        trace = (out / 'driving.jsonl').open('x')
        processes, logs = [], []
        pids = []
        sequence = 0
        gate = vision.ContextGate()
        def record(kind, value):
            trace.write(json.dumps(dict(at=time.monotonic(), kind=kind, value=value)) + '\n'); trace.flush()
        def command(args, timeout=15):
            result = sp.run([str(x) for x in args], capture_output=True, text=True, timeout=timeout)
            if result.returncode:
                raise RuntimeError(str(args[:2]) + ': ' + result.stderr[-1000:] + result.stdout[-1000:])
            return result.stdout
        def until(label, probe, seconds=15, *, bounded=True):
            end = min(time.monotonic() + seconds, deadline) if bounded and deadline else time.monotonic() + seconds
            while time.monotonic() < end:
                value = probe()
                if value: return value
                time.sleep(.2)
            self.fail(label)
        def clients(): return json.loads(command(['hyprctl', '-i', signature, '-j', 'clients']))
        def owned(kind):
            for row in clients():
                if row['class'] == kind and Path(f'/proc/{row["pid"]}/cgroup').read_text() == scope:
                    self.assertEqual(row['workspace']['name'], workspace)
                    return row
        def launch(args, name, env):
            log = (out / (name + '.log')).open('x'); logs.append(log)
            process = sp.Popen([str(Path.home() / '.local/bin/agent-app'), '--', *map(str, args)], env=env,
                                   cwd=ROOT, stdout=log, stderr=sp.STDOUT)
            processes.append(process)
            return process
        def state(operation='state', value=''):
            nonlocal sequence
            sequence += 1
            path = profile / 'companion-audition.json'
            temporary = path.with_suffix('.new')
            temporary.write_text(json.dumps(dict(sequence=sequence, operation=operation, value=value)))
            temporary.replace(path)
            def response():
                target = profile / 'companion-audition-result.json'
                if not target.exists(): return None
                result = json.loads(target.read_text())
                return result if result.get('sequence') == sequence else None
            result = until('native audition response: ' + operation, response)
            self.assertFalse(result.get('error'), result)
            self.assertFalse(result.get('visionError'), result)
            record(operation, result)
            return result
        def review(label):
            if not manual: return
            record('manual-review-wait', label)
            print('MANUAL_REVIEW_READY ' + json.dumps(dict(label=label, workspace=workspace,
                remainingSeconds=max(0, int(deadline - time.monotonic())))), flush=True)
            while time.monotonic() < deadline:
                readable, _, _ = select.select([sys.stdin], [], [], max(0, deadline - time.monotonic()))
                if not readable: break
                line = sys.stdin.readline()
                if not line: self.fail('manual review terminal closed; remaining scenarios are unverified')
                action = line.strip()
                if action == 'next':
                    record('manual-review-continued', label)
                    return
                if action == 'stop': self.fail('owner stopped manual review; remaining scenarios are unverified')
                print('Manual review commands: next / stop', flush=True)
            self.fail('bounded manual review ended; remaining scenarios are unverified')
        try:
            baseline = json.loads(command(['hyprctl', '-i', signature, '-j', 'activewindow']))
            record('live-active-before', dict(address=baseline.get('address'), pid=baseline.get('pid')))
            original_env = dict(os.environ, PYTHONDONTWRITEBYTECODE='1')
            for key in ('LIBGL_ALWAYS_SOFTWARE', '__GLX_VENDOR_LIBRARY_NAME', '__EGL_VENDOR_LIBRARY_FILENAMES'):
                original_env.pop(key, None)
            registry = json.loads((Path(os.environ['XDG_RUNTIME_DIR']) / 'omp-heavy-jobs/registry.json').read_text())
            reservation = registry['jobs'].get(scope.strip().rsplit('/', 1)[-1], {})
            self.assertIn('/gpu/host', reservation.get('keys', ()), 'native Vulkan rehearsal requires shared GPU admission')
            record('expected-renderer', dict(api='Vulkan', gpuKey='/gpu/host'))
            fixture_path = out / 'fixture.json'; fixture_path.write_text(json.dumps(dict(routine='read')))
            def show_fixture(**value):
                contents = json.dumps(value)
                temporary = fixture_path.with_suffix('.new')
                temporary.write_text(contents)
                temporary.replace(fixture_path)
                receipt = fixture_path.with_suffix('.printed.json')
                until('fixture prints requested content', lambda: receipt.exists() and receipt.read_text() == contents)
                record('fixture-printed', value)
            fixture_env = dict(original_env, HOME=str(profile), XDG_CONFIG_HOME=str(profile / 'config'),
                XDG_CACHE_HOME=str(profile / 'cache'), XDG_DATA_HOME=str(profile / 'share'),
                XDG_STATE_HOME=str(profile / 'state'))
            terminal = launch(['dbus-run-session', '--', 'kitty', '--config', 'NONE', '--class', 'agent-test',
                '-o', 'shell_integration=disabled',
                '-o', 'linux_display_server=x11', '-o', 'sync_to_monitor=no',
                '-o', 'confirm_os_window_close=0', '-o', 'font_size=15',
                sys.executable, '-m', 'Linux.tests.test_mascot_vision_runtime', '--fixture', fixture_path],
                'fixture', fixture_env)
            target = until('owned content fixture mapped', lambda: owned('agent-test'))
            pids.append(target['pid']); record('owned-content', target)
            monitor = next(row for row in json.loads(command(['hyprctl', '-i', signature, '-j', 'monitors'])) if row['id'] == target['monitor'])
            width, height = round(monitor['width'] / monitor['scale']), round(monitor['height'] / monitor['scale'])
            selector = json.dumps('address:' + target['address'])
            def dispatch(value): command(['hyprctl', '-i', signature, 'eval', 'hl.dispatch(hl.dsp.window.' + value + ')'])
            dispatch('set_prop({window=' + selector + ',prop=\"render_unfocused\",value=\"true\"})')
            if not target['floating']: dispatch('float({window=' + selector + '})')
            dispatch('fullscreen_state({window=' + selector + ',internal=0,client=0,action=\"set\"})')
            dispatch('resize({window=' + selector + ',x=' + str(min(1100, width-100)) +
                     ',y=' + str(min(380, round(height*.3))) + ',relative=false})')
            dispatch('move({window=' + selector + ',x=' + str(monitor['x']+50) +
                     ',y=' + str(monitor['y']+round(height*.62)) + ',relative=false})')
            target = until('owned content placement applied', lambda: next((row for row in clients()
                if row['address'] == target['address'] and row['floating'] and row['size'][0] <= 1100 and row['size'][1] <= 380), None))
            record('owned-content-placement', target)
            player_dir = Path(os.environ['MATEENGINE_VISION_PLAYER'])
            vision_config = out / 'vision.json'
            vision_config.write_text(json.dumps(dict(allowedApps=['agent-test'], allowedWindows=[target['address']],
                workContexts=[['agent-test', '^Work discussion$']], intervalSeconds=20,
                agentDir=os.environ.get('MATEENGINE_VISION_AGENT_DIR', str(Path.home() / '.omp/agent')))))
            vision_config.chmod(0o600)
            player_env = dict(original_env, PYTHONPATH=str(backend), MATEENGINE_COMPANION_AUDITION='1',
                MATEENGINE_VISION_CONFIG=str(vision_config), MATEENGINE_VISION_WINDOW=target['address'],
                MATEENGINE_VISION_STATE=str(data / 'vision-state.json'))
            player = launch([sys.executable, '-m', 'remote.desktop_launch', '--config', config,
                '--player', player_dir, '--profile', profile,
                '--steam-root', steam_root, '--steam-pid', steam_pid,
                '--steam-wait', '0', '--library-dir', os.environ['MATEENGINE_RUNTIME_LIBS'],
                '--log-file', out / 'player.log'], 'launcher', player_env)
            pet = until('owned player mapped', lambda: owned('MateEngineX.x86_64'), 120)
            pids.append(pet['pid']); record('owned-player', pet)
            self.assertIn('Vulkan renderer=[', (out / 'player.log').read_text(), 'verify the actual native renderer, not only launcher flags')
            command(['hyprctl', '-i', signature, 'eval', 'hl.dispatch(hl.dsp.window.set_prop({window=' +
                json.dumps('address:' + pet['address']) + ',prop=\"render_unfocused\",value=\"true\"}))'])
            capture = vision.WindowCapture(player_dir / 'vision/vision-toplevels')
            until('native UI ready', lambda: state()['head']['y'] > 0, 30)
            pet_selector = json.dumps('address:' + pet['address'])
            command(['hyprctl', '-i', signature, 'eval', 'hl.dispatch(hl.dsp.window.move({window=' +
                pet_selector + ',x=' + str(monitor['x'] + max(0, (width - pet['size'][0]) // 2)) +
                ',y=' + str(monitor['y'] + max(0, (height - pet['size'][1]) // 2)) + ',relative=false}))'])
            def capture_avatar():
                client = owned('MateEngineX.x86_64')
                record('avatar-capture-geometry', client)
                x, y = client['at']
                w, h = client['size']
                self.assertGreater(min(x + w, monitor['x'] + width) - max(x, monitor['x']), 0,
                    'owned avatar must intersect its output before requesting a frame')
                self.assertGreater(min(y + h, monitor['y'] + height) - max(y, monitor['y']), 0,
                    'owned avatar must intersect its output before requesting a frame')
                return capture.capture(vision.Window.from_client(client))
            observation = state()
            if not observation['contextEnabled']: state('context.toggle')
            current = vision.Window.from_client(next(row for row in clients() if row['address'] == target['address']))
            fixture_image, _ = capture.capture(current)
            (out / 'initial-owned-input.png').write_bytes(fixture_image)
            last_status = [None]
            self.assertFalse(vision.session_locked(), 'a locked session cannot exercise vision')
            def wait_clip(clip, seconds=12):
                return until('native clip ' + clip, lambda: (lambda s: s if s.get('visionClip') == clip and s.get('visionApplied') else None)(state()), seconds)
            for routine, clip, props in [('read','MagnifierHold',1), ('study','NotebookHold',2),
                    ('chat','ChatHold',0), ('work','NotebookHold',2), ('podcast','HeadphonesHold',1),
                    ('film','PopcornHold',1), ('entertainment','WatchHold',0)]:
                changed_at = time.time()
                show_fixture(routine=routine)
                def observed():
                    path = data / 'vision-state.json'
                    if not path.exists(): return None
                    packet = json.loads(path.read_text())
                    health = tuple(packet.get(key) for key in ('unavailableStage', 'routine', 'permitted', 'providerReady', 'epoch'))
                    if health != last_status[0]:
                        record('observer-status', packet)
                        last_status[0] = health
                    return packet if packet.get('permitted') and packet.get('routine') == routine and packet.get('observedUtc', 0) >= changed_at else None
                packet = until('production observer classifies visible ' + routine, observed, 45)
                record('real-luna-production-observer', packet)
                result = wait_clip(clip)
                self.assertEqual(result['visionProps'], props)
                time.sleep(.7)
                review(routine + ': inspect current-avatar pose, prop grip, face and hair clearance')
                pet_image, _ = capture_avatar()
                (out / (routine + '-avatar.png')).write_bytes(pet_image)
                record('avatar-capture', routine)
                print('NATIVE_FRAME_READY ' + routine, flush=True)
                if routine == 'read':
                    before_image, before_pixels = capture.capture(current)
                    (out / 'read-before-scroll-input.png').write_bytes(before_image)
                    show_fixture(routine='read', page=1)
                    time.sleep(.4)
                    page_image, page_pixels = capture.capture(current)
                    (out / 'read-scroll-input.png').write_bytes(page_image)
                    record('scroll-frame-change', dict(ratio=vision.change_ratio(before_pixels, page_pixels),
                        changed=before_image != page_image))
                    self.assertGreater(vision.change_ratio(before_pixels, page_pixels), .18,
                        'owned fixture must present the newly printed page before checking the scroll reaction')
                    wait_clip('MagnifierLower')
                    wait_clip('MagnifierRaise', 45); wait_clip('MagnifierHold')
                    state('radial'); time.sleep(.4)
                    self.assertFalse(state()['visionApplied'], 'manual menu must own animation immediately')
                    until('menu revokes observer permission', lambda: not json.loads((data / 'vision-control.json').read_text())['enabled'])
                    state('close')
                    state('seat.upper', target['address'])
                    until('native seated support settled', lambda: (lambda s: s['sitting'] and not s['seatTransition'])(state()), 20)
                    wait_clip('MagnifierHold', 45)
                    review('read-seated: inspect seat contact, magnifier and window-edge clearance')
                    seated_image, _ = capture_avatar()
                    (out / 'read-seated-avatar.png').write_bytes(seated_image)
                if routine == 'study': wait_clip('NotebookWrite', 18)
                if routine == 'podcast': wait_clip('HeadphonesAdjust', 38)
                if routine == 'film':
                    wait_clip('PopcornBite', 33)
                    lower_y = monitor['y'] + round(height * .36)
                    dispatch('move({window=' + selector + ',x=' + str(monitor['x'] + 50) +
                        ',y=' + str(lower_y) + ',relative=false})')
                    target = until('owned bottom ledge has space below it', lambda: next((row for row in clients()
                        if row['address'] == target['address'] and abs(row['at'][1] - lower_y) <= 1), None))
                    record('owned-bottom-ledge-placement', target)
                    state('seat.lower', target['address'])
                    until('native lying support settled', lambda: (lambda s: s['sitting'] and not s['seatTransition'] and s['framePose'] == 'Lie')(state()), 20)
                    wait_clip('PopcornHold', 45)
                    lying_image, _ = capture_avatar()
                    (out / 'film-lying-avatar.png').write_bytes(lying_image)
                    stable_window = next(row for row in clients() if row['address'] == target['address'])
                    changed_at = time.time()
                    show_fixture(routine='comments', title=stable_window['title'])
                    def comments_observed():
                        packet = json.loads((data / 'vision-state.json').read_text())
                        return packet if packet.get('permitted') and packet.get('routine') == 'read' and packet.get('observedUtc', 0) >= changed_at else None
                    comments = until('same-title video scroll redirects real vision to public comments', comments_observed, 45)
                    record('same-title-comments-observer', comments)
                    after_scroll = next(row for row in clients() if row['address'] == target['address'])
                    self.assertEqual((after_scroll['address'], after_scroll['pid'], after_scroll['title']),
                        (stable_window['address'], stable_window['pid'], stable_window['title']))
                    comments_input, _ = capture.capture(vision.Window.from_client(after_scroll))
                    (out / 'comments-input.png').write_bytes(comments_input)
                    result = wait_clip('MagnifierHold', 20)
                    self.assertTrue(result['sitting'] and not result['seatTransition'], 'comments retain their existing window support')
                    self.assertEqual((result['framePose'], result['seatEdge']), ('Lie', 'bottom'))
                    comments_image, _ = capture_avatar()
                    (out / 'comments-lying-avatar.png').write_bytes(comments_image)
                    record('comments-support', result)
                    state('seat.watch', target['address'])
                    until('opposite lying support settled', lambda: (lambda s: s['sitting'] and not s['seatTransition'] and s['framePose'] == 'Watch')(state()), 20)
                    wait_clip('MagnifierHold', 45)
                    watch_image, _ = capture_avatar()
                    (out / 'comments-watch-avatar.png').write_bytes(watch_image)
                    print('NATIVE_FRAME_READY comments-lying', flush=True)
            self.assertTrue(state('drag.press')['dragging'], 'native pointer-down callback enters manual drag ownership')
            time.sleep(.4)
            self.assertFalse(state()['visionApplied'], 'manual drag callback interrupts contextual animation')
            state('drag.release')
            state('context.toggle')
            until('context off revokes capture permission', lambda: not json.loads((data / 'vision-control.json').read_text())['enabled'])
            until('observer publishes denied context', lambda: not json.loads((data / 'vision-state.json').read_text())['permitted'])
            self.assertFalse(state()['visionApplied'], 'privacy denial cancels immediately rather than stowing through private content')
            after = json.loads(command(['hyprctl', '-i', signature, '-j', 'activewindow']))
            record('live-active-after', dict(address=after.get('address'), pid=after.get('pid')))
            if not manual:
                self.assertNotIn(after.get('pid'), pids, 'an owned hidden client must not steal the human focus')
        finally:
            for name in ('vision-control.json', 'vision-state.json'):
                path = data / name
                if path.exists(): record('before-teardown-' + name, json.loads(path.read_text()))
            try:
                for process in reversed(processes):
                    stop_player_tree(process)
                def reaped():
                    if any(row['pid'] in pids for row in clients()): return False
                    for pid in pids:
                        try:
                            state_code = Path(f'/proc/{pid}/stat').read_text().rsplit(') ', 1)[1].split()[0]
                            if state_code != 'Z': return False
                        except FileNotFoundError: pass
                    return True
                windows_gone = until('owned windows and live processes reaped', reaped, 10, bounded=False)
                record('teardown', dict(exits=[p.returncode for p in processes], windowsGone=windows_gone))
            finally:
                trace.close()
                for log in logs: log.close()


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--fixture': fixture(Path(sys.argv[2]))
    else: unittest.main()
