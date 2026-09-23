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

The prototype builds against the game's bundled Mono/.NET Standard 2.0 runtime.
It exposes leased on-foot inputs, target-facing, native formation orders and
weapon/action telemetry. `combat/combatpilot.py` now adds a bounded local melee
loop: a connected assistant chooses a target/duration while the local process
approaches, faces, winds up/releases attacks and reacts to native incoming-attack
cues. It has no pathfinder, line-of-sight check, weapon switching, or mounted/ranged
combat. [Pilot instructions](../combat/PILOT.md) and
[protocol, limitations and acceptance status](REALTIME_COMBAT.md) are authoritative.

`CombatTelemetry` reads current weapon usage and native action channel 1. Attack
capability uses native strike-type lookup in the current stance/offhand context.
Blocking is conservatively advertised only with an intact wielded shield.
The enemy sample is now the nearest 32 hostile humans within 100 scene units;
it does not imply visibility or a clear route. Native weapon length is not a
guaranteed hit distance. `block_direction` already mirrors opposing left/right.

Live acceptance is **partial**. In disposable Custom Battles on v1.4.8.119303,
the combat module loaded alongside Strategic Bridge and correctly read Elthild
on foot. A 0.7-second left strafe moved 1.913 metres horizontally. Shield wall
and move orders had native readback and visible troop response; hold fire had
readback. An unrenewed 200 ms input expired with control/input released. F10
latched off and rejected input; F9 cleared the latch without enabling control.
Attack telemetry showed `ReadyMelee` / `AttackReady` followed by `ReleaseMelee` /
`AttackRelease`; an upward block showed `DefendShield` / `Defend`. These are
animation observations, not verified hits or intercepted enemy attacks.

Player knockout yielded no main agent; the menu was ineligible and a pilot
invocation sent zero inputs. A later sustained pilot run against Arentor wielding
throwing axes accepted 56 inputs in 6.172 seconds and moved Elthild 8.478 metres
horizontally; the gap fell from 17.395 to 5.228 metres. Health samples were 100,
66, 30, then no main agent. The first sample after the final live-player sample
(99 ms later) showed a changed token, disabled/unowned input and zero leases.
The report stopped with `MISSION_MISMATCH`, `release: context_changed`, and zero
attack cycles. That run never reached melee range. The pilot does not react to
ranged attacks.

A later one-on-one melee test used Arentor with a two-handed axe against Elthild
with axe and shield. The pilot accepted 96 inputs and planned 3 attack cycles in
10.547 seconds, with no manual attack input during the goal. The game displayed
102 cut damage to the shoulder, Arentor defeating Elthild in the kill feed, then
battle victory. Native telemetry included melee ready/release actions and player
health 100, 73, then 55. The pilot stopped because its requested target was no
longer suitable and acknowledged release. The victory snapshot showed Arentor
alive at 55 health, control disabled, input unowned and both leases zero.
**Live approach, knockout release and one melee duel victory are tested; general
combat reliability and the full acceptance matrix remain incomplete.** This win
does not establish a success rate, other weapon support or effective shield
interception; the two-handed pilot backed away from recognized melee threats.

Windows command replacement contention (`WinError 5`) and dropped native replies
are repaired. The client retries only provably unpublished identical packets
within the original TTL and a bounded deadline. The bridge retains and republishes
a failed acknowledgment from application ticks, at most every 20 ms, including
after mission end, without replaying the command or resetting its sequence.
The completed offline checkpoint is 382 C# argument/protocol checks, native
lifecycle/runtime contract checks, 69 Python checks (42 pilot, 27 client), and
six scratch checks against real Windows locked reply files in isolated paths.

The local evidence/backups directory is
`combat/integration-runs/20260923-125544/` (ignored by Git). All 25 backed-up
`Game Saves` files, including cloud metadata, matched their hashes in the final
verification (`save-verification-final.json`). The game and launcher are fully
closed. Its `run.json` identifies the initial deployment; later telemetry/transport
tests must not be attributed to that initial DLL hash. The final deployed test
DLL SHA-256 is `B18DF67DA7C913DA7DCC600FC8DD48D9C7C5A9CACE5877FC9A37468C36A2CBBA`.
The ranged-opponent run is recorded in `pilot-arentor-throwing-report.json` and
`pilot-arentor-throwing-trace.json`; the melee duel is in `pilot-live-report.json`,
`pilot-live-trace.json` and `melee-victory-state.json` within that evidence
directory. The coordinator owns further deployment and live testing. Finish the
outstanding acceptance matrix before claiming general autonomous combat or
promoting the module into main.

## Safety

Do not use surrender, war declarations, kingdom exits, destructive inventory operations, or other irreversible actions merely as API tests. Use read-only tests or disposable saves first.
