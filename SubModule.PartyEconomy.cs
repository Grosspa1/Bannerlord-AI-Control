using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordStrategicBridge
{
    public partial class SubModule
    {
        private static MobileParty RequirePartyEconomyContext()
        {
            if (Campaign.Current == null) throw new InvalidOperationException("NOT_IN_CAMPAIGN");
            MobileParty main = MobileParty.MainParty;
            object active = GetActiveGameState();
            PartyEconomyRules.ValidateContext(Campaign.Current != null,
                main != null && Hero.MainHero != null,
                Hero.MainHero != null && Hero.MainHero.IsPrisoner,
                main != null && main.IsActive, Mission.Current != null,
                main != null && main.MapEvent != null,
                PlayerEncounter.CurrentBattleSimulation != null,
                active == null ? "" : active.GetType().Name,
                Campaign.Current.SaveHandler == null || Campaign.Current.SaveHandler.IsSaving,
                PlayerEncounter.Current != null && (main == null || main.CurrentSettlement == null ||
                    PlayerEncounter.EncounteredParty != main.CurrentSettlement.Party ||
                    PlayerEncounter.Battle != null || IsPostBattleDecisionPending()));
            return main;
        }

        private static Settlement RequirePartyEconomySettlement(MobileParty main)
        {
            Settlement settlement = main.CurrentSettlement;
            PartyEconomyRules.ValidateSettlement(settlement != null,
                settlement != null && (settlement.IsTown || settlement.IsVillage),
                settlement != null && settlement.IsUnderSiege,
                settlement != null && settlement.IsUnderRaid,
                settlement != null && FactionManager.IsAtWarAgainstFaction(Hero.MainHero.MapFaction, settlement.MapFaction),
                settlement == null || !settlement.IsVillage || settlement.Village.VillageState == Village.VillageStates.Normal);
            return settlement;
        }

        private static int RecruitmentCost(CharacterObject troop, MobileParty main)
        {
            return Campaign.Current.Models.PartyWageModel.GetTroopRecruitmentCost(
                troop, main.LeaderHero, false).RoundedResultNumber;
        }

        private static string InspectRecruitment(MobileParty main)
        {
            Settlement settlement = RequirePartyEconomySettlement(main);
            StringBuilder json = new StringBuilder();
            json.Append("{\"settlement_id\":").Append(J(settlement.StringId))
                .Append(",\"gold\":").Append(Hero.MainHero.Gold)
                .Append(",\"party_members\":").Append(main.MemberRoster.TotalManCount)
                .Append(",\"party_limit\":").Append(main.Party.PartySizeLimit)
                .Append(",\"volunteers\":[");
            int written = 0;
            foreach (Hero notable in settlement.Notables)
            {
                if (notable.VolunteerTypes == null) continue;
                for (int slot = 0; slot < notable.VolunteerTypes.Length; slot++)
                {
                    CharacterObject troop = notable.VolunteerTypes[slot];
                    if (troop == null) continue;
                    if (written++ > 0) json.Append(',');
                    json.Append("{\"notable_id\":").Append(J(notable.StringId))
                        .Append(",\"notable\":").Append(J(notable.Name.ToString()))
                        .Append(",\"slot\":").Append(slot)
                        .Append(",\"troop_id\":").Append(J(troop.StringId))
                        .Append(",\"troop\":").Append(J(troop.Name.ToString()))
                        .Append(",\"tier\":").Append(troop.Tier)
                        .Append(",\"cost\":").Append(RecruitmentCost(troop, main))
                        .Append(",\"unlocked\":").Append(HeroHelper.HeroCanRecruitFromHero(Hero.MainHero, notable, slot) ? "true" : "false")
                        .Append('}');
                }
            }
            return json.Append("]}").ToString();
        }

        private static string RecruitOne(MobileParty main, string argument)
        {
            RecruitOneRequest request = RecruitOneRequest.Parse(argument);
            Settlement settlement = RequirePartyEconomySettlement(main);
            Hero notable = null;
            foreach (Hero candidate in settlement.Notables)
                if (candidate.StringId == request.NotableId) { notable = candidate; break; }
            if (notable == null || notable.CurrentSettlement != settlement ||
                notable.VolunteerTypes == null || request.Slot >= notable.VolunteerTypes.Length)
                throw new InvalidOperationException("VOLUNTEER_NOT_FOUND");
            CharacterObject troop = notable.VolunteerTypes[request.Slot];
            if (troop == null) throw new InvalidOperationException("VOLUNTEER_CHANGED");
            if (main.LeaderHero != Hero.MainHero) throw new InvalidOperationException("MAIN_HERO_NOT_PARTY_LEADER");
            int cost = RecruitmentCost(troop, main);
            int goldBefore = Hero.MainHero.Gold;
            int membersBefore = main.MemberRoster.TotalManCount;
            int troopBefore = main.MemberRoster.GetTroopCount(troop);
            request.Validate(troop.StringId, HeroHelper.HeroCanRecruitFromHero(Hero.MainHero, notable, request.Slot),
                cost, goldBefore, membersBefore, main.Party.PartySizeLimit);

            RecruitmentCampaignBehavior behavior = Campaign.Current.GetCampaignBehavior<RecruitmentCampaignBehavior>();
            if (behavior == null) throw new InvalidOperationException("RECRUITMENT_BEHAVIOR_UNAVAILABLE");
            // This canonical action is private in v1.4.8. Resolve the exact signature;
            // never emulate its gold/roster/event changes with direct mutations.
            MethodInfo recruit = typeof(RecruitmentCampaignBehavior).GetMethod("GetRecruitVolunteerFromIndividual",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new Type[] { typeof(MobileParty), typeof(CharacterObject), typeof(Hero), typeof(int) }, null);
            if (recruit == null) throw new MissingMethodException("GetRecruitVolunteerFromIndividual");
            try
            {
                recruit.Invoke(behavior, new object[] { main, troop, notable, request.Slot });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("RECRUIT_RESULT_UNCERTAIN: inspect recruits, troops and gold before any retry.", ex);
            }
            finally
            {
                // Even an exception can follow partial campaign changes.
                _svRosterAt = DateTime.MinValue;
                _svHeroAt = DateTime.MinValue;
                _svMarketAt = DateTime.MinValue;
            }
            if (notable.VolunteerTypes[request.Slot] != null ||
                main.MemberRoster.GetTroopCount(troop) != troopBefore + 1 ||
                main.MemberRoster.TotalManCount != membersBefore + 1 || Hero.MainHero.Gold != goldBefore - cost)
                throw new InvalidOperationException("RECRUIT_RESULT_UNCERTAIN: action returned but postconditions differ; inspect before retry.");

            return "{\"recruited\":1,\"troop_id\":" + J(troop.StringId) +
                ",\"gold_spent\":" + cost.ToString(CultureInfo.InvariantCulture) +
                ",\"gold\":" + Hero.MainHero.Gold.ToString(CultureInfo.InvariantCulture) +
                ",\"party_members\":" + main.MemberRoster.TotalManCount.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string InspectItemStacks(ItemRoster roster, MobileParty main, Settlement settlement)
        {
            // GetPrice's isSelling argument is from the player's perspective.
            // InventoryLogic.GetItemPrice(element, true) calls market.GetPrice(..., false, ...).
            IMarketData market = settlement == null ? null :
                (settlement.IsTown ? (IMarketData)settlement.Town.MarketData : settlement.Village.MarketData);
            if (settlement != null && market == null) throw new InvalidOperationException("MARKET_DATA_UNAVAILABLE");
            StringBuilder json = new StringBuilder("[");
            int written = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                ItemRosterElement element = roster.GetElementCopyAtIndex(i);
                EquipmentElement equipment = element.EquipmentElement;
                ItemObject item = equipment.Item;
                if (item == null || element.Amount <= 0) continue;
                if (written++ > 0) json.Append(',');
                json.Append("{\"item_id\":").Append(J(item.StringId))
                    .Append(",\"modifier_id\":").Append(J(equipment.ItemModifier == null ? "" : equipment.ItemModifier.StringId))
                    .Append(",\"name\":").Append(J(equipment.GetModifiedItemName().ToString()))
                    .Append(",\"count\":").Append(element.Amount)
                    .Append(",\"food\":").Append(item.IsFood ? "true" : "false")
                    .Append(",\"quest\":").Append(equipment.IsQuestItem ? "true" : "false")
                    .Append(",\"weight\":").Append(Num(item.Weight));
                if (market != null)
                {
                    json.Append(",\"buy_unit_price\":").Append(market.GetPrice(equipment, main, false, settlement.Party))
                        .Append(",\"sell_unit_price\":").Append(market.GetPrice(equipment, main, true, settlement.Party));
                }
                json.Append('}');
            }
            return json.Append(']').ToString();
        }

        private static string InspectMarket(MobileParty main)
        {
            Settlement settlement = RequirePartyEconomySettlement(main);
            return "{\"settlement_id\":" + J(settlement.StringId) +
                ",\"settlement\":" + J(settlement.Name.ToString()) +
                ",\"quote_kind\":\"current_single_unit_not_bulk_total\",\"market_items\":" +
                InspectItemStacks(settlement.ItemRoster, main, settlement) +
                ",\"player_items\":" + InspectItemStacks(main.ItemRoster, main, settlement) + "}";
        }

        private static string InspectEconomy(MobileParty main)
        {
            double foodUse = -main.FoodChange;
            return "{\"gold\":" + Hero.MainHero.Gold.ToString(CultureInfo.InvariantCulture) +
                ",\"food_units\":" + main.TotalFoodAtInventory.ToString(CultureInfo.InvariantCulture) +
                ",\"food_change_per_day\":" + Num(main.FoodChange) +
                ",\"estimated_food_days\":" + (foodUse > 0 ? Num(main.TotalFoodAtInventory / foodUse) : "null") +
                ",\"weight\":" + Num(main.TotalWeightCarried) +
                ",\"capacity\":" + main.InventoryCapacity.ToString(CultureInfo.InvariantCulture) +
                ",\"daily_wage\":" + main.TotalWage.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static bool TryProcessPartyEconomyCommand(string verb, string argument, out string message)
        {
            message = "";
            if (!PartyEconomyRules.Handles(verb)) return false;
            if (PartyEconomyRules.IsReadOnly(verb) && !string.IsNullOrWhiteSpace(argument))
                throw new ArgumentException(verb + " accepts no arguments.");
            MobileParty main = RequirePartyEconomyContext();
            if (verb == "troops") message = RosterJson(main.MemberRoster, int.MaxValue);
            else if (verb == "recruits" || verb == "inspect_recruits") message = InspectRecruitment(main);
            else if (verb == "recruit_one") message = RecruitOne(main, argument);
            else if (verb == "inventory") message = InspectItemStacks(main.ItemRoster, main, null);
            else if (verb == "market") message = InspectMarket(main);
            else if (verb == "economy_status") message = InspectEconomy(main);
            return true;
        }
    }
}
