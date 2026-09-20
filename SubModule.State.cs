using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BannerlordStrategicBridge
{
    public partial class SubModule
    {
        private static DateTime _svRosterAt = DateTime.MinValue;
        private static DateTime _svNearbyAt = DateTime.MinValue;
        private static DateTime _svHeroAt = DateTime.MinValue;
        private static DateTime _svWorldAt = DateTime.MinValue;
        private static DateTime _svMarketAt = DateTime.MinValue;
        private static string _svTroops = "[]";
        private static string _svPrisoners = "[]";
        private static string _svInventory = "[]";
        private static string _svInventorySummary = "{}";
        private static string _svNearbyParties = "[]";
        private static string _svNearbySettlements = "[]";
        private static string _svHeroProgress = "{}";
        private static string _svClan = "{}";
        private static string _svKingdom = "{}";
        private static string _svQuests = "[]";
        private static string _svCurrentSettlement = "{}";

        private static bool SV_Due(ref DateTime stamp, int ms)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - stamp).TotalMilliseconds < ms) return false;
            stamp = now;
            return true;
        }

        private static object SV_GetAny(object obj, params string[] names)
        {
            if (obj == null || names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                object v = GetProp(obj, names[i]);
                if (v != null) return v;
            }
            return null;
        }

        private static object SV_GetMember(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            object p = GetProp(obj, name);
            if (p != null) return p;
            try
            {
                FieldInfo f = obj.GetType().GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                return f == null ? null : f.GetValue(obj);
            }
            catch { return null; }
        }

        private static object SV_Invoke(object obj, string name, params object[] args)
        {
            if (obj == null) return null;
            if (args == null) args = new object[0];
            MethodInfo[] methods = obj.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                bool ok = true;
                for (int j = 0; j < ps.Length; j++)
                {
                    if (args[j] == null)
                    {
                        if (ps[j].ParameterType.IsValueType && Nullable.GetUnderlyingType(ps[j].ParameterType) == null)
                        { ok = false; break; }
                    }
                    else if (!ps[j].ParameterType.IsInstanceOfType(args[j]) &&
                             !(ps[j].ParameterType.IsValueType && args[j].GetType() == ps[j].ParameterType))
                    { ok = false; break; }
                }
                if (!ok) continue;
                try { return m.Invoke(obj, args); }
                catch { }
            }
            return null;
        }

        private static double SV_Result(object explained, double fallback)
        {
            if (explained == null) return fallback;
            object v = SV_GetAny(explained, "ResultNumber", "Result", "BaseNumber");
            return v == null ? fallback : DoubleValue(v, fallback);
        }

        private static object SV_PartyBase(object main)
        {
            return SV_GetAny(main, "Party", "PartyBase");
        }

        private static string SV_TroopRosterJson(object roster, object partyBase, bool prisoners)
        {
            StringBuilder b = new StringBuilder("[");
            if (roster == null) return "[]";
            Type rt = roster.GetType();
            MethodInfo getChar = rt.GetMethod("GetCharacterAtIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo getNum = rt.GetMethod("GetElementNumber", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(int) }, null);
            MethodInfo getWounded = rt.GetMethod("GetElementWoundedNumber", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(int) }, null);
            MethodInfo getXp = rt.GetMethod("GetElementXp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(int) }, null);
            int count = IntValue(GetProp(roster, "Count"), 0);
            object campaign = GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current");
            object models = GetProp(campaign, "Models");
            object recruitModel = GetProp(models, "PrisonerRecruitmentCalculationModel");
            int written = 0;
            for (int i = 0; i < count; i++)
            {
                object ch = getChar == null ? null : getChar.Invoke(roster, new object[] { i });
                if (ch == null) continue;
                int num = getNum == null ? 0 : IntValue(getNum.Invoke(roster, new object[] { i }), 0);
                int wounded = getWounded == null ? 0 : IntValue(getWounded.Invoke(roster, new object[] { i }), 0);
                int xp = getXp == null ? 0 : IntValue(getXp.Invoke(roster, new object[] { i }), 0);
                if (written++ > 0) b.Append(',');
                double power = DoubleValue(SV_Invoke(ch, "GetPower"), 0);
                double battlePower = DoubleValue(SV_Invoke(ch, "GetBattlePower"), power);
                int wage = IntValue(GetProp(ch, "TroopWage"), 0);
                b.Append("{\"id\":").Append(J(StringId(ch)))
                 .Append(",\"name\":").Append(J(ReadName(ch)))
                 .Append(",\"count\":").Append(num)
                 .Append(",\"healthy\":").Append(Math.Max(0, num - wounded))
                 .Append(",\"wounded\":").Append(wounded)
                 .Append(",\"xp_or_conformity\":").Append(xp)
                 .Append(",\"tier\":").Append(IntValue(GetProp(ch, "Tier"), 0))
                 .Append(",\"level\":").Append(IntValue(GetProp(ch, "Level"), 0))
                 .Append(",\"wage_each\":").Append(wage)
                 .Append(",\"wage_total\":").Append(wage * num)
                 .Append(",\"power_each\":").Append(Num(power))
                 .Append(",\"power_total\":").Append(Num(power * Math.Max(0, num - wounded)))
                 .Append(",\"battle_power_each\":").Append(Num(battlePower))
                 .Append(",\"hero\":").Append(BoolValue(GetProp(ch, "IsHero")) ? "true" : "false")
                 .Append(",\"mounted\":").Append(BoolValue(GetProp(ch, "IsMounted")) ? "true" : "false")
                 .Append(",\"ranged\":").Append(BoolValue(GetProp(ch, "IsRanged")) ? "true" : "false");

                if (prisoners)
                {
                    int needed = IntValue(GetProp(ch, "ConformityNeededToRecruitPrisoner"), 0);
                    object calc = SV_Invoke(recruitModel, "CalculateRecruitableNumber", partyBase, ch);
                    object change = SV_Invoke(recruitModel, "GetConformityChangePerHour", partyBase, ch);
                    b.Append(",\"conformity\":").Append(xp)
                     .Append(",\"conformity_needed\":").Append(needed)
                     .Append(",\"recruitable_count\":").Append(IntValue(calc, 0))
                     .Append(",\"conformity_per_hour\":").Append(Num(SV_Result(change, 0)));
                }
                else
                {
                    object upgrades = GetProp(ch, "UpgradeTargets");
                    IEnumerable ue = AsEnumerable(upgrades);
                    b.Append(",\"upgrades\":[");
                    int u = 0;
                    if (ue != null)
                    {
                        foreach (object target in ue)
                        {
                            if (target == null) continue;
                            if (u > 0) b.Append(',');
                            object xpCost = SV_Invoke(ch, "GetUpgradeXpCost", partyBase, u);
                            object goldCost = SV_Invoke(ch, "GetUpgradeGoldCost", partyBase, u);
                            b.Append("{\"id\":").Append(J(StringId(target)))
                             .Append(",\"name\":").Append(J(ReadName(target)))
                             .Append(",\"tier\":").Append(IntValue(GetProp(target, "Tier"), 0))
                             .Append(",\"xp_cost\":").Append(IntValue(xpCost, 0))
                             .Append(",\"gold_cost\":").Append(IntValue(goldCost, 0))
                             .Append("}");
                            u++;
                        }
                    }
                    b.Append(']');
                }
                b.Append('}');
            }
            b.Append(']');
            return b.ToString();
        }

        private static string SV_InventoryJson(object roster)
        {
            StringBuilder b = new StringBuilder("[");
            if (roster == null) return "[]";
            Type rt = roster.GetType();
            MethodInfo getItem = rt.GetMethod("GetItemAtIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(int) }, null);
            MethodInfo getNum = rt.GetMethod("GetElementNumber", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(int) }, null);
            MethodInfo getCost = rt.GetMethod("GetElementUnitCost", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(int) }, null);
            int count = IntValue(GetProp(roster, "Count"), 0);
            int written = 0;
            for (int i = 0; i < count; i++)
            {
                object item = getItem == null ? null : getItem.Invoke(roster, new object[] { i });
                if (item == null) continue;
                int n = getNum == null ? 0 : IntValue(getNum.Invoke(roster, new object[] { i }), 0);
                if (n <= 0) continue;
                int cost = getCost == null ? 0 : IntValue(getCost.Invoke(roster, new object[] { i }), 0);
                double weight = DoubleValue(GetProp(item, "Weight"), 0);
                object horse = GetProp(item, "HorseComponent");
                if (written++ > 0) b.Append(',');
                b.Append("{\"id\":").Append(J(StringId(item)))
                 .Append(",\"name\":").Append(J(ReadName(item)))
                 .Append(",\"count\":").Append(n)
                 .Append(",\"type\":").Append(J(Convert.ToString(GetProp(item, "ItemType"), CultureInfo.InvariantCulture)))
                 .Append(",\"category\":").Append(J(ReadName(GetProp(item, "ItemCategory"))))
                 .Append(",\"unit_value\":").Append(IntValue(GetProp(item, "Value"), 0))
                 .Append(",\"unit_cost\":").Append(cost)
                 .Append(",\"weight_each\":").Append(Num(weight))
                 .Append(",\"weight_total\":").Append(Num(weight * n))
                 .Append(",\"food\":").Append(BoolValue(GetProp(item, "IsFood")) ? "true" : "false")
                 .Append(",\"trade_good\":").Append(BoolValue(GetProp(item, "IsTradeGood")) ? "true" : "false")
                 .Append(",\"animal\":").Append(BoolValue(GetProp(item, "IsAnimal")) ? "true" : "false")
                 .Append(",\"mount\":").Append(horse != null && BoolValue(GetProp(horse, "IsMount")) ? "true" : "false")
                 .Append(",\"pack_animal\":").Append(horse != null && BoolValue(GetProp(horse, "IsPackAnimal")) ? "true" : "false")
                 .Append(",\"livestock\":").Append(horse != null && BoolValue(GetProp(horse, "IsLiveStock")) ? "true" : "false")
                 .Append('}');
            }
            b.Append(']');
            return b.ToString();
        }

        private static string SV_InventorySummaryJson(object main, object roster)
        {
            double capacity = DoubleValue(GetProp(main, "InventoryCapacity"), 0);
            double carried = DoubleValue(GetProp(main, "TotalWeightCarried"), 0);
            double free = capacity - carried;
            return new StringBuilder("{")
                .Append("\"stacks\":").Append(IntValue(GetProp(roster, "Count"), 0)).Append(',')
                .Append("\"total_value\":").Append(IntValue(GetProp(roster, "TotalValue"), 0)).Append(',')
                .Append("\"food_units\":").Append(IntValue(GetProp(roster, "TotalFood"), 0)).Append(',')
                .Append("\"food_variety\":").Append(IntValue(GetProp(roster, "FoodVariety"), 0)).Append(',')
                .Append("\"mounts\":").Append(IntValue(GetProp(roster, "NumberOfMounts"), 0)).Append(',')
                .Append("\"pack_animals\":").Append(IntValue(GetProp(roster, "NumberOfPackAnimals"), 0)).Append(',')
                .Append("\"carried_weight\":").Append(Num(carried)).Append(',')
                .Append("\"capacity\":").Append(Num(capacity)).Append(',')
                .Append("\"free_capacity\":").Append(Num(free)).Append(',')
                .Append("\"over_capacity\":").Append(free < 0 ? "true" : "false")
                .Append('}').ToString();
        }

        private static List<object> SV_StaticObjects(string typeName)
        {
            List<object> values = new List<object>();
            Type t = FindType(typeName);
            if (t == null) return values;
            PropertyInfo[] ps = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < ps.Length; i++)
            {
                try { object v = ps[i].GetValue(null, null); if (v != null) values.Add(v); } catch { }
            }
            FieldInfo[] fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < fs.Length; i++)
            {
                try { object v = fs[i].GetValue(null); if (v != null && !values.Contains(v)) values.Add(v); } catch { }
            }
            return values;
        }

        private static string SV_HeroProgressJson(object hero)
        {
            if (hero == null) return "{}";
            object dev = GetProp(hero, "HeroDeveloper");
            StringBuilder b = new StringBuilder("{");
            b.Append("\"level\":").Append(IntValue(GetProp(hero, "Level"), 0)).Append(',')
             .Append("\"unspent_focus_points\":").Append(IntValue(GetProp(dev, "UnspentFocusPoints"), 0)).Append(',')
             .Append("\"unspent_attribute_points\":").Append(IntValue(GetProp(dev, "UnspentAttributePoints"), 0)).Append(',')
             .Append("\"total_xp\":").Append(Num(DoubleValue(GetProp(dev, "TotalXp"), 0))).Append(',');

            b.Append("\"attributes\":[");
            List<object> attrs = SV_StaticObjects("TaleWorlds.Core.DefaultCharacterAttributes");
            int aw = 0;
            for (int i = 0; i < attrs.Count; i++)
            {
                object a = attrs[i];
                if (a == null || a.GetType().Name.IndexOf("CharacterAttribute", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (aw++ > 0) b.Append(',');
                b.Append("{\"id\":").Append(J(StringId(a))).Append(",\"name\":").Append(J(ReadName(a)))
                 .Append(",\"value\":").Append(IntValue(SV_Invoke(hero, "GetAttributeValue", a), 0)).Append('}');
            }
            b.Append("],\"skills\":[");
            List<object> skills = SV_StaticObjects("TaleWorlds.Core.DefaultSkills");
            int sw = 0;
            for (int i = 0; i < skills.Count; i++)
            {
                object s = skills[i];
                if (s == null || s.GetType().Name.IndexOf("SkillObject", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (sw++ > 0) b.Append(',');
                b.Append("{\"id\":").Append(J(StringId(s))).Append(",\"name\":").Append(J(ReadName(s)))
                 .Append(",\"value\":").Append(IntValue(SV_Invoke(hero, "GetSkillValue", s), 0))
                 .Append(",\"focus\":").Append(IntValue(SV_Invoke(dev, "GetFocus", s), 0))
                 .Append(",\"xp\":").Append(Num(DoubleValue(SV_Invoke(dev, "GetSkillXp", s), 0)))
                 .Append(",\"xp_progress\":").Append(Num(DoubleValue(SV_Invoke(dev, "GetSkillXpProgress", s), 0)))
                 .Append('}');
            }
            b.Append("],\"available_perks\":[");
            object perks = GetStatic("TaleWorlds.CampaignSystem.CharacterDevelopment.PerkObject", "All");
            IEnumerable pe = AsEnumerable(perks);
            int pw = 0;
            if (pe != null)
            {
                foreach (object p in pe)
                {
                    object skill = GetProp(p, "Skill");
                    int need = (int)Math.Ceiling(DoubleValue(GetProp(p, "RequiredSkillValue"), 0));
                    int have = IntValue(SV_Invoke(hero, "GetSkillValue", skill), 0);
                    bool chosen = BoolValue(SV_Invoke(hero, "GetPerkValue", p));
                    object alt = GetProp(p, "AlternativePerk");
                    bool altChosen = alt != null && BoolValue(SV_Invoke(hero, "GetPerkValue", alt));
                    if (chosen || altChosen || have < need) continue;
                    if (pw++ > 0) b.Append(',');
                    b.Append("{\"id\":").Append(J(StringId(p)))
                     .Append(",\"name\":").Append(J(ReadName(p)))
                     .Append(",\"skill\":").Append(J(ReadName(skill)))
                     .Append(",\"required_skill\":").Append(need)
                     .Append(",\"alternative\":").Append(J(ReadName(alt))).Append('}');
                }
            }
            b.Append("]}");
            return b.ToString();
        }

        private static string SV_CompanionsJson(object clan, object main)
        {
            StringBuilder b = new StringBuilder("[");
            IEnumerable e = AsEnumerable(GetProp(clan, "Companions"));
            int n = 0;
            if (e != null)
            {
                foreach (object h in e)
                {
                    if (n++ > 0) b.Append(',');
                    b.Append("{\"id\":").Append(J(StringId(h)))
                     .Append(",\"name\":").Append(J(ReadName(h)))
                     .Append(",\"level\":").Append(IntValue(GetProp(h, "Level"), 0))
                     .Append(",\"hp\":").Append(IntValue(GetProp(h, "HitPoints"), 0))
                     .Append(",\"max_hp\":").Append(IntValue(GetProp(h, "MaxHitPoints"), 0))
                     .Append(",\"wounded\":").Append(BoolValue(GetProp(h, "IsWounded")) ? "true" : "false")
                     .Append(",\"party\":").Append(J(ReadName(GetProp(h, "PartyBelongedTo"))))
                     .Append(",\"settlement\":").Append(J(ReadName(SV_GetAny(h, "CurrentSettlement", "StayingInSettlement"))))
                     .Append(",\"roles\":").Append(J(Convert.ToString(SV_Invoke(main, "GetHeroPartyRoles", h), CultureInfo.InvariantCulture)))
                     .Append('}');
                }
            }
            b.Append(']');
            return b.ToString();
        }

        private static string SV_ClanJson(object campaign, object hero, object main)
        {
            object clan = SV_GetAny(hero, "Clan");
            if (clan == null) clan = GetStatic("TaleWorlds.CampaignSystem.Clan", "PlayerClan");
            if (clan == null) return "{}";
            object kingdom = GetProp(clan, "Kingdom");
            object models = GetProp(campaign, "Models");
            object finance = GetProp(models, "ClanFinanceModel");
            object income = SV_Invoke(finance, "CalculateClanIncome", clan, false, false, false);
            object expenses = SV_Invoke(finance, "CalculateClanExpenses", clan, false, false, false);
            object net = SV_Invoke(finance, "CalculateClanGoldChange", clan, false, false, false);
            StringBuilder b = new StringBuilder("{");
            b.Append("\"name\":").Append(J(ReadName(clan))).Append(',')
             .Append("\"tier\":").Append(IntValue(GetProp(clan, "Tier"), 0)).Append(',')
             .Append("\"renown\":").Append(Num(DoubleValue(GetProp(clan, "Renown"), 0))).Append(',')
             .Append("\"influence\":").Append(Num(DoubleValue(GetProp(clan, "Influence"), 0))).Append(',')
             .Append("\"gold\":").Append(IntValue(GetProp(clan, "Gold"), 0)).Append(',')
             .Append("\"strength\":").Append(Num(DoubleValue(GetProp(clan, "CurrentTotalStrength"), 0))).Append(',')
             .Append("\"mercenary\":").Append(BoolValue(GetProp(clan, "IsUnderMercenaryService")) ? "true" : "false").Append(',')
             .Append("\"kingdom\":").Append(J(ReadName(kingdom))).Append(',')
             .Append("\"daily_income\":").Append(Num(SV_Result(income, 0))).Append(',')
             .Append("\"daily_expenses\":").Append(Num(SV_Result(expenses, 0))).Append(',')
             .Append("\"daily_net_gold\":").Append(Num(SV_Result(net, 0))).Append(',')
             .Append("\"companions\":").Append(SV_CompanionsJson(clan, main)).Append(',')
             .Append("\"fiefs\":").Append(NamesJson(GetProp(clan, "Fiefs"), 50)).Append(',')
             .Append("\"settlements\":").Append(NamesJson(GetProp(clan, "Settlements"), 50)).Append(',')
             .Append("\"workshops\":").Append(NamesJson(GetProp(hero, "OwnedWorkshops"), 30)).Append(',')
             .Append("\"caravans\":").Append(NamesJson(GetProp(hero, "OwnedCaravans"), 30)).Append(',')
             .Append("\"war_parties\":").Append(NamesJson(GetProp(clan, "WarPartyComponents"), 30))
             .Append('}');
            return b.ToString();
        }

        private static string SV_KingdomJson(object hero)
        {
            object clan = SV_GetAny(hero, "Clan");
            object kingdom = GetProp(clan, "Kingdom");
            if (kingdom == null) return "{}";
            return new StringBuilder("{")
                .Append("\"name\":").Append(J(ReadName(kingdom))).Append(',')
                .Append("\"ruler\":").Append(J(ReadName(GetProp(kingdom, "Leader")))).Append(',')
                .Append("\"strength\":").Append(Num(DoubleValue(GetProp(kingdom, "CurrentTotalStrength"), 0))).Append(',')
                .Append("\"wars\":").Append(NamesJson(GetProp(kingdom, "FactionsAtWarWith"), 40)).Append(',')
                .Append("\"allied_kingdoms\":").Append(NamesJson(GetProp(kingdom, "AlliedKingdoms"), 40)).Append(',')
                .Append("\"clans\":").Append(NamesJson(GetProp(kingdom, "Clans"), 60)).Append(',')
                .Append("\"armies\":").Append(NamesJson(GetProp(kingdom, "Armies"), 30)).Append(',')
                .Append("\"fiefs\":").Append(NamesJson(GetProp(kingdom, "Fiefs"), 80))
                .Append('}').ToString();
        }

        private static string SV_QuestJson(object campaign)
        {
            object qm = SV_GetAny(campaign, "QuestManager", "Quests");
            object quests = qm == null ? null : SV_GetAny(qm, "Quests", "AllQuests", "QuestList", "QuestsList");
            if (quests == null && qm is IEnumerable) quests = qm;
            if (quests == null) quests = GetStatic("TaleWorlds.CampaignSystem.QuestBase", "AllQuests");
            StringBuilder b = new StringBuilder("[");
            IEnumerable e = AsEnumerable(quests);
            int n = 0;
            if (e != null)
            {
                foreach (object q in e)
                {
                    if (q == null) continue;
                    object ongoing = GetProp(q, "IsOngoing");
                    if (ongoing != null && !BoolValue(ongoing)) continue;
                    if (n++ > 0) b.Append(',');
                    object due = GetProp(q, "QuestDueTime");
                    b.Append("{\"id\":").Append(J(StringId(q)))
                     .Append(",\"title\":").Append(J(Convert.ToString(GetProp(q, "Title"), CultureInfo.InvariantCulture)))
                     .Append(",\"giver\":").Append(J(ReadName(GetProp(q, "QuestGiver"))))
                     .Append(",\"reward_gold\":").Append(IntValue(SV_GetMember(q, "RewardGold"), 0))
                     .Append(",\"due_time\":").Append(J(Convert.ToString(due, CultureInfo.InvariantCulture)))
                     .Append(",\"remaining_hours\":").Append(Num(DoubleValue(GetProp(due, "RemainingHoursFromNow"), 0)))
                     .Append(",\"tracked\":").Append(BoolValue(GetProp(q, "IsTrackEnabled")) ? "true" : "false")
                     .Append(",\"tasks\":[");
                    IEnumerable tasks = AsEnumerable(GetProp(q, "TaskList"));
                    int t = 0;
                    if (tasks != null)
                    {
                        foreach (object task in tasks)
                        {
                            if (t++ > 0) b.Append(',');
                            b.Append("{\"type\":").Append(J(task.GetType().FullName))
                             .Append(",\"active\":").Append(BoolValue(GetProp(task, "IsActive")) ? "true" : "false")
                             .Append(",\"logged\":").Append(BoolValue(GetProp(task, "IsLogged")) ? "true" : "false")
                             .Append('}');
                        }
                    }
                    b.Append("]}");
                    if (n >= 40) break;
                }
            }
            b.Append(']');
            return b.ToString();
        }

        private static string SV_SettlementJson(object s, bool includeMarket)
        {
            if (s == null) return "{}";
            object town = GetProp(s, "Town");
            object party = GetProp(s, "Party");
            object garrison = SV_GetAny(town, "GarrisonParty", "GarrisonMobileParty");
            StringBuilder b = new StringBuilder("{");
            b.Append("\"id\":").Append(J(StringId(s))).Append(',')
             .Append("\"name\":").Append(J(ReadName(s))).Append(',')
             .Append("\"kind\":").Append(J(BoolValue(GetProp(s, "IsTown")) ? "town" : (BoolValue(GetProp(s, "IsCastle")) ? "castle" : (BoolValue(GetProp(s, "IsVillage")) ? "village" : "settlement")))).Append(',')
             .Append("\"owner_clan\":").Append(J(ReadName(GetProp(s, "OwnerClan")))).Append(',')
             .Append("\"owner\":").Append(J(ReadName(GetProp(s, "Owner")))).Append(',')
             .Append("\"militia\":").Append(Num(DoubleValue(GetProp(s, "Militia"), 0))).Append(',')
             .Append("\"prosperity\":").Append(Num(DoubleValue(SV_GetAny(town, "Prosperity", GetProp(s, "Prosperity") == null ? "__none" : "Prosperity"), DoubleValue(GetProp(s, "Prosperity"), 0)))).Append(',')
             .Append("\"loyalty\":").Append(Num(DoubleValue(GetProp(town, "Loyalty"), 0))).Append(',')
             .Append("\"security\":").Append(Num(DoubleValue(GetProp(town, "Security"), 0))).Append(',')
             .Append("\"garrison_count\":").Append(RosterCount(garrison, "MemberRoster")).Append(',')
             .Append("\"defender_strength\":").Append(Num(DoubleValue(GetProp(party, "EstimatedStrength"), 0))).Append(',')
             .Append("\"under_siege\":").Append(GetProp(s, "SiegeEvent") != null ? "true" : "false");
            if (includeMarket)
            {
                object items = GetProp(s, "ItemRoster");
                b.Append(",\"market_summary\":{\"stacks\":").Append(IntValue(GetProp(items, "Count"), 0))
                 .Append(",\"total_value\":").Append(IntValue(GetProp(items, "TotalValue"), 0)).Append("}")
                 .Append(",\"market_inventory\":").Append(SV_InventoryJson(items));
            }
            b.Append('}');
            return b.ToString();
        }

        private static string SV_ArmyJson(object main)
        {
            object army = GetProp(main, "Army");
            if (army == null) return "{}";
            return new StringBuilder("{")
                .Append("\"name\":").Append(J(ReadName(army))).Append(',')
                .Append("\"type\":").Append(J(Convert.ToString(GetProp(army, "ArmyType"), CultureInfo.InvariantCulture))).Append(',')
                .Append("\"owner\":").Append(J(ReadName(GetProp(army, "ArmyOwner")))).Append(',')
                .Append("\"leader_party\":").Append(J(ReadName(GetProp(army, "LeaderParty")))).Append(',')
                .Append("\"cohesion\":").Append(Num(DoubleValue(GetProp(army, "Cohesion"), 0))).Append(',')
                .Append("\"daily_cohesion_change\":").Append(Num(DoubleValue(GetProp(army, "DailyCohesionChange"), 0))).Append(',')
                .Append("\"morale\":").Append(Num(DoubleValue(GetProp(army, "Morale"), 0))).Append(',')
                .Append("\"strength\":").Append(Num(DoubleValue(SV_GetAny(army, "EstimatedStrength", "TotalStrength"), 0))).Append(',')
                .Append("\"healthy\":").Append(IntValue(GetProp(army, "TotalHealthyMembers"), 0)).Append(',')
                .Append("\"members\":").Append(IntValue(GetProp(army, "TotalManCount"), 0)).Append(',')
                .Append("\"parties\":").Append(NamesJson(GetProp(army, "Parties"), 30))
                .Append('}').ToString();
        }

        private static string SV_SiegeJson(object main, object currentSettlement)
        {
            object siege = SV_GetAny(main, "SiegeEvent");
            if (siege == null && currentSettlement != null) siege = GetProp(currentSettlement, "SiegeEvent");
            object camp = GetProp(main, "BesiegerCamp");
            if (siege == null && camp == null) return "{}";
            object besieged = SV_GetAny(siege, "BesiegedSettlement", "Settlement");
            if (besieged == null && currentSettlement != null) besieged = currentSettlement;
            return new StringBuilder("{")
                .Append("\"active\":true,")
                .Append("\"settlement\":").Append(J(ReadName(besieged))).Append(',')
                .Append("\"besieger_party\":").Append(J(ReadName(GetProp(camp, "BesiegerParty")))).Append(',')
                .Append("\"main_party_is_besieger\":").Append(camp != null ? "true" : "false")
                .Append('}').ToString();
        }

        private static string SV_NearbyPartiesJson(object main)
        {
            List<NearbyEntry> list = NearbyParties(main);
            object mainFaction = GetProp(main, "MapFaction");
            object hero = GetStatic("TaleWorlds.CampaignSystem.Hero", "MainHero");
            StringBuilder b = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) b.Append(',');
                object p = list[i].Obj;
                object leader = GetProp(p, "LeaderHero");
                object faction = GetProp(p, "MapFaction");
                bool hostile = BoolValue(SV_Invoke(mainFaction, "IsAtWarWith", faction));
                if (!hostile && leader != null) hostile = BoolValue(SV_Invoke(hero, "IsEnemy", leader));
                int relation = leader == null ? 0 : IntValue(SV_Invoke(hero, "GetRelation", leader), 0);
                b.Append("{\"id\":").Append(J(StringId(p)))
                 .Append(",\"name\":").Append(J(ReadName(p)))
                 .Append(",\"leader\":").Append(J(ReadName(leader)))
                 .Append(",\"kind\":").Append(J(KindOfParty(p)))
                 .Append(",\"distance\":").Append(Num(list[i].Dist))
                 .Append(",\"members\":").Append(RosterCount(p, "MemberRoster"))
                 .Append(",\"healthy\":").Append(HealthyCount(p))
                 .Append(",\"speed\":").Append(Num(DoubleValue(GetProp(p, "Speed"), 0)))
                 .Append(",\"strength\":").Append(Num(DoubleValue(GetProp(SV_PartyBase(p), "EstimatedStrength"), 0)))
                 .Append(",\"faction\":").Append(J(ReadName(faction)))
                 .Append(",\"hostile\":").Append(hostile ? "true" : "false")
                 .Append(",\"relation\":").Append(relation)
                 .Append(",\"behavior\":").Append(J(Convert.ToString(GetProp(p, "DefaultBehavior"), CultureInfo.InvariantCulture)))
                 .Append(",\"target_party\":").Append(J(ReadName(GetProp(p, "TargetParty"))))
                 .Append(",\"target_settlement\":").Append(J(ReadName(GetProp(p, "TargetSettlement"))))
                 .Append(",\"current_settlement\":").Append(J(ReadName(GetProp(p, "CurrentSettlement"))))
                 .Append(",\"army\":").Append(J(ReadName(GetProp(p, "Army"))))
                 .Append('}');
            }
            b.Append(']');
            return b.ToString();
        }

        private static string SV_NearbySettlementsJson(object main)
        {
            List<NearbyEntry> list = NearbySettlements(main);
            StringBuilder b = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) b.Append(',');
                object s = list[i].Obj;
                b.Append("{\"id\":").Append(J(StringId(s)))
                 .Append(",\"name\":").Append(J(ReadName(s)))
                 .Append(",\"kind\":").Append(J(list[i].Kind))
                 .Append(",\"distance\":").Append(Num(list[i].Dist))
                 .Append(",\"owner\":").Append(J(ReadName(GetProp(s, "OwnerClan"))))
                 .Append(",\"militia\":").Append(Num(DoubleValue(GetProp(s, "Militia"), 0)))
                 .Append(",\"under_siege\":").Append(GetProp(s, "SiegeEvent") != null ? "true" : "false")
                 .Append('}');
            }
            b.Append(']');
            return b.ToString();
        }

        private static string SV_MenuJson(object campaign)
        {
            object menu = GetProp(campaign, "CurrentMenuContext");
            object gameMenu = SV_GetAny(menu, "GameMenu", "CurrentGameMenu");
            object conversationHero = GetStatic("TaleWorlds.CampaignSystem.Hero", "OneToOneConversationHero");
            return new StringBuilder("{")
                .Append("\"menu_active\":").Append(menu != null ? "true" : "false").Append(',')
                .Append("\"menu_id\":").Append(J(Convert.ToString(SV_GetAny(gameMenu, "StringId", "Id"), CultureInfo.InvariantCulture))).Append(',')
                .Append("\"menu_name\":").Append(J(ReadName(gameMenu))).Append(',')
                .Append("\"conversation_active\":").Append(conversationHero != null ? "true" : "false").Append(',')
                .Append("\"conversation_hero\":").Append(J(ReadName(conversationHero)))
                .Append('}').ToString();
        }

        private static void SV_RefreshCaches(object campaign, object hero, object main, object currentSettlement)
        {
            object partyBase = SV_PartyBase(main);
            object memberRoster = GetProp(main, "MemberRoster");
            object prisonerRoster = GetProp(main, "PrisonRoster");
            object itemRoster = SV_GetAny(main, "ItemRoster");
            if (itemRoster == null) itemRoster = GetProp(partyBase, "ItemRoster");

            if (SV_Due(ref _svRosterAt, 1500))
            {
                _svTroops = SV_TroopRosterJson(memberRoster, partyBase, false);
                _svPrisoners = SV_TroopRosterJson(prisonerRoster, partyBase, true);
                _svInventory = SV_InventoryJson(itemRoster);
                _svInventorySummary = SV_InventorySummaryJson(main, itemRoster);
            }
            if (SV_Due(ref _svNearbyAt, 1000))
            {
                _svNearbyParties = main == null ? "[]" : SV_NearbyPartiesJson(main);
                _svNearbySettlements = main == null ? "[]" : SV_NearbySettlementsJson(main);
            }
            if (SV_Due(ref _svHeroAt, 5000))
            {
                _svHeroProgress = SV_HeroProgressJson(hero);
            }
            if (SV_Due(ref _svWorldAt, 5000))
            {
                _svClan = SV_ClanJson(campaign, hero, main);
                _svKingdom = SV_KingdomJson(hero);
                _svQuests = SV_QuestJson(campaign);
            }
            if (SV_Due(ref _svMarketAt, 5000))
            {
                _svCurrentSettlement = SV_SettlementJson(currentSettlement, true);
            }
        }

        private static string BuildStrategicStateV3()
        {
            object campaign = GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current");
            object hero = GetStatic("TaleWorlds.CampaignSystem.Hero", "MainHero");
            object main = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "MainParty");
            object mission = GetStatic("TaleWorlds.MountAndBlade.Mission", "Current");
            object encounter = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Current");
            object encounteredParty = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "EncounteredParty");
            object encounterSettlement = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "EncounterSettlement");
            object battle = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Battle");
            object currentSettlement = SV_GetAny(main, "CurrentSettlement");
            if (currentSettlement == null) currentSettlement = SV_GetAny(hero, "CurrentSettlement", "StayingInSettlement");

            if (campaign == null || hero == null || main == null)
                return "{\"schema\":\"bannerlord.strategic_state.v3\",\"campaign_loaded\":false}";

            SV_RefreshCaches(campaign, hero, main, currentSettlement);
            object partyBase = SV_PartyBase(main);
            object memberRoster = GetProp(main, "MemberRoster");
            object prisonRoster = GetProp(main, "PrisonRoster");
            object speedExplained = GetProp(main, "SpeedExplained");
            object foodChangeExplained = GetProp(main, "FoodChangeExplained");
            double capacity = DoubleValue(GetProp(main, "InventoryCapacity"), 0);
            double carried = DoubleValue(GetProp(main, "TotalWeightCarried"), 0);

            StringBuilder b = new StringBuilder("{");
            b.Append("\"schema\":\"bannerlord.strategic_state.v3\",")
             .Append("\"updated_utc\":").Append(J(DateTime.UtcNow.ToString("o"))).Append(',')
             .Append("\"campaign_loaded\":true,")
             .Append("\"sampling_ms\":{\"fast\":350,\"nearby\":1000,\"rosters\":1500,\"hero\":5000,\"world\":5000,\"market\":5000},")
             .Append("\"session\":{")
             .Append("\"time_mode\":").Append(J(Convert.ToString(GetProp(campaign, "TimeControlMode"), CultureInfo.InvariantCulture))).Append(',')
             .Append("\"waiting\":").Append(BoolValue(GetProp(campaign, "IsMainPartyWaiting")) ? "true" : "false").Append(',')
             .Append("\"mission_active\":").Append(mission != null ? "true" : "false").Append(',')
             .Append("\"menu\":").Append(SV_MenuJson(campaign)).Append(',')
             .Append("\"encounter\":{\"active\":").Append(encounter != null ? "true" : "false")
             .Append(",\"battle_ready\":").Append(battle != null ? "true" : "false")
             .Append(",\"party\":").Append(J(ReadName(encounteredParty)))
             .Append(",\"settlement\":").Append(J(ReadName(encounterSettlement))).Append("}},")
             .Append("\"hero\":{\"id\":").Append(J(StringId(hero)))
             .Append(",\"name\":").Append(J(ReadName(hero)))
             .Append(",\"gold\":").Append(IntValue(GetProp(hero, "Gold"), 0))
             .Append(",\"hp\":").Append(IntValue(GetProp(hero, "HitPoints"), 0))
             .Append(",\"max_hp\":").Append(IntValue(GetProp(hero, "MaxHitPoints"), 0))
             .Append(",\"wounded\":").Append(BoolValue(GetProp(hero, "IsWounded")) ? "true" : "false")
             .Append(",\"progression\":").Append(_svHeroProgress).Append("},")
             .Append("\"party\":{")
             .Append("\"name\":").Append(J(ReadName(main))).Append(',')
             .Append("\"members\":").Append(RosterCount(main, "MemberRoster")).Append(',')
             .Append("\"healthy\":").Append(HealthyCount(main)).Append(',')
             .Append("\"wounded\":").Append(IntValue(GetProp(memberRoster, "TotalWounded"), 0)).Append(',')
             .Append("\"prisoners\":").Append(RosterCount(main, "PrisonRoster")).Append(',')
             .Append("\"party_size_limit\":").Append(IntValue(GetProp(partyBase, "PartySizeLimit"), 0)).Append(',')
             .Append("\"prisoner_size_limit\":").Append(IntValue(GetProp(partyBase, "PrisonerSizeLimit"), 0)).Append(',')
             .Append("\"speed\":").Append(Num(DoubleValue(GetProp(main, "Speed"), 0))).Append(',')
             .Append("\"speed_explained_total\":").Append(Num(SV_Result(speedExplained, DoubleValue(GetProp(main, "Speed"), 0)))).Append(',')
             .Append("\"morale\":").Append(Num(DoubleValue(GetProp(main, "Morale"), 0))).Append(',')
             .Append("\"food\":").Append(Num(DoubleValue(GetProp(main, "Food"), 0))).Append(',')
             .Append("\"food_inventory\":").Append(IntValue(GetProp(main, "TotalFoodAtInventory"), 0)).Append(',')
             .Append("\"food_change_per_day\":").Append(Num(DoubleValue(GetProp(main, "FoodChange"), SV_Result(foodChangeExplained, 0)))).Append(',')
             .Append("\"food_days\":").Append(Num(DoubleValue(SV_Invoke(main, "GetNumDaysForFoodToLast"), 0))).Append(',')
             .Append("\"daily_wage\":").Append(IntValue(GetProp(main, "TotalWage"), 0)).Append(',')
             .Append("\"carried_weight\":").Append(Num(carried)).Append(',')
             .Append("\"inventory_capacity\":").Append(Num(capacity)).Append(',')
             .Append("\"over_capacity\":").Append(carried > capacity && capacity > 0 ? "true" : "false").Append(',')
             .Append("\"current_settlement\":").Append(J(ReadName(GetProp(main, "CurrentSettlement")))).Append(',')
             .Append("\"target_settlement\":").Append(J(ReadName(GetProp(main, "TargetSettlement")))).Append(',')
             .Append("\"target_party\":").Append(J(ReadName(GetProp(main, "TargetParty")))).Append(',')
             .Append("\"behavior\":").Append(J(Convert.ToString(GetProp(main, "DefaultBehavior"), CultureInfo.InvariantCulture))).Append(',')
             .Append("\"inventory_summary\":").Append(_svInventorySummary).Append(',')
             .Append("\"troops\":").Append(_svTroops).Append(',')
             .Append("\"prisoner_roster\":").Append(_svPrisoners).Append(',')
             .Append("\"inventory\":").Append(_svInventory).Append("},")
             .Append("\"clan\":").Append(_svClan).Append(',')
             .Append("\"kingdom\":").Append(_svKingdom).Append(',')
             .Append("\"quests\":").Append(_svQuests).Append(',')
             .Append("\"army\":").Append(SV_ArmyJson(main)).Append(',')
             .Append("\"siege\":").Append(SV_SiegeJson(main, currentSettlement)).Append(',')
             .Append("\"current_settlement\":").Append(_svCurrentSettlement).Append(',')
             .Append("\"nearby_parties\":").Append(_svNearbyParties).Append(',')
             .Append("\"nearby_settlements\":").Append(_svNearbySettlements)
             .Append('}');
            return b.ToString();
        }
    }
}
