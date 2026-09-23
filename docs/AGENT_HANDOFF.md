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

## Party / Economy continuation (2026-09-23)

- Reviewed GitHub main at `48b0d1f` and the remote agent/runtime-reliability,
  agent/state-telemetry, agent/world-diplomacy, and integration/v0.3 branches.
  The specialist branches retain their original commit IDs; main includes their
  contributions under separate integrated commits. No open PRs existed at the
  start of this continuation.
- The original build checkout remains on agent/world-diplomacy with uncommitted
  SubModule.cs and compile_mono20.rsp edits plus ignored Party/Economy candidates.
  Those files were preserved; they are not the integrated source of truth.
- Work continues in the isolated `feature/party-economy-controls` branch and
  `C:\Users\Public\BannerlordControllerBuild-party-economy` worktree.
- The first slice adds recruitment inspection, guarded single recruitment,
  troop/inventory inspection, native market quotes, and an economy summary.
  [Command contract, verification, and remaining work](PARTY_ECONOMY.md).
- No live game DLL, bridge command file, or save was changed. Deployment and
  disposable-save validation remain pending; do not infer runtime success from
  compilation or the offline suite.

## Safety

Do not use surrender, war declarations, kingdom exits, destructive inventory operations, or other irreversible actions merely as API tests. Use read-only tests or disposable saves first.
