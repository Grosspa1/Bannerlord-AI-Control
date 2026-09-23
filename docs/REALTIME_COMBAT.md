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
  pre-mission tick. State is written at most every 100 ms. This is a prototype
  file transport, not a hard real-time latency guarantee or a completed AI policy.
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
commandable formations with observed orders, and up to 32 active enemy humans
within 100 scene units. The enemy sample is bounded in native enumeration order,
not guaranteed nearest or visible. No battle outcome or hit prediction is made.

## Acceptance checklist (in-game work remains)

Build verification on September 23, 2026: the module compiled against the
installed game; 263 protocol, 91 input-argument and 28 formation-argument checks
passed, along with 17 Python client tests and native lifecycle/runtime contract
checks. The ZIP contains the independent module and its local client. This work
did not deploy the combat module, alter the live installation or send game commands.

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
acceptance pass. No live combat success has been claimed for this prototype.
