using System.Globalization;
using System.Text;

namespace BannerlordStrategicBridge
{
    internal static class JsonText
    {
        internal static string Quote(string value)
        {
            StringBuilder json = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '\\') json.Append("\\\\");
                else if (c == '"' || c < ' ')
                    json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else json.Append(c);
            }
            return json.Append('"').ToString();
        }
    }
}
