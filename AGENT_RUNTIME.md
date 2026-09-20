# Runtime / Reliability Specialist Handoff

Target: **Mount & Blade II: Bannerlord v1.4.8.119303**

Branch: `agent/runtime-reliability`

This work modifies the local development repository only. It does **not** deploy or overwrite the live Bannerlord module.

## Architecture findings

The bridge runs on Bannerlord's main thread at roughly 350 ms intervals and uses:
- `command.txt` for controller-to-game commands.
- `response.json` for the latest command response.
- `state.json` for campaign/runtime telemetry.
- Reflection for CampaignSystem/SandBox APIs so the build can keep a small reference surface.

The existing Python client already atomically publishes `command.txt`. The baseline C# bridge did not atomically publish state/responses and only kept its duplicate-command guard in memory.

## Send Troops lifecycle

Exact v1.4.8 path:
1. `Helpers.MenuHelper.EncounterOrderAttackConsequence(MenuCallbackArgs)`
2. `PlayerEncounter.InitSimulation(...)`
3. `MapState.StartBattleSimulation()`
4. `BattleSimulation.Skip()` requests skip-mode simulation; it does **not** commit results.
5. Future `MapState` ticks call `BattleSimulation.Tick()`.
6. Simulation rounds use `MapEvent.SimulatePlayerEncounterBattle()` until the map event has a winner.
7. A later tick sets `BattleSimulation.IsSimulationFinished`.
8. Vanilla scoreboard Done releases simulation sources with `MapState.EndBattleSimulation()` then calls `BattleSimulation.OnFinished()`.
9. `OnFinished()` activates the encounter menu.
10. Encounter initialization calls `PlayerEncounter.Update()`.
11. `DoApplyMapEventResults()` calls `MapEvent.CalculateAndCommitMapEventResults()`.

That last commit path applies battle results including XP, renown, influence, morale, gold, loot/prisoners and captured members. Therefore `Skip()` alone is not a completed autoresolve.

The bridge patch tracks:
`simulation_starting -> simulation_running -> results_pending -> post_battle / completed`

After simulation finishes it calls the same release sequence used by the vanilla scoreboard. It only drives `PlayerEncounter.Update()` while the encounter is in Wait/PrepareResults/ApplyResults. It does not force later conversation/loot states to finish.

## Encounter semantics

Exact encounter states are:
`Begin, Wait, PrepareResults, ApplyResults, PlayerVictory, PlayerTotalDefeat, CaptureHeroes, FreeHeroes, LootParty, LootInventory, LootShips, End`.

A new post-battle guard rejects mutating bridge commands once the encounter has moved past Begin/Wait. Read-only `status`, `list_saves` and `inspect` remain available.

`surrender` now uses the vanilla semantic path:
`PlayerEncounter.PlayerSurrender = true; PlayerEncounter.Update();`
rather than reflecting the private `PlayerSurrenderInternal`.
`retreat` now reads the static `PlayerEncounter.CurrentBattleSimulation`. During simulation it releases `MapState`'s simulation source before calling `BattleSimulation.OnPlayerRetreat()`; otherwise it calls static `PlayerEncounter.LeaveBattle()`.

Real-time `attack` remains intentionally unsupported and returns `REALTIME_COMBAT_DISABLED`.

## Save/load/session changes

Added commands:
- `save` / `quicksave`: queue `Campaign.Current.SaveHandler.QuickSaveCurrentGame()`.
- `save_as <name>`: queue `SaveHandler.SaveAs(name)`.
- `continue_latest`: pick the newest entry returned by `MBSaveLoad.GetSaveFiles()`.
- `exit` / `quit`: call `Module.CurrentModule.ShutDownWithDelay(...)`.

Save commands remain pending while `SaveHandler.IsSaving` is true and complete only after the save handler returns idle. `save_as` additionally verifies `MBSaveLoad.ActiveSaveSlotName`.

`load_save` requires the main menu and invokes private `SandBox.View.SandBoxViewSubModule.ContinueCampaign(string)`, using its exact static `_instance` when available. The command is complete only when Campaign exists, the active save slot matches, and active game state is `MapState`.

`list_saves` now reports structured JSON metadata including save name, corruption flag, creation time, character name, game version and unique game id.

Each module load creates a new `session_id`, published in state and responses.
## Transport/idempotency changes

Server-side state, responses, journals and type inspection output use temp-file + flush + same-volume atomic replace/move.

Command reads use `FileShare.ReadWrite | FileShare.Delete` so a controller atomic replacement cannot create a partial-read failure.

Each command gets:
- `journal/<command_id>.txt` with payload fingerprint and status.
- `responses/<command_id>.json` final/working response archive.

If Bannerlord restarts with a command journaled as `started` but not finalized, the bridge returns `RECOVERY_UNCERTAIN` instead of replaying the side effect.

Reusing a command ID with a different payload returns `COMMAND_ID_CONFLICT`.

`blctl.py` now waits through `running/accepted/waiting` responses for a final result and uses a 120-second final-response timeout.

## Validation completed

- Source compiles successfully against the installed v1.4.8.119303 Mono/.NET API and TaleWorlds assemblies.
- No live DLL was overwritten.
- No destructive in-game action was used as an API test.

## Live validation still required by coordinator

Use a disposable save only:
1. Verify `list_saves` metadata and `continue_latest`.
2. `save_as runtime-test`, wait for final response, exit to menu, then `load_save runtime-test`.
3. Fight weak looters with `send_troops`; compare pre/post casualties, XP, gold, renown/influence/morale, inventory and prisoners.
4. Confirm post-battle states block unrelated mutating commands until vanilla loot/prisoner/conversation UI is resolved.
5. Test simulated retreat separately.
6. Test surrender only on a disposable save.
7. Verify graceful `exit` preserves Bannerlord's normal safe-shutdown behavior.
