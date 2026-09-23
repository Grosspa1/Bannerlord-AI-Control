# Agent Handoff

Target: **Mount & Blade II: Bannerlord v1.4.8.119303**

## Contribution rules

1. Treat GitHub as the shared source of truth.
2. Read `SubModule.cs` before proposing changes.
3. Prefer Bannerlord managed campaign APIs, action classes, behaviors, and menu consequences over UI automation.
4. Avoid direct state mutation when a canonical Bannerlord action exists.
5. Keep changes focused on the assigned domain.
6. Do not modify the live Bannerlord installation.
7. Return a focused patch/branch/PR plus preconditions and test cases.
8. The coordinator owns compilation, deployment, and live integration testing.

## Specialist domains

- **State / Telemetry** — troop roster, inventory, clan/kingdom, quests, settlements, nearby threats/opportunities.
- **Party / Troops** — recruitment, upgrades, prisoners, companions, transfers, party management.
- **Economy / Inventory** — markets, food, horses, buying/selling, workshops, caravans.
- **World / Diplomacy** — settlements, armies, sieges, mercenary/vassal/kingdom actions.
- **Runtime / Reliability** — save/load, command transport, encounter state machine, Send Troops lifecycle.

## Known API findings

- Encounter helper type: `Helpers.MenuHelper`
- Send Troops consequence: `Helpers.MenuHelper.EncounterOrderAttackConsequence(MenuCallbackArgs)`
- `PlayerEncounter.CurrentBattleSimulation` exposes `Play()`, `FastForward()`, `Skip()`, `Pause()`
- Save UI entry point: `SandBox.View.SandBoxViewSubModule.ContinueCampaign(string)`
- `list_saves` works
- `load_save` remains under investigation
- Current bridge polling is on Bannerlord's main thread at roughly 350 ms
- Runtime specialist findings and test plan: \`AGENT_RUNTIME.md\`

## Real-time combat continuation (2026-09-23)

The user explicitly requested real-time combat controls for both their character
and troops. This expands scope through an independent `BannerlordCombatBridge`
module under `combat/`, without changing the strategic bridge's Send Troops path.
Worktree: `C:\Users\Public\BannerlordControllerBuild-combat`, branch
`feature/realtime-combat-control`, based on main `48b0d1f`. Party/Economy work,
including its uncommitted live-test fixes, remains in its separate worktree.

The prototype builds and passes offline tests. It exposes leased on-foot inputs,
target-facing, native formation orders and telemetry; it does not provide an
autonomous fighting policy. [Setup, limitations and acceptance tests](REALTIME_COMBAT.md).
No combat DLL deployment or live battle commands have been performed. Complete
the disposable Custom Battle acceptance pass before describing combat as working
in-game or promoting this module into main.

## Safety

Do not use surrender, war declarations, kingdom exits, destructive inventory operations, or other irreversible actions merely as API tests. Use read-only tests or disposable saves first.
