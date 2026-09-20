import json
import os
import sys
import time
import uuid

ROOT = r"C:\Users\Public\BannerlordBridge"
CMD = os.path.join(ROOT, "command.txt")
RESP = os.path.join(ROOT, "response.json")
STATE = os.path.join(ROOT, "state.json")


def atomic_command_write(text: str, command_id: str) -> None:
    tmp = os.path.join(ROOT, f"command.{command_id}.tmp")
    with open(tmp, "w", encoding="utf-8", newline="") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    deadline = time.time() + 2.0
    while True:
        try:
            os.replace(tmp, CMD)
            return
        except OSError:
            if time.time() >= deadline:
                try:
                    os.unlink(tmp)
                except OSError:
                    pass
                raise
            time.sleep(0.05)


def read_json(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def main():
    os.makedirs(ROOT, exist_ok=True)
    if len(sys.argv) < 2:
        print("usage: blctl.py <verb> [argument]")
        return 2

    verb = sys.argv[1].strip()
    arg = " ".join(sys.argv[2:]).strip()
    cid = str(int(time.time() * 1000)) + "-" + uuid.uuid4().hex[:8]
    atomic_command_write(cid + "|" + verb + "|" + arg, cid)

    deadline = time.time() + 15.0
    while time.time() < deadline:
        try:
            response = read_json(RESP)
            if response.get("id") == cid:
                print(json.dumps(response, indent=2))
                if os.path.exists(STATE):
                    try:
                        print(json.dumps(read_json(STATE), indent=2))
                    except Exception as exc:
                        print(f"State read warning: {exc}", file=sys.stderr)
                return 0 if response.get("ok") else 1
        except Exception:
            pass
        time.sleep(0.15)

    print("Timed out waiting for Bannerlord bridge.", file=sys.stderr)
    return 3


if __name__ == "__main__":
    raise SystemExit(main())
