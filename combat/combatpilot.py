"""Bounded local melee controller for the single-player Combat Bridge."""
import argparse
from dataclasses import dataclass
import json
import math
from pathlib import Path
import sys
import time

from combatctl import CombatClient, DEFAULT_ROOT, angles


TICK_SECONDS = 0.1
STATE_MAX_AGE_MS = 500
INPUT_TTL_MS = 400
WINDUP_SECONDS = 0.35
RECOVERY_SECONDS = 0.65
WINDUP_RANGE_MARGIN = 0.25


class PilotStop(RuntimeError):
    """A policy stop; starting again requires another explicit invocation."""


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def vector(value):
    return isinstance(value, (list, tuple)) and len(value) == 3 and all(finite(v) for v in value)


@dataclass(frozen=True)
class Goal:
    target: object = "nearest"
    seconds: float = 10.0
    max_distance: float = 20.0
    health_floor: float = 20.0
    melee_distance: float = 1.8

    def validate(self):
        if not finite(self.seconds) or not 1 <= self.seconds <= 300:
            raise ValueError("Duration must be between 1 and 300 seconds.")
        if self.target != "nearest" and (not isinstance(self.target, int) or
                isinstance(self.target, bool) or not 0 <= self.target <= 2147483647):
            raise ValueError("Target must be nearest or a nonnegative Int32 enemy index.")
        if not finite(self.max_distance) or not 2 <= self.max_distance <= 35:
            raise ValueError("Maximum approach distance must be between 2 and 35 scene units.")
        if not finite(self.health_floor) or not 0 <= self.health_floor <= 10000:
            raise ValueError("Health floor must be between 0 and 10000 hit points.")
        if not finite(self.melee_distance) or not 0.6 <= self.melee_distance <= 2.5:
            raise ValueError("Melee distance must be between 0.6 and 2.5 scene units.")
        if self.melee_distance >= self.max_distance:
            raise ValueError("Melee distance must be smaller than maximum approach distance.")


@dataclass(frozen=True)
class Decision:
    forward: float
    yaw: float
    pitch: float
    attack: str
    block: str
    target: int
    distance: float
    phase: str

    def argument(self):
        return "|".join(map(str, [self.forward, 0, self.yaw, self.pitch, self.attack, self.block, 0]))


