# Player input adapter: API evidence and limits

Verified against the locally installed Bannerlord **v1.4.8.119303** managed assemblies using Mono.Cecil. This is API/IL verification; it does not prove that movement, attacks, or camera behavior have passed an in-game test.

`PlayerInput` acquires only the existing human player agent in an active single-player field, siege, or sally-out battle. It rejects multiplayer/replays, deployment, arenas, tournaments, scene conversations, naval battles, mounted agents, dead/replaced agents, paused/inactive game states, lost focus, visible menu cursors, photo mode, and object-use/locked movement. It leaves `Agent.Controller` as `Player` and never hands the character to native AI.

## Verified control surface

- `MBSubModuleBase.OnBeforeMissionBehaviorInitialize(Mission)` is public virtual. Add the bridge through `Mission.AddMissionBehavior(MissionBehavior)` here if the bridge relies on its `OnBehaviorInitialize`. `Mission.AfterStart` calls that before hook, initializes existing behaviors, and only then invokes `OnMissionBehaviorInitialize`. Adding at the latter hook does not automatically initialize the new behavior: `AddMissionBehavior` invokes only `OnCreated`, whose base implementation is empty.
- `Mission.OnPreTick(float)` waits for prior tick completion and invokes `OnPreMissionTick(float)` in **reverse behavior-list order**. A newly appended bridge therefore runs before the existing player controller.
- `PlayerInput.ValidateTickOrder(mission, driver)` checks the actual list indices without changing them. The mission driver must call it before applying input and fail closed if a later-added/replaced native controller would execute first.
- `MissionMainAgentController.OnPreMissionTick` runs its `ControlTick` and `LookTick` only while `IsDisabled == false`. The public setter writes a managed Boolean field. Crucially, `MissionScreen.UpdateCamera` resets this field each frame (`IL_0138` through `IL_0180`). The adapter therefore requires it to be false at acquisition and **reasserts true on each bridge pre-tick**, before the native controller's pre-tick. It restores false at release. A false value between frames is normal, not proof of an external takeover.
- Native `ControlTick` clears `EventControlFlags`, `MovementFlags`, and `MovementInputVector` before gathering physical controls. Native `LookTick` writes `Agent.LookDirection`. Simply writing these from a normal mission tick without suppressing the native controller is insufficient.
- `Agent.MovementInputVector` is a public `Vec2` setter. Native input maps `MovementAxisX` to `x` and `MovementAxisY` to `y`. Ground movement needs this vector; the native on-foot code uses movement flags for attack/defense rather than forward/strafe flags. The adapter normalizes diagonals to length one.
- `Agent.MovementFlags` and `Agent.EventControlFlags` are public setters. Attack/defense are held directional flags; jump is an event flag set for one frame. Releasing attack is ordinary game input and can release an already prepared swing. A stop is not a rewind or a guarantee that an already committed attack will be canceled.
- `Agent.LookDirection` is a public `Vec3` setter. `Vec2.FromRotation(yaw)` is `(-sin(yaw), cos(yaw))`; the adapter extends that convention with pitch. Yaw zero faces world +Y, positive yaw faces toward -X, and positive pitch looks upward.
- The controller is in `Modules/Native/bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.View.dll`. Add normal compile references to View, InputSystem, and ScreenSystem as well as the existing game assemblies and bundled Mono/.NET Standard facade. No Harmony patch is needed for this implementation.

## Adapter contract

`Acquire(Mission)`, `Apply(Mission, string)`, and `Tick(Mission)` return null on success and a human-readable reason on rejection. `Apply` accepts exactly:

```text
forward|strafe|yaw|pitch|attack|block|jump
```

Movement is in `[-1,1]`; yaw is `[-pi,pi]` radians; pitch is `[-1.4,1.4]`. Attack and block are `none`, `up`, `down`, `left`, or `right`, with at most one held simultaneously. Jump is `0` or `1`. A new valid application of jump `1` produces one event; repeated ticks do not repeat it. An invalid argument never replaces the current input. The caller must reject duplicate/replayed packets and must define their expiry.

`Release()` is idempotent. Only a successful acquisition permits it to restore the controller. It neutralizes vectors/flags only if they still equal the adapter's most recently written values, and re-enables the controller if it is still disabled. It neither restores stale physical key states nor touches a replacement player agent. This public Boolean has no native ownership token, so cooperating mods must not simultaneously control the same agent.

The mission bridge owns enable/disable, bounded wall-clock leases, session IDs, sequence checks, and emergency takeover. It must enforce expiry/release from **application ticks as well as mission ticks**, since pause and menus can stop mission ticks. F10 should latch takeover until a fresh explicit enable, rather than let queued packets immediately reacquire. Native `Input.IsKeyPressed(InputKey.F10)` and `Input.IsKeyDownImmediate(InputKey.F10)` are public methods; `InputKey.F10` is a named enum member.

Weapon selection is deliberately omitted from the first command. Public `Agent.TryToWieldWeaponInSlot(EquipmentIndex, Agent.WeaponWieldActionType, bool)` and one-frame `Wield0..Wield3` event flags exist, but their inventory/animation preconditions have not been validated here.

## In-game checks still required

Use a disposable custom battle with an on-foot player. Confirm neutral enable, movement each axis, normalized diagonals, world yaw/pitch, held directional attack/block, one jump per accepted packet, timeout neutralization, F10 takeover, menu/pause/focus-loss release, death and agent replacement, and mission exit. The adapter controls the agent's look/aim; the native mission camera separately maintains its own bearing/elevation, so camera synchronization remains an in-game validation item. Confirm repeated acquire/release does not leave native controls disabled. Attack acknowledgment means input was accepted, not that an enemy was hit.
