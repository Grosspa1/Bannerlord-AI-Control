"""Offline policy and loop tests. No game files or commands are used."""
import copy
import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import combatpilot as pilot


def sample():
    return {
        "schema": "bannerlord.combat.v1", "session": "session", "mission": "token",
        "updated_utc_ms": 1_000_000, "active": True, "eligible": True,
        "emergency_stop": False, "enabled": True, "input_owned": False, "mode": "Battle",
        "main_agent": {"index": 1, "name": "player", "active": True, "mounted": False,
                       "health": 100, "position": [0, 0, 0], "look": [0, 1, 0],
                       "weapon": {"known": True, "is_melee": True, "can_swing": True,
                                  "can_thrust": True, "can_block": True}},
        "nearby_enemies": [
            {"index": 2, "name": "enemy", "active": True, "mounted": False,
             "health": 100, "position": [0, 1.5, 0], "look": [0, -1, 0]},
            {"index": 3, "name": "other", "active": True, "mounted": False,
             "health": 100, "position": [0, 5, 0], "look": [0, -1, 0]},
        ],
    }


class FakeClock:
    def __init__(self):
        self.elapsed = 0.0

    def monotonic(self):
        return self.elapsed

    def time(self):
        return 1000 + self.elapsed

    def sleep(self, seconds):
        if seconds < 0:
            raise AssertionError("Negative sleep")
        self.elapsed += seconds


class FakeClient:
    def __init__(self, clock):
        self.clock = clock
        self.snapshot = sample()
        self.snapshot["enabled"] = False
        self.commands = []
        self.bound = 0
        self.reads = 0
        self.context = None
        self.before_read = None
        self.fail_input = False
        self.fail_release = False
        self.freeze_timestamp = False
        self.delay_enable = False

    def state(self):
        self.reads += 1
        if self.before_read:
            self.before_read(self)
        state = copy.deepcopy(self.snapshot)
        if not self.freeze_timestamp:
            state["updated_utc_ms"] = int(self.clock.time() * 1000)
        return state

    def bind(self):
        self.bound += 1
        state = self.state()
        self.context = state["session"], state["mission"]
        return state

    def send(self, kind, args="", ttl_ms=700):
        self.commands.append((kind, args, ttl_ms, self.clock.elapsed))
        if kind == "enable" and not self.delay_enable:
            self.snapshot["enabled"] = True
        if kind == "input" and self.fail_input:
            raise RuntimeError("Uncertain acknowledgment.")
        return {"ok": True}

    def release(self):
        if self.fail_release:
            raise RuntimeError("Release unavailable.")
        if (self.snapshot["session"], self.snapshot["mission"]) != self.context:
            return None
        self.send("release")
        return {"ok": True}


