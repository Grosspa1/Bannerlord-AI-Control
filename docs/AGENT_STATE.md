# AGENT_STATE — State / Telemetry specialist

Target: Mount & Blade II: Bannerlord v1.4.8.119303

Source of truth reviewed:
- GitHub repo: Grosspa1/Bannerlord-AI-Control
- Branch: main
- README.md
- docs/AGENT_HANDOFF.md
- SubModule.cs blob: 4fe21732f78deca4d83cad12481667a42400fe5d

## Architecture summary

The bridge is a single reflection-heavy MBSubModuleBase.
OnApplicationTick polls at roughly 350 ms.
Each poll processes command.txt and writes state.json.
Responses are written to response.json and diagnostics to bridge.log.
Commands are semantic campaign actions; combat control is deliberately out of scope.
Battles use Bannerlord's native Send Troops / battle simulation path.
GitHub main is cleaner than the stale local source previously inspected:
the duplicate DoFollow / InvokeNoArg / settlement helper definitions are not present on main.

## STATE objective

Expose enough structured state for strategic play without screenshots:
troops, prisoners, inventory/logistics, hero progression, clan/kingdom,
quests, settlements, armies/sieges, nearby parties, menu/encounter state.
Prefer managed APIs and reflection. Missing optional members must degrade safely.
## Exact v1.4.8 API findings — party/logistics

TaleWorlds.CampaignSystem.Party.MobileParty:
- static MainParty, All
- Party
- Speed, SpeedExplained
- CurrentSettlement, HomeSettlement
- DefaultBehavior, TargetSettlement, TargetParty
- InventoryCapacity, InventoryCapacityExplainedNumber
- Morale
- Food, TotalFoodAtInventory
- FoodChange, BaseFoodChange, FoodChangeExplained
- TotalWage, TotalWageExplained
- MemberRoster, PrisonRoster, ItemRoster
- BesiegedSettlement
- PartySizeRatio
- GetNumDaysForFoodToLast()
- GetTotalLandStrengthWithFollowers(bool)
- GetHeroPartyRoles(Hero)

TaleWorlds.CampaignSystem.Party.PartyBase:
- MemberRoster, PrisonRoster, ItemRoster
- PartySizeLimit, PrisonerSizeLimit
- NumberOfHealthyMembers, NumberOfWoundedTotalMembers
- NumberOfAllMembers, NumberOfPrisoners
- NumberOfMounts, NumberOfPackAnimals
- NumberOfMenWithHorse, NumberOfMenWithoutHorse
- EstimatedStrength
- SiegeEvent, MapEvent, MapFaction
## Exact v1.4.8 API findings — troops/prisoners

TroopRoster runtime methods already used by main:
- Count
- TotalManCount, TotalHealthyCount, TotalWounded
- GetCharacterAtIndex(int)
- GetElementNumber(int)
- GetElementWoundedNumber(int)
- GetElementXp(int)

CharacterObject exact members:
- Level, Tier, TroopWage
- IsHero, IsMounted, IsRanged
- ConformityNeededToRecruitPrisoner
- UpgradeTargets
- GetUpgradeXpCost(PartyBase,int)
- GetUpgradeGoldCost(PartyBase,int)
- GetPower()
- GetBattlePower()

PrisonerRecruitmentCalculationModel exact methods:
- GetConformityChangePerHour(PartyBase, CharacterObject)
- GetPrisonerRecruitmentMoraleEffect(PartyBase, CharacterObject, int)
- IsPrisonerRecruitable(PartyBase, CharacterObject, out int)
- ShouldPartyRecruitPrisoners(PartyBase)
- CalculateRecruitableNumber(PartyBase, CharacterObject)

Treat TroopRoster.GetElementXp for prisoner stacks as conformity storage.
## Exact v1.4.8 API findings — hero/clan/kingdom/quests

Hero exact members:
- MainHero, OneToOneConversationHero
- Gold, HitPoints, MaxHitPoints, Age, IsWounded
- Clan, PartyBelongedTo, CurrentSettlement, StayingInSettlement
- OwnedWorkshops, OwnedCaravans
- HeroDeveloper
- GetSkillValue(SkillObject)
- GetAttributeValue(CharacterAttribute)
- GetPerkValue(PerkObject)

HeroDeveloper exact members:
- UnspentFocusPoints, UnspentAttributePoints, TotalXp
- GetSkillXp(SkillObject), GetSkillXpProgress(SkillObject)
- GetFocus(SkillObject)
- GetPerkValue(PerkObject)

Clan exact members:
- Kingdom, Fiefs, Villages, Settlements, Companions, WarPartyComponents
- Influence, CurrentTotalStrength, Gold, Renown, Tier
- RenownRequirementForNextTier, CompanionLimit, WarPartyLimit
- IsUnderMercenaryService, FactionsAtWarWith

Kingdom exact members include Leader, Clans, Armies, CurrentTotalStrength,
FactionsAtWarWith, AlliedKingdoms, Fiefs, Towns, Villages, Settlements.

