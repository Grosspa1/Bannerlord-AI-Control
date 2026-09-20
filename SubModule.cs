using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using TaleWorlds.MountAndBlade;

namespace BannerlordStrategicBridge
{
    public partial class SubModule : MBSubModuleBase
    {
        private const string Root = @"C:\Users\Public\BannerlordBridge";
        private const string CommandPath = Root + @"\command.txt";
        private const string ResponsePath = Root + @"\response.json";
        private const string StatePath = Root + @"\state.json";
        private const string LogPath = Root + @"\bridge.log";
        private const string JournalRoot = Root + @"\journal";
        private const string ResponseArchiveRoot = Root + @"\responses";
        private DateTime _lastPoll = DateTime.MinValue;
        private string _lastCommandId = "";
        private static string _sessionId = "";
        private static string _pendingCommandId = "";
        private static string _pendingVerb = "";
        private static string _pendingArg = "";
        private static string _pendingRaw = "";
        private static string _pendingPhase = "";
        private static DateTime _pendingStartedUtc = DateTime.MinValue;
        private static bool _pendingSawBusy;
        private static bool _pendingSkipIssued;
        private static bool _pendingSimulationReleased;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ResponseArchiveRoot);
            Directory.CreateDirectory(JournalRoot);
            _sessionId = Guid.NewGuid().ToString("N");
            Log("Bannerlord Strategic Bridge loaded. session=" + _sessionId);
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            if ((DateTime.UtcNow - _lastPoll).TotalMilliseconds < 350.0)
                return;
            _lastPoll = DateTime.UtcNow;

            try
            {
                AdvancePendingOperation();
                ProcessCommand();
            }
            catch (Exception ex)
            {
                Log("COMMAND ERROR: " + ex);
            }