class MeleePolicy:
    """Deterministic policy. The runner owns I/O, time, and the native input lease."""

    def __init__(self, goal):
        goal.validate()
        self.goal = goal
        self.context = None
        self.main_index = None
        self.target_index = None
        self.target_name = None
        self.phase = "ready"
        self.phase_until = 0.0
        self.progress_distance = None
        self.progress_since = None
        self.last_timestamp = None
        self.attack_cycles = 0
        self.target_changes = 0

    def _validate(self, state, wall_ms):
        if not isinstance(state, dict) or state.get("schema") != "bannerlord.combat.v1":
            raise PilotStop("Unsupported combat telemetry schema.")
        timestamp = state.get("updated_utc_ms")
        if not finite(timestamp) or not -100 <= wall_ms - timestamp <= STATE_MAX_AGE_MS:
            raise PilotStop("Combat telemetry is stale; no further input will be sent.")
        if self.last_timestamp is not None and timestamp < self.last_timestamp:
            raise PilotStop("Combat telemetry timestamp moved backwards.")
        self.last_timestamp = timestamp
        context = (state.get("session"), state.get("mission"))
        if not all(isinstance(v, str) and v for v in context):
            raise PilotStop("Combat context is missing.")
        if self.context is not None and context != self.context:
            raise PilotStop("Control token or mission changed; no automatic re-enable.")
        if state.get("emergency_stop") is not False:
            raise PilotStop("Emergency stop is set or unavailable; clear it manually before a new run.")
        if state.get("active") is not True or state.get("eligible") is not True or state.get("mode") != "Battle":
            raise PilotStop("Battle is unavailable: " + str(state.get("reason", "unknown")))
        main = state.get("main_agent")
        if not isinstance(main, dict) or main.get("active") is not True or main.get("mounted") is not False:
            raise PilotStop("A living player character on foot is required.")
        index = main.get("index")
        if not isinstance(index, int) or isinstance(index, bool) or index < 0:
            raise PilotStop("Player identity is unavailable.")
        if self.main_index is not None and index != self.main_index:
            raise PilotStop("Player character changed.")
        health = main.get("health")
        if not finite(health) or health <= 0 or health <= self.goal.health_floor:
            raise PilotStop("Player health is at or below the requested floor.")
        if not vector(main.get("position")) or not vector(main.get("look")):
            raise PilotStop("Player position or facing is invalid.")
        weapon = main.get("weapon")
        if not isinstance(weapon, dict) or weapon.get("known") is not True or weapon.get("is_melee") is not True or not (
                weapon.get("can_swing") is True or weapon.get("can_thrust") is True):
            raise PilotStop("Equip a verified melee weapon; current weapon telemetry is missing or unsupported.")
        self.context, self.main_index = context, index
        return main, weapon

    def _enemies(self, state, main):
        candidates = []
        sample = state.get("nearby_enemies")
        if not isinstance(sample, list):
            raise PilotStop("Enemy telemetry is unavailable.")
        for enemy in sample:
            if not isinstance(enemy, dict) or enemy.get("active") is not True or enemy.get("mounted") is not False:
                continue
            index = enemy.get("index")
            if not isinstance(index, int) or isinstance(index, bool) or index < 0 or index == self.main_index:
                continue
            if not finite(enemy.get("health")) or enemy["health"] <= 0 or not vector(enemy.get("position")):
                continue
            delta = [enemy["position"][i] - main["position"][i] for i in range(3)]
            distance = math.hypot(delta[0], delta[1])
            # No navigation or line-of-sight oracle exists. Reject different floors.
            if abs(delta[2]) > 1.5 or not 0.05 <= distance <= self.goal.max_distance:
                continue
            candidates.append((distance, index, enemy))
        return sorted(candidates, key=lambda value: (value[0], value[1]))

    def _select(self, candidates, now):
        if self.goal.target != "nearest":
            chosen = next((c for c in candidates if c[1] == self.goal.target), None)
            if chosen is None:
                raise PilotStop("The requested enemy is no longer a suitable sampled target.")
        else:
            # Keep the selected enemy instead of oscillating between equal distances.
            chosen = next((c for c in candidates if c[1] == self.target_index and
                           c[2].get("name") == self.target_name), None)
            if chosen is None:
                chosen = candidates[0] if candidates else None
            if chosen is None:
                raise PilotStop("No suitable nearby enemy is sampled; the pilot will not search blindly.")
        distance, index, enemy = chosen
        if index != self.target_index or enemy.get("name") != self.target_name:
            if self.goal.target != "nearest" and self.target_index is not None:
                raise PilotStop("The selected enemy identity changed.")
            self.target_index, self.target_name = index, enemy.get("name")
            self.target_changes += 1
            # Changing targets must not shorten recovery or an interrupted swing.
            cooldown = now + (RECOVERY_SECONDS if self.phase == "windup" else 0.2)
            self.phase_until = max(cooldown, self.phase_until if self.phase == "recover" else 0)
            self.phase = "recover"
            self.progress_since, self.progress_distance = now, distance
        return distance, enemy

    @staticmethod
    def _threat(candidates, main):
        """Use explicit, recognized attack telemetry only; proximity is not an attack."""
        for distance, _, enemy in candidates:
            if distance > 3.0 or not vector(enemy.get("look")):
                continue
            toward = [main["position"][i] - enemy["position"][i] for i in range(2)]
            length = math.hypot(enemy["look"][0], enemy["look"][1])
            if length < 0.01 or sum(toward[i] * enemy["look"][i] for i in range(2)) / (distance * length) < 0.65:
                continue
            attack = enemy.get("attack")
            if not isinstance(attack, dict) or attack.get("known") is not True or attack.get("active") is not True:
                continue
            # The native adapter maps attacker direction into defender input.
            direction = attack.get("block_direction")
            if direction in ("up", "down", "left", "right"):
                return distance, enemy, direction
        return None

    def decide(self, state, now, wall_ms):
        main, weapon = self._validate(state, wall_ms)
        candidates = self._enemies(state, main)
        distance, enemy = self._select(candidates, now)
        yaw, pitch = angles({"main_agent": main, "nearby_enemies": [enemy]}, target=enemy["index"])
        threat = self._threat(candidates, main)
        if threat is not None:
            # Blocking cancels the attack cycle. Do not instantly swing on its end.
            self.phase, self.phase_until = "recover", now + RECOVERY_SECONDS
            self.progress_since, self.progress_distance = now, distance
            threat_distance, attacker, direction = threat
            yaw, pitch = angles({"main_agent": main, "nearby_enemies": [attacker]}, target=attacker["index"])
            if weapon.get("can_block") is True:
                return Decision(0, yaw, pitch, "none", direction, attacker["index"], threat_distance, "block")
            return Decision(-0.4, yaw, pitch, "none", "none", attacker["index"], threat_distance, "back_away")
        # Small range jitter must not turn a held windup into repeated short taps.
        attack_range = self.goal.melee_distance + (WINDUP_RANGE_MARGIN if self.phase == "windup" else 0)
        if distance > attack_range:
            cooldown = now + (RECOVERY_SECONDS if self.phase == "windup" else 0.2)
            self.phase_until = max(cooldown, self.phase_until if self.phase == "recover" else 0)
            self.phase = "recover"
            if self.progress_distance - distance >= 0.4:
                self.progress_since, self.progress_distance = now, distance
            elif now - self.progress_since >= 3.0:
                raise PilotStop("Approach made no progress for three seconds; navigation needs another decision.")
            # Slow down close to melee range to limit overshoot between samples.
            forward = 0.35 if distance < self.goal.melee_distance + 1.0 else 0.75
            return Decision(forward, yaw, pitch, "none", "none", enemy["index"], distance, "approach")
        self.progress_since, self.progress_distance = now, distance
        if self.phase == "windup" and now >= self.phase_until:
            self.phase, self.phase_until = "recover", now + RECOVERY_SECONDS
        elif self.phase != "windup" and now >= self.phase_until:
            self.phase, self.phase_until = "windup", now + WINDUP_SECONDS
            self.attack_cycles += 1
        attack = ("up" if weapon.get("can_swing") is True else "down") if self.phase == "windup" else "none"
        return Decision(0, yaw, pitch, attack, "none", enemy["index"], distance, self.phase)


