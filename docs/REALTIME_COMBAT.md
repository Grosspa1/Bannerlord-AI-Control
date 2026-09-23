# Real-time combat prototype

The user's September 23 continuation explicitly expands the earlier strategic-only
scope. `combat/` is an independent module on `feature/realtime-combat-control`,
based on main `48b0d1f`. The strategic bridge and its Send Troops implementation
remain unchanged. Party/Economy work is preserved in its separate worktree.

Target: Bannerlord **v1.4.8.119303**, single-player deployed land field battles.
No multiplayer, replay, tournament, scene conversations, deployment or naval
control. Character input is initially on foot. Formation commands also permit
mounted player commanders. See [setup and examples](../combat/README.md).

## Execution model

- One MissionLogic is added through `OnBeforeMissionBehaviorInitialize`; the
  later hook is too late to receive `OnBehaviorInitialize`.
- Native player input is temporarily suppressed during a lease. Its camera code
  resets `IsDisabled`, so the adapter reapplies suppression in each pre-tick.
  The bridge verifies it precedes the native controller in the reverse pre-tick
  order; unsupported ordering fails closed without rearranging other mods.
- Commands are sampled at most every 20 ms; held inputs are applied every
  pre-mission tick. Periodic state is sampled every 100 ms, with an additional
  snapshot after a command. This file transport has no hard real-time latency
  guarantee.
- An application-tick watchdog also releases control when mission ticks pause.
  Character input has a separate deadline: formation orders or enable packets
  cannot prolong a stale movement/attack.
- Native orders preserve selection, check ownership/destinations and observe the
  resulting native order. The bridge never reassigns an AI agent's controller.

## Wire protocol

Directory: `C:\Users\Public\BannerlordCombatBridge`. It is separate from the
strategic bridge directory. Files: `command.json`, `response.json`, `state.json`,
`combat.log`. Publish commands by replacing the file atomically. One writer only.
No network listener, external service, API key or extra mod runtime is required.

```json
{"session":"copy from fresh state","mission":"copy from fresh state","seq":1,"sent_utc_ms":0,"ttl_ms":700,"kind":"input","args":"1|0|0|0|none|none|0"}
```

The example timestamp is a placeholder. Use current UTC Unix milliseconds.
Every field is required; command size <=4096 UTF-8 bytes, args <=512 characters.
Sequence is positive and strictly increasing for the current mission token;
TTL is 100..1000 ms, future timestamps beyond 100 ms and expired commands fail.
The module changes its session at load and its mission token on mission/control
reset. Stale files cannot become future attacks. Sequence is consumed before
native effects; errors and missing acknowledgments must not automatically retry.

| kind | args | Behavior |
| --- | --- | --- |
| `status` | empty | Request fresh telemetry; no lease renewal. |
| `enable` | empty | Enable commands for this lease if eligible and F10 is clear. |
| `release` | empty | Neutralize owned input and disable; does not undo troop orders. |
| `input` | `forward\|strafe\|yaw\|pitch\|attack\|block\|jump` | Apply bounded input until its own deadline. |
| `order` | `formationIndex\|order[\|x\|y]` | Native formation command; orders persist in the game. |

Input movement is [-1,1] (diagonals normalized), yaw [-pi,pi], pitch [-1.4,1.4].
Attack/block are none/up/down/left/right and mutually exclusive. Jump is 0 or 1;
one accepted jump packet produces one event, not one event per held frame.

Replies identify session/mission/sequence and `ok`, `message`, updated time.
Input success means accepted controls, not damage dealt. Orders return JSON text
in `message` with the observed native order and selection-restoration result;
post-dispatch errors report `FORMATION_RESULT_UNCERTAIN`. Inspect fresh state.

Telemetry includes agent health, position and look, input ownership/deadlines,
commandable formations with observed orders, and the nearest 32 active enemy
humans within 100 scene units, sorted by distance and then native agent index.
Mounted enemies remain in that bounded sample. Proximity does not establish
visibility, a clear route, or that a particular enemy is targeting the player.

Each agent also has `weapon` and `attack` objects:

