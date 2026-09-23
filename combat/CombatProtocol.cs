using System;
using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace BannerlordCombatBridge
{
    [DataContract]
    public sealed class CombatCommand
    {
        [DataMember(IsRequired = true)] public string session;
        [DataMember(IsRequired = true)] public string mission;
        [DataMember(IsRequired = true)] public string kind;
        [DataMember(IsRequired = true)] public string args;
        [DataMember(IsRequired = true)] public long seq;
        [DataMember(IsRequired = true)] public long sent_utc_ms;
        [DataMember(IsRequired = true)] public int ttl_ms;
    }

    public static class CombatProtocol
    {
        public const int MaximumCommandCharacters = 4096;
        public const int MaximumArgumentCharacters = 512;

        public static CombatCommand Parse(string json)
        {
            if (json == null)
                throw new InvalidOperationException("INVALID_JSON");
            if (json.Length > MaximumCommandCharacters)
                throw new InvalidOperationException("COMMAND_TOO_LARGE");
            RequireSingleObject(json);

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                using (XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(bytes, XmlDictionaryReaderQuotas.Max))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CombatCommand));
                    CombatCommand command = (CombatCommand)serializer.ReadObject(reader);
                    if (command == null || reader.Read())
                        throw new SerializationException("Expected one command object.");
                    return command;
                }
            }
            catch (SerializationException ex)
            {
                throw new InvalidOperationException("INVALID_JSON", ex);
            }
            catch (XmlException ex)
            {
                throw new InvalidOperationException("INVALID_JSON", ex);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("INVALID_JSON", ex);
            }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException("INVALID_JSON", ex);
            }
        }

        private static void RequireSingleObject(string json)
        {
            // DataContractJsonSerializer can stop after the first object even if
            // trailing data remains. Check the document boundary independently.
            int start = 0;
            while (start < json.Length && IsJsonWhitespace(json[start])) start++;
            if (start == json.Length || json[start] != '{')
                throw new InvalidOperationException("INVALID_JSON");
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = start; i < json.Length; i++)
            {
                char character = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                    continue;
                }
                if (character == '"') inString = true;
                else if (character == '{' || character == '[') depth++;
                else if (character == '}' || character == ']')
                {
                    depth--;
                    if (depth != 0) continue;
                    for (int trailing = i + 1; trailing < json.Length; trailing++)
                        if (!IsJsonWhitespace(json[trailing]))
                            throw new InvalidOperationException("INVALID_JSON");
                    return;
                }
            }
            throw new InvalidOperationException("INVALID_JSON");
        }

        private static bool IsJsonWhitespace(char character)
        {
            return character == ' ' || character == '\t' || character == '\r' || character == '\n';
        }

        public static void Validate(CombatCommand command, string expectedSession, string expectedMission, long lastSeq, long nowMs)
        {
            if (command == null)
                throw new InvalidOperationException("INVALID_COMMAND");
            if (String.IsNullOrEmpty(expectedSession) || String.IsNullOrEmpty(command.session) ||
                !String.Equals(command.session, expectedSession, StringComparison.Ordinal))
                throw new InvalidOperationException("SESSION_MISMATCH");
            if (String.IsNullOrEmpty(expectedMission) || String.IsNullOrEmpty(command.mission) ||
                !String.Equals(command.mission, expectedMission, StringComparison.Ordinal))
                throw new InvalidOperationException("MISSION_MISMATCH");
            if (command.seq < 1)
                throw new InvalidOperationException("INVALID_SEQUENCE");
            if (command.seq <= lastSeq)
                throw new InvalidOperationException("REPLAYED_SEQUENCE");
            if (command.kind != "status" && command.kind != "enable" && command.kind != "release" &&
                command.kind != "input" && command.kind != "order")
                throw new InvalidOperationException("UNSUPPORTED_KIND");
            if (command.args == null || command.args.Length > MaximumArgumentCharacters)
                throw new InvalidOperationException("INVALID_ARGS");
            if (command.ttl_ms < 100 || command.ttl_ms > 1000)
                throw new InvalidOperationException("INVALID_TTL");
            if (command.sent_utc_ms < 0 || nowMs < 0)
                throw new InvalidOperationException("INVALID_TIMESTAMP");

            // Subtract ordered, nonnegative timestamps instead of adding TTL to a
            // potentially maximal timestamp. Both differences are overflow safe.
            if (command.sent_utc_ms > nowMs && command.sent_utc_ms - nowMs > 100)
                throw new InvalidOperationException("FUTURE_COMMAND");
            if (command.sent_utc_ms <= nowMs && nowMs - command.sent_utc_ms >= command.ttl_ms)
                throw new InvalidOperationException("STALE_COMMAND");
        }

        public static string Json(string value)
        {
            if (value == null) return "null";
            StringBuilder output = new StringBuilder(value.Length + 2);
            output.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (character < 32)
                        {
                            output.Append("\\u");
                            output.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else output.Append(character);
                        break;
                }
            }
            output.Append('"');
            return output.ToString();
        }

        public static string Number(float value)
        {
            return Single.IsNaN(value) || Single.IsInfinity(value)
                ? "null"
                : value.ToString("R", CultureInfo.InvariantCulture);
        }

        public static long NowMs()
        {
            return (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond;
        }
    }
}