            try
            {
                AtomicWriteAllText(StatePath, BuildStrategicStateV3());
            }
            catch (Exception ex)
            {
                Log("STATE ERROR: " + ex);
            }
        }

        private static void Log(string text)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine);
            }
            catch { }
        }
        private static void AtomicWriteAllText(string path, string text)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch { }
            }
        }

        private static string TryReadShared(string path)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                    return sr.ReadToEnd();
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static string SafeFileToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "empty";
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    b.Append(c);
                else
                    b.Append('_');
            }
            return b.ToString();
        }

        private static string PayloadKey(string raw)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw ?? ""));
        }

        private static string JournalFilePath(string id)
        {
            return Path.Combine(JournalRoot, SafeFileToken(id) + ".txt");
        }

        private static void WriteJournal(string id, string status, string raw)
        {
            AtomicWriteAllText(JournalFilePath(id),
                (id ?? "") + "\t" + (status ?? "") + "\t" + PayloadKey(raw) +
                Environment.NewLine);
        }

        private static string[] ReadJournal(string id)
        {
            string raw = TryReadShared(JournalFilePath(id));
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return raw.Trim().Split('\t');
        }

        private string ResponseArchivePath(string id)
        {
            return Path.Combine(ResponseArchiveRoot, SafeFileToken(id) + ".json");
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type t = assemblies[i].GetType(fullName, false);
                if (t != null)
                    return t;
            }
            return null;
        }

        private static object GetStatic(string typeName, string propName)
        {
            try
            {
                Type t = FindType(typeName);
                if (t == null)
                    return null;
                PropertyInfo p = t.GetProperty(propName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null)
                    return p.GetValue(null, null);
                FieldInfo f = t.GetField(propName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return f == null ? null : f.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null)
                return null;
            try
            {
                Type t = obj.GetType();
                PropertyInfo p = t.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null)
                    return p.GetValue(obj, null);
                FieldInfo f = t.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return f == null ? null : f.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadName(object obj)
        {
            if (obj == null)
                return "";
            try
            {
                object n = GetProp(obj, "Name");
                if (n != null)
                    return n.ToString();
                MethodInfo m = obj.GetType().GetMethod("GetName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (m != null)
                {
                    object x = m.Invoke(obj, null);
                    if (x != null)
                        return x.ToString();
                }
            }
            catch { }
            return "";
        }

        private static int IntValue(object value, int fallback)
        {            if (value == null)
                return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static double DoubleValue(object value, double fallback)
        {
            if (value == null)
                return fallback;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool BoolValue(object value)
        {
            if (value == null)
                return false;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private static string J(string text)
        {
            if (text == null) text = "";
            string bs = ((char)92).ToString();
            string q = ((char)34).ToString();
            text = text.Replace(bs, bs + bs);
            text = text.Replace(q, bs + "u0022");
            text = text.Replace(((char)13).ToString(), bs + "r");
            text = text.Replace(((char)10).ToString(), bs + "n");
            return q + text + q;
        }

        private static string Num(double x)
        {            return x.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static object GetPositionObject(object obj)
        {
            object p = GetProp(obj, "Position");
            if (p != null) return p;
            p = GetProp(obj, "Position2D");
            if (p != null) return p;
            p = GetProp(obj, "GetPosition2D");
            return p;
        }

        private static bool TryXY(object obj, out double x, out double y)
        {
            x = 0.0; y = 0.0;
            object p = GetPositionObject(obj);
            if (p == null) return false;
            object ox = GetProp(p, "X");
            object oy = GetProp(p, "Y");
            if (ox == null || oy == null) return false;
            x = DoubleValue(ox, 0.0);
            y = DoubleValue(oy, 0.0);
            return true;
        }

        private static double Distance(object a, object b)
        {            double ax, ay, bx, by;
            if (!TryXY(a, out ax, out ay) || !TryXY(b, out bx, out by))
                return 999999.0;
            double dx = ax - bx;
            double dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private sealed class NearbyEntry
        {
            public object Obj;
            public double Dist;
            public string Name;
            public int Count;
            public string Kind;
        }

        private static IEnumerable AsEnumerable(object obj)
        {
            IEnumerable e = obj as IEnumerable;
            return e;
        }

        private static int RosterCount(object party, string rosterName)
        {
            object roster = GetProp(party, rosterName);
            return IntValue(GetProp(roster, "TotalManCount"), 0);
        }

        private static int HealthyCount(object party)
        {            object roster = GetProp(party, "MemberRoster");
            int total = IntValue(GetProp(roster, "TotalManCount"), 0);
            object h = GetProp(roster, "TotalHealthyCount");
            return h == null ? total : IntValue(h, total);
        }

        private static string KindOfParty(object p)
        {
            if (BoolValue(GetProp(p, "IsBandit"))) return "bandit";
            if (BoolValue(GetProp(p, "IsLordParty"))) return "lord";
            if (BoolValue(GetProp(p, "IsCaravan"))) return "caravan";
            if (BoolValue(GetProp(p, "IsVillager"))) return "villager";
            if (BoolValue(GetProp(p, "IsPatrolParty"))) return "patrol";
            return "party";
        }

        private static string FactionName(object p)
        {
            object f = GetProp(p, "MapFaction");
            return f == null ? "" : ReadName(f);
        }

        private static string LeaderName(object p)
        {
            object h = GetProp(p, "LeaderHero");
            return h == null ? "" : ReadName(h);
        }

        private static List<NearbyEntry> NearbyParties(object main)
        {            List<NearbyEntry> list = new List<NearbyEntry>();
            object all = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "All");
            IEnumerable e = AsEnumerable(all);
            if (e == null) return list;
            foreach (object p in e)
            {
                if (object.ReferenceEquals(p, main)) continue;
                NearbyEntry n = new NearbyEntry();
                n.Obj = p;
                n.Dist = Distance(main, p);
                n.Name = ReadName(p);
                n.Count = RosterCount(p, "MemberRoster");
                n.Kind = KindOfParty(p);
                list.Add(n);
            }
            list.Sort(delegate(NearbyEntry a, NearbyEntry b)
            {
                return a.Dist.CompareTo(b.Dist);
            });
            if (list.Count > 15)
                list.RemoveRange(15, list.Count - 15);
            return list;
        }

        private static List<NearbyEntry> NearbySettlements(object main)
        {
            List<NearbyEntry> list = new List<NearbyEntry>();
            object all = GetStatic("TaleWorlds.CampaignSystem.Settlements.Settlement", "All");            IEnumerable e = AsEnumerable(all);
            if (e == null) return list;
            foreach (object s in e)
            {
                NearbyEntry n = new NearbyEntry();
                n.Obj = s;
                n.Dist = Distance(main, s);
                n.Name = ReadName(s);
                if (BoolValue(GetProp(s, "IsTown"))) n.Kind = "town";
                else if (BoolValue(GetProp(s, "IsCastle"))) n.Kind = "castle";
                else if (BoolValue(GetProp(s, "IsVillage"))) n.Kind = "village";
                else n.Kind = "settlement";
                list.Add(n);
            }
            list.Sort(delegate(NearbyEntry a, NearbyEntry b)
            {
                return a.Dist.CompareTo(b.Dist);
            });
            if (list.Count > 12)
                list.RemoveRange(12, list.Count - 12);
            return list;
        }


        private static string StringId(object obj)
        {
            return Convert.ToString(GetProp(obj, "StringId"), CultureInfo.InvariantCulture) ?? "";
        }

        private static int EnumerableCount(object obj)
        {
            IEnumerable e = AsEnumerable(obj);
            if (e == null) return 0;
            int n = 0;
            foreach (object x in e) n++;
            return n;
        }

        private static object Call0(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                MethodInfo m = obj.GetType().GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                return m == null ? null : m.Invoke(obj, null);
            }
            catch { return null; }
        }

        private static object Call1(object obj, string name, object arg)
        {
            if (obj == null) return null;
            MethodInfo[] ms = obj.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != name) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length != 1) continue;
                if (arg != null && !ps[0].ParameterType.IsInstanceOfType(arg)) continue;
                try { return ms[i].Invoke(obj, new object[] { arg }); }
                catch { }
            }
            return null;
        }

        private static string NamesJson(object values, int max)
        {
            StringBuilder b = new StringBuilder();
            b.Append("[");
            IEnumerable e = AsEnumerable(values);
            int n = 0;
            if (e != null)
            {
                foreach (object x in e)
                {
                    if (n >= max) break;
                    if (n > 0) b.Append(",");
                    b.Append(J(ReadName(x)));
                    n++;
                }
            }
            b.Append("]");
            return b.ToString();
        }

        private static object FindHero(string query)
        {
            object all = GetStatic("TaleWorlds.CampaignSystem.Hero", "AllAliveHeroes");
            IEnumerable e = AsEnumerable(all);
            if (e == null) return null;
            object partial = null;
            foreach (object h in e)
            {
                string name = ReadName(h);
                string id = StringId(h);
                if (string.Equals(id, query, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
                    return h;
                if (partial == null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    partial = h;
            }
            return partial;
        }

        private static string RosterJson(object roster, int max)
        {
            StringBuilder b = new StringBuilder();
            b.Append("[");
            if (roster == null) { b.Append("]"); return b.ToString(); }
            int count = IntValue(GetProp(roster, "Count"), 0);
            Type rt = roster.GetType();
            MethodInfo getChar = rt.GetMethod("GetCharacterAtIndex",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo getNum = rt.GetMethod("GetElementNumber",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(int) }, null);
            MethodInfo getWounded = rt.GetMethod("GetElementWoundedNumber",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(int) }, null);
            MethodInfo getXp = rt.GetMethod("GetElementXp",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(int) }, null);
            int written = 0;
            for (int i = 0; i < count && written < max; i++)
            {
                object ch = getChar == null ? null : getChar.Invoke(roster, new object[] { i });
                if (ch == null) continue;
                int num = getNum == null ? 0 : IntValue(getNum.Invoke(roster, new object[] { i }), 0);
                int wounded = getWounded == null ? 0 : IntValue(getWounded.Invoke(roster, new object[] { i }), 0);
                int xp = getXp == null ? 0 : IntValue(getXp.Invoke(roster, new object[] { i }), 0);
                if (written > 0) b.Append(",");
                b.Append("{\"id\":").Append(J(StringId(ch)))
                    .Append(",\"name\":").Append(J(ReadName(ch)))
                    .Append(",\"number\":").Append(num)
                    .Append(",\"wounded\":").Append(wounded)
                    .Append(",\"xp\":").Append(xp)
                    .Append(",\"tier\":").Append(IntValue(GetProp(ch, "Tier"), 0))
                    .Append(",\"level\":").Append(IntValue(GetProp(ch, "Level"), 0))
                    .Append(",\"wage\":").Append(IntValue(GetProp(ch, "TroopWage"), 0))
                    .Append(",\"hero\":").Append(BoolValue(GetProp(ch, "IsHero")) ? "true" : "false")
                    .Append(",\"mounted\":").Append(BoolValue(GetProp(ch, "IsMounted")) ? "true" : "false")
                    .Append(",\"ranged\":").Append(BoolValue(GetProp(ch, "IsRanged")) ? "true" : "false")
                    .Append(",\"power\":").Append(Num(DoubleValue(Call0(ch, "GetPower"), 0)))
                    .Append(",\"upgrades\":").Append(NamesJson(GetProp(ch, "UpgradeTargets"), 4))
                    .Append("}");
                written++;
            }
            b.Append("]");
            return b.ToString();
        }

        private static string InventoryJson(object roster, int max)
        {
            StringBuilder b = new StringBuilder();
            b.Append("[");
            if (roster == null) { b.Append("]"); return b.ToString(); }
            int count = IntValue(GetProp(roster, "Count"), 0);
            Type rt = roster.GetType();
            MethodInfo getItem = rt.GetMethod("GetItemAtIndex",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(int) }, null);
            MethodInfo getNum = rt.GetMethod("GetElementNumber",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(int) }, null);
            MethodInfo getCost = rt.GetMethod("GetElementUnitCost",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(int) }, null);
            int written = 0;
            for (int i = 0; i < count && written < max; i++)
            {
                object item = getItem == null ? null : getItem.Invoke(roster, new object[] { i });
                if (item == null) continue;
                int num = getNum == null ? 0 : IntValue(getNum.Invoke(roster, new object[] { i }), 0);
                int cost = getCost == null ? 0 : IntValue(getCost.Invoke(roster, new object[] { i }), 0);
                if (written > 0) b.Append(",");
                object cat = GetProp(item, "ItemCategory");
                b.Append("{\"id\":").Append(J(StringId(item)))
                    .Append(",\"name\":").Append(J(ReadName(item)))
                    .Append(",\"count\":").Append(num)
                    .Append(",\"unit_cost\":").Append(cost)
                    .Append(",\"base_value\":").Append(IntValue(GetProp(item, "Value"), 0))
                    .Append(",\"type\":").Append(J(Convert.ToString(GetProp(item, "ItemType"), CultureInfo.InvariantCulture)))
                    .Append(",\"category\":").Append(J(ReadName(cat)))
                    .Append("}");
                written++;
            }
            b.Append("]");
            return b.ToString();
        }

        private static string FactionNamesJson(object values, int max)
        {
            return NamesJson(values, max);
        }

        private static string ClanJson(object hero)
        {
            object clan = GetProp(hero, "Clan");
            if (clan == null) clan = GetStatic("TaleWorlds.CampaignSystem.Clan", "PlayerClan");
            if (clan == null) return "{}";
            object kingdom = GetProp(clan, "Kingdom");
            StringBuilder b = new StringBuilder();
            b.Append("{")
                .Append("\"name\":").Append(J(ReadName(clan))).Append(",")
                .Append("\"tier\":").Append(IntValue(GetProp(clan, "Tier"), 0)).Append(",")
                .Append("\"renown\":").Append(Num(DoubleValue(GetProp(clan, "Renown"), 0))).Append(",")
                .Append("\"renown_next_tier\":").Append(IntValue(GetProp(clan, "RenownRequirementForNextTier"), 0)).Append(",")
                .Append("\"influence\":").Append(Num(DoubleValue(GetProp(clan, "Influence"), 0))).Append(",")
                .Append("\"gold\":").Append(IntValue(GetProp(clan, "Gold"), 0)).Append(",")
                .Append("\"strength\":").Append(Num(DoubleValue(GetProp(clan, "CurrentTotalStrength"), 0))).Append(",")
                .Append("\"companion_limit\":").Append(IntValue(GetProp(clan, "CompanionLimit"), 0)).Append(",")
                .Append("\"war_party_limit\":").Append(IntValue(GetProp(clan, "WarPartyLimit"), 0)).Append(",")
                .Append("\"mercenary\":").Append(BoolValue(GetProp(clan, "IsUnderMercenaryService")) ? "true" : "false").Append(",")
                .Append("\"kingdom\":").Append(J(ReadName(kingdom))).Append(",")
                .Append("\"fiefs\":").Append(NamesJson(GetProp(clan, "Fiefs"), 30)).Append(",")
                .Append("\"companions\":").Append(NamesJson(GetProp(clan, "Companions"), 30)).Append(",")
                .Append("\"wars\":").Append(FactionNamesJson(GetProp(clan, "FactionsAtWarWith"), 30))
                .Append("}");
            return b.ToString();
        }

        private static string SettlementJson(object s)
        {
            if (s == null) return "{}";
            object party = GetProp(s, "Party");
            object town = GetProp(s, "Town");
            string kind = BoolValue(GetProp(s, "IsTown")) ? "town" :
                (BoolValue(GetProp(s, "IsCastle")) ? "castle" :
                (BoolValue(GetProp(s, "IsVillage")) ? "village" : "settlement"));
            StringBuilder b = new StringBuilder();
            b.Append("{")
                .Append("\"id\":").Append(J(StringId(s))).Append(",")
                .Append("\"name\":").Append(J(ReadName(s))).Append(",")
                .Append("\"kind\":").Append(J(kind)).Append(",")
                .Append("\"owner\":").Append(J(ReadName(GetProp(s, "OwnerClan")))).Append(",")
                .Append("\"militia\":").Append(Num(DoubleValue(GetProp(s, "Militia"), 0))).Append(",")
                .Append("\"garrison_or_party\":").Append(IntValue(GetProp(party, "NumberOfAllMembers"), 0)).Append(",")
                .Append("\"strength\":").Append(Num(DoubleValue(GetProp(party, "EstimatedStrength"), 0))).Append(",")
                .Append("\"market_items\":").Append(IntValue(GetProp(GetProp(s, "ItemRoster"), "Count"), 0)).Append(",")
                .Append("\"prosperity\":").Append(Num(DoubleValue(GetProp(town, "Prosperity"), 0))).Append(",")
                .Append("\"loyalty\":").Append(Num(DoubleValue(GetProp(town, "Loyalty"), 0))).Append(",")
                .Append("\"security\":").Append(Num(DoubleValue(GetProp(town, "Security"), 0)))
                .Append("}");
            return b.ToString();
        }

        private static object GetAnyProp(object obj, params string[] names)
        {
            if (obj == null || names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                object v = GetProp(obj, names[i]);
                if (v != null) return v;
            }
            return null;
        }

        private static object PartyBaseOf(object main)
        {
            return GetAnyProp(main, "Party", "PartyBase");
        }

        private static object ItemRosterOf(object main)
        {
            object r = GetProp(main, "ItemRoster");
            if (r != null) return r;
            return GetProp(PartyBaseOf(main), "ItemRoster");
        }

        private static string ArmyJson(object main)
        {
            object army = GetProp(main, "Army");
            if (army == null) return "{}";
            object leaderParty = GetAnyProp(army, "LeaderParty", "ArmyOwnerParty");
            StringBuilder b = new StringBuilder();
            b.Append("{")
                .Append("\"name\":").Append(J(ReadName(army))).Append(",")
                .Append("\"leader_party\":").Append(J(ReadName(leaderParty))).Append(",")
                .Append("\"leader\":").Append(J(LeaderName(leaderParty))).Append(",")
                .Append("\"cohesion\":").Append(Num(DoubleValue(GetProp(army, "Cohesion"), 0))).Append(",")
                .Append("\"parties\":").Append(EnumerableCount(GetAnyProp(army, "Parties", "PartiesInArmy")))
                .Append("}");
            return b.ToString();
        }

        private static string KingdomJson(object hero)
        {
            object clan = GetProp(hero, "Clan");
            if (clan == null) clan = GetStatic("TaleWorlds.CampaignSystem.Clan", "PlayerClan");
            object kingdom = GetProp(clan, "Kingdom");
            if (kingdom == null) return "{}";
            StringBuilder b = new StringBuilder();
            b.Append("{")
                .Append("\"name\":").Append(J(ReadName(kingdom))).Append(",")
                .Append("\"ruler\":").Append(J(ReadName(GetProp(kingdom, "Leader")))).Append(",")
                .Append("\"strength\":").Append(Num(DoubleValue(GetProp(kingdom, "CurrentTotalStrength"), 0))).Append(",")
                .Append("\"wars\":").Append(NamesJson(GetProp(kingdom, "FactionsAtWarWith"), 30)).Append(",")
                .Append("\"allies\":").Append(NamesJson(GetProp(kingdom, "AlliedKingdoms"), 30)).Append(",")
                .Append("\"armies\":").Append(EnumerableCount(GetProp(kingdom, "Armies"))).Append(",")
                .Append("\"fiefs\":").Append(NamesJson(GetProp(kingdom, "Fiefs"), 50))
                .Append("}");
            return b.ToString();
        }

        private static string QuestsJson(object campaign)
        {
            object qm = GetAnyProp(campaign, "QuestManager", "Quests");
            object quests = null;
            if (qm != null)
                quests = GetAnyProp(qm, "Quests", "AllQuests", "QuestList", "QuestsList");
            if (quests == null && qm is IEnumerable) quests = qm;
            if (quests == null)
                quests = GetStatic("TaleWorlds.CampaignSystem.QuestBase", "AllQuests");

            StringBuilder b = new StringBuilder();
            b.Append("[");
            IEnumerable e = AsEnumerable(quests);
            int n = 0;
            if (e != null)
            {
                foreach (object q in e)
                {
                    if (q == null) continue;
                    object ongoing = GetProp(q, "IsOngoing");
                    if (ongoing != null && !BoolValue(ongoing)) continue;
                    if (n > 0) b.Append(",");
                    b.Append("{\"id\":").Append(J(StringId(q)))
                        .Append(",\"title\":").Append(J(Convert.ToString(GetProp(q, "Title"), CultureInfo.InvariantCulture)))
                        .Append(",\"giver\":").Append(J(ReadName(GetProp(q, "QuestGiver"))))
                        .Append(",\"ongoing\":").Append(BoolValue(GetProp(q, "IsOngoing")) ? "true" : "false")
                        .Append(",\"finalized\":").Append(BoolValue(GetProp(q, "IsFinalized")) ? "true" : "false")
                        .Append("}");
                    n++;
                    if (n >= 30) break;
                }
            }
            b.Append("]");
            return b.ToString();
        }

        private static string BuildState()
        {
            object campaign = GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current");
            object hero = GetStatic("TaleWorlds.CampaignSystem.Hero", "MainHero");
            object main = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "MainParty");            object enc = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Current");
            object encParty = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "EncounteredParty");
            object battle = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Battle");
            object simulation = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "CurrentBattleSimulation");
            object mission = GetStatic("TaleWorlds.MountAndBlade.Mission", "Current");
            object saveHandler = GetProp(campaign, "SaveHandler");
            object activeGameState = GetActiveGameState();
            object partyBase = PartyBaseOf(main);
            object memberRoster = GetProp(main, "MemberRoster");
            object prisonRoster = GetProp(main, "PrisonRoster");
            object itemRoster = ItemRosterOf(main);
            object currentSettlement = GetProp(main, "CurrentSettlement");
            if (currentSettlement == null) currentSettlement = GetProp(hero, "CurrentSettlement");

            StringBuilder b = new StringBuilder();
            b.Append("{");
            b.Append("\"bridge_version\":\"0.3-runtime\",");
            b.Append("\"updated_utc\":").Append(J(DateTime.UtcNow.ToString("o"))).Append(",");
            b.Append("\"session_id\":").Append(J(_sessionId)).Append(",");
            b.Append("\"runtime\":{");
            b.Append("\"busy\":").Append(_pendingCommandId.Length > 0 ? "true" : "false").Append(",");
            b.Append("\"command_id\":").Append(J(_pendingCommandId)).Append(",");
            b.Append("\"verb\":").Append(J(_pendingVerb)).Append(",");
            b.Append("\"phase\":").Append(J(_pendingPhase)).Append(",");
            b.Append("\"active_game_state\":").Append(J(activeGameState == null ? "" : activeGameState.GetType().FullName)).Append(",");
            b.Append("\"save_busy\":").Append(BoolValue(GetProp(saveHandler, "IsSaving")) ? "true" : "false").Append(",");
            b.Append("\"active_save_slot\":").Append(J(Convert.ToString(GetStatic("TaleWorlds.Core.MBSaveLoad", "ActiveSaveSlotName"), CultureInfo.InvariantCulture)));
            b.Append("},");
            b.Append("\"campaign_loaded\":").Append(campaign != null ? "true" : "false").Append(",");
            b.Append("\"time_mode\":").Append(J(Convert.ToString(GetProp(campaign, "TimeControlMode"), CultureInfo.InvariantCulture))).Append(",");
            b.Append("\"mission_active\":").Append(mission != null ? "true" : "false").Append(",");
            b.Append("\"hero\":{");
            b.Append("\"name\":").Append(J(ReadName(hero))).Append(",");
            b.Append("\"gold\":").Append(IntValue(GetProp(hero, "Gold"), 0)).Append(",");
            b.Append("\"age\":").Append(Num(DoubleValue(GetProp(hero, "Age"), 0))).Append(",");
            b.Append("\"hit_points\":").Append(IntValue(GetProp(hero, "HitPoints"), 0)).Append(",");
            b.Append("\"max_hit_points\":").Append(IntValue(GetProp(hero, "MaxHitPoints"), 0)).Append(",");
            b.Append("\"wounded\":").Append(BoolValue(GetProp(hero, "IsWounded")) ? "true" : "false").Append(",");
            b.Append("\"spouse\":").Append(J(ReadName(GetProp(hero, "Spouse"))));
            b.Append("},");

            b.Append("\"party\":{");
            b.Append("\"name\":").Append(J(ReadName(main))).Append(",");
            b.Append("\"members\":").Append(RosterCount(main, "MemberRoster")).Append(",");
            b.Append("\"healthy\":").Append(HealthyCount(main)).Append(",");
            b.Append("\"prisoners\":").Append(RosterCount(main, "PrisonRoster")).Append(",");
            b.Append("\"wounded\":").Append(IntValue(GetProp(memberRoster, "TotalWounded"), 0)).Append(",");
            b.Append("\"party_limit\":").Append(IntValue(GetAnyProp(main, "PartySizeLimit", "PartySizeLimitExplained"), 0)).Append(",");
            b.Append("\"prisoner_limit\":").Append(IntValue(GetAnyProp(main, "PrisonerSizeLimit", "PrisonerSizeLimitExplained"), 0)).Append(",");
            b.Append("\"speed\":").Append(Num(DoubleValue(GetProp(main, "Speed"), 0))).Append(",");
            b.Append("\"morale\":").Append(Num(DoubleValue(GetProp(main, "Morale"), 0))).Append(",");
            b.Append("\"food\":").Append(Num(DoubleValue(GetProp(main, "Food"), 0))).Append(",");
            b.Append("\"wage\":").Append(IntValue(GetProp(main, "TotalWage"), 0)).Append(",");
            b.Append("\"faction\":").Append(J(FactionName(main))).Append(",");
            b.Append("\"current_settlement\":").Append(J(ReadName(GetProp(main, "CurrentSettlement")))).Append(",");
            b.Append("\"target_settlement\":").Append(J(ReadName(GetProp(main, "TargetSettlement"))));
            b.Append("},");
            b.Append("\"troops\":").Append(RosterJson(memberRoster, 100)).Append(",");
            b.Append("\"prisoner_roster\":").Append(RosterJson(prisonRoster, 100)).Append(",");
            b.Append("\"inventory_summary\":{")
                .Append("\"stacks\":").Append(IntValue(GetProp(itemRoster, "Count"), 0)).Append(",")
                .Append("\"total_value\":").Append(IntValue(GetProp(itemRoster, "TotalValue"), 0)).Append(",")
                .Append("\"food_units\":").Append(IntValue(GetProp(itemRoster, "TotalFood"), 0)).Append(",")
                .Append("\"food_variety\":").Append(IntValue(GetProp(itemRoster, "FoodVariety"), 0)).Append(",")
                .Append("\"mounts\":").Append(IntValue(GetProp(itemRoster, "NumberOfMounts"), 0)).Append(",")
                .Append("\"pack_animals\":").Append(IntValue(GetProp(itemRoster, "NumberOfPackAnimals"), 0))
                .Append("},");
            b.Append("\"inventory\":").Append(InventoryJson(itemRoster, 120)).Append(",");
            b.Append("\"clan\":").Append(ClanJson(hero)).Append(",");
            b.Append("\"kingdom\":").Append(KingdomJson(hero)).Append(",");
            b.Append("\"army\":").Append(ArmyJson(main)).Append(",");
            b.Append("\"current_settlement_detail\":").Append(SettlementJson(currentSettlement)).Append(",");
            b.Append("\"quests\":").Append(QuestsJson(campaign)).Append(",");
            b.Append("\"encounter\":{");
            b.Append("\"active\":").Append(enc != null ? "true" : "false").Append(",");
            b.Append("\"battle_ready\":").Append(battle != null ? "true" : "false").Append(",");
            b.Append("\"state\":").Append(J(Convert.ToString(GetProp(enc, "EncounterState"), CultureInfo.InvariantCulture))).Append(",");
            b.Append("\"simulation_active\":").Append(simulation != null ? "true" : "false").Append(",");
            b.Append("\"simulation_finished\":").Append(BoolValue(GetProp(simulation, "IsSimulationFinished")) ? "true" : "false").Append(",");
            b.Append("\"party\":").Append(J(ReadName(encParty))).Append(",");
            b.Append("\"party_size\":").Append(RosterCount(GetProp(encParty, "MobileParty"), "MemberRoster"));
            b.Append("},");

            List<NearbyEntry> parties = main == null ? new List<NearbyEntry>() : NearbyParties(main);
            b.Append("\"nearby_parties\":[");
            for (int i = 0; i < parties.Count; i++)
            {
                if (i > 0) b.Append(",");
                NearbyEntry n = parties[i];
                b.Append("{\"name\":").Append(J(n.Name))
                    .Append(",\"id\":").Append(J(Convert.ToString(GetProp(n.Obj, "StringId"), CultureInfo.InvariantCulture)))
                    .Append(",\"leader\":").Append(J(LeaderName(n.Obj)))
                    .Append(",\"kind\":").Append(J(n.Kind))
                    .Append(",\"count\":").Append(n.Count)
                    .Append(",\"wounded\":").Append(IntValue(GetProp(GetProp(n.Obj, "MemberRoster"), "TotalWounded"), 0))
                    .Append(",\"speed\":").Append(Num(DoubleValue(GetProp(n.Obj, "Speed"), 0)))
                    .Append(",\"strength\":").Append(Num(DoubleValue(GetProp(GetAnyProp(n.Obj, "Party", "PartyBase"), "EstimatedStrength"), 0)))
                    .Append(",\"distance\":").Append(Num(n.Dist))
                    .Append(",\"faction\":").Append(J(FactionName(n.Obj)))
                    .Append("}");
            }
            b.Append("],");

            List<NearbyEntry> settlements = main == null ? new List<NearbyEntry>() : NearbySettlements(main);
            b.Append("\"nearby_settlements\":[");
            for (int i = 0; i < settlements.Count; i++)
            {                if (i > 0) b.Append(",");
                NearbyEntry n = settlements[i];
                object owner = GetProp(n.Obj, "OwnerClan");
                b.Append("{\"name\":").Append(J(n.Name))
                    .Append(",\"id\":").Append(J(Convert.ToString(GetProp(n.Obj, "StringId"), CultureInfo.InvariantCulture)))
                    .Append(",\"kind\":").Append(J(n.Kind))
                    .Append(",\"distance\":").Append(Num(n.Dist))
                    .Append(",\"owner\":").Append(J(ReadName(owner)))
                    .Append(",\"detail\":").Append(SettlementJson(n.Obj))
                    .Append("}");
            }
            b.Append("]");
            b.Append("}");
            return b.ToString();
        }

        private static object FindSettlement(string query)
        {
            object all = GetStatic("TaleWorlds.CampaignSystem.Settlements.Settlement", "All");
            IEnumerable e = AsEnumerable(all);
            if (e == null) return null;
            object contains = null;
            foreach (object s in e)
            {
                string name = ReadName(s);
                string id = Convert.ToString(GetProp(s, "StringId"), CultureInfo.InvariantCulture);
                if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, query, StringComparison.OrdinalIgnoreCase))
                    return s;
                if (contains == null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    contains = s;
            }
            return contains;
        }
        private static object FindParty(string query)
        {
            object main = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "MainParty");
            object all = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "All");
            IEnumerable e = AsEnumerable(all);
            if (e == null) return null;

            object exact = null;
            object partial = null;
            double exactDistance = double.MaxValue;
            double partialDistance = double.MaxValue;

            foreach (object p in e)
            {
                if (object.ReferenceEquals(p, main)) continue;
                string name = ReadName(p);
                string leader = LeaderName(p);
                string id = Convert.ToString(GetProp(p, "StringId"), CultureInfo.InvariantCulture);

                if (!string.IsNullOrEmpty(id) &&
                    string.Equals(id, query, StringComparison.OrdinalIgnoreCase))
                    return p;
                if (!string.IsNullOrEmpty(leader) &&
                    string.Equals(leader, query, StringComparison.OrdinalIgnoreCase))
                    return p;

                double d = Distance(main, p);
                if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
                {
                    if (d < exactDistance)
                    {
                        exact = p;
                        exactDistance = d;
                    }
                }
                else if ((name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                          leader.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) &&
                         d < partialDistance)
                {
                    partial = p;
                    partialDistance = d;
                }
            }
            return exact ?? partial;
        }

        private static object InvokeByName(object target, string methodName, object specialArg)
        {
            if (target == null) throw new InvalidOperationException("Target is null.");
            MethodInfo[] methods = target.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != methodName) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 1) continue;
                if (specialArg != null && !ps[0].ParameterType.IsInstanceOfType(specialArg))
                    continue;
                object[] args = new object[ps.Length];
                args[0] = specialArg;
                bool usable = true;
                for (int j = 1; j < ps.Length; j++)
                {
                    Type pt = ps[j].ParameterType;
                    if (pt.IsEnum)
                        args[j] = Enum.ToObject(pt, 1);
                    else if (pt == typeof(bool))
                        args[j] = false;
                    else if (ps[j].IsOptional)
                        args[j] = ps[j].DefaultValue;
                    else
                    {
                        usable = false;
                        break;
                    }
                }
                if (usable)
                    return m.Invoke(target, args);
            }
            throw new MissingMethodException(target.GetType().FullName, methodName);
        }
        private static object InvokeStaticOneArg(string typeName, string methodName, object arg)
        {
            Type t = FindType(typeName);
            if (t == null) throw new TypeLoadException(typeName);
            MethodInfo[] methods = t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != methodName) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1)
                    return m.Invoke(null, new object[] { arg });
            }
            throw new MissingMethodException(typeName, methodName);
        }

        private static void HoldMainParty()
        {
            object main = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "MainParty");
            if (main == null) throw new InvalidOperationException("No campaign party.");
            MethodInfo m = main.GetType().GetMethod("SetMoveModeHold",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (m == null) throw new MissingMethodException("SetMoveModeHold");
            m.Invoke(main, null);
        }
        private static string DoTravel(string destination)
        {
            object main = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "MainParty");
            object s = FindSettlement(destination);
            if (main == null) throw new InvalidOperationException("No campaign party.");
            if (s == null) throw new InvalidOperationException("Settlement not found: " + destination);
            InvokeByName(main, "SetMoveGoToSettlement", s);
            return "Travelling to " + ReadName(s) + ".";
        }

        private static string DoEngage(string target)
        {
            object main = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "MainParty");
            object p = FindParty(target);
            if (main == null) throw new InvalidOperationException("No campaign party.");
            if (p == null) throw new InvalidOperationException("Party not found: " + target);
            InvokeByName(main, "SetMoveEngageParty", p);
            return "Engaging " + ReadName(p) + ".";
        }

        private static string DoFollow(string target)
        {
            object main = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "MainParty");
            object p = FindParty(target);
            if (main == null) throw new InvalidOperationException("No campaign party.");
            if (p == null) throw new InvalidOperationException("Party not found: " + target);
            InvokeByName(main, "SetMoveEscortParty", p);
            return "Following " + ReadName(p) + ".";
        }

        private static object InvokeNoArg(object target, string methodName)
        {
            if (target == null) throw new InvalidOperationException("Target is null.");
            MethodInfo m = target.GetType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (m == null) throw new MissingMethodException(target.GetType().FullName, methodName);
            return m.Invoke(target, null);
        }

        private static object BuildMenuCallbackArgs(object campaign)
        {
            object menuContext = GetProp(campaign, "CurrentMenuContext");
            Type argsType = FindType("TaleWorlds.CampaignSystem.GameMenus.MenuCallbackArgs");
            Type textType = FindType("TaleWorlds.Localization.TextObject");
            if (argsType == null || textType == null)
                throw new TypeLoadException("MenuCallbackArgs/TextObject");
            if (menuContext == null)
                throw new InvalidOperationException("Game menu context is not available.");
            object textObject = Activator.CreateInstance(textType);
            ConstructorInfo[] cs = argsType.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < cs.Length; i++)
            {
                ParameterInfo[] ps = cs[i].GetParameters();
                if (ps.Length == 2 &&
                    ps[0].ParameterType.IsInstanceOfType(menuContext) &&
                    ps[1].ParameterType.IsInstanceOfType(textObject))
                    return cs[i].Invoke(new object[] { menuContext, textObject });
            }
            throw new MissingMethodException("MenuCallbackArgs(MenuContext, TextObject)");
        }

        private static string DoEnterSettlement(string name)
        {
            object main = GetStatic("TaleWorlds.CampaignSystem.Party.MobileParty", "MainParty");
            if (main == null) throw new InvalidOperationException("No campaign party.");
            object requested = string.IsNullOrWhiteSpace(name) ? null : FindSettlement(name);
            object current = GetProp(main, "CurrentSettlement");
            if (current != null && (requested == null || object.ReferenceEquals(current, requested)))
                return "Already in " + ReadName(current) + ".";
            object enc = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Current");
            object encSettlement = GetProp(enc, "EncounterSettlement");
            if (enc != null && encSettlement != null &&
                (requested == null || object.ReferenceEquals(encSettlement, requested)))
            {
                InvokeNoArg(enc, "EnterSettlement");
                return "Entered " + ReadName(encSettlement) + ".";
            }
            if (requested == null)
                throw new InvalidOperationException("No settlement encounter is available.");
            InvokeByName(main, "SetMoveGoToSettlement", requested);
            return "Travelling to " + ReadName(requested) + " for entry.";
        }

        private static string DoLeaveSettlement()
        {
            object enc = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Current");
            if (enc == null) throw new InvalidOperationException("No active settlement encounter.");
            InvokeNoArg(enc, "LeaveSettlement");
            return "Left settlement.";
        }

        private static object InvokeStaticNoArg(string typeName, string methodName)
        {
            Type t = FindType(typeName);
            if (t == null) throw new TypeLoadException(typeName);
            MethodInfo m = t.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            if (m == null) throw new MissingMethodException(typeName, methodName);
            return m.Invoke(null, null);
        }

        private static object InvokeOneString(object target, string methodName, string arg)
        {
            if (target == null) throw new InvalidOperationException("Target is null.");
            MethodInfo m = target.GetType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(string) }, null);
            if (m == null) throw new MissingMethodException(target.GetType().FullName, methodName);
            return m.Invoke(target, new object[] { arg });
        }

        private static string DoSurrender()
        {
            object enc = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Current");
            if (enc == null) throw new InvalidOperationException("No active player encounter.");
            Type t = FindType("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter");
            PropertyInfo p = t == null ? null : t.GetProperty("PlayerSurrender",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (p == null) throw new MissingMemberException("PlayerEncounter.PlayerSurrender");
            p.SetValue(null, true, null);
            InvokeStaticNoArg("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Update");
            return "Surrendered encounter through PlayerEncounter.PlayerSurrender + Update.";
        }

        private static string DoRetreat()
        {
            object enc = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Current");
            if (enc == null) throw new InvalidOperationException("No active player encounter.");
            object sim = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter",
                "CurrentBattleSimulation");
            if (sim != null)
            {
                object active = GetActiveGameState();
                if (active != null && active.GetType().Name == "MapState")
                    InvokeNoArg(active, "EndBattleSimulation");
                InvokeNoArg(sim, "OnPlayerRetreat");
                return "Retreated from simulated battle through vanilla simulation lifecycle.";
            }
            InvokeStaticNoArg("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "LeaveBattle");
            return "Left battle.";
        }

        private static string DoTimeSpeed(int speed)
        {
            if (speed != 0 && speed != 3 && speed != 4)
                throw new ArgumentOutOfRangeException("speed",
                    "Only safe time modes 0 (Stop), 3 (StoppablePlay), and 4 (StoppableFastForward) are allowed.");
            object campaign = GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current");
            if (campaign == null)
                throw new InvalidOperationException("No campaign loaded.");
            MethodInfo m = campaign.GetType().GetMethod("SetTimeSpeed",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(int) }, null);
            if (m == null)
                throw new MissingMethodException("Campaign.SetTimeSpeed");
            m.Invoke(campaign, new object[] { speed });
            return "Safe time mode set to " + speed.ToString(CultureInfo.InvariantCulture) + ".";
        }

        private static object FindLoadedSubModule(string fullTypeName)
        {
            object module = GetStatic("TaleWorlds.MountAndBlade.Module", "CurrentModule");
            if (module == null)
                throw new InvalidOperationException("Bannerlord module registry is unavailable.");
            MethodInfo collect = module.GetType().GetMethod("CollectSubModules",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (collect == null)
                throw new MissingMethodException("Module.CollectSubModules");
            IEnumerable items = AsEnumerable(collect.Invoke(module, null));
            if (items != null)
            {
                foreach (object item in items)
                {
                    if (item != null && item.GetType().FullName == fullTypeName)
                        return item;
                }
            }
            return null;
        }

        private static object GetActiveGameState()
        {
            object game = GetStatic("TaleWorlds.Core.Game", "Current");
            object manager = GetProp(game, "GameStateManager");
            return GetProp(manager, "ActiveState");
        }

        private static bool IsPostBattleDecisionPending()
        {
            object enc = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Current");
            if (enc == null) return false;
            string state = Convert.ToString(GetProp(enc, "EncounterState"),
                CultureInfo.InvariantCulture) ?? "";
            return state.Length > 0 && state != "Begin" && state != "Wait";
        }

        private static string MetaValue(object meta, string key)
        {
            if (meta == null) return "";
            try
            {
                PropertyInfo p = meta.GetType().GetProperty("Item",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, typeof(string), new Type[] { typeof(string) }, null);
                if (p == null) return "";
                object value = p.GetValue(meta, new object[] { key });
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
            catch { return ""; }
        }

        private static object[] GetSaveFiles()
        {
            Type t = FindType("TaleWorlds.Core.MBSaveLoad");
            if (t == null) throw new TypeLoadException("TaleWorlds.Core.MBSaveLoad");
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "GetSaveFiles") continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                object result = null;
                if (ps.Length == 0)
                    result = ms[i].Invoke(null, null);
                else if (ps.Length == 1)
                    result = ms[i].Invoke(null, new object[] { null });
                else
                    continue;
                IEnumerable e = AsEnumerable(result);
                if (e == null) return new object[0];
                List<object> values = new List<object>();
                foreach (object x in e) values.Add(x);
                return values.ToArray();
            }
            throw new MissingMethodException("MBSaveLoad.GetSaveFiles");
        }

        private static string DoListSaves()
        {
            object[] files = GetSaveFiles();
            StringBuilder b = new StringBuilder();
            b.Append("[");
            for (int i = 0; i < files.Length; i++)
            {
                if (i > 0) b.Append(",");
                object info = files[i];
                object meta = GetProp(info, "MetaData");
                b.Append("{\"name\":").Append(J(Convert.ToString(GetProp(info, "Name"),
                        CultureInfo.InvariantCulture)))
                    .Append(",\"corrupted\":").Append(BoolValue(GetProp(info, "IsCorrupted")) ? "true" : "false")
                    .Append(",\"creation_time\":").Append(J(MetaValue(meta, "CreationTime")))
                    .Append(",\"character_name\":").Append(J(MetaValue(meta, "CharacterName")))
                    .Append(",\"game_version\":").Append(J(MetaValue(meta, "NewGameVersion")))
                    .Append(",\"unique_game_id\":").Append(J(MetaValue(meta, "UniqueGameId")))
                    .Append("}");
            }
            b.Append("]");
            return b.ToString();
        }

        private static string NormalizeSaveName(string saveName)
        {
            string name = (saveName ?? "").Trim();
            if (name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return name;
        }

        private static object FindSandboxViewSubModule()
        {
            Type t = FindType("SandBox.View.SandBoxViewSubModule");
            if (t != null)
            {
                FieldInfo f = t.GetField("_instance",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null)
                {
                    object value = f.GetValue(null);
                    if (value != null) return value;
                }
            }
            return FindLoadedSubModule("SandBox.View.SandBoxViewSubModule");
        }

        private static string DoLoadSave(string saveName)
        {
            if (GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current") != null)
                throw new InvalidOperationException("LOAD_REQUIRES_MAIN_MENU");
            string name = NormalizeSaveName(saveName);
            if (name.Length == 0)
                throw new ArgumentException("load_save requires a save name.");

            object instance = FindSandboxViewSubModule();
            if (instance == null)
                throw new InvalidOperationException("Loaded SandBoxViewSubModule instance not found.");
            MethodInfo m = instance.GetType().GetMethod("ContinueCampaign",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(string) }, null);
            if (m == null)
                throw new MissingMethodException(instance.GetType().FullName, "ContinueCampaign");
            m.Invoke(instance, new object[] { name });
            return name;
        }

        private static string LatestSaveName()
        {
            object[] files = GetSaveFiles();
            if (files.Length == 0)
                throw new InvalidOperationException("NO_SAVE_FILES");
            return Convert.ToString(GetProp(files[0], "Name"), CultureInfo.InvariantCulture) ?? "";
        }

        private static void StartSaveRequest(string mode, string arg)
        {
            object campaign = GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current");
            object handler = GetProp(campaign, "SaveHandler");
            if (handler == null)
                throw new InvalidOperationException("NOT_IN_CAMPAIGN");
            if (BoolValue(GetProp(handler, "IsSaving")))
                throw new InvalidOperationException("SAVE_IN_PROGRESS");

            if (mode == "save_as")
            {
                string name = NormalizeSaveName(arg);
                if (name.Length == 0)
                    throw new ArgumentException("save_as requires a save name.");
                InvokeOneString(handler, "SaveAs", name);
            }
            else
            {
                InvokeNoArg(handler, "QuickSaveCurrentGame");
            }
        }

        private static string DoGracefulExit()
        {
            object module = GetStatic("TaleWorlds.MountAndBlade.Module", "CurrentModule");
            if (module == null)
                throw new InvalidOperationException("RUNTIME_NOT_READY");
            MethodInfo m = module.GetType().GetMethod("ShutDownWithDelay",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(string), typeof(int) }, null);
            if (m == null)
                throw new MissingMethodException(module.GetType().FullName, "ShutDownWithDelay");
            m.Invoke(module, new object[] { "Bannerlord Strategic Bridge requested graceful exit", 1 });
            return "Graceful shutdown requested.";
        }

        private static string StartAutoResolve()
        {
            object enc = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Current");
            object battle = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter", "Battle");
            if (enc == null || battle == null)
                throw new InvalidOperationException("NO_ACTIVE_ENCOUNTER");
            object campaign = GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current");
            object callbackArgs = BuildMenuCallbackArgs(campaign);
            InvokeStaticOneArg("Helpers.MenuHelper",
                "EncounterOrderAttackConsequence", callbackArgs);
            return "Send Troops simulation started.";
        }

        private static string InspectType(string typeName)
        {
            Type t = FindType(typeName);
            if (t == null) throw new TypeLoadException(typeName);
            StringBuilder b = new StringBuilder();
            b.AppendLine(t.AssemblyQualifiedName);
            b.AppendLine("PROPERTIES");
            PropertyInfo[] props = t.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            for (int i = 0; i < props.Length; i++)
                b.AppendLine(props[i].PropertyType.FullName + " " + props[i].Name);
            b.AppendLine("METHODS");
            MethodInfo[] methods = t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
                b.AppendLine(methods[i].ToString());
            string path = Root + @"\inspect.txt";
            AtomicWriteAllText(path, b.ToString());
            return "Type dump written to " + path;
        }

        private void WriteResponse(string id, bool ok, string status,
            string message, string errorCode)
        {
            string json = "{\"id\":" + J(id) +
                ",\"ok\":" + (ok ? "true" : "false") +
                ",\"status\":" + J(status) +
                ",\"message\":" + J(message) +
                ",\"error_code\":" + J(errorCode ?? "") +
                ",\"session_id\":" + J(_sessionId) +
                ",\"updated_utc\":" + J(DateTime.UtcNow.ToString("o")) + "}";
            AtomicWriteAllText(ResponsePath, json);
            AtomicWriteAllText(ResponseArchivePath(id), json);
        }

        private void BeginPending(string id, string verb, string arg, string raw, string phase)
        {
            _pendingCommandId = id;
            _pendingVerb = verb;
            _pendingArg = arg;
            _pendingRaw = raw;
            _pendingPhase = phase;
            _pendingStartedUtc = DateTime.UtcNow;
            _pendingSawBusy = false;
            _pendingSkipIssued = false;
            _pendingSimulationReleased = false;
            WriteResponse(id, true, "running", phase, "");
        }

        private void FinishPending(bool ok, string message, string errorCode)
        {
            string id = _pendingCommandId;
            string raw = _pendingRaw;
            string verb = _pendingVerb;
            WriteResponse(id, ok, ok ? "completed" : "error", message, errorCode);
            WriteJournal(id, ok ? "done" : "failed", raw);
            Log((ok ? "OK " : "FAIL ") + verb + " -> " + message);
            _lastCommandId = id;
            _pendingCommandId = "";
            _pendingVerb = "";
            _pendingArg = "";
            _pendingRaw = "";
            _pendingPhase = "";
            _pendingStartedUtc = DateTime.MinValue;
            _pendingSawBusy = false;
            _pendingSkipIssued = false;
            _pendingSimulationReleased = false;
        }

        private void AdvancePendingOperation()
        {
            if (_pendingCommandId.Length == 0)
                return;
            if ((DateTime.UtcNow - _pendingStartedUtc).TotalSeconds > 90.0)
            {
                FinishPending(false, "Pending operation timed out.", "RUNTIME_TIMEOUT");
                return;
            }

            if (_pendingVerb == "save" || _pendingVerb == "quicksave" ||
                _pendingVerb == "save_as")
            {
                object campaign = GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current");
                object handler = GetProp(campaign, "SaveHandler");
                if (handler == null)
                {
                    FinishPending(false, "Campaign disappeared while saving.", "NOT_IN_CAMPAIGN");
                    return;
                }
                bool busy = BoolValue(GetProp(handler, "IsSaving"));
                if (busy)
                {
                    _pendingSawBusy = true;
                    _pendingPhase = "saving";
                    return;
                }
                if (_pendingSawBusy)
                {
                    string slot = Convert.ToString(GetStatic("TaleWorlds.Core.MBSaveLoad",
                        "ActiveSaveSlotName"), CultureInfo.InvariantCulture) ?? "";
                    if (_pendingVerb == "save_as" &&
                        !string.Equals(slot, NormalizeSaveName(_pendingArg),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        FinishPending(false, "Save completed but active slot did not match request.",
                            "SAVE_VERIFY_FAILED");
                        return;
                    }
                    FinishPending(true, "Save completed. active_slot=" + slot, "");
                }
                return;
            }

            if (_pendingVerb == "load_save" || _pendingVerb == "continue_latest")
            {
                object campaign = GetStatic("TaleWorlds.CampaignSystem.Campaign", "Current");
                string slot = Convert.ToString(GetStatic("TaleWorlds.Core.MBSaveLoad",
                    "ActiveSaveSlotName"), CultureInfo.InvariantCulture) ?? "";
                object active = GetActiveGameState();
                if (campaign != null &&
                    string.Equals(slot, NormalizeSaveName(_pendingArg),
                        StringComparison.OrdinalIgnoreCase) &&
                    active != null && active.GetType().Name == "MapState")
                {
                    FinishPending(true, "Loaded save " + slot + ".", "");
                }
                else
                {
                    _pendingPhase = "loading";
                }
                return;
            }

            if (_pendingVerb == "autoresolve" || _pendingVerb == "send_troops")
            {
                object sim = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter",
                    "CurrentBattleSimulation");
                if (!_pendingSimulationReleased)
                {
                    if (sim == null)
                    {
                        _pendingPhase = "simulation_starting";
                        return;
                    }
                    if (!_pendingSkipIssued)
                    {
                        InvokeNoArg(sim, "Skip");
                        _pendingSkipIssued = true;
                        _pendingPhase = "simulation_running";
                        return;
                    }
                    if (!BoolValue(GetProp(sim, "IsSimulationFinished")))
                    {
                        _pendingPhase = "simulation_running";
                        return;
                    }

                    object active = GetActiveGameState();
                    if (active != null && active.GetType().Name == "MapState")
                        InvokeNoArg(active, "EndBattleSimulation");
                    InvokeNoArg(sim, "OnFinished");
                    _pendingSimulationReleased = true;
                    _pendingPhase = "results_pending";
                    return;
                }

                object enc = GetStatic("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter",
                    "Current");
                if (enc == null)
                {
                    FinishPending(true, "Send Troops completed and encounter finalized.", "");
                    return;
                }

                string encounterState = Convert.ToString(GetProp(enc, "EncounterState"),
                    CultureInfo.InvariantCulture) ?? "";
                if (encounterState == "Wait" || encounterState == "PrepareResults" ||
                    encounterState == "ApplyResults")
                {
                    InvokeStaticNoArg("TaleWorlds.CampaignSystem.Encounters.PlayerEncounter",
                        "Update");
                    _pendingPhase = "results_pending";
                    return;
                }

                _pendingPhase = "post_battle:" + encounterState;
                FinishPending(true,
                    "Send Troops simulation and core result transition completed; post-battle state=" +
                    encounterState + ".", "");
                return;
            }
        }

        private void ReplayArchivedResponse(string id)
        {
            string archive = ResponseArchivePath(id);
            string json = TryReadShared(archive);
            if (!string.IsNullOrWhiteSpace(json))
                AtomicWriteAllText(ResponsePath, json);
            else
                WriteResponse(id, false, "error",
                    "Prior command finalized but archived response is unavailable.",
                    "RECOVERY_UNCERTAIN");
        }

        private void ProcessCommand()
        {
            string raw = TryReadShared(CommandPath);
            if (raw == null)
                return;
            raw = raw.Trim();
            if (raw.Length == 0)
                return;
            string[] parts = raw.Split(new char[] { '|' }, 3);
            if (parts.Length < 2)
                return;
            string id = parts[0].Trim();
            if (id.Length == 0 || id == _lastCommandId)
                return;
            string verb = parts[1].Trim().ToLowerInvariant();
            string arg = parts.Length > 2 ? parts[2].Trim() : "";

            if (_pendingCommandId == id)
                return;

            string[] journal = ReadJournal(id);
            if (journal != null && journal.Length >= 3 && journal[0] == id)
            {
                if (journal[2] != PayloadKey(raw))
                {
                    WriteResponse(id, false, "error",
                        "Command id was reused with different payload.",
                        "COMMAND_ID_CONFLICT");
                    _lastCommandId = id;
                    return;
                }
                if (journal[1] == "done" || journal[1] == "failed")
                {
                    ReplayArchivedResponse(id);
                    _lastCommandId = id;
                    return;
                }
                if (journal[1] == "started" && _pendingCommandId != id)
                {
                    WriteResponse(id, false, "error",
                        "Command was interrupted before a final result; refusing automatic replay.",
                        "RECOVERY_UNCERTAIN");
                    WriteJournal(id, "failed", raw);
                    _lastCommandId = id;
                    return;
                }
            }

            if (_pendingCommandId.Length > 0 && _pendingCommandId != id)
            {
                WriteResponse(id, false, "error",
                    "Runtime is busy with " + _pendingVerb + " (" + _pendingPhase + ").",
                    "RUNTIME_BUSY");
                _lastCommandId = id;
                return;
            }

            WriteJournal(id, "started", raw);
            try
            {
                string message = "";
                bool asynchronous = false;
                bool readOnly = verb == "status" || verb == "list_saves" ||
                    verb == "saves" || verb == "inspect";
                if (!readOnly && IsPostBattleDecisionPending())
                    throw new InvalidOperationException("POST_BATTLE_DECISION_PENDING");

                if (verb == "status")
                    message = "State refreshed.";
                else if (verb == "hold")
                {
                    HoldMainParty();
                    message = "Main party ordered to hold.";
                }
                else if (verb == "travel")
                    message = DoTravel(arg);
                else if (verb == "enter_settlement")
                    message = DoEnterSettlement(arg);
                else if (verb == "leave_settlement")
                    message = DoLeaveSettlement();
                else if (verb == "engage")
                    message = DoEngage(arg);
                else if (verb == "pause")
                    message = DoTimeSpeed(0);
                else if (verb == "resume" || verb == "normal")
                    message = DoTimeSpeed(3);
                else if (verb == "fast" || verb == "fastest")
                    message = DoTimeSpeed(4);
                else if (verb == "time")
                {
                    int speed;
                    if (!int.TryParse(arg, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out speed))
                        throw new ArgumentException("time requires an integer speed.");
                    message = DoTimeSpeed(speed);
                }
                else if (verb == "list_saves" || verb == "saves")
                    message = DoListSaves();
                else if (verb == "save" || verb == "quicksave" || verb == "save_as")
                {
                    StartSaveRequest(verb == "save_as" ? "save_as" : "quicksave", arg);
                    BeginPending(id, verb, arg, raw, "save_queued");
                    asynchronous = true;
                }
                else if (verb == "load_save")
                {
                    string name = DoLoadSave(arg);
                    BeginPending(id, verb, name, raw, "load_requested");
                    asynchronous = true;
                }
                else if (verb == "continue_latest")
                {
                    string name = LatestSaveName();
                    name = DoLoadSave(name);
                    BeginPending(id, verb, name, raw, "load_requested");
                    asynchronous = true;
                }
                else if (verb == "autoresolve" || verb == "send_troops")
                {
                    StartAutoResolve();
                    BeginPending(id, verb, arg, raw, "simulation_starting");
                    asynchronous = true;
                }
                else if (verb == "attack")
                    throw new InvalidOperationException("REALTIME_COMBAT_DISABLED");
                else if (verb == "surrender")
                    message = DoSurrender();
                else if (verb == "retreat")
                    message = DoRetreat();
                else if (verb == "exit" || verb == "quit")
                    message = DoGracefulExit();
                else if (verb == "inspect")
                    message = InspectType(arg);
                else
                    throw new InvalidOperationException("Unknown command: " + verb);

                if (!asynchronous)
                {
                    WriteResponse(id, true, "completed", message, "");
                    WriteJournal(id, "done", raw);
                    _lastCommandId = id;
                    Log("OK " + raw + " -> " + message);
                }
            }
            catch (Exception ex)
            {
                string msg = ex.GetType().Name + ": " + ex.Message;
                if (ex.InnerException != null)
                    msg += " | " + ex.InnerException.GetType().Name + ": " +
                        ex.InnerException.Message;
                string code = "COMMAND_FAILED";
                if (ex.Message == "REALTIME_COMBAT_DISABLED")
                    code = "REALTIME_COMBAT_DISABLED";
                else if (ex.Message == "LOAD_REQUIRES_MAIN_MENU")
                    code = "LOAD_REQUIRES_MAIN_MENU";
                else if (ex.Message == "NO_ACTIVE_ENCOUNTER")
                    code = "NO_ACTIVE_ENCOUNTER";
                else if (ex.Message == "SAVE_IN_PROGRESS")
                    code = "SAVE_IN_PROGRESS";
                else if (ex.Message == "NOT_IN_CAMPAIGN")
                    code = "NOT_IN_CAMPAIGN";
                else if (ex.Message == "POST_BATTLE_DECISION_PENDING")
                    code = "POST_BATTLE_DECISION_PENDING";
                WriteResponse(id, false, "error", msg, code);
                WriteJournal(id, "failed", raw);
                _lastCommandId = id;
                Log("FAIL " + raw + " -> " + ex);
            }
        }

    }
}