- `weapon.known`, `is_melee`, `can_swing` and `can_thrust` describe the currently
  wielded usage. Swing means the native overhead input has a swing animation;
  thrust means the native downward input has a thrust animation. Checks include
  the current mount, stance, offhand and low-look context. Missing or failed
  capability reads are unknown and must not authorize an attack.
- `weapon.can_block` is conservatively true only with a wielded, intact shield.
  False with `block_capability_known: false` means weapon-only defense has not
  been established, not that the weapon cannot parry. Wielded slots, item ID,
  usage, class and ammo are also available. `reach` is native weapon length in
  metres, not a guaranteed distance between agents for a hit.
- `attack` reads native action channel 1. `active` requires matching melee ready
  or release action/stage observations. `direction` identifies the attack;
  `block_direction` already applies the native opposing-direction mapping:
  left to right, right to left, up to up and down to down. Do not mirror it again.
  `movement_flags` are input intent, not evidence of animation or contact.

## Bounded local pilot

`combat/combatpilot.py` supplies a local reactive melee loop through the existing
client. A connected assistant chooses a target and a short duration; the Python
process handles inputs between assistant responses. It needs no model API key
and does not make model calls. See [pilot instructions](../combat/PILOT.md).

The loop runs at up to approximately 10 Hz with input leases of at most 400 ms.
It requires fresh, eligible telemetry and a verified on-foot melee weapon. It
approaches a selected nearby enemy, faces it, alternates windup and release,
and prioritizes observed incoming melee attacks by blocking with a verified
shield or backing away. It stops on the health floor, stale state, loss of the
player or target, a changed control token, an unavailable battle, a failed
command, or a stalled approach. F10 cannot trigger an automatic restart.

The pilot does not equip weapons, find paths, assess line of sight, predict hits,
or control mounted/ranged combat. Troop orders are chosen separately and persist
after the pilot finishes. A completed interval or an accepted input is not proof
of a successful hit, block, kill, or victory.

## Acceptance status: partial, September 23, 2026

Deliberate integration tests installed and ran the independent combat module
alongside Strategic Bridge in Bannerlord **v1.4.8.119303**, using disposable
Custom Battles with on-foot players Elthild and Arentor. The following observations are
confirmed; they do not complete the full acceptance matrix.

| Check | Observed result |
| --- | --- |
| Module and telemetry | Both bridges loaded; combat state identified the live mission and on-foot player. Native weapon and action telemetry were readable. |
| Left strafe | A `strafe=-1` command for 0.7 seconds moved the player approximately 1.913 metres in the horizontal plane. |
| Troop orders | Shield wall and move produced the expected native order state and visible troop movement. Hold fire was accepted and read back. |
| Input timeout | An unrenewed 200 ms input ended with `COMMAND_LEASE_EXPIRED`; both `enabled` and `input_owned` were false. |
| Emergency stop | F10 latched control off and blocked subsequent input. F9 cleared the latch while leaving control disabled. |
| Attack animation | An overhead input produced `ReadyMelee` / `AttackReady`, followed by `ReleaseMelee` / `AttackRelease`. This is animation evidence, not damage confirmation. |
| Shield block animation | Upward block produced `DefendShield` / `Defend`. Successful interception of an enemy strike remains unverified. |
| Knockout and menu rejection | A knocked-out player yielded no main agent; the menu state was ineligible and a pilot invocation sent zero inputs. |
| Sustained pilot approach and knockout release | Against Arentor wielding throwing axes, the pilot accepted 56 inputs over 6.172 seconds. Elthild moved 8.478 metres horizontally; target distance fell from 17.395 to 5.228 metres. Health samples were 100, 66, 30, then no main agent. The next sample after the last live-player sample (99 ms later) showed a changed token, `enabled: false`, `input_owned: false` and zero leases. The report stopped with `MISSION_MISMATCH` and `release: context_changed`. No melee range was reached and zero attack cycles occurred. |
| One-on-one melee pilot victory | Arentor, wielding a two-handed axe, fought Elthild with axe and shield. The pilot accepted 96 inputs and planned 3 attack cycles over 10.547 seconds, with no manual attack input during the goal. Native telemetry recorded ready/release melee actions; the game displayed 102 cut damage to the shoulder, Arentor defeating Elthild in the kill feed, then battle victory. Arentor finished at 55 health. The pilot stopped when its target became unsuitable and acknowledged release; the victory snapshot had control disabled, input unowned and both leases zero. This is one observed duel win, not evidence of general combat reliability. |
| Save preservation and shutdown | Final verification after the test confirmed matching hashes for all 25 backed-up files under `Game Saves`, including cloud metadata. The game and launcher were then fully closed. |

Local raw evidence and backups are under
`combat/integration-runs/20260923-125544/` in the combat worktree; this directory
is intentionally not committed. `run.json` describes the initial deployed build;
the session also tested later telemetry and transport builds, so the initial DLL
hash must not be treated as the hash for every observation. The final deployed
test DLL SHA-256 is
`B18DF67DA7C913DA7DCC600FC8DD48D9C7C5A9CACE5877FC9A37468C36A2CBBA`.
`pilot-arentor-throwing-report.json` and `pilot-arentor-throwing-trace.json`
contain the sustained-approach evidence from that build. `pilot-live-report.json`
and `pilot-live-trace.json` contain the later melee duel, and
`melee-victory-state.json` records released control with Arentor alive at 55
health. `save-verification-final.json` records the final matching file hashes.

At this checkpoint, **sustained approach, knockout release and one melee duel
victory are tested**. The first run demonstrated a limitation: Elthild was
knocked out during approach by a throwing-axe opponent. The later melee run
demonstrated the swing/release loop, damage and a victory with a different player
weapon. Ranged-threat reactions remain outside the pilot's capabilities. One
duel does not validate other weapons, larger battles or a dependable win rate.
Remaining checks include confirmed interception of enemy strikes with a shield,
all movement and aim axes, camera synchronization, jump, focus-loss handling,
repeated mission transitions, all order variants and the full rejection matrix
below.

Testing exposed intermittent Windows replacement contention (`WinError 5`) and
a dropped native acknowledgment after a failed replacement. Both transport fixes
are implemented and tested. The client retries an identical command only while
it can establish that publication did not occur, within a bounded deadline and
the original TTL. The native bridge retains the exact acknowledgment and retries
only file publication on application ticks, at most once every 20 ms, including
after mission end. It never repeats the native action or resets its sequence.
An unresolved acknowledgment still stops the client; it is not grounds to replay.

The completed offline checkpoint comprised 263 protocol, 91 input-argument and
28 formation-argument checks (382 C# checks), native lifecycle/runtime contract
checks, and 69 Python checks (42 pilot and 27 client). Six additional scratch
checks used real Windows locked response files to verify acknowledgment
retention, retry after unlocking, supersession and clearing. These transport
tests used isolated directories. Offline checks do not replace outstanding live
tests.

## Remaining acceptance matrix

Use a disposable Custom Battle, not a valued campaign save.

1. Verify module loads alongside and independently of Strategic Bridge; state
   reports the exact current mission and main agent.
2. Check movement each axis, diagonal normalization, aim axes, blocking, a short
   attack and one jump; verify native controls return after each action.
3. Stop the client during movement/block/attack: verify input clears within the
   declared lease. Send orders during a held input and verify they do not renew it.
4. F10 while moving, then continue old packets: control must remain off. F9 alone
   must not reacquire control. A fresh explicit enable is required.
5. Pause/open menus, lose focus, die/replace main agent, exit and reload battle:
   no residual control. Repeated acquire/release must preserve native controls.
6. Issue each supported order to an owned formation; observe troop response and
   previous selection restoration. Reject enemy/allied non-owned and empty
   formations, invalid destinations and non-battle contexts.
7. Replay sequence, stale token, expired timestamp, invalid float and malformed
   JSON: reject without renewed input. Verify no changes to campaign saves.

Offline verification covers protocol/argument contracts, client behavior and
installed native lifecycle/API assumptions. It does not substitute for this
acceptance pass. Preserve the distinction between the observations above and
untested actions when handing the prototype to another agent or promoting it.
