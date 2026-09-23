# Party and Economy controls

Target: Bannerlord **v1.4.8.119303**. Based on main `48b0d1f`.

## Commands

Command transport and journaling are unchanged: `id|verb|argument`. Structured
results remain JSON text inside the existing response `message` field; clients
must parse that field separately. Read commands accept no arguments.

| Command | Result / arguments |
| --- | --- |
| `troops` | Fresh member roster, including counts, wounded, XP and upgrade names. |
| `recruits` / `inspect_recruits` | Current settlement's volunteer slots, exact notable/troop IDs, model cost, relation unlock, gold and party capacity. An unlocked slot is not a claim of affordability. |
| `recruit_one` | `notable_id\|slot\|troop_id\|max_cost[\|gold_reserve\|party_reserve]`; recruits exactly one volunteer. Slot is zero-based; IDs are exact and case-sensitive. |
| `inventory` | Fresh player item stacks with item and modifier IDs, counts, modified names, food/quest flags and weight. |
| `market` | Current town/village stock and player inventory with native unit buy/sell quotes for each equipment element. Quotes are not bulk totals or guaranteed executable future prices. |
| `economy_status` | Gold, food, food change/day, estimated food days, carried weight, capacity and daily party wage. Food days is null when food is not being consumed; this is not a full clan-income forecast. |

All commands require an active, non-captive player party on the campaign map,
with no save, mission, battle simulation, map event or unrelated encounter in
progress. They reject inventory/party/conversation screens. A peaceful encounter
with the current settlement may pass the context guard; existing post-battle
protection still applies to mutations. Recruitment and market inspection also
require an entered friendly/neutral town or normal village without siege/raid.
Castles, looted villages, hostile settlements, and distant settlement names are
rejected.

Example workflow, with IDs and costs copied from the current `recruits` result:

```powershell
.\bl-dev.ps1 command recruits
.\bl-dev.ps1 command recruit_one 'EXACT_NOTABLE_ID|0|EXACT_TROOP_ID|50|100|2'
.\bl-dev.ps1 command troops
.\bl-dev.ps1 command economy_status
.\bl-dev.ps1 command market
```

`max_cost` limits the cost of that one recruit. `gold_reserve` is the minimum
gold remaining afterward. `party_reserve` is the number of party slots to leave
open. Reserves default to zero; empty fields, negatives, fractional values,
overflow and extra fields are rejected. A changed slot, a locked slot, increased
price, insufficient gold or insufficient capacity fails before the action.

Recruitment invokes the exact private
`RecruitmentCampaignBehavior.GetRecruitVolunteerFromIndividual(MobileParty,
CharacterObject, Hero, int)` method. It performs Bannerlord's own gold, volunteer,
roster and event changes. Success requires observed volunteer removal, one added
troop, one added party member, and the expected gold deduction. An exception or
unexpected result reports `RECRUIT_RESULT_UNCERTAIN` in the error message and
invalidates telemetry caches. Inspect the state before issuing a new command ID;
never automatically retry an uncertain action. Existing journal recovery and
duplicate-ID rules remain in effect. Other validation errors appear in the
message under the existing `COMMAND_FAILED` code (existing runtime codes are
preserved).

## Findings from candidate review

The ignored legacy Party/Economy candidates in the original build checkout are
reference material only. They were not copied wholesale into this branch.

- The game API applies a recruitment without checking all of the bridge's
  relation, capacity or reserve requirements. Validate first, then observe the
  result; don't assume a successful reflection call means the intended outcome.
- `InventoryLogic.CanPlayerCompleteTransaction()` does not generally guarantee
  player funds, player carrying capacity or merchant funds. The candidate's
  "funds/capacity" promise is therefore not sufficient for enabling trade writes.
- Inventory transfers operate on the provided rosters. Error handling after
  transfer and before/after `DoneLogic` needs separate treatment; swallowing
  reset errors cannot establish rollback. A commit can also dispatch events.
- `InventoryLogic.GetItemPrice(element, buying)` negates `buying` when passing
  `isSelling` to the native market data. This branch reads the actual town/village
  market data directly, includes modifiers, and never creates a trade session.
- The shared JSON encoder now escapes every U+0000 through U+001F character,
  including tabs in names, so the added structured inspection payloads remain
  valid JSON.

## Verification

Run from this worktree:

```powershell
.\bl-dev.ps1 build
.\tests\Test-PartyEconomy.ps1
```

The production DLL is compiled with `/nostdlib+`, Bannerlord's bundled Mono
4.7.1 API assemblies, the .NET Standard 2.0 facade, and installed TaleWorlds
assemblies. No modern .NET runtime is introduced. Offline policy/JSON regression
tests are compiled against the same bundled framework references and execute in
the installed Windows .NET Framework CLR, not inside Bannerlord's Mono host.
Metadata checks inspect the installed private recruitment signature, native
price-direction convention and built DLL's runtime references. These checks do
not prove campaign behavior, menu eligibility or save persistence.

As of 2026-09-23: production build passed; 104 offline regression checks passed;
API and assembly-reference checks passed. Bannerlord was closed. No live module
deployment or live command was performed. Local Version.xml reports v1.4.8;
the full build suffix 119303 remains the requested target, not independently
confirmed by that file.

## Disposable-save integration plan

1. Back up the installed bridge DLL and use the existing deployment helper only
   while Bannerlord is closed. Launch a disposable save and record its identity,
   gold, party counts, inventory, volunteer slots and live game version.
2. Run `troops`, `inventory` and `economy_status` on the campaign map. Enter a town
   and a village; run `recruits` and `market`. Compare quoted prices for an
   unmodified food and a modified equipment stack with vanilla trade UI. Verify
   that reads change no gold, stocks or troop counts.
3. Reject an incorrect troop ID, locked volunteer, too-low price cap, unaffordable
   reserve, full/reserved party and malformed argument. Verify zero changes for
   each rejection. Also reject during party/inventory screens and combat.
4. Recruit one available volunteer. Verify one exact slot cleared, one troop
   added, expected gold deducted, recruitment events, and fresh telemetry.
5. Replay the identical command ID/payload: verify archived response and no
   second recruit. Reuse that ID with a different payload: verify conflict.
6. Save and reload the disposable campaign; verify the changed roster and gold.
   Restore the previous bridge DLL afterward if validation fails.

## Next slices

Complete the disposable-save checks above before promotion. Then add market
transaction previews and explicit funds/capacity limits with measured rollback
and uncertain-commit handling, followed by a one-unit buy/sell round trip on a
disposable save. Review the existing upgrade/prisoner candidate separately:
item consumption and hero-prisoner handling need canonical lifecycle validation.
Bulk recruitment, troop upgrades, prisoner mutations, workshops, caravans and
real-time combat are not enabled by this change.
