using System;
using System.Globalization;

namespace BannerlordStrategicBridge
{
    // Pure validation shared by the game adapter and the offline regression suite.
    internal sealed class RecruitOneRequest
    {
        internal string NotableId;
        internal int Slot;
        internal string TroopId;
        internal int MaxCost;
        internal int GoldReserve;
        internal int PartyReserve;

        internal static RecruitOneRequest Parse(string argument)
        {
            string[] parts = (argument ?? "").Split('|');
            if (parts.Length < 4 || parts.Length > 6)
                throw new ArgumentException("Expected notable_id|slot|troop_id|max_cost[|gold_reserve|party_reserve].");
            RecruitOneRequest request = new RecruitOneRequest();
            request.NotableId = parts[0].Trim();
            request.Slot = NonNegative(parts[1]);
            request.TroopId = parts[2].Trim();
            request.MaxCost = NonNegative(parts[3]);
            request.GoldReserve = parts.Length > 4 ? NonNegative(parts[4]) : 0;
            request.PartyReserve = parts.Length > 5 ? NonNegative(parts[5]) : 0;
            if (request.NotableId.Length == 0 || request.TroopId.Length == 0)
                throw new ArgumentException("Exact notable and troop IDs are required.");
            return request;
        }

        private static int NonNegative(string text)
        {
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
                throw new ArgumentException("Slot, cost, and reserves must be non-negative integers.");
            return value;
        }

        internal void Validate(string actualTroopId, bool unlocked, int cost, int gold,
            int members, int limit)
        {
            if (!string.Equals(TroopId, actualTroopId, StringComparison.Ordinal))
                throw new InvalidOperationException("VOLUNTEER_CHANGED");
            if (!unlocked) throw new InvalidOperationException("VOLUNTEER_LOCKED");
            if (cost < 0 || cost > MaxCost) throw new InvalidOperationException("RECRUIT_PRICE_LIMIT");
            if ((long)gold - cost < GoldReserve) throw new InvalidOperationException("RECRUIT_GOLD_RESERVE");
            if (members < 0 || limit < 0 || (long)members + 1 + PartyReserve > limit)
                throw new InvalidOperationException("RECRUIT_PARTY_RESERVE");
        }
    }

    internal static class PartyEconomyRules
    {
        internal static bool IsReadOnly(string verb)
        {
            return verb == "troops" || verb == "recruits" || verb == "inspect_recruits" ||
                verb == "inventory" || verb == "market" || verb == "economy_status";
        }

        internal static bool Handles(string verb)
        {
            return IsReadOnly(verb) || verb == "recruit_one";
        }

        internal static void ValidateContext(bool campaign, bool mainParty, bool prisoner,
            bool activeParty, bool mission, bool mapEvent, bool simulation, string activeState,
            bool saveBusy, bool encounter)
        {
            if (!campaign || !mainParty) throw new InvalidOperationException("NOT_IN_CAMPAIGN");
            if (prisoner || !activeParty) throw new InvalidOperationException("PARTY_UNAVAILABLE");
            if (mission || mapEvent || simulation || encounter || activeState != "MapState")
                throw new InvalidOperationException("PARTY_ECONOMY_CONTEXT_BUSY");
            if (saveBusy) throw new InvalidOperationException("SAVE_IN_PROGRESS");
        }

        internal static void ValidateSettlement(bool present, bool townOrVillage, bool besieged,
            bool raided, bool atWar, bool normalVillage)
        {
            if (!present || !townOrVillage) throw new InvalidOperationException("TOWN_OR_VILLAGE_REQUIRED");
            if (besieged || raided || atWar || !normalVillage)
                throw new InvalidOperationException("SETTLEMENT_ACCESS_UNAVAILABLE");
        }
    }
}
