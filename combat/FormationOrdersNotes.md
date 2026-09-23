# Formation order adapter

This adapter is for single-player deployed field battles on Bannerlord v1.4.8.119303. It uses the player's native order controller. It does not claim formations, change their owner, use the AI order controller, or change enemy or allied NPC teams.

## Interface

- `UnavailableReason(Mission)` returns null for an eligible context, otherwise a reason. An eligible context does not imply that any formation is commandable. Mounted commanders are allowed.
- `Snapshot(Mission)` returns a JSON object containing `available`, `reason`, and `formations`. Each formation includes its native zero-based `index`, class, unit count, current x/y, delegated AI status, and current movement/arrangement/firing order.
- `Execute(Mission, argument)` accepts `formationIndex|order`, or `formationIndex|move|x|y`. It returns an issued result and observed native order. Invalid requests throw a prefixed exception before issuance. Native exceptions or failed postconditions after issuance produce `FORMATION_RESULT_UNCERTAIN`; inspect state instead of automatically retrying.
- `Parse(...)` tests the argument contract without invoking the game engine.

Supported orders:

| Bridge order | Native order |
| --- | --- |
| `move` | `OrderType.Move` with a validated world position |
| `charge` | `OrderType.Charge` |
| `stop` | `OrderType.StandYourGround` |
| `hold_fire` | `OrderType.HoldFire` |
| `fire_at_will` | `OrderType.FireAtWill` |
| `line` | `OrderType.ArrangementLine` |
| `shield_wall` | `OrderType.ArrangementCloseOrder` |

Coordinates are absolute scene x/y, not screen pixels, relative movement, or campaign-map coordinates. Use the current formation snapshot as a reference. A valid destination does not prove a complete path exists or guarantee that soldiers have arrived there. A successful result means the native order is observable, not that its tactical goal was achieved.

## Authority and lifecycle

The mission must be the active, loaded, continuing, unpaused single-player field battle; deployment must be complete. Multiplayer, replay, naval battles, sieges, tournaments, inactive game states, focus loss, photo mode, menus and object interaction are rejected. The living human main agent must be player controlled on `Mission.PlayerTeam`.

Both `PlayerOrderController.Owner` and `Formation.PlayerOwner` must be exactly the current player agent; controller and formation teams must match `Mission.PlayerTeam`. `IsFormationSelectable` must also pass and the formation must contain units. Native `IsFormationSelectable` alone is insufficient: its private implementation allows an owner-less controller to select other formations.

The adapter saves the original selected formations, selects only the requested formation, issues the native order, checks its observed order category, and restores the original still-selectable formations in a `finally` block. The response reports whether the full selection was restored. It does not manufacture substitute selections if a formation stops being selectable.

Ordinary orders to player-owned formations previously delegated to AI end that delegation through vanilla `OrderController.BeforeSetOrder`. This is the same behavior as a manual player order. The adapter never invokes `SetControlledByAI` directly. Troop orders persist after releasing the bridge, just as vanilla orders do.

## Installed API evidence

Inspected managed IL with the game's bundled Mono.Cecil on 2026-09-23. No game commands or native engine methods were executed during this inspection.

- `TaleWorlds.MountAndBlade.dll` SHA256: `19387F31557FF840D14F378F6BBDF1D58FCFF406AB9FF2294DFBB3E49A50B87E`.
- `TaleWorlds.Engine.dll` SHA256: `BFD5DB82D92F75EF15A5EB9A9F0C9391412566933193122DB1684019ED704D68`.
- Public controller APIs: `SelectFormation`, `ClearSelectedFormations`, `SetOrder`, `SetOrderWithPosition`, `IsFormationSelectable`, and static `GetActiveMovementOrderOf`/`GetActiveArrangementOrderOf`/`GetActiveFiringOrderOf`.
- `SetOrder` preserves vanilla before/after hooks, gestures, agent updates and `OnOrderIssued` dispatch. `StandYourGround` maps to `MovementOrderStop`; `ArrangementCloseOrder` maps to `ArrangementOrderShieldWall`.
- `Mission.IsOrderPositionAvailable(in WorldPosition, Team)` verifies position validity, nonzero navmesh, the mission's team-specific availability predicate and scene boundaries. The old compiler consumes the readonly-ref parameter with a `ref` call. Additional hard-boundary and navigation-blocker checks precede it.
- The world position is created from the current scene with x/y and the player's height as an initial navigation query height. This is restricted to field battles because arbitrary x/y cannot reliably choose a siege floor or ship deck.

Standalone adapter compilation passed with Bannerlord's bundled Mono 4.7.1 API references and .NET Standard 2.0 facade. Twenty-eight pure argument tests passed, including exact order names, token counts, integer overflow, NaN/infinity/float overflow and decimal formatting under French culture.

## Required live checks

Compilation and argument validation do not verify native battle execution. In a disposable custom battle, verify:

1. Snapshot formation indexes against the native selection UI; NPC-owned and enemy formations are absent.
2. Order one formation while multiple others are selected; only the target changes and the original selection returns.
3. Exercise every order and compare the observed order plus visible soldier behavior. Check mounted commander orders separately.
4. Reject an out-of-bounds or blocked move before any selection/order changes; verify valid navigation and actual arrival independently.
5. Reject commands during deployment, pause, focus loss, menus, mission end, death, or loss of command authority.
6. Test a player who is only a sergeant; commands must remain limited to that player's formations.
7. Test a player-owned formation delegated to AI; normal order should take it back through the native path, while an NPC-owned formation remains unavailable.

No live combat test was performed by this adapter task.
