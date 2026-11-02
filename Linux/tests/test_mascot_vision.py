import json
import os
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import threading
import time
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import mascot_vision as vision
import prepare_vision_assets as vision_assets


class VisionPolicyTests(unittest.TestCase):
    def test_authoring_attachment_preserves_bone_local_axes_for_native_import(self):
        source = dict(bone='RightHand', position_m=[0, .175, .018],
                      rotation_quaternion_xyzw=[-.70710677, 0, 0, .70710677])
        result = vision_assets.attachment(source)
        self.assertEqual(result['position'], dict(x=0, y=.175, z=.018))
        self.assertEqual(result['rotation'], dict(x=-.70710677, y=0, z=0, w=.70710677))

    def setUp(self):
        self.window = vision.Window('0xa', 123, 'ayugram', 'Test channel', 0, 0, 800, 600, True)
        self.policy = vision.PrivacyPolicy({'ayugram'}, set(), ())

    def decision(self, **changes):
        result = dict(routine='read', accent='none', confidence=.9, target=[.1, .2, .5, .4])
        result.update(changes)
        return result

    def test_disallowed_or_locked_surface_is_rejected_before_capture(self):
        self.assertTrue(self.policy.permits(self.window))
        self.assertFalse(self.policy.permits(self.window, locked=True))
        self.assertFalse(self.policy.permits(vision.Window('0xb', 456, 'keepassxc', 'Vault', 0, 0, 800, 600, True)))
        self.assertFalse(vision.PrivacyPolicy({'ayugram'}, {'0xb'}, ()).permits(self.window))
        self.window.title = 'Введите пароль'
        self.assertFalse(self.policy.permits(self.window))

    def test_work_chat_requires_a_local_explicit_relationship_hint(self):
        result = vision.validate_decision(self.decision(routine='work'), work_hint=False)
        self.assertEqual(result['routine'], 'chat')
        result = vision.validate_decision(self.decision(routine='work'), work_hint=True)
        self.assertEqual(result['routine'], 'work')

    def test_unknown_content_cannot_force_an_emotional_reaction(self):
        result = vision.validate_decision(self.decision(confidence=.3, accent='laugh'))
        self.assertEqual((result['routine'], result['accent']), ('unknown', 'none'))
        result = vision.validate_decision(self.decision(routine='unknown', accent='surprise'))
        self.assertEqual(result['accent'], 'none')

    def test_untrusted_model_cannot_inject_commands_or_invalid_coordinates(self):
        for changes in ({'command': 'quit'}, {'routine': 'launch'}, {'accent': 'dance'},
                        {'target': [0, 0, 2, 1]}, {'target': [float('nan'), 0, .5, .5]},
                        {'target': [.8, .2, .5, .2]}, {'confidence': True}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                vision.validate_decision(self.decision(**changes))

    def test_all_approved_activity_results_survive_validation(self):
        for routine in ('read', 'study', 'chat', 'work', 'podcast', 'film', 'entertainment'):
            with self.subTest(routine=routine):
                self.assertEqual(vision.validate_decision(self.decision(routine=routine), work_hint=True)['routine'], routine)

    def test_stale_response_cannot_resume_after_switching_away_and_back(self):
        gate = vision.ContextGate()
        first = gate.observe(self.window, allowed=True)
        other = vision.Window('0xb', 124, 'ayugram', 'Other chat', 0, 0, 800, 600, True)
        gate.observe(other, allowed=True)
        gate.observe(self.window, allowed=True)
        self.assertFalse(gate.current(first))
        current = gate.token
        self.assertTrue(gate.current(current))
        gate.observe(self.window, allowed=False)
        self.assertFalse(gate.current(current))

    def test_changed_tab_title_invalidates_old_context_without_window_switch(self):
        gate = vision.ContextGate()
        old = gate.observe(self.window, allowed=True)
        self.window.title = 'A different visible article'
        self.assertNotEqual(gate.observe(self.window, allowed=True), old)
        self.assertFalse(gate.current(old))

    def test_first_reading_result_is_rejected_after_content_changes(self):
        state = vision.DecisionState()
        state.frame(bytes([0, 255]) * 2048, 100)
        submitted = state.frame_revision
        state.frame(bytes([255, 0]) * 2048, 101)
        self.assertFalse(state.accept(self.decision(), submitted, 100))
        self.assertEqual(state.decision['routine'], 'unknown')

    def test_moving_video_keeps_bounded_routine_but_not_old_accent_or_target(self):
        state = vision.DecisionState()
        state.frame(bytes([0, 255]) * 2048, 100)
        submitted = state.frame_revision
        state.frame(bytes([255, 0]) * 2048, 101)
        self.assertTrue(state.accept(self.decision(routine='film', accent='surprise'), submitted, 100))
        self.assertEqual((state.decision['routine'], state.decision['accent']), ('film', 'none'))
        self.assertEqual(state.decision['target'], vision.NEUTRAL_TARGET)
        self.assertEqual(state.observed, 100, 'motion must not renew original inference age')

    def test_settled_video_to_comments_refreshes_without_title_change(self):
        gate = vision.ContextGate()
        token = gate.observe(self.window, allowed=True)
        state = vision.DecisionState()
        state.frame(bytes([0, 255]) * 2048, 100)
        submitted = state.frame_revision
        state.accept(self.decision(routine='film'), submitted, 100)
        state.frame(bytes([255, 0]) * 2048, 101)
        self.assertEqual(gate.observe(self.window, allowed=True), token)
        self.assertTrue(state.inference_due(2.4, 20, settled=True, submitted_revision=submitted))
        self.assertTrue(state.accept(self.decision(routine='read'), state.frame_revision, 102.4))
        self.assertEqual(state.decision['routine'], 'read')
        self.assertNotEqual(state.decision['target'], vision.NEUTRAL_TARGET)

    def test_continuous_video_motion_does_not_request_at_frame_rate(self):
        state = vision.DecisionState()
        state.frame(bytes([0, 255]) * 2048, 100)
        submitted = state.frame_revision
        state.accept(self.decision(routine='film'), submitted, 100)
        for second in range(1, 20):
            state.frame(bytes([255, 0] if second % 2 else [0, 255]) * 2048, 100 + second)
            self.assertFalse(state.inference_due(second, 20, settled=False, submitted_revision=submitted))
        self.assertTrue(state.inference_due(20, 20, settled=False, submitted_revision=submitted))

    def test_stable_refresh_is_coalesced_and_text_waits_for_scroll_to_settle(self):
        state = vision.DecisionState()
        state.frame(bytes([0, 255]) * 2048, 100)
        submitted = state.frame_revision
        state.accept(self.decision(routine='film'), submitted, 100)
        state.frame(bytes([255, 0]) * 2048, 101)
        self.assertFalse(state.inference_due(1, 20, settled=True, submitted_revision=submitted))
        self.assertTrue(state.inference_due(2, 20, settled=True, submitted_revision=submitted))
        self.assertFalse(state.inference_due(5, 20, settled=True, submitted_revision=state.frame_revision))
        state.accept(self.decision(routine='read'), state.frame_revision, 102)
        self.assertFalse(state.inference_due(21, 20, settled=False, submitted_revision=submitted))

    def test_reading_scroll_carry_is_nonsemantic_and_expires(self):
        state = vision.DecisionState()
        state.frame(bytes([0, 255]) * 2048, 100)
        state.accept(self.decision(accent='smile'), state.frame_revision, 100)
        state.frame(bytes([255, 0]) * 2048, 101)
        self.assertTrue(state.reading_carry)
        self.assertEqual(state.decision['accent'], 'none')
        state.expire(110)
        self.assertEqual(state.decision['routine'], 'unknown')

    def test_sparse_text_scroll_lowers_lens_despite_large_unchanged_background(self):
        first = bytearray(4096)
        second = bytearray(4096)
        first[::64] = bytes([120]) * 64
        second[16::64] = bytes([120]) * 64
        state = vision.DecisionState()
        state.frame(bytes(first), 100)
        state.accept(self.decision(), state.frame_revision, 100)
        self.assertTrue(state.frame(bytes(second), 101), 'text occupies much less than 18% of a window')
        self.assertTrue(state.reading_carry)
        self.assertEqual(state.decision['accent'], 'none')

    def test_small_counter_change_does_not_count_as_page_scroll(self):
        pixels = bytearray(4096)
        pixels[::64] = bytes([120]) * 64
        state = vision.DecisionState()
        state.frame(bytes(pixels), 100)
        state.accept(self.decision(), state.frame_revision, 100)
        pixels[1] = 120
        self.assertFalse(state.frame(bytes(pixels), 101))
        self.assertFalse(state.reading_carry)

    def test_native_epoch_revokes_missed_foreground_round_trip(self):
        gate = vision.ContextGate()
        first = gate.observe(self.window, allowed=True, lease=('native', 1))
        gate.observe(self.window, allowed=True, lease=('native', 3))
        self.assertFalse(gate.current(first), 'A→B→A can occur between observer samples')

    def test_exact_capture_rejects_metadata_match_from_another_address(self):
        capture = vision.WindowCapture(sys.executable)
        rows = [dict(address='0xb', identifier='other', app_id='ayugram', title=self.window.title)]
        with patch.object(vision, 'command', return_value=json.dumps(rows[0]).encode()) as command:
            with self.assertRaises(RuntimeError):
                capture.capture(self.window)
            self.assertEqual(command.call_count, 1, 'grim must not run against the wrong window')

    def test_control_lease_is_private_live_and_matches_native_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'control.json'
            control = dict(version=1, lease='native', epoch=4, identity=self.window.identity,
                           enabled=True, notBeforeUtc=99, expiresUtc=102)
            path.write_text(json.dumps(control))
            path.chmod(0o600)
            self.assertEqual(vision.control_lease(path, self.window, 100), control)
            self.assertIsNone(vision.control_lease(path, self.window, 103))
            control['enabled'] = False
            path.write_text(json.dumps(control))
            self.assertIsNone(vision.control_lease(path, self.window, 100))

    def test_atomic_packet_contains_no_title_or_image_and_has_an_expiry(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'vision.json'
            gate = vision.ContextGate()
            gate.observe(self.window, allowed=True)
            vision.publish(path, self.window, gate, self.decision(), sequence=4, now=100, permitted=True)
            data = json.loads(path.read_text())
            self.assertEqual((data['sequence'], data['identity'], data['routine']), (4, '0xa:123', 'read'))
            self.assertGreater(data['expiresUtc'], 100)
            self.assertLessEqual(data['expiresUtc'], 102, 'a stopped observer must lose its heartbeat lease promptly')
            self.assertNotIn('title', data)
            self.assertNotIn('image', data)
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)

    def test_packet_writer_refuses_symlink_target(self):
        with tempfile.TemporaryDirectory() as directory:
            outside = Path(directory) / 'outside'
            outside.write_text('preserve')
            path = Path(directory) / 'vision.json'
            path.symlink_to(outside)
            gate = vision.ContextGate()
            gate.observe(self.window, allowed=True)
            with self.assertRaises(ValueError):
                vision.publish(path, self.window, gate, self.decision(), sequence=1, now=100, permitted=True)
            self.assertEqual(outside.read_text(), 'preserve')

    def test_image_change_uses_content_not_compressed_png_encoding(self):
        self.assertEqual(vision.change_ratio(bytes([0, 0, 255, 255]), bytes([0, 0, 255, 255])), 0)
        self.assertEqual(vision.change_ratio(bytes([0, 0, 255, 255]), bytes([255, 255, 0, 0])), 1)
        self.assertEqual(vision.change_ratio(b'', b'abc'), 1)

    def test_unrendered_or_flat_window_is_not_sent_as_content(self):
        self.assertFalse(vision.usable_sample(bytes(4096)))
        self.assertFalse(vision.usable_sample(bytes([255]) * 4096))
        self.assertFalse(vision.usable_sample(bytes([0, 255])))
        self.assertTrue(vision.usable_sample(bytes([0, 255]) * 2048))

    def test_revoked_permission_stops_capture_and_running_transport(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = root / 'config.json'
            config.write_text(json.dumps(dict(allowedApps=['ayugram'])))
            control_path = root / 'vision-control.json'
            control = dict(version=1, lease='native', epoch=1, identity=self.window.identity,
                           enabled=True, notBeforeUtc=time.time() - 1, expiresUtc=time.time() + 1.5)
            control_path.write_text(json.dumps(control)); control_path.chmod(0o600)
            requests = []

            class Pending:
                def __init__(self, *_args, **_kwargs):
                    self.cancelled = False
                    requests.append(self)
                def done(self):
                    return False
                def cancel(self):
                    self.cancelled = True

            captures = []
            def capture(_window):
                captures.append(_window.identity)
                if len(captures) == 2:
                    control.update(enabled=False, epoch=2)
                    control_path.write_text(json.dumps(control))
                return b'image', bytes([0, 255]) * 2048

            args = SimpleNamespace(config=config, state=root / 'vision-state.json', capture_helper=Path(sys.executable),
                                   signature='native-test', window=None, seconds=.8, interval=2, sample_interval=.25)
            with patch.object(vision, 'compositor_client', return_value=self.window), \
                    patch.object(vision, 'session_locked', return_value=False), \
                    patch.object(vision.WindowCapture, 'capture', side_effect=capture), \
                    patch.object(vision, 'ModelRequest', Pending):
                vision.run(args)
            self.assertEqual(len(captures), 2, 'revocation must stop further pixel capture')
            self.assertEqual(len(requests), 1, 'inference is single-flight, without a queued upload')
            self.assertTrue(requests[0].cancelled, 'running transport must be cancelled, not just its future')
            self.assertFalse(json.loads(args.state.read_text())['permitted'])

    def test_model_request_cancellation_reaps_its_actual_child(self):
        original = subprocess.Popen
        def pending_process(*_args, **_kwargs):
            return original(['sleep', '30'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        with patch.object(vision.subprocess, 'Popen', side_effect=pending_process):
            request = vision.ModelRequest(b'image', app='ayugram', work_hint=False, previous='unknown',
                model='openai-codex/gpt-6-luna', control_path='/absent', control={}, observed_utc=time.time())
            request.cancel()
            self.assertIsNotNone(request.process.poll())
            self.assertFalse(request.thread.is_alive())


@unittest.skipUnless(os.environ.get('MATEENGINE_VISION_INFERENCE') == '1', 'explicit real-provider integration required')
class VisionModelIntegrationTests(unittest.TestCase):
    def test_same_app_images_select_reading_and_study(self):
        """Provider binding from public image fixtures, not desktop-capture evidence."""
        fixtures = Path(__file__).with_name('fixtures')
        with tempfile.TemporaryDirectory() as directory:
            agent_dir = str(Path.home() / '.omp/agent')
            private_home = Path(directory) / 'provider-home'
            private_home.mkdir()
            private_env = dict(os.environ, HOME=str(private_home), XDG_CONFIG_HOME=str(private_home / 'config'),
                XDG_DATA_HOME=str(private_home / 'data'), XDG_CACHE_HOME=str(private_home / 'cache'),
                XDG_STATE_HOME=str(private_home / 'state'))
            original_popen = subprocess.Popen
            def private_provider(command, **kwargs):
                return original_popen(['dbus-run-session', '--', *command], env=private_env, **kwargs)
            control_path = Path(directory) / 'control.json'
            current = dict(version=1, lease='provider-integration', epoch=1, identity='public-fixture',
                           enabled=True, notBeforeUtc=time.time() - 1, expiresUtc=time.time() + 1.5)
            stopped = threading.Event()
            def renew():
                while not stopped.is_set():
                    current['expiresUtc'] = time.time() + 1.5
                    temporary = control_path.with_suffix('.new')
                    temporary.write_text(json.dumps(current)); temporary.chmod(0o600)
                    temporary.replace(control_path)
                    stopped.wait(.2)
            renewer = threading.Thread(target=renew)
            renewer.start()
            self.addCleanup(lambda: (stopped.set(), renewer.join(timeout=2)))
            while not control_path.exists():
                time.sleep(.01)
            try:
                for name, expected in (('vision-news', 'read'), ('vision-study', 'study')):
                    with self.subTest(activity=expected):
                        image = Path(directory) / (name + '.png')
                        subprocess.run(['magick', str(fixtures / (name + '.svg')), str(image)],
                                       check=True, capture_output=True, timeout=15)
                        with patch.object(vision.subprocess, 'Popen', side_effect=private_provider):
                            decision = vision.infer(image.read_bytes(), app='same-browser', work_hint=False,
                                previous='unknown', model='openai-codex/gpt-6-luna', agent_dir=agent_dir,
                                control_path=control_path, control=current, observed_utc=time.time())
                        self.assertEqual(decision['routine'], expected)
                        print(json.dumps(dict(input=name, model='openai-codex/gpt-6-luna', decision=decision)))
            finally:
                stopped.set()
                renewer.join(timeout=2)


if __name__ == '__main__':
    unittest.main()
