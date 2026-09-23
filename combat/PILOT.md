# Local combat pilot

`combatpilot.py` adds a bounded, reactive melee loop to the single-player Combat
Bridge. An external assistant chooses a target and duration; this local Python
process updates movement, facing, attack release, and defensive reactions between
assistant responses. It uses only the Python standard library and the existing
`CombatClient`. It makes no model API calls and needs no API key.

GPT-5.5 can act as that assistant when it runs in an agent connected to your gaming
PC with permission to read the bridge files and launch the Python clients. Give
that agent the installed module folder and these instructions. A chat without
those local tools cannot operate the game merely by receiving the mod ZIP. The mod itself
does not create that connection or choose a model.

Opening the launcher, configuring a Custom Battle, and focusing the game are UI
setup tasks: you can do them yourself, or an assistant can use an available desktop
connector. Once the battle is ready and focused, the bridge uses local files and
Python commands; the assistant needs file access and command execution to run the
pilot. If desktop control is unavailable, the user can complete UI setup manually.
If local file or command tools are disabled or unavailable, the assistant cannot
run the pilot. Report the missing capability without bypassing the restriction.
When using Local Commander, `Agent control is disabled by the local operator`
means control must be enabled through its normal local operator control before
that agent can continue. Enable it only when the user explicitly requests it;
do not switch tools to get around an operator stop.

On this PC, Local Commander protects the live game directory from command
execution. Use its `run_process` tool with `executable: "python.exe"`,
`cwd: "C:\\Users\\Public\\BannerlordControllerBuild-combat"`, and
`args: ["combat\\combatctl.py", "status"]`. For a ready battle, use
`args: ["combat\\combatpilot.py", "engage", "nearest", "--seconds", "5"]`.
The bare executable name selects Local Commander's configured Python allowlist
entry. Do not widen that allowlist or the live-install permissions. These clients
use the same bridge directory as the installed copies. `run_process` preserves
sibling imports; Local Commander's isolated `run_python` runner is not the
documented entry point for this multi-file client.

This is an experimental controller, not a complete game-playing agent. Offline
tests cover its decisions and stop conditions. The repository's live test report
is the source for which actions have actually been observed in the game.
One live Custom Battle duel has been won: Arentor with a two-handed axe defeated
Elthild, then the pilot released control. A separate run lost to throwing axes
before reaching melee range. These results do not establish reliable combat
performance or projectile defense.

## Start a short goal

Enable the Combat Bridge, enter a disposable single-player Custom Battle, finish
deployment, dismount, and equip a melee weapon. Close menus and keep the game
focused. Launch these commands through a local assistant or a background terminal
that does not take focus away from the game. From the installed module directory
on this PC:

```powershell
Set-Location -LiteralPath 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\BannerlordCombatBridge'
python combatctl.py status
python combatpilot.py engage nearest --seconds 5
```

`Combat state is stale` is expected when the game is closed and can occur while
it is loading. Start the game with the module enabled, finish loading the battle,
then read `status` again. A fresh state at the main menu can still report no
active mission. Start the pilot only after a fresh state reports an eligible,
focused battle. Stale state is a stop condition, not a successful connection.

Start the goal when a suitable opponent is within 20 scene units, as shown by
the current positions in `status`. The pilot stops without input if none is
nearby; it does not search the map. The optional `--max-distance 35` extends
the approach radius. Inspect state before explicitly starting another goal.

Choose a particular enemy using its current `nearby_enemies[].index`:

```powershell
python combatpilot.py engage 17 --seconds 5 --health-floor 35
```

An alternate test bridge directory can be supplied before `engage`:

```powershell
python combatpilot.py --root C:\path\to\test-bridge engage nearest --seconds 5
```

The goal duration is 1–300 seconds, default 10. The default health floor is **20
absolute hit points**; reaching it ends the goal. `--max-distance` defaults to 20
scene units and permits 2–35. `--melee-distance` defaults to 1.8 scene units and
permits 0.6–2.5. It is an approach cutoff, not a hit or weapon-reach guarantee.

Only one command producer may control the bridge at a time. Issue formation
orders before starting the pilot or after it finishes. Read-only `combatctl.py
status` is safe while the pilot runs. For example, to command an actually owned
formation whose returned index is 0, then control your character:

```powershell
python combatctl.py order 0 charge
python combatpilot.py engage nearest --seconds 5
python combatctl.py status
```

Formation orders persist after the pilot stops. It does not cancel them or choose
new troop tactics by itself. A pilot refuses to start when the initial state
already reports enabled or owned input; this check is not a multi-writer lock.