def run_goal(client, goal, clock=time):
    """Run one goal once. Never reacquire a changed token or retry failed actions."""
    goal.validate()
    policy = MeleePolicy(goal)
    started = clock.monotonic()
    deadline = started + goal.seconds
    report = {"status": "stopped", "reason": None, "accepted_inputs": 0,
              "attack_cycles": 0, "target_changes": 0, "release": "not_enabled"}
    attempted_enable = False
    try:
        state = client.bind()
        policy.decide(state, clock.monotonic(), int(clock.time() * 1000))
        if state.get("enabled") is not False or state.get("input_owned") is not False:
            raise PilotStop("Another controller may already own input. End that action before starting a pilot goal.")
        report["initial_health"] = state["main_agent"]["health"]
        attempted_enable = True
        client.send("enable", ttl_ms=INPUT_TTL_MS)
        enabled_deadline = clock.monotonic() + 0.25
        seen_enabled = False
        while deadline - clock.monotonic() >= 0.1:
            tick_start = clock.monotonic()
            state = client.state()
            # state() may block on replacement. Never decide on a sample after the deadline.
            if deadline - clock.monotonic() < 0.1:
                break
            decision = policy.decide(state, clock.monotonic(), int(clock.time() * 1000))
            if state.get("enabled") is not True:
                # The response file is published just before the state file. A
                # just-acknowledged enable can briefly expose the preceding sample.
                if not seen_enabled and clock.monotonic() < enabled_deadline:
                    clock.sleep(0.01)
                    continue
                raise PilotStop("Native control was disabled; no automatic re-enable.")
            seen_enabled = True
            remaining_ms = int((deadline - clock.monotonic()) * 1000)
            if remaining_ms < 100:
                break
            client.send("input", decision.argument(), ttl_ms=min(INPUT_TTL_MS, remaining_ms))
            report["accepted_inputs"] += 1
            report["last_phase"], report["last_target"] = decision.phase, decision.target
            report["last_observed_health"] = state["main_agent"]["health"]
            clock.sleep(max(0, min(TICK_SECONDS - (clock.monotonic() - tick_start), deadline - clock.monotonic())))
        if report["accepted_inputs"] == 0:
            report["status"], report["reason"] = "stopped", "Duration expired before any input was accepted."
        else:
            report["status"], report["reason"] = "completed", "Requested duration elapsed. Input acceptance does not confirm hits."
    except (PilotStop, RuntimeError, ValueError, OSError) as error:
        report["reason"] = str(error)
    except KeyboardInterrupt:
        report["reason"] = "Interrupted by the operator."
    finally:
        if attempted_enable:
            try:
                response = client.release()
                report["release"] = "acknowledged" if response is not None else "context_changed"
            except (RuntimeError, ValueError, OSError):
                report["release"] = "unconfirmed; last input expires within its lease"
                if report["status"] == "completed":
                    report["status"] = "uncertain"
                    report["reason"] = "Duration elapsed but release was not acknowledged; inspect fresh state before another goal."
        report["elapsed_seconds"] = round(clock.monotonic() - started, 3)
        report["attack_cycles"], report["target_changes"] = policy.attack_cycles, policy.target_changes
    return report


def target_argument(value):
    if value == "nearest":
        return value
    try:
        return int(value)
    except ValueError:
        raise argparse.ArgumentTypeError("Use nearest or an enemy index.")


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    commands = result.add_subparsers(dest="command", required=True)
    engage = commands.add_parser("engage", help="Approach and engage a sampled on-foot enemy for a bounded interval")
    engage.add_argument("target", type=target_argument, nargs="?", default="nearest")
    engage.add_argument("--seconds", type=float, default=10.0)
    engage.add_argument("--max-distance", type=float, default=20.0)
    engage.add_argument("--health-floor", type=float, default=20.0, help="Absolute hit-point floor; 0 only stops at death")
    engage.add_argument("--melee-distance", type=float, default=1.8, help="Scene-unit attack distance; not a reach or hit guarantee")
    return result


def main():
    options = parser().parse_args()
    goal = Goal(options.target, options.seconds, options.max_distance, options.health_floor, options.melee_distance)
    try:
        goal.validate()
        report = run_goal(CombatClient(options.root), goal)
        print(json.dumps(report, indent=2))
        return 0 if report["status"] == "completed" else 1
    except (ValueError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
