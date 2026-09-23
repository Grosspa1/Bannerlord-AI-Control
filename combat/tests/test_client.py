"""Offline combat-client regressions. All bridge files live in temporary folders."""
import contextlib
import copy
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import tempfile
import sys
import unittest
from unittest import mock


CLIENT_PATH = Path(__file__).resolve().parents[1] / "combatctl.py"
SPEC = importlib.util.spec_from_file_location("combat_client_under_test", CLIENT_PATH)
client_module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(client_module)


class FakeClock:
    def __init__(self):
        self.elapsed = 0.0
        self.on_sleep = None

    def time(self):
        return 1000.0 + self.elapsed

    def time_ns(self):
        return int(self.time() * 1_000_000_000)

    def monotonic(self):
        return self.elapsed

    def sleep(self, seconds):
        self.elapsed += seconds
        if self.on_sleep:
            self.on_sleep()


class ClientTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="bannerlord-combat-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.clock = FakeClock()
        self.clock_patch = mock.patch.object(client_module, "time", self.clock)
        self.clock_patch.start()
        self.addCleanup(self.clock_patch.stop)
        self.state = {
            "schema": "bannerlord.combat.v1", "session": "session-a",
            "mission": "mission-a", "updated_utc_ms": 1_000_000,
            "active": True, "eligible": True, "reason": "DISABLED",
            "main_agent": {"index": 1, "position": [10, 20, 3], "look": [0, 1, 0]},
            "nearby_enemies": [
                {"index": 2, "active": True, "position": [10, 30, 3]},
                {"index": 3, "active": False, "position": [20, 20, 3]},
            ],
        }
        self.write_state()
        # Explicit root is mandatory here; never instantiate against DEFAULT_ROOT.
        self.client = client_module.CombatClient(self.root)

    def write_state(self):
        (self.root / "state.json").write_text(json.dumps(self.state), encoding="utf-8")

    def response_for(self, command, **changes):
        response = {key: command[key] for key in ("session", "mission", "seq")}
        response.update(ok=True, message="accepted", updated_utc_ms=int(self.clock.time() * 1000))
        response.update(changes)
        (self.root / "response.json").write_text(json.dumps(response), encoding="utf-8")
        return response

    def options(self, *arguments):
        return client_module.parser().parse_args(["--root", str(self.root)] + list(arguments))

    @staticmethod
    def windows_error(code=5):
        error = PermissionError("Simulated Windows rename contention")
        error.winerror = code
        return error

    def test_command_is_fsynced_then_published_once_as_complete_json(self):
        destination = self.root / "command.json"
        destination.write_text("previous command", encoding="utf-8")
        actual_replace = os.replace
        replacements = []
        actual_fsync = os.fsync
        with mock.patch.object(client_module.os, "fsync", wraps=actual_fsync) as fsync:
            def publish(source, target):
                self.assertEqual(destination.read_text(encoding="utf-8"), "previous command")
                self.assertEqual(Path(source).parent, self.root)
                self.assertEqual(Path(target), destination)
                self.assertTrue(fsync.called)
                raw = Path(source).read_bytes()
                self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
                command = json.loads(raw)
                replacements.append(command)
                actual_replace(source, target)
                self.response_for(command)
            with mock.patch.object(client_module.os, "replace", side_effect=publish):
                response = self.client.send("input", '0|1|0|0|none|none|0')
        self.assertTrue(response["ok"])
        self.assertEqual(len(replacements), 1)
        command = replacements[0]
        self.assertEqual(set(command), {"session", "mission", "seq", "sent_utc_ms", "ttl_ms", "kind", "args"})
        self.assertEqual((command["session"], command["mission"]), ("session-a", "mission-a"))
        self.assertEqual(command["sent_utc_ms"], 1_000_000)
        self.assertEqual(command["ttl_ms"], 700)
        self.assertEqual(command["kind"], "input")
        self.assertEqual(command["args"], '0|1|0|0|none|none|0')
        self.assertIsInstance(command["seq"], int)
        self.assertEqual(list(self.root.glob("command.*.tmp")), [])

    def test_timeout_latches_until_an_explicit_fresh_bind(self):
        with self.assertRaisesRegex(RuntimeError, "acknowledgment"):
            self.client.send("order", "0|charge")
        self.assertTrue(self.client.failed)
        with mock.patch.object(client_module.os, "replace") as replace:
            with self.assertRaisesRegex(RuntimeError, "Previous command"):
                self.client.send("input", "1|0|0|0|none|none|0")
            replace.assert_not_called()
        self.state["updated_utc_ms"] = int(self.clock.time() * 1000)
        self.write_state()
        self.client.bind()
        self.assertFalse(self.client.failed)

    def test_release_remains_available_after_an_uncertain_command(self):
        self.client.bind()
        self.client.failed = True
        actual_replace = os.replace
        def publish(source, target):
            command = json.loads(Path(source).read_text(encoding="utf-8"))
            actual_replace(source, target)
            self.response_for(command)
        with mock.patch.object(client_module.os, "replace", side_effect=publish):
            self.assertTrue(self.client.release()["ok"])
        self.assertEqual(json.loads((self.root / "command.json").read_text())["kind"], "release")

    def test_response_must_match_sequence_session_and_mission(self):
        for field, wrong in (("seq", 0), ("session", "old-session"), ("mission", "old-mission")):
            with self.subTest(field=field):
                self.clock.elapsed = 0
                self.client = client_module.CombatClient(self.root)
                actual_replace = os.replace
                published = []
                def publish(source, target):
                    command = json.loads(Path(source).read_text(encoding="utf-8"))
                    actual_replace(source, target)
                    published.append(command)
                    self.response_for(command, **{field: wrong})
                def finish():
                    if self.clock.elapsed >= 0.03:
                        self.response_for(published[0], message="correct acknowledgment")
                self.clock.on_sleep = finish
                with mock.patch.object(client_module.os, "replace", side_effect=publish):
                    response = self.client.send("status")
                self.assertEqual(response["message"], "correct acknowledgment")
                self.assertEqual(len(published), 1)
                self.assertGreaterEqual(self.clock.elapsed, 0.03)

    def test_timeout_never_republishes_or_retries_action(self):
        actual_replace = os.replace
        with mock.patch.object(client_module.os, "replace", wraps=actual_replace) as replace:
            with self.assertRaisesRegex(RuntimeError, "acknowledgment|expire|stale"):
                self.client.send("order", "0|charge")
        self.assertEqual(replace.call_count, 1)
        self.assertEqual(json.loads((self.root / "command.json").read_text())["kind"], "order")
        self.assertEqual(list(self.root.glob("command.*.tmp")), [])

    def test_rejected_response_is_not_retried(self):
        actual_replace = os.replace
        def publish(source, target):
            command = json.loads(Path(source).read_text(encoding="utf-8"))
            actual_replace(source, target)
            self.response_for(command, ok=False, message="CONTROL_NOT_ENABLED")
        with mock.patch.object(client_module.os, "replace", side_effect=publish) as replace:
            with self.assertRaisesRegex(RuntimeError, "CONTROL_NOT_ENABLED"):
                self.client.send("input", "0|0|0|0|none|none|0")
        self.assertEqual(replace.call_count, 1)

    def test_publication_failure_cleans_temporary_file_without_retry(self):
        with mock.patch.object(client_module.os, "replace", side_effect=PermissionError("locked")) as replace:
            with self.assertRaises(PermissionError):
                self.client.send("status")
        self.assertEqual(replace.call_count, 1)
        self.assertEqual(list(self.root.glob("command.*.tmp")), [])
        self.assertFalse((self.root / "command.json").exists())
        self.assertTrue(self.client.failed)

    def test_known_unpublished_windows_contention_retries_identical_packet(self):
        destination = self.root / "command.json"
        destination.write_text('{"seq":0,"session":"old","mission":"old"}', encoding="utf-8")
        actual_replace = os.replace
        attempts = []
        def publish(source, target):
            attempts.append(Path(source).read_bytes())
            if len(attempts) < 3:
                self.assertEqual(json.loads(destination.read_text())["seq"], 0)
                raise self.windows_error(5 if len(attempts) == 1 else 32)
            actual_replace(source, target)
            self.response_for(json.loads(attempts[-1]))
        with mock.patch.object(client_module.os, "replace", side_effect=publish):
            response = self.client.send("input", "1|0|0|0|none|none|0", ttl_ms=400)
        self.assertTrue(response["ok"])
        self.assertEqual(len(attempts), 3)
        self.assertEqual(len(set(attempts)), 1)
        self.assertEqual(json.loads(attempts[0])["sent_utc_ms"], 1_000_000)
        self.assertAlmostEqual(self.clock.elapsed, 0.02)

    def test_rename_contention_budget_is_bounded_by_original_ttl(self):
        for ttl, budget in ((100, 0.025), (400, 0.1), (700, 0.1)):
            with self.subTest(ttl=ttl):
                self.clock.elapsed = 0
                self.client = client_module.CombatClient(self.root)
                with mock.patch.object(client_module.os, "replace", side_effect=self.windows_error(33)) as replace:
                    with self.assertRaises(PermissionError):
                        self.client.send("input", "1|0|0|0|none|none|0", ttl_ms=ttl)
                self.assertGreater(replace.call_count, 1)
                self.assertLessEqual(self.clock.elapsed, budget + 0.00001)
                self.assertTrue(self.client.failed)
                self.assertFalse((self.root / "command.json").exists())
                self.assertEqual(list(self.root.glob("command.*.tmp")), [])

    def test_postpublication_rename_error_is_uncertain_and_never_retried(self):
        actual_replace = os.replace
        def publish_then_error(source, target):
            actual_replace(source, target)
            raise self.windows_error()
        with mock.patch.object(client_module.os, "replace", side_effect=publish_then_error) as replace:
            with self.assertRaises(PermissionError):
                self.client.send("order", "0|charge")
        self.assertEqual(replace.call_count, 1)
        self.assertTrue(self.client.failed)
        self.assertEqual(json.loads((self.root / "command.json").read_text())["kind"], "order")

    def test_current_destination_packet_blocks_retry_even_if_source_remains(self):
        def copy_then_error(source, target):
            Path(target).write_bytes(Path(source).read_bytes())
            raise self.windows_error()
        with mock.patch.object(client_module.os, "replace", side_effect=copy_then_error) as replace:
            with self.assertRaises(PermissionError):
                self.client.send("order", "0|charge")
        self.assertEqual(replace.call_count, 1)
        self.assertTrue(self.client.failed)

    def test_mutated_temporary_packet_never_retries(self):
        def mutate_then_error(source, target):
            original = Path(source).read_bytes()
            Path(source).write_bytes(original.replace(b'"status"', b'"enable"'))
            raise self.windows_error()
        with mock.patch.object(client_module.os, "replace", side_effect=mutate_then_error) as replace:
            with self.assertRaises(PermissionError):
                self.client.send("status")
        self.assertEqual(replace.call_count, 1)
        self.assertFalse((self.root / "command.json").exists())

    def test_replaced_temporary_file_identity_blocks_retry_even_with_same_bytes(self):
        actual_replace = os.replace
        def substitute_then_error(source, target):
            replacement = self.root / "substitute.tmp"
            replacement.write_bytes(Path(source).read_bytes())
            actual_replace(replacement, source)
            raise self.windows_error()
        with mock.patch.object(client_module.os, "replace", side_effect=substitute_then_error) as replace:
            with self.assertRaises(PermissionError):
                self.client.send("status")
        self.assertEqual(replace.call_count, 1)
        self.assertFalse((self.root / "command.json").exists())

    def test_unreadable_destination_is_ambiguous_and_not_retried(self):
        actual_read = Path.read_bytes
        def read(path):
            if path.name == "command.json":
                raise self.windows_error()
            return actual_read(path)
        with mock.patch.object(Path, "read_bytes", read), mock.patch.object(
                client_module.os, "replace", side_effect=self.windows_error()) as replace:
            with self.assertRaises(PermissionError):
                self.client.send("status")
        self.assertEqual(replace.call_count, 1)
        self.assertTrue(self.client.failed)

    def test_unexpected_windows_error_is_not_retried(self):
        with mock.patch.object(client_module.os, "replace", side_effect=self.windows_error(1117)) as replace:
            with self.assertRaises(PermissionError):
                self.client.send("status")
        self.assertEqual(replace.call_count, 1)

    def test_retry_never_refreshes_expired_command_timestamp(self):
        def delay_then_error(source, target):
            self.clock.elapsed += 0.5
            raise self.windows_error()
        with mock.patch.object(client_module.os, "replace", side_effect=delay_then_error) as replace:
            with self.assertRaises(PermissionError):
                self.client.send("status", ttl_ms=400)
        self.assertEqual(replace.call_count, 1)
        self.assertFalse((self.root / "command.json").exists())

    @unittest.skipUnless(sys.platform == "win32", "Exercises actual Win32 file sharing on a temporary directory")
    def test_real_windows_read_handle_contention_recovers_before_publication(self):
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                      wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        destination = self.root / "command.json"
        destination.write_text('{"seq":0,"session":"old","mission":"old"}', encoding="utf-8")
        # Read access/share, deliberately without FILE_SHARE_DELETE. No game files.
        handle = kernel.CreateFileW(str(destination), 0x80000000, 1, None, 3, 0, None)
        self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
        actual_replace = os.replace
        failures = []
        publications = []
        def publish(source, target):
            command = json.loads(Path(source).read_bytes())
            try:
                actual_replace(source, target)
            except OSError as error:
                failures.append(error.winerror)
                raise
            publications.append(command)
            self.response_for(command)
        def unlock():
            nonlocal handle
            if self.clock.elapsed >= 0.02 and handle is not None:
                kernel.CloseHandle(handle)
                handle = None
        self.clock.on_sleep = unlock
        try:
            with mock.patch.object(client_module.os, "replace", side_effect=publish):
                result = self.client.send("input", "0|1|0|0|none|none|0", ttl_ms=400)
        finally:
            if handle is not None:
                kernel.CloseHandle(handle)
        self.assertTrue(result["ok"])
        self.assertGreaterEqual(len(failures), 1)
        self.assertTrue(all(code in (5, 32, 33) for code in failures))
        self.assertEqual(len(publications), 1)

    def test_stale_future_and_inactive_state_are_rejected(self):
        for updated in (998999, 1_000_101):
            with self.subTest(updated=updated):
                self.state["updated_utc_ms"] = updated
                self.write_state()
                with self.assertRaisesRegex(RuntimeError, "stale"):
                    self.client.state()
        self.state.update(updated_utc_ms=1_000_000, active=False)
        self.write_state()
        with self.assertRaisesRegex(RuntimeError, "battle"):
            self.client.state()
        self.assertFalse(self.client.state(require_mission=False)["active"])

    def test_unreadable_state_retries_reads_only(self):
        (self.root / "state.json").write_text("{", encoding="utf-8")
        self.clock.on_sleep = self.write_state
        self.assertEqual(self.client.state()["mission"], "mission-a")
        self.assertGreater(self.clock.elapsed, 0)
        self.assertFalse((self.root / "command.json").exists())

    def test_ineligible_bind_never_publishes(self):
        self.state.update(eligible=False, reason="paused")
        self.write_state()
        with self.assertRaisesRegex(RuntimeError, "paused"):
            self.client.send("enable")
        self.assertIsNone(self.client.context)
        self.assertFalse((self.root / "command.json").exists())

    def test_release_skips_a_different_mission_or_session(self):
        for field in ("session", "mission"):
            with self.subTest(field=field):
                self.client.context = ("session-a", "mission-a")
                changed = copy.deepcopy(self.state)
                changed[field] = "replacement"
                with mock.patch.object(self.client, "state", return_value=changed), mock.patch.object(self.client, "send") as send:
                    self.client.release()
                    send.assert_not_called()

    def test_input_aborts_changed_context_without_automatic_reenable(self):
        for field in ("session", "mission"):
            with self.subTest(field=field):
                fake = mock.Mock()
                fake.context = ("session-a", "mission-a")
                fake.bind.return_value = self.state
                changed = copy.deepcopy(self.state)
                changed[field] = "replacement"
                fake.state.return_value = changed
                with mock.patch.object(client_module, "CombatClient", return_value=fake):
                    with self.assertRaisesRegex(RuntimeError, "mission changed|released"):
                        client_module.run(self.options("input", "--seconds", "0.1"))
                fake.send.assert_called_once_with("enable")
                fake.release.assert_called_once_with()

    def test_invalid_numeric_cli_arguments_fail_before_enable(self):
        invalid = [
            ["input", "--forward", "1.01"], ["input", "--strafe", "-1.01"],
            ["input", "--forward", "nan"], ["input", "--seconds", "inf"],
            ["input", "--seconds", "0.01"], ["input", "--seconds", "10.01"],
            ["input", "--yaw", "3.15"], ["input", "--pitch", "1.41"],
            ["input", "--attack", "up", "--block", "down"],
            ["order", "-1", "charge"], ["order", "0", "charge", "1"],
            ["order", "0", "move", "1"], ["order", "0", "move", "1", "nan"],
            ["order", "2147483648", "charge"], ["order", "0", "move", "1e100", "0"],
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                fake = mock.Mock()
                with mock.patch.object(client_module, "CombatClient", return_value=fake):
                    with self.assertRaises(ValueError):
                        client_module.run(self.options(*arguments))
                fake.bind.assert_not_called()
                fake.send.assert_not_called()

    def test_missing_inactive_or_too_close_target_fails_before_enable(self):
        too_close = {"index": 4, "active": True, "position": [10, 20, 30]}
        self.state["nearby_enemies"].append(too_close)
        for target in (999, 3, 4):
            with self.subTest(target=target):
                fake = mock.Mock()
                fake.bind.return_value = self.state
                fake.state.return_value = self.state
                fake.context = ("session-a", "mission-a")
                with mock.patch.object(client_module, "CombatClient", return_value=fake):
                    with self.assertRaisesRegex(RuntimeError, "Target"):
                        client_module.run(self.options("input", "--target", str(target)))
                fake.send.assert_not_called()

    def test_order_is_enabled_sent_once_and_released(self):
        fake = mock.Mock()
        fake.bind.return_value = self.state
        fake.send.return_value = {"ok": True}
        with mock.patch.object(client_module, "CombatClient", return_value=fake), contextlib.redirect_stdout(io.StringIO()):
            client_module.run(self.options("order", "2", "move", "10.5", "20.25"))
        self.assertEqual(fake.send.call_args_list, [mock.call("enable"), mock.call("order", "2|move|10.5|20.25")])
        fake.release.assert_called_once_with()

    def test_angles_preserve_view_or_use_explicit_radians(self):
        self.assertEqual(client_module.angles(self.state), (0.0, 0.0))
        self.assertEqual(client_module.angles(self.state, 1.2, -0.3), (1.2, -0.3))
        self.state["main_agent"]["look"] = [1, 0, 1]
        yaw, pitch = client_module.angles(self.state)
        self.assertAlmostEqual(yaw, -math.pi / 2)
        self.assertAlmostEqual(pitch, math.pi / 4)
        self.state["main_agent"]["look"] = [0, 0, 1]
        self.assertEqual(client_module.angles(self.state)[1], 1.4)

    def test_target_angles_match_bannerlord_world_axes_and_clamp_pitch(self):
        target = self.state["nearby_enemies"][0]
        for position, expected_yaw in (([10, 30, 3], 0), ([20, 20, 3], -math.pi / 2), ([0, 20, 3], math.pi / 2)):
            with self.subTest(position=position):
                target["position"] = position
                yaw, pitch = client_module.angles(self.state, target=2)
                self.assertAlmostEqual(yaw, expected_yaw)
                self.assertEqual(pitch, 0)
        target["position"] = [10, 20.1, 1000]
        self.assertEqual(client_module.angles(self.state, target=2)[1], 1.4)
        target["position"] = [10, 20.1, -1000]
        self.assertEqual(client_module.angles(self.state, target=2)[1], -1.4)


if __name__ == "__main__":
    unittest.main(verbosity=2)
