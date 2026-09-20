# World / Diplomacy Specialist Handoff

Target: **Mount & Blade II: Bannerlord v1.4.8.119303**

Source of truth: `Grosspa1/Bannerlord-AI-Control`, branch `main`.

## Scope

World / Diplomacy owns settlement entry/exit/waiting, armies, mercenary/vassal
membership, kingdom departure, kingdom war/peace/policy proposals, governors,
and siege lifecycle. Real-time combat stays out of scope: siege assaults must
use Bannerlord's native **Send Troops / battle simulation** path.

Party/troop transfers, recruitment, companions, and garrison roster mutation
belong to the Party / Troops specialist.

## Current bridge architecture

`SubModule.cs` is a reflection-based campaign bridge. It polls
`C:\Users\Public\BannerlordBridge\command.txt` about every 350 ms on the
Bannerlord main thread, deduplicates by command id, writes `response.json`,
and continuously refreshes `state.json`.

The compiler response file currently compiles only `SubModule.cs`; adding a
new partial C# source requires updating `compile_mono20.rsp`.

Current telemetry already exposes hero, party, troops, prisoners, inventory,
clan, kingdom, army, settlement, quests, encounters, and nearby map objects.## Version verification

API signatures below were checked against the decompiled TaleWorlds archive at
commit `3b60fca5c4980a1c4e71957ffad2af05f6739d0d`. Its `BuildInfo.cs` reports
`BuildVersion = "119303"` and `GameVersion = "v1.4.8.119303"`.

## Settlement lifecycle

Canonical actions:

- `EnterSettlementAction.ApplyForParty(MobileParty, Settlement)`
- `LeaveSettlementAction.ApplyForParty(MobileParty)`

For an active player encounter, prefer `PlayerEncounter.Current.EnterSettlement()`
and `PlayerEncounter.Current.LeaveSettlement()` so encounter/menu state is kept.

The existing bridge already contains `DoEnterSettlement()` and
`DoLeaveSettlement()`; this specialist patch only wires them into
`ProcessCommand()` as `enter_settlement` and `leave_settlement`.

Settlement waiting is a game-menu state, not just a time-speed change.
Native waiting uses `town_wait_menus` / `village_wait_menus`,
`PlayerEncounter.Current.IsPlayerWaiting = true`, and
`MobileParty.MainParty.SetMoveModeHold()`. Check
`SettlementAccessModel.CanMainHeroDoSettlementAction(...WaitInSettlement...)`
before allowing it.

## Armies

Create: `Kingdom.CreateArmy(Hero, Settlement, Army.ArmyTypes, parties)`.
Gate it with `ArmyManagementCalculationModel.CanPlayerCreateArmy(...)`.### Critical army influence rule

Player-led army creation must reproduce invitation influence costs explicitly.
For each invited party use
`ArmyManagementCalculationModel.CalculatePartyInfluenceCost(MainParty, party)`,
verify sufficient influence, and deduct through `ChangeClanInfluenceAction`.

Do not rely on `Army.OnAddPartyInternal` for this when the main party is the
army leader; otherwise the bridge can create free summons.

Join an army with the canonical property/event path:

`MobileParty.MainParty.Army = targetArmy;`
`targetArmy.AddPartyToMergedParties(MobileParty.MainParty);`

Leave with `MobileParty.MainParty.Army = null`, plus native siege cleanup if
currently attached to another leader's siege.

For voluntary player-led disband, use the path that maps to
`ArmyDispersionReason.DismissalRequestedWithInfluence` so Bannerlord applies
the normal influence and relation penalties. Do not use
`ApplyByObjectiveFinished()` for a player-requested dismissal.

## Mercenary / vassal

Mercenary eligibility:
`FactionHelper.CanPlayerOfferMercenaryService(...)`.

Join with `ChangeKingdomAction.ApplyByJoinFactionAsMercenary(...)`, using the
native `MinorFactionsModel` award factor and normal 5-influence joining award.

Native player mercenary exit first removes current clan influence, then calls
`ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(Clan.PlayerClan)`.Vassal eligibility:
`FactionHelper.CanPlayerOfferVassalage(...)`.

Core join transition:
`ChangeKingdomAction.ApplyByJoinToKingdom(Clan.PlayerClan, targetKingdom)`.
If converting from mercenary service in the same kingdom, preserve the native
`EndMercenaryServiceAction.EndByBecomingVassal(...)` flow. Do not implement
final vassal join until native reward handling is also reproduced.

Kingdom exit should use the canonical `ChangeKingdomAction` methods. Keep
ordinary leave and rebellion as distinct irreversible commands.

## Kingdom decisions

Do not call war/peace actions directly. Preserve council mechanics by creating
`DeclareWarDecision`, `MakePeaceKingdomDecision`, or
`KingdomPolicyDecision`, call `IsAllowed()`, then
`Kingdom.AddDecision(decision)`.

Never pass `ignoreInfluenceCost = true` for a bridge-initiated player
proposal.

## Governors

Use `ChangeGovernorAction.Apply(Town, Hero)` and
`RemoveGovernorOfIfExists(Town)`. Require player-clan ownership and
`Campaign.Current.Models.ClanPoliticsModel.CanHeroBeGovernor(hero)`.

## Siege lifecycle

Start through `Campaign.Current.SiegeEventManager.StartSiegeEvent(...)`,
then `PlayerSiege.StartPlayerSiege(BattleSideEnum.Attacker)` and
`PlayerSiege.StartSiegePreparation()` when following the native player flow.Siege waiting should remain in `menu_siege_strategies`; do not mutate
construction progress directly.

For siege engines, use the `SiegeEventModel` availability methods, construct
or reuse `SiegeEngineConstructionProgress`, switch to custom strategy when
the player commands the siege, and deploy through
`siegeEventSide.SiegeEngines.DeploySiegeEngineAtIndex(...)`.

Siege assault must be **Send Troops only**. Validate commander status,
preparation, healthy troops, and morale, then establish the native
`assault_town_order_attack` encounter/menu. Runtime owns the existing
`send_troops` simulation lifecycle. Never call `StartSiegeMission()`.

Break siege through the native `BesiegerCamp = null` / encounter cleanup
path, not direct settlement or siege-event backing-field mutation.

## Recommended integration order

1. Wire existing `enter_settlement` / `leave_settlement`.
2. Add World telemetry through the State specialist.
3. Settlement waiting.
4. Join/leave armies.
5. Player-led army creation with exact influence parity.
6. Governors.
7. Siege start/wait/break and engine construction.
8. Siege assault -> Runtime Send Troops.
9. Kingdom proposals.
10. Mercenary service.
11. Vassal join/kingdom exit after full native semantics are reproduced.

Coordinator owns compilation, deployment, and live-game testing. Use
read-only checks or disposable saves for army, faction, and siege tests; never
use irreversible kingdom/war/exit actions merely as API probes.