## What the loop does

- Reads fresh battle telemetry and sends acknowledged leased input at up to
  roughly 10 Hz. Each input lease is at most 400 ms. File I/O and native frame
  timing can reduce that rate. The client can wait briefly for Windows command-file
  sharing contention only while the exact packet is provably unpublished. It
  retains that packet's original sequence, timestamp, and expiry; it never repeats
  an action after publication or an acknowledgment timeout.
- Selects a living, unmounted enemy on a similar vertical level. Native telemetry
  publishes the nearest 32 active enemy humans within 100 scene units, sorted by
  distance. `nearest` chooses the closest suitable target in that sample inside
  the requested approach radius. Mounted enemies can occupy sample slots, and the
  sample is not proof of visibility or a clear route.
- Keeps the chosen target until it disappears or becomes unsuitable. A specific
  target goal stops instead of silently switching enemies. Facing uses current
  world positions; approach slows near the melee cutoff.
- Requires explicit `main_agent.weapon.known` and `is_melee`, plus a verified
  swing or thrust. Missing telemetry, fists, and ranged weapons stop the pilot
  before it enables control. It does not equip weapons.
- Holds a verified overhead swing, or a thrust for a thrust-only weapon, for
  0.35 seconds, then sends neutral attack input for at least 0.65 seconds. It does
  not leave the attack button held throughout the goal. A 0.25-unit range margin
  keeps small target movement from interrupting an existing windup; it does not
  extend the range at which a new attack starts. Approach and target changes
  preserve recovery after an attack is released or interrupted.
- Gives a nearby, facing attacker priority when native telemetry explicitly
  identifies an active melee attack and a block direction. With a verified usable
  shield it faces the attacker and blocks using the native direction mapping;
  otherwise it backs away. Unknown attack telemetry is not treated as a prediction.
- Stops on the health floor, death, mounting, a changed player, stale or backwards
  telemetry, an unavailable battle, a changed session/control token, or a failed
  command. A stalled approach stops after three seconds without useful progress.

It has no pathfinder, collision avoidance, line-of-sight check, flank awareness,
hit prediction, weapon switching, mounted combat, or ranged combat. Allies and
obstacles may block a target. Fixed windup/recovery durations may need adjustment
after live testing across weapon types. Native `weapon.reach` is blade length in
metres, not the distance between two agents at which a hit is guaranteed.

## Stop and inspect

**F10 stops native bridge control and latches it off.** F9 only clears the latch;
the current pilot does not rebind or enable again. Starting another goal requires
a new explicit invocation. Ctrl+C also ends the local process's goal and attempts
release. The native input watchdog remains the fallback if the process crashes.

The pilot emits a JSON report and exits. `status: completed` means the requested
interval ended, not that an enemy was hit or defeated. `stopped` includes the
reason. `uncertain` means the final release acknowledgment was unavailable; check
fresh state before another goal. `accepted_inputs` counts acknowledgments, while
`attack_cycles` counts planned windups, not hits. Health fields are observed
snapshots and do not prove that a particular action caused damage.

Do not automatically restart after F10, a changed token, an uncertain result,
stalled approach, or lost game focus. Inspect the game and fresh state, then choose
another goal explicitly. A token change can mean a manual stop or a different
mission; it must never be treated as permission to continue fighting.

## Instructions for an assistant with local tools

Use this module only in a single-player disposable Custom Battle until its live
acceptance checks are complete. Read `combatctl.py status` before each goal. Choose
troop orders using currently commandable formation indices, then run one short
pilot goal, usually 3–10 seconds. Wait for its result, inspect fresh battle state,
and decide what to do next. Do not run another writer concurrently. Never infer a
hit, victory, or successful block from an input acknowledgment. Do not issue
another action automatically if a command outcome is uncertain, the user has
pressed F10, or the mission token has changed. Report the observed outcome and
the pilot's stop reason accurately.

## Offline verification

Tests are optional developer checks and are **not needed to play**. The installed
ZIP includes the clients and guides, but not the source repository's `combat/tests`
directory. Running test discovery from the installed module folder will fail.

On this PC the source checkout is
`C:\Users\Public\BannerlordControllerBuild-combat`. To run the pilot tests there:

```powershell
Set-Location -LiteralPath 'C:\Users\Public\BannerlordControllerBuild-combat'
python -m unittest discover -s combat/tests -p test_pilot.py -v
```

These tests use an in-memory fake client and clock. They never publish game
commands or touch the live bridge directory.