QuestBase exact members:
- QuestDueTime, TaskList, JournalEntries, QuestGiver, Title
- IsTrackEnabled, IsOngoing, IsFinalized, IsRemainingTimeHidden
- RelationshipChangeWithQuestGiver, IsSpecialQuest, SpecialQuestType
- RewardGold backing member is exposed through reflection.
## Exact v1.4.8 API findings — finance

Campaign.Current.Models.ClanFinanceModel exposes:
- CalculateClanGoldChange(Clan,bool,bool,bool)
- CalculateClanIncome(Clan,bool,bool,bool)
- CalculateClanExpenses(Clan,bool,bool,bool)
- CalculateOwnerIncomeFromCaravan(MobileParty)
- CalculateOwnerIncomeFromWorkshop(Workshop)

Use apply-withdrawal/mutation flags as false for telemetry.
Return unavailable/null rather than zero if reflection fails.

## Recommended state schema

Root schema: bannerlord.strategic_state.v3

Top-level:
- updated_utc, campaign_loaded, sampling_ms
- session
- hero
- party
- clan
- kingdom
- quests
- army
- siege
- current_settlement
- nearby_parties
- nearby_settlements

party contains:
- members/healthy/wounded/prisoners
- party_size_limit/prisoner_size_limit
- speed/morale/food/food_change/food_days/daily_wage
- carried_weight/inventory_capacity when accessible
- current/target settlement and target party
- troops[]
- prisoner_roster[]
- inventory_summary
- inventory[]
Troop entry:
- id, name, count, healthy, wounded
- xp, tier, level
- wage_each, wage_total
- power_each, power_total, battle_power_each
- mounted, ranged, hero
- upgrades[] with target id/name/tier/xp_cost/gold_cost

Prisoner entry:
- id, name, count, wounded
- conformity
- conformity_needed
- recruitable_count
- conformity_per_hour
- tier, wage, power

Inventory entry:
- id, name, count
- ItemType and ItemCategory
- unit/base value
- weight each/total when accessible
- food/trade-good/animal/mount/pack-animal flags when accessible

Nearby party entry:
- id/name/leader/kind
- distance, members, healthy, wounded
- speed, EstimatedStrength
- faction
- hostile via faction war state / hero enemy relation
- relation to leader when available
- behavior, target party, target settlement, current settlement, army

Settlement detail:
- owner/owner clan
- militia, garrison count, defender strength
- prosperity, loyalty, security
- under_siege
- market summary; full market inventory only for current/explicit settlement
## Polling/caching recommendation

Keep the outer bridge tick at ~350 ms, but do NOT rebuild everything every tick.

Fast every 350 ms:
- campaign/time/menu/encounter flags
- main party speed/morale/food/wage
- current target/current settlement
- hero HP/gold

Nearby every ~1 s:
- nearest parties and settlements
- hostility, distance, strength, speed, destination

Rosters/inventory every ~1.5 s:
- exact troop/prisoner stacks
- upgrade paths/costs
- inventory stacks/logistics

Hero/clan/kingdom/quests every ~5 s:
- skills/focus/attributes/perks
- companion roles/assets/finance
- diplomacy lists
- quest timers/tasks

Market/current settlement detail every ~5 s.
Do not enumerate every world market or large explanation tree every 350 ms.
Cache expensive model calculations and global lists.

## Patch design

Keep main's existing BuildState() as rollback.
Add a partial source file SubModule.State.cs containing BuildStrategicStateV3()
and cached state helpers.
Change OnApplicationTick state write from BuildState() to BuildStrategicStateV3().
Add SubModule.State.cs to compile_mono20.rsp.

This isolates STATE changes and minimizes merge conflicts with PARTY/ECONOMY/WORLD/RUNTIME.
## Integration safety

No direct campaign mutation is required by this patch.
No OCR or screen coordinates are required.
All optional access is reflection based and should fail soft.
Do not represent absent members as factual zero when zero changes decisions.
Prefer JSON null or an explicit available=false marker.

## Preconditions / coordinator tests

1. Compile against Bannerlord bundled Mono 4.7.1 API/netstandard using compile_mono20.rsp.
2. Output only to BannerlordControllerBuild; do not overwrite installed DLL.
3. Parse state.json as JSON after campaign load.
4. Verify exact roster totals reconcile to party totals.
5. Verify prisoner conformity/recruitable counts against party screen.
6. Verify food days and wage against UI once.
7. Verify inventory capacity/weight against UI once.
8. Verify a companion's roles and hero skill/focus values.
9. Verify clan daily income/expense/net against finance screen.
10. Verify current wars/allies and nearby hostility.
11. Verify quest due time/reward/tasks for at least one active quest.
12. Benchmark state generation while traveling at fast-forward.

## GitHub status

GitHub main was successfully read and is the source used for this handoff.
The ChatGPT GitHub connector was read-only, so the STATE work is pushed through local Git via Remote Desktop Commander.
Branch: `agent/state-telemetry`.
No live Bannerlord installation files are modified by this branch.
