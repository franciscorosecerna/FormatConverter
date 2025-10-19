using Newtonsoft.Json.Linq;
using System.Globalization;

namespace FormatTest
{
    public static class JTokenComparer
    {
        public static JToken? Normalize(JToken token)
        {
            if (token == null) return null;

            switch (token.Type)
            {
                case JTokenType.Float:
                    double value = token.Value<double>();
                    if (value % 1 == 0)
                        return new JValue((long)value);
                    return token;

                case JTokenType.Date:
                    DateTime dt = token.Value<DateTime>();
                    return new JValue(dt.ToUniversalTime().ToString("o"));

                case JTokenType.String:
                    string strValue = token.Value<string>()!;
                    if (TryParseAsDate(strValue, out DateTime parsedDate))
                    {
                        return new JValue(parsedDate.ToUniversalTime().ToString("o"));
                    }
                    return token;

                case JTokenType.Object:
                    var obj = new JObject();
                    foreach (var prop in token.Children<JProperty>().OrderBy(p => p.Name))
                    {
                        obj.Add(prop.Name, Normalize(prop.Value));
                    }
                    return obj;

                case JTokenType.Array:
                    var arr = new JArray();
                    foreach (var item in token.Children())
                    {
                        arr.Add(Normalize(item) ?? JValue.CreateNull());
                    }
                    return arr;

                default:
                    return token;
            }
        }

        private static bool TryParseAsDate(string value, out DateTime result)
        {
            string[] formats =
            [
                "yyyy-MM-dd",
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-ddTHH:mm:sszzz",
                "yyyy-MM-ddTHH:mm:ss.fff",
                "yyyy-MM-ddTHH:mm:ss.fffzzz",
                "dd/MM/yyyy HH:mm:ss",
                "dd/MM/yyyy",
                "o",
                "s"
            ];

            return DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out result);
        }

        public static bool AreEqual(JToken expected, JToken actual)
        {
            var normExpected = Normalize(expected);
            var normActual = Normalize(actual);
            return JToken.DeepEquals(normExpected, normActual);
        }

        public static void AssertEqual(JToken expected, JToken actual, string? message = null)
        {
            if (!AreEqual(expected, actual))
            {
                string error = message ?? "Los tokens no son iguales después de normalizar.";
                throw new InvalidOperationException(error +
                    $"\nExpected:\n{expected}\nActual:\n{actual}");
            }
        }
    }
}