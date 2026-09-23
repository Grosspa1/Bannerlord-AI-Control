"""Local single-player combat client. Python 3, standard library only."""
import argparse
import json
import math
import os
from pathlib import Path
import sys
import time
import uuid

DEFAULT_ROOT = Path(r"C:\Users\Public\BannerlordCombatBridge")
PUBLICATION_RETRY_SECONDS = 0.1
PUBLICATION_MIN_REMAINING_MS = 50


class CombatClient:
    def __init__(self, root=DEFAULT_ROOT):
        self.root = Path(root)
        self.sequence = time.time_ns() // 1000
        self.context = None
        self.failed = False

    def state(self, require_mission=True):
        # Transient file replacement is read-only and safe to retry.
        deadline = time.monotonic() + 0.3
        while True:
            try:
                data = json.loads((self.root / "state.json").read_text(encoding="utf-8-sig"))
                break
            except (OSError, ValueError):
                if time.monotonic() >= deadline:
                    raise RuntimeError("No readable combat state. Enable the Combat Bridge mod and enter a battle.")
                time.sleep(0.01)
        age = int(time.time() * 1000) - data.get("updated_utc_ms", 0)
        if age < -100 or age > 1000:
            raise RuntimeError("Combat state is stale. The game may be stopped or loading.")
        if require_mission and not data.get("active"):
            raise RuntimeError("Enter a single-player field battle first.")
        return data

    def bind(self):
        state = self.state()
        if not state.get("eligible"):
            raise RuntimeError(state.get("reason", "Battle is unavailable."))
        self.context = (state["session"], state["mission"])
        self.failed = False
        return state

    def send(self, kind, args="", ttl_ms=700):
        if self.failed and kind != "release":
            raise RuntimeError("Previous command failed or is uncertain. Inspect the game and explicitly bind before starting a new action.")
        if self.context is None:
            self.bind()
        self.sequence += 1
        session, mission = self.context
        command = dict(session=session, mission=mission, seq=self.sequence,
                       sent_utc_ms=int(time.time() * 1000), ttl_ms=ttl_ms, kind=kind, args=args)
        publication_started = time.monotonic()
        payload = json.dumps(command, separators=(",", ":")).encode("utf-8")
        temporary = self.root / ("command." + uuid.uuid4().hex + ".tmp")
        try:
            with temporary.open("xb") as output:
                output.write(payload)
                output.flush()
                os.fsync(output.fileno())
            self._publish(temporary, payload, command, publication_started)
        except (OSError, RuntimeError, ValueError):
            self.failed = True
            raise
        finally:
            try:
                temporary.unlink()
            except OSError:
                # A leftover uniquely named temp file is never a native command.
                # Do not turn cleanup failure into an action replay or hide its cause.
                pass
        deadline = time.monotonic() + 2
        while time.monotonic() < deadline:
            try:
                response = json.loads((self.root / "response.json").read_text(encoding="utf-8-sig"))
            except (OSError, ValueError):
                time.sleep(0.01)
                continue
            if (response.get("seq"), response.get("session"), response.get("mission")) == (self.sequence, session, mission):
                if not response.get("ok"):
                    self.failed = True
                    raise RuntimeError(response.get("message", "Command rejected."))
                return response
            time.sleep(0.01)
        self.failed = True
        raise RuntimeError("No matching acknowledgment. Input will expire; inspect state before issuing a new action.")

    def _publish(self, temporary, payload, command, started):
        destination = self.root / "command.json"
        original = temporary.stat()
        identity = (original.st_dev, original.st_ino, original.st_size)
        deadline = started + min(PUBLICATION_RETRY_SECONDS, command["ttl_ms"] / 4000.0)
        last_error = None
        while True:
            age_ms = int(time.time() * 1000) - command["sent_utc_ms"]
            if time.monotonic() >= deadline or age_ms < -100 or command["ttl_ms"] - age_ms < PUBLICATION_MIN_REMAINING_MS:
                if last_error is not None:
                    raise last_error
                raise RuntimeError("Command publication deadline expired before a safe rename; no action was published.")
            try:
                # CPython on Windows uses MoveFileExW(REPLACE_EXISTING), without
                # a copy fallback. Source and destination are in the same folder.
                os.replace(temporary, destination)
                return  # Never rename again after a successful publication.
            except OSError as error:
                # Only these native failures may be transient read/share contention.
                # Every other error, or inability to prove nonpublication, stops.
                if getattr(error, "winerror", None) not in (5, 32, 33) or not self._unpublished(
                        temporary, destination, payload, identity, command):
                    raise
                last_error = error
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise
                time.sleep(min(0.01, remaining))

    @staticmethod
    def _unpublished(temporary, destination, payload, identity, command):
        try:
            source = temporary.stat()
            if not source.st_ino or (source.st_dev, source.st_ino, source.st_size) != identity:
                return False
            if temporary.read_bytes() != payload:
                return False
            try:
                published = json.loads(destination.read_bytes())
            except FileNotFoundError:
                return True
            if not isinstance(published, dict):
                return False
            key = ("session", "mission", "seq")
            return tuple(published.get(k) for k in key) != tuple(command[k] for k in key)
        except (OSError, ValueError):
            # Missing/mutated source or unreadable/already-published destination:
            # the result is ambiguous, so do not republish anything.
            return False

    def release(self):
        if self.context is not None:
            state = self.state()
            if (state.get("session"), state.get("mission")) == self.context:
                return self.send("release")


