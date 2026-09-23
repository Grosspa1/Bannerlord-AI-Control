using System;
using System.Globalization;
using System.Threading;
using BannerlordStrategicBridge;

internal static class PartyEconomyRulesTests
{
    private static int passed;

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        passed++;
    }

    private static void Reject(Action action, string expected, string name)
    {
        try { action(); }
        catch (Exception ex)
        {
            Check(expected == null ? ex is ArgumentException : ex.Message == expected, name);
            return;
        }
        throw new Exception("FAIL: accepted " + name);
    }

    private static void Context(int blocked, string state)
    {
        PartyEconomyRules.ValidateContext(blocked != 0, blocked != 1, blocked == 2,
            blocked != 3, blocked == 4, blocked == 5, blocked == 6, state,
            blocked == 7, blocked == 8);
    }

    public static int Main()
    {
        try
        {
            RecruitOneRequest request = RecruitOneRequest.Parse("notable_1|0|troop_1|50|100|2");
            Check(request.NotableId == "notable_1" && request.Slot == 0 && request.TroopId == "troop_1", "exact selection");
            request.Validate("troop_1", true, 50, 150, 17, 20);
            passed++;
            Reject(delegate { request.Validate("troop_2", true, 50, 150, 17, 20); }, "VOLUNTEER_CHANGED", "stale slot");
            Reject(delegate { request.Validate("TROOP_1", true, 50, 150, 17, 20); }, "VOLUNTEER_CHANGED", "case-sensitive ID");
            Reject(delegate { request.Validate("troop_1", false, 50, 150, 17, 20); }, "VOLUNTEER_LOCKED", "relation-locked slot");
            Reject(delegate { request.Validate("troop_1", true, 51, 151, 17, 20); }, "RECRUIT_PRICE_LIMIT", "price increased");
            Reject(delegate { request.Validate("troop_1", true, -1, 150, 17, 20); }, "RECRUIT_PRICE_LIMIT", "invalid negative cost");
            Reject(delegate { request.Validate("troop_1", true, 50, 149, 17, 20); }, "RECRUIT_GOLD_RESERVE", "gold reserve");
            Reject(delegate { request.Validate("troop_1", true, 50, 150, 18, 20); }, "RECRUIT_PARTY_RESERVE", "party reserve");
            Reject(delegate { request.Validate("troop_1", true, 50, 150, -1, 20); }, "RECRUIT_PARTY_RESERVE", "invalid member count");
            RecruitOneRequest basic = RecruitOneRequest.Parse("n|1|t|0");
            Check(basic.GoldReserve == 0 && basic.PartyReserve == 0, "default reserves");
            basic.Validate("t", true, 0, 0, 9, 10);
            passed++;
            Reject(delegate { basic.Validate("t", true, 0, 0, 10, 10); }, "RECRUIT_PARTY_RESERVE", "full party");
            Reject(delegate { basic.Validate("t", true, 0, 0, 11, 10); }, "RECRUIT_PARTY_RESERVE", "overfull party");
            RecruitOneRequest large = RecruitOneRequest.Parse("n|0|t|2147483647|2147483647|2147483647");
            Reject(delegate { large.Validate("t", true, int.MaxValue, int.MinValue, 0, int.MaxValue); }, "RECRUIT_GOLD_RESERVE", "gold underflow");
            Reject(delegate { large.Validate("t", true, 0, int.MaxValue, 1, int.MaxValue); }, "RECRUIT_PARTY_RESERVE", "capacity overflow");
            string[] malformed = { null, "", "n|0|t", "n|0|t|1|0|0|extra", "|0|t|1", "n|0||1",
                "n|-1|t|1", "n|x|t|1", "n|0|t|-1", "n|0|t|2147483648", "n|0|t|1|", "n|0|t|1|-1", "n|0|t|1|0|-1", "n|0|t|1.5" };
            foreach (string value in malformed)
                Reject(delegate { RecruitOneRequest.Parse(value); }, null, "malformed request " + value);
            Context(-1, "MapState");
            passed++;
            for (int i = 0; i <= 8; i++)
            {
                int blocked = i;
                string expected = i < 2 ? "NOT_IN_CAMPAIGN" : i < 4 ? "PARTY_UNAVAILABLE" :
                    i == 7 ? "SAVE_IN_PROGRESS" : "PARTY_ECONOMY_CONTEXT_BUSY";
                Reject(delegate { Context(blocked, "MapState"); }, expected, "context guard " + i);
            }
            foreach (string state in new string[] { "", "InventoryState", "PartyState", "MissionState", "GameMenuState" })
                Reject(delegate { Context(-1, state); }, "PARTY_ECONOMY_CONTEXT_BUSY", "active UI " + state);
            PartyEconomyRules.ValidateSettlement(true, true, false, false, false, true);
            passed++;
            Reject(delegate { PartyEconomyRules.ValidateSettlement(false, false, false, false, false, true); }, "TOWN_OR_VILLAGE_REQUIRED", "outside settlement");
            Reject(delegate { PartyEconomyRules.ValidateSettlement(true, false, false, false, false, true); }, "TOWN_OR_VILLAGE_REQUIRED", "castle market");
            Reject(delegate { PartyEconomyRules.ValidateSettlement(true, true, true, false, false, true); }, "SETTLEMENT_ACCESS_UNAVAILABLE", "siege");
            Reject(delegate { PartyEconomyRules.ValidateSettlement(true, true, false, true, false, true); }, "SETTLEMENT_ACCESS_UNAVAILABLE", "raid");
            Reject(delegate { PartyEconomyRules.ValidateSettlement(true, true, false, false, true, true); }, "SETTLEMENT_ACCESS_UNAVAILABLE", "hostile settlement");
            Reject(delegate { PartyEconomyRules.ValidateSettlement(true, true, false, false, false, false); }, "SETTLEMENT_ACCESS_UNAVAILABLE", "looted village");
            foreach (string verb in new string[] { "troops", "recruits", "inspect_recruits", "market", "inventory", "economy_status" })
                Check(PartyEconomyRules.Handles(verb) && PartyEconomyRules.IsReadOnly(verb), "read routing " + verb);
            Check(PartyEconomyRules.Handles("recruit_one") && !PartyEconomyRules.IsReadOnly("recruit_one"), "mutation classification");
            foreach (string verb in new string[] { "buy", "sell", "sell_loot", "recruit_all", "upgrade_all", "ransom_all", "attack", "status", "" })
                Check(!PartyEconomyRules.Handles(verb), "unexposed command " + verb);
            Check(JsonText.Quote(null) == "\"\"", "null JSON string");
            Check(JsonText.Quote("a\"b\\c") == "\"a\\u0022b\\\\c\"", "quotes and backslash");
            for (int c = 0; c < 32; c++)
                Check(JsonText.Quote(((char)c).ToString()) == "\"\\u" + c.ToString("x4") + "\"", "JSON control " + c);
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                Check(RecruitOneRequest.Parse("n|0|t|1500").MaxCost == 1500, "invariant parsing");
                Reject(delegate { RecruitOneRequest.Parse("n|0|t|1,5"); }, null, "localized decimal rejected");
            }
            finally { Thread.CurrentThread.CurrentCulture = before; }
            Console.WriteLine("PASS: " + passed + " offline regression checks (no game or bridge state touched).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
