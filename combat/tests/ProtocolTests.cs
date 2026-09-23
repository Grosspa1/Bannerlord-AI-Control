using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using BannerlordCombatBridge;

public static class ProtocolTests
{
    private static int checks;
    private const long Now = 100000;
    private const string ValidJson = "{\"session\":\"session-a\",\"mission\":\"mission-a\",\"kind\":\"input\",\"args\":\"\",\"seq\":1,\"sent_utc_ms\":100000,\"ttl_ms\":500}";

    public static int Main()
    {
        try
        {
            TestParsing();
            TestValidation();
            TestSerializationHelpers();
            Console.WriteLine("Combat protocol: " + checks + " checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Combat protocol failed after " + checks + " checks: " + ex);
            return 1;
        }
    }

    private static void TestParsing()
    {
        CombatCommand parsed = CombatProtocol.Parse(ValidJson);
        Check(parsed.session == "session-a" && parsed.mission == "mission-a" && parsed.kind == "input", "string fields");
        Check(parsed.args == "" && parsed.seq == 1 && parsed.sent_utc_ms == Now && parsed.ttl_ms == 500, "numeric fields");
        Check(CombatProtocol.Parse(" \r\n" + ValidJson + " \t").seq == 1, "outer whitespace");
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(null); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(""); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse("  \r\n"); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse("{"); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse("null"); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse("[]"); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(ValidJson + "{}"); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(ValidJson + "trailing"); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(ValidJson.Replace("\"input\"", "\"input")); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(ValidJson.Replace("\"seq\":1", "\"seq\":9223372036854775808")); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(ValidJson.Replace("\"seq\":1", "\"seq\":1,\"seq\":2")); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(ValidJson.Replace("\"ttl_ms\":500", "\"ttl_ms\":2147483648")); });
        Reject("INVALID_JSON", delegate { CombatProtocol.Parse(ValidJson.Replace("\"sent_utc_ms\":100000", "\"sent_utc_ms\":1.5")); });

        string[] fields = new string[] { "\"session\":\"session-a\",", "\"mission\":\"mission-a\",", "\"kind\":\"input\",", "\"args\":\"\",", "\"seq\":1,", "\"sent_utc_ms\":100000,", ",\"ttl_ms\":500" };
        foreach (string field in fields)
        {
            string missing = ValidJson.Replace(field, "");
            Reject("INVALID_JSON", delegate { CombatProtocol.Parse(missing); });
        }
        string padded = ValidJson + new string(' ', CombatProtocol.MaximumCommandCharacters - ValidJson.Length);
        Check(CombatProtocol.Parse(padded).seq == 1, "maximum payload accepted");
        Reject("COMMAND_TOO_LARGE", delegate { CombatProtocol.Parse(padded + " "); });
        Reject("COMMAND_TOO_LARGE", delegate { CombatProtocol.Parse(new string(' ', 4097)); });

        CombatCommand original = Fresh();
        original.args = "a\n\t\u0001 \\\" {} [] 雪";
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CombatCommand));
        using (MemoryStream stream = new MemoryStream())
        {
            serializer.WriteObject(stream, original);
            CombatCommand roundTrip = CombatProtocol.Parse(Encoding.UTF8.GetString(stream.ToArray()));
            Check(roundTrip.args == original.args && roundTrip.seq == original.seq, "DataContract serialization round trip");
        }
    }

    private static void TestValidation()
    {
        Validate(Fresh());
        Check(true, "valid command");
        foreach (string kind in new string[] { "status", "enable", "release", "input", "order" })
        {
            CombatCommand command = Fresh(); command.kind = kind; Validate(command);
            Check(true, "kind " + kind);
        }
        Reject("INVALID_COMMAND", delegate { Validate(null); });
        RejectCommand("SESSION_MISMATCH", delegate(CombatCommand c) { c.session = null; });
        RejectCommand("SESSION_MISMATCH", delegate(CombatCommand c) { c.session = ""; });
        RejectCommand("SESSION_MISMATCH", delegate(CombatCommand c) { c.session = "SESSION-A"; });
        Reject("SESSION_MISMATCH", delegate { CombatProtocol.Validate(Fresh(), "", "mission-a", 0, Now); });
        RejectCommand("MISSION_MISMATCH", delegate(CombatCommand c) { c.mission = null; });
        RejectCommand("MISSION_MISMATCH", delegate(CombatCommand c) { c.mission = ""; });
        RejectCommand("MISSION_MISMATCH", delegate(CombatCommand c) { c.mission = "MISSION-A"; });
        Reject("MISSION_MISMATCH", delegate { CombatProtocol.Validate(Fresh(), "session-a", "", 0, Now); });
        RejectCommand("INVALID_SEQUENCE", delegate(CombatCommand c) { c.seq = 0; });
        RejectCommand("INVALID_SEQUENCE", delegate(CombatCommand c) { c.seq = -1; });
        Reject("REPLAYED_SEQUENCE", delegate { CombatProtocol.Validate(Fresh(), "session-a", "mission-a", 1, Now); });
        Reject("REPLAYED_SEQUENCE", delegate { CombatProtocol.Validate(Fresh(), "session-a", "mission-a", 2, Now); });
        Reject("REPLAYED_SEQUENCE", delegate { CombatProtocol.Validate(Fresh(), "session-a", "mission-a", Int64.MaxValue, Now); });
        CombatCommand maximalSeq = Fresh(); maximalSeq.seq = Int64.MaxValue;
        CombatProtocol.Validate(maximalSeq, "session-a", "mission-a", Int64.MaxValue - 1, Now);
        Check(true, "maximum sequence");
        RejectCommand("UNSUPPORTED_KIND", delegate(CombatCommand c) { c.kind = null; });
        RejectCommand("UNSUPPORTED_KIND", delegate(CombatCommand c) { c.kind = "Input"; });
        RejectCommand("UNSUPPORTED_KIND", delegate(CombatCommand c) { c.kind = "attack"; });
        RejectCommand("INVALID_ARGS", delegate(CombatCommand c) { c.args = null; });
        CombatCommand maxArgs = Fresh(); maxArgs.args = new string('a', 512); Validate(maxArgs);
        Check(true, "maximum args");
        RejectCommand("INVALID_ARGS", delegate(CombatCommand c) { c.args = new string('a', 513); });
        foreach (int ttl in new int[] { Int32.MinValue, -1, 0, 99, 1001, Int32.MaxValue })
        {
            int invalidTtl = ttl;
            RejectCommand("INVALID_TTL", delegate(CombatCommand c) { c.ttl_ms = invalidTtl; });
        }
        foreach (int ttl in new int[] { 100, 1000 })
        {
            CombatCommand command = Fresh(); command.ttl_ms = ttl; Validate(command);
            Check(true, "valid ttl boundary");
        }
        RejectCommand("INVALID_TIMESTAMP", delegate(CombatCommand c) { c.sent_utc_ms = -1; });
        RejectCommand("INVALID_TIMESTAMP", delegate(CombatCommand c) { c.sent_utc_ms = Int64.MinValue; });
        Reject("INVALID_TIMESTAMP", delegate { CombatProtocol.Validate(Fresh(), "session-a", "mission-a", 0, -1); });
        CombatCommand futureBoundary = Fresh(); futureBoundary.sent_utc_ms = Now + 100; Validate(futureBoundary);
        Check(true, "future tolerance boundary");
        RejectCommand("FUTURE_COMMAND", delegate(CombatCommand c) { c.sent_utc_ms = Now + 101; });
        RejectCommand("FUTURE_COMMAND", delegate(CombatCommand c) { c.sent_utc_ms = Int64.MaxValue; });
        CombatCommand notExpired = Fresh(); notExpired.sent_utc_ms = Now - 499; Validate(notExpired);
        Check(true, "one millisecond before expiration");
        RejectCommand("STALE_COMMAND", delegate(CombatCommand c) { c.sent_utc_ms = Now - 500; });
        RejectCommand("STALE_COMMAND", delegate(CombatCommand c) { c.sent_utc_ms = 0; });

        CombatCommand nearMax = Fresh(); nearMax.sent_utc_ms = Int64.MaxValue - 499;
        CombatProtocol.Validate(nearMax, "session-a", "mission-a", 0, Int64.MaxValue);
        Check(true, "TTL expiry addition does not overflow");
        nearMax.sent_utc_ms = Int64.MaxValue - 500;
        Reject("STALE_COMMAND", delegate { CombatProtocol.Validate(nearMax, "session-a", "mission-a", 0, Int64.MaxValue); });
        nearMax.sent_utc_ms = Int64.MaxValue;
        CombatProtocol.Validate(nearMax, "session-a", "mission-a", 0, Int64.MaxValue - 100);
        Check(true, "future tolerance addition does not overflow");
        Reject("FUTURE_COMMAND", delegate { CombatProtocol.Validate(nearMax, "session-a", "mission-a", 0, Int64.MaxValue - 101); });
    }

    private static void TestSerializationHelpers()
    {
        Check(CombatProtocol.Json(null) == "null", "JSON null");
        Check(CombatProtocol.Json("") == "\"\"", "JSON empty string");
        Check(CombatProtocol.Json("\\\"") == "\"\\\\\\\"\"", "JSON slash and quote");
        Check(CombatProtocol.Json("\b\f\n\r\t") == "\"\\b\\f\\n\\r\\t\"", "JSON short escapes");
        Check(CombatProtocol.Json("\u0000\u0001\u001f") == "\"\\u0000\\u0001\\u001f\"", "JSON control escapes");
        StringBuilder allControls = new StringBuilder();
        for (int i = 0; i < 32; i++) allControls.Append((char)i);
        string controlsJson = CombatProtocol.Json(allControls.ToString());
        foreach (char c in controlsJson) Check(c >= 32, "no raw JSON control");
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(string));
        using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(controlsJson)))
            Check((string)serializer.ReadObject(stream) == allControls.ToString(), "all controls round trip");
        Check(CombatProtocol.Json("雪") == "\"雪\"", "Unicode text");
        Check(CombatProtocol.Number(Single.NaN) == "null", "NaN is JSON null");
        Check(CombatProtocol.Number(Single.PositiveInfinity) == "null", "positive infinity is JSON null");
        Check(CombatProtocol.Number(Single.NegativeInfinity) == "null", "negative infinity is JSON null");
        Check(CombatProtocol.Number(0f) == "0", "zero number");
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
            Check(CombatProtocol.Number(1.25f) == "1.25", "invariant decimal");
            Check(Single.Parse(CombatProtocol.Number(Single.MaxValue), CultureInfo.InvariantCulture) == Single.MaxValue, "max float round trip");
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
        long before = (DateTime.UtcNow.Ticks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / TimeSpan.TicksPerMillisecond;
        long actual = CombatProtocol.NowMs();
        long after = (DateTime.UtcNow.Ticks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / TimeSpan.TicksPerMillisecond;
        Check(actual >= before && actual <= after, "UTC epoch milliseconds");
    }

    private static CombatCommand Fresh() { return CombatProtocol.Parse(ValidJson); }
    private static void Validate(CombatCommand command) { CombatProtocol.Validate(command, "session-a", "mission-a", 0, Now); }
    private static void RejectCommand(string expected, Action<CombatCommand> change)
    {
        CombatCommand command = Fresh(); change(command);
        Reject(expected, delegate { Validate(command); });
    }
    private static void Reject(string expected, Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message == expected, "expected " + expected + ", got " + ex.Message);
            return;
        }
        throw new Exception("Expected rejection: " + expected);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }
}