class PolicyTests(unittest.TestCase):
    def setUp(self):
        self.state = sample()
        self.policy = pilot.MeleePolicy(pilot.Goal())

    def decide(self, at=0):
        self.state["updated_utc_ms"] = 1_000_000 + int(at * 1000)
        return self.policy.decide(self.state, at, self.state["updated_utc_ms"])

    def test_faces_enemy_in_world_axes_and_approaches(self):
        self.state["nearby_enemies"] = [self.state["nearby_enemies"][0]]
        self.state["nearby_enemies"][0]["position"] = [5, 0, 0]
        decision = self.decide()
        self.assertEqual(decision.phase, "approach")
        self.assertEqual(decision.forward, 0.75)
        self.assertEqual(decision.yaw, -math.pi / 2)
        self.assertEqual(decision.attack, "none")

    def test_slows_down_near_melee_range(self):
        self.state["nearby_enemies"][0]["position"][1] = 2.2
        self.assertEqual(self.decide().forward, 0.35)

    def test_attack_windup_is_released_then_recovered(self):
        self.assertEqual(self.decide(0).attack, "none")
        self.assertEqual(self.decide(0.2).attack, "up")
        self.assertEqual(self.decide(0.4).attack, "up")
        self.assertEqual(self.decide(0.56).attack, "none")
        self.assertEqual(self.decide(1.2).attack, "none")
        self.assertEqual(self.decide(1.22).attack, "up")
        self.assertEqual(self.policy.attack_cycles, 2)

    def test_thrust_only_weapon_uses_down(self):
        self.state["main_agent"]["weapon"]["can_swing"] = False
        self.decide()
        self.assertEqual(self.decide(0.2).attack, "down")

    def test_target_hysteresis_keeps_initial_target(self):
        self.assertEqual(self.decide().target, 2)
        self.state["nearby_enemies"][1]["position"][1] = 0.9
        self.assertEqual(self.decide(0.1).target, 2)

    def test_nearest_retargets_dead_enemy_with_neutral_interval(self):
        self.decide()
        self.decide(0.2)
        self.state["nearby_enemies"][0]["health"] = 0
        self.state["nearby_enemies"][1]["position"][1] = 1
        decision = self.decide(0.3)
        self.assertEqual((decision.target, decision.attack), (3, "none"))
        self.assertEqual(self.policy.target_changes, 2)

    def test_explicit_target_never_retargets(self):
        self.policy = pilot.MeleePolicy(pilot.Goal(target=3))
        self.assertEqual(self.decide().target, 3)
        self.state["nearby_enemies"][1]["active"] = False
        with self.assertRaisesRegex(pilot.PilotStop, "requested enemy"):
            self.decide(0.1)

    def test_explicit_target_rejects_identity_change(self):
        self.policy = pilot.MeleePolicy(pilot.Goal(target=2))
        self.decide()
        self.state["nearby_enemies"][0]["name"] = "replacement"
        with self.assertRaisesRegex(pilot.PilotStop, "identity changed"):
            self.decide(0.1)

    def test_ignores_mounted_distant_dead_and_different_floor_targets(self):
        for mutation in ({"mounted": True}, {"position": [0, 21, 0]},
                         {"health": 0}, {"position": [0, 1, 2]}, {"position": [0, 0, 0]},
                         {"position": [float("nan"), 1, 0]}, {"index": True}):
            with self.subTest(mutation=mutation):
                self.setUp()
                self.state["nearby_enemies"] = [self.state["nearby_enemies"][0]]
                self.state["nearby_enemies"][0].update(mutation)
                with self.assertRaisesRegex(pilot.PilotStop, "No suitable"):
                    self.decide()

    def test_unknown_or_non_melee_weapon_is_rejected(self):
        for weapon in (None, {}, {"known": True, "is_melee": False, "can_swing": True},
                       {"is_melee": True, "can_swing": True},
                       {"known": True, "is_melee": True, "can_swing": 1, "can_thrust": False}):
            with self.subTest(weapon=weapon):
                self.setUp()
                self.state["main_agent"]["weapon"] = weapon
                with self.assertRaisesRegex(pilot.PilotStop, "verified melee"):
                    self.decide()

    def test_malformed_unrelated_enemy_does_not_corrupt_facing(self):
        self.state["nearby_enemies"].insert(0, {"active": True})
        decision = self.decide()
        self.assertEqual((decision.target, decision.yaw), (2, 0))

    def test_verified_threat_uses_native_block_mapping(self):
        self.state["nearby_enemies"][0]["attack"] = {
            "known": True, "active": True, "direction": "AttackLeft", "block_direction": "right"}
        decision = self.decide()
        self.assertEqual((decision.phase, decision.attack, decision.block), ("block", "none", "right"))

    def test_unknown_block_ability_backs_away_without_attack(self):
        self.state["main_agent"]["weapon"]["can_block"] = None
        self.state["nearby_enemies"][0]["attack"] = {"known": True, "active": True, "block_direction": "up"}
        decision = self.decide()
        self.assertEqual((decision.forward, decision.attack, decision.block), (-0.4, "none", "none"))

    def test_unknown_attack_facing_away_or_distant_enemy_does_not_trigger_block(self):
        for change in ("unknown", "not_attacking", "away", "far", "bad_direction"):
            with self.subTest(change=change):
                self.setUp()
                enemy = self.state["nearby_enemies"][0]
                enemy["attack"] = {"known": True, "active": True, "block_direction": "up"}
                if change == "unknown":
                    enemy["attack"]["known"] = False
                elif change == "not_attacking":
                    enemy["attack"]["active"] = False
                elif change == "away":
                    enemy["look"] = [0, 1, 0]
                elif change == "far":
                    enemy["position"] = [0, 4, 0]
                else:
                    enemy["attack"]["block_direction"] = "invalid"
                self.assertEqual(self.decide().block, "none")

    def test_nearby_other_attacker_takes_defensive_priority(self):
        self.decide()
        enemy = self.state["nearby_enemies"][1]
        enemy["position"] = [1, 0, 0]
        enemy["look"] = [-1, 0, 0]
        enemy["attack"] = {"known": True, "active": True, "block_direction": "left"}
        decision = self.decide(0.2)
        self.assertEqual((decision.target, decision.block, decision.yaw), (3, "left", -math.pi / 2))
        self.assertEqual(self.policy.target_index, 2)

    def test_attack_does_not_resume_immediately_after_threat(self):
        enemy = self.state["nearby_enemies"][0]
        enemy["attack"] = {"known": True, "active": True, "block_direction": "up"}
        self.decide()
        enemy["attack"]["active"] = False
        self.assertEqual(self.decide(0.1).attack, "none")
        self.assertEqual(self.decide(0.7).attack, "up")

    def test_retreat_faces_the_attacker_instead_of_locked_target(self):
        self.state["main_agent"]["weapon"]["can_block"] = False
        self.decide()
        enemy = self.state["nearby_enemies"][1]
        enemy.update(position=[1, 0, 0], look=[-1, 0, 0],
                     attack={"known": True, "active": True, "block_direction": "left"})
        decision = self.decide(0.2)
        self.assertEqual((decision.target, decision.forward, decision.yaw), (3, -0.4, -math.pi / 2))
        self.assertEqual(decision.distance, 1)
        self.assertEqual(self.policy.target_index, 2)

    def test_windup_continues_through_small_range_jitter(self):
        self.decide()
        self.decide(0.2)
        self.state["nearby_enemies"][0]["position"][1] = 1.95
        decision = self.decide(0.3)
        self.assertEqual((decision.attack, decision.forward), ("up", 0))
        self.assertEqual(self.decide(0.56).attack, "none")

    def test_approach_does_not_shorten_completed_attack_recovery(self):
        self.decide()
        self.decide(0.2)
        self.decide(0.56)
        self.state["nearby_enemies"][0]["position"][1] = 2.2
        self.assertEqual(self.decide(0.6).phase, "approach")
        self.state["nearby_enemies"][0]["position"][1] = 1.5
        self.assertEqual(self.decide(0.9).attack, "none")
        self.assertEqual(self.decide(1.22).attack, "up")

    def test_interrupted_windup_observes_full_recovery(self):
        self.decide()
        self.decide(0.2)
        self.state["nearby_enemies"][0]["position"][1] = 3
        self.assertEqual(self.decide(0.3).phase, "approach")
        self.state["nearby_enemies"][0]["position"][1] = 1.5
        self.assertEqual(self.decide(0.6).attack, "none")
        self.assertEqual(self.decide(0.96).attack, "up")

    def test_retarget_does_not_bypass_swing_recovery(self):
        self.decide()
        self.decide(0.2)
        self.state["nearby_enemies"][0]["health"] = 0
        self.state["nearby_enemies"][1]["position"][1] = 1.5
        self.assertEqual(self.decide(0.3).attack, "none")
        self.assertEqual(self.decide(0.6).attack, "none")
        self.assertEqual(self.decide(0.96).attack, "up")

    def test_no_progress_stops_approach(self):
        self.state["nearby_enemies"][0]["position"][1] = 4
        self.decide()
        self.decide(2.9)
        with self.assertRaisesRegex(pilot.PilotStop, "no progress"):
            self.decide(3.01)

    def test_progress_resets_stall_timer(self):
        self.state["nearby_enemies"][0]["position"][1] = 4
        self.decide()
        self.state["main_agent"]["position"][1] = 0.5
        self.decide(2.5)
        self.assertEqual(self.decide(3.2).phase, "approach")

    def test_dead_mounted_paused_and_bad_position_stop(self):
        for key, value in (("health", 0), ("health", 20), ("mounted", True), ("active", False),
                           ("position", [0, float("inf"), 0]), ("look", [0, None, 0])):
            with self.subTest(key=key, value=value):
                self.setUp()
                self.state["main_agent"][key] = value
                with self.assertRaises(pilot.PilotStop):
                    self.decide()
        self.setUp()
        self.state["eligible"] = False
        with self.assertRaises(pilot.PilotStop):
            self.decide()

    def test_f10_missing_latch_invalid_schema_and_missing_context_stop(self):
        for key, value in (("emergency_stop", True), ("emergency_stop", None), ("schema", "next"),
                           ("session", ""), ("active", False), ("mode", "Deployment")):
            with self.subTest(key=key):
                self.setUp()
                self.state[key] = value
                with self.assertRaises(pilot.PilotStop):
                    self.decide()

    def test_token_session_or_player_replacement_never_rebinds(self):
        for key in ("mission", "session", "player"):
            with self.subTest(key=key):
                self.setUp()
                self.decide()
                if key == "player":
                    self.state["main_agent"]["index"] = 20
                else:
                    self.state[key] = "replacement"
                with self.assertRaises(pilot.PilotStop):
                    self.decide(0.1)

    def test_stale_future_and_backwards_timestamps_stop(self):
        for timestamp in (999499, 1_000_101, float("nan"), None):
            with self.subTest(timestamp=timestamp):
                self.setUp()
                self.state["updated_utc_ms"] = timestamp
                with self.assertRaises(pilot.PilotStop):
                    self.policy.decide(self.state, 0, 1_000_000)
        self.setUp()
        self.decide()
        self.state["updated_utc_ms"] -= 1
        with self.assertRaisesRegex(pilot.PilotStop, "backwards"):
            self.policy.decide(self.state, 0.01, 1_000_010)


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.client = FakeClient(self.clock)

    def run_goal(self, **kwargs):
        return pilot.run_goal(self.client, pilot.Goal(seconds=kwargs.pop("seconds", 1), **kwargs), self.clock)

    def test_bounded_loop_sends_heartbeats_then_releases(self):
        report = self.run_goal()
        self.assertEqual(report["status"], "completed")
        self.assertEqual(report["release"], "acknowledged")
        self.assertEqual(self.client.commands[0][0], "enable")
        self.assertEqual(self.client.commands[-1][0], "release")
        inputs = [c for c in self.client.commands if c[0] == "input"]
        self.assertGreaterEqual(len(inputs), 9)
        self.assertLessEqual(len(inputs), 11)
        self.assertTrue(all(100 <= c[2] <= pilot.INPUT_TTL_MS for c in inputs))
        self.assertTrue(all(c[3] < 1 for c in inputs))
        self.assertIn("up", {c[1].split("|")[4] for c in inputs})
        self.assertEqual(inputs[-1][1].split("|")[4], "none")
        self.assertEqual(self.client.bound, 1)

    def test_invalid_goal_does_not_bind_or_publish(self):
        for changes in ({"seconds": 0.9}, {"seconds": 300.1}, {"seconds": float("nan")},
                        {"target": -1}, {"target": True}, {"max_distance": 36},
                        {"health_floor": -1}, {"melee_distance": 5}):
            with self.subTest(changes=changes):
                with self.assertRaises(ValueError):
                    self.run_goal(**changes)
        self.assertEqual(self.client.bound, 0)
        self.assertEqual(self.client.commands, [])

    def test_invalid_initial_state_never_enables_or_releases(self):
        self.client.snapshot["main_agent"].pop("weapon")
        report = self.run_goal()
        self.assertEqual(report["status"], "stopped")
        self.assertEqual(self.client.commands, [])
        self.assertEqual(report["release"], "not_enabled")

    def test_existing_controller_is_not_taken_over_or_released(self):
        for key in ("enabled", "input_owned"):
            with self.subTest(key=key):
                self.setUp()
                self.client.snapshot[key] = True
                report = self.run_goal()
                self.assertIn("Another controller", report["reason"])
                self.assertEqual(self.client.commands, [])

    def test_token_change_stops_and_does_not_publish_into_new_context(self):
        def change(client):
            if client.reads >= 4:
                client.snapshot["mission"] = "new-token"
        self.client.before_read = change
        report = self.run_goal()
        self.assertIn("token", report["reason"])
        self.assertEqual(report["release"], "context_changed")
        self.assertEqual(self.client.bound, 1)
        self.assertEqual([c[0] for c in self.client.commands], ["enable", "input", "input"])

    def test_death_f10_lost_eligibility_or_disabled_state_stops_inputs(self):
        for reason in ("death", "f10", "paused", "disabled", "weapon"):
            with self.subTest(reason=reason):
                self.setUp()
                def change(client):
                    if client.reads >= 4:
                        if reason == "death":
                            client.snapshot["main_agent"]["health"] = 0
                        elif reason == "f10":
                            client.snapshot["emergency_stop"] = True
                        elif reason == "paused":
                            client.snapshot["eligible"] = False
                        elif reason == "weapon":
                            client.snapshot["main_agent"]["weapon"]["is_melee"] = False
                        else:
                            client.snapshot["enabled"] = False
                self.client.before_read = change
                report = self.run_goal()
                self.assertEqual(report["status"], "stopped")
                self.assertEqual([c[0] for c in self.client.commands], ["enable", "input", "input", "release"])

    def test_uncertain_input_is_not_replayed(self):
        self.client.fail_input = True
        report = self.run_goal()
        self.assertIn("Uncertain", report["reason"])
        self.assertEqual([c[0] for c in self.client.commands], ["enable", "input", "release"])

    def test_stale_sample_cannot_renew_input_indefinitely(self):
        self.client.freeze_timestamp = True
        report = self.run_goal(seconds=10)
        self.assertIn("stale", report["reason"])
        self.assertLess(report["elapsed_seconds"], 1)
        self.assertLessEqual(report["accepted_inputs"], 6)

    def test_slow_state_read_never_sends_input_after_deadline(self):
        def delay(client):
            if client.reads > 1:
                self.clock.sleep(1.1)
        self.client.before_read = delay
        report = self.run_goal()
        self.assertEqual(report["accepted_inputs"], 0)
        self.assertEqual(report["status"], "stopped")
        self.assertIn("before any input", report["reason"])
        self.assertEqual([c[0] for c in self.client.commands], ["enable", "release"])

    def test_delayed_enable_ack_cannot_report_completed_without_inputs(self):
        original_send = self.client.send
        def delayed_send(kind, args="", ttl_ms=700):
            result = original_send(kind, args, ttl_ms)
            if kind == "enable":
                self.clock.sleep(1.1)
            return result
        self.client.send = delayed_send
        report = self.run_goal()
        self.assertEqual(report["status"], "stopped")
        self.assertEqual(report["accepted_inputs"], 0)
        self.assertIn("before any input", report["reason"])
        self.assertEqual(report["release"], "acknowledged")
        self.assertEqual([c[0] for c in self.client.commands], ["enable", "release"])

    def test_release_failure_reports_watchdog_fallback(self):
        self.client.fail_release = True
        report = self.run_goal()
        self.assertIn("unconfirmed", report["release"])
        self.assertEqual(report["status"], "uncertain")

    def test_maximum_duration_stays_bounded(self):
        report = self.run_goal(seconds=300)
        self.assertEqual(report["status"], "completed")
        self.assertLessEqual(report["elapsed_seconds"], 300)
        self.assertLessEqual(report["accepted_inputs"], 3001)
        self.assertTrue(all(c[3] < 300 for c in self.client.commands if c[0] == "input"))

    def test_enable_response_may_precede_enabled_state(self):
        self.client.delay_enable = True
        def lag(client):
            if self.clock.elapsed >= 0.03:
                client.snapshot["enabled"] = True
        self.client.before_read = lag
        report = self.run_goal()
        self.assertEqual(report["status"], "completed")
        self.assertGreater(report["accepted_inputs"], 0)
        self.assertEqual(sum(c[0] == "enable" for c in self.client.commands), 1)

    def test_enable_without_observable_state_stops_without_retry(self):
        self.client.delay_enable = True
        report = self.run_goal()
        self.assertEqual(report["status"], "stopped")
        self.assertEqual(report["accepted_inputs"], 0)
        self.assertEqual([c[0] for c in self.client.commands], ["enable", "release"])

    def test_cli_parses_agent_target_and_finite_bounds(self):
        options = pilot.parser().parse_args(["engage", "23", "--seconds", "7", "--health-floor", "40"])
        self.assertEqual((options.target, options.seconds, options.health_floor), (23, 7, 40))
        self.assertEqual(pilot.parser().parse_args(["engage", "nearest"]).target, "nearest")


if __name__ == "__main__":
    unittest.main(verbosity=2)