def angles(state, yaw=None, pitch=None, target=None):
    main = state["main_agent"]
    look = main["look"]
    if target is not None:
        enemy = next((a for a in state["nearby_enemies"] if a["index"] == target and a["active"]), None)
        if enemy is None:
            raise RuntimeError("Target is no longer in the current nearby-enemy sample.")
        delta = [enemy["position"][i] - main["position"][i] for i in range(3)]
        horizontal = math.hypot(delta[0], delta[1])
        if horizontal < 0.01:
            raise RuntimeError("Target is too close to derive a stable aim direction.")
        return math.atan2(-delta[0], delta[1]), max(-1.4, min(1.4, math.atan2(delta[2], horizontal)))
    return (math.atan2(-look[0], look[1]) if yaw is None else yaw,
            max(-1.4, min(1.4, math.atan2(look[2], math.hypot(look[0], look[1])))) if pitch is None else pitch)


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    commands = result.add_subparsers(dest="command", required=True)
    commands.add_parser("status")
    commands.add_parser("release")
    order = commands.add_parser("order")
    order.add_argument("formation", type=int)
    order.add_argument("order", choices=["charge", "stop", "move", "hold_fire", "fire_at_will", "line", "shield_wall"])
    order.add_argument("coordinates", type=float, nargs="*")
    action = commands.add_parser("input")
    action.add_argument("--forward", type=float, default=0)
    action.add_argument("--strafe", type=float, default=0)
    action.add_argument("--yaw", type=float)
    action.add_argument("--pitch", type=float)
    action.add_argument("--target", type=int, help="Face a nearby enemy index; does not imply a hit or clear line of sight")
    action.add_argument("--attack", choices=["none", "up", "down", "left", "right"], default="none")
    action.add_argument("--block", choices=["none", "up", "down", "left", "right"], default="none")
    action.add_argument("--jump", action="store_true")
    action.add_argument("--seconds", type=float, default=0.3)
    return result


def run(options):
    client = CombatClient(options.root)
    if options.command == "status":
        print(json.dumps(client.state(require_mission=False), indent=2))
        return
    if options.command == "release":
        state = client.state()
        client.context = (state["session"], state["mission"])
        print(json.dumps(client.send("release"), indent=2))
        return
    if options.command == "order":
        expected = 2 if options.order == "move" else 0
        if not 0 <= options.formation <= 2147483647 or len(options.coordinates) != expected or not all(
                math.isfinite(value) and abs(value) <= 3.4028234663852886e38 for value in options.coordinates):
            raise ValueError("Use an Int32 formation index; only move takes two finite single-precision coordinates.")
    else:
        values = [options.forward, options.strafe, options.seconds]
        values += [v for v in [options.yaw, options.pitch] if v is not None]
        if not all(map(math.isfinite, values)) or not 0.05 <= options.seconds <= 10:
            raise ValueError("Use finite values and a duration between 0.05 and 10 seconds.")
        if abs(options.forward) > 1 or abs(options.strafe) > 1:
            raise ValueError("Movement must be between -1 and 1.")
        if options.attack != "none" and options.block != "none":
            raise ValueError("Attack and block cannot be held together.")
        if options.yaw is not None and abs(options.yaw) > math.pi or options.pitch is not None and abs(options.pitch) > 1.4:
            raise ValueError("Yaw must be within +/-pi; pitch within +/-1.4 radians.")
    state = client.bind()
    if options.command == "input":
        yaw, pitch = angles(state, options.yaw, options.pitch, options.target)
        if not math.isfinite(yaw) or not math.isfinite(pitch):
            raise ValueError("The current state cannot supply a finite aim direction.")
    try:
        client.send("enable")
        if options.command == "order":
            argument = "|".join(map(str, [options.formation, options.order] + options.coordinates))
            print(json.dumps(client.send("order", argument), indent=2))
        else:
            deadline = time.monotonic() + options.seconds
            first = True
            while time.monotonic() < deadline:
                state = client.state()
                if (state["session"], state["mission"]) != client.context:
                    raise RuntimeError("Control was released or the mission changed. No automatic re-enable.")
                yaw, pitch = angles(state, options.yaw, options.pitch, options.target)
                argument = "|".join(map(str, [options.forward, options.strafe, yaw, pitch,
                    options.attack, options.block, int(options.jump and first)]))
                client.send("input", argument)
                first = False
                time.sleep(min(0.05, max(0, deadline - time.monotonic())))
            print("Input accepted for the requested interval; inspect the game for its outcome.")
    finally:
        try:
            client.release()
        except (RuntimeError, OSError):
            # The independent game watchdog still expires the last input lease.
            print("Release acknowledgment unavailable; the last input lease expires automatically.", file=sys.stderr)


if __name__ == "__main__":
    try:
        run(parser().parse_args())
    except (RuntimeError, ValueError, OSError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
