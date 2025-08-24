using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Bxml
{
    public class BxmlOutputStrategy : BaseOutputStrategy
    {
        private const int MAX_STRING_TABLE_SIZE = 100000;

        public override string Serialize(JToken data)
        {
            try
            {
                using var buffer = new MemoryStream();
                using var writer = new BinaryWriter(buffer);

                writer.Write(Encoding.ASCII.GetBytes("BXML"));

                var elements = new List<BxmlElement>();
                var stringTable = new Dictionary<string, uint>();
                uint stringIndex = 0;

                uint AddString(string s)
                {
                    if (string.IsNullOrEmpty(s)) s = "";

                    if (!stringTable.ContainsKey(s))
                    {
                        if (stringTable.Count >= MAX_STRING_TABLE_SIZE)
                        {
                            throw new FormatException($"String table size limit {MAX_STRING_TABLE_SIZE} exceeded");
                        }
                        stringTable[s] = stringIndex++;
                    }
                    return stringTable[s];
                }

                var rootElement = ConvertJsonToBxmlElement("Root", data, AddString);
                elements.Add(rootElement);

                writer.Write((uint)elements.Count);

                foreach (var element in elements)
                {
                    WriteElement(writer, element);
                }

                WriteStringTable(writer, stringTable);

                var result = buffer.ToArray();
                return FormatOutput(result);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                throw new FormatException($"BXML serialization failed: {ex.Message}", ex);
            }
        }

        private BxmlElement ConvertJsonToBxmlElement(string name, JToken token, Func<string, uint> addString)
        {
            var element = new BxmlElement
            {
                NameIndex = addString(name)
            };

            switch (token.Type)
            {
                case JTokenType.Object:
                    ConvertObjectToElement(element, (JObject)token, addString);
                    break;

                case JTokenType.Array:
                    ConvertArrayToElement(element, (JArray)token, addString);
                    break;

                case JTokenType.String:
                    ConvertStringToElement(element, token.Value<string>(), addString);
                    break;

                case JTokenType.Integer:
                    ConvertIntegerToElement(element, token.Value<long>(), addString);
                    break;

                case JTokenType.Float:
                    ConvertFloatToElement(element, token.Value<double>(), addString);
                    break;

                case JTokenType.Boolean:
                    ConvertBooleanToElement(element, token.Value<bool>(), addString);
                    break;

                case JTokenType.Date:
                    ConvertDateToElement(element, token.Value<DateTime>(), addString);
                    break;

                case JTokenType.Null:
                    element.Attributes[addString("type")] = addString("null");
                    break;

                default:
                    ConvertStringToElement(element, token.ToString(), addString);
                    break;
            }

            return element;
        }

        private void ConvertObjectToElement(BxmlElement element, JObject obj, Func<string, uint> addString)
        {
            element.Attributes[addString("type")] = addString("object");

            var properties = Config.SortKeys
                ? obj.Properties().OrderBy(p => p.Name)
                : obj.Properties();

            foreach (var property in properties)
            {
                if (Config.NoMetadata && IsMetadataField(property.Name))
                    continue;

                var child = ConvertJsonToBxmlElement(property.Name, property.Value, addString);
                element.Children.Add(child);
            }
        }

        private void ConvertArrayToElement(BxmlElement element, JArray array, Func<string, uint> addString)
        {
            element.Attributes[addString("type")] = addString("array");

            foreach (var item in array)
            {
                var child = ConvertJsonToBxmlElement("item", item, addString);
                element.Children.Add(child);
            }
        }

        private static void ConvertStringToElement(BxmlElement element, string? value, Func<string, uint> addString)
        {
            element.Attributes[addString("type")] = addString("string");
            element.TextIndex = addString(value ?? "");
        }

        private void ConvertIntegerToElement(BxmlElement element, long value, Func<string, uint> addString)
        {
            var formattedValue = FormatIntegerValue(value);
            var typeString = DetermineIntegerType(value);

            element.Attributes[addString("type")] = addString(typeString);
            element.TextIndex = addString(formattedValue);
        }

        private void ConvertFloatToElement(BxmlElement element, double value, Func<string, uint> addString)
        {
            var formattedValue = FormatNumberValue(value);
            element.Attributes[addString("type")] = addString("float");
            element.TextIndex = addString(formattedValue);
        }

        private static void ConvertBooleanToElement(BxmlElement element, bool value, Func<string, uint> addString)
        {
            element.Attributes[addString("type")] = addString("bool");
            element.TextIndex = addString(value.ToString().ToLowerInvariant());
        }

        private void ConvertDateToElement(BxmlElement element, DateTime value, Func<string, uint> addString)
        {
            var formattedValue = FormatDateTime(value);
            element.Attributes[addString("type")] = addString("date");
            element.TextIndex = addString(formattedValue);
        }

        private static bool IsMetadataField(string fieldName)
        {
            return fieldName.StartsWith("_") ||
                   fieldName.StartsWith("@") ||
                   fieldName.StartsWith("$") ||
                   fieldName.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("version", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("schema", StringComparison.OrdinalIgnoreCase);
        }

        private static string DetermineIntegerType(long value)
        {
            if (value >= int.MinValue && value <= int.MaxValue)
                return "int";
            return "long";
        }

        private string FormatIntegerValue(long value)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{value:X}",
                    "scientific" => ((double)value).ToString("E"),
                    _ => value.ToString()
                };
            }
            return value.ToString();
        }

        private string FormatNumberValue(double value)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{(long)value:X}",
                    "scientific" => value.ToString("E"),
                    _ => value.ToString()
                };
            }
            return value.ToString();
        }

        private string FormatDateTime(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds().ToString(),
                    "rfc3339" => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }

            return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        }

        private void WriteElement(BinaryWriter writer, BxmlElement element)
        {
            writer.Write((byte)1);

            writer.Write(element.NameIndex);

            writer.Write((uint)element.Attributes.Count);

            var attributes = Config.SortKeys
                ? element.Attributes.OrderBy(kvp => kvp.Key)
                : element.Attributes.AsEnumerable();

            foreach (var attr in attributes)
            {
                writer.Write(attr.Key);
                writer.Write(attr.Value);
            }

            if (element.TextIndex.HasValue)
            {
                writer.Write((byte)1);
                writer.Write(element.TextIndex.Value);
            }
            else
            {
                writer.Write((byte)0);
            }

            writer.Write((uint)element.Children.Count);
            foreach (var child in element.Children)
            {
                WriteElement(writer, child);
            }
        }

        private static void WriteStringTable(BinaryWriter writer, Dictionary<string, uint> stringTable)
        {
            writer.Write((uint)stringTable.Count);

            var sortedStrings = stringTable.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key).ToArray();

            foreach (var str in sortedStrings)
            {
                byte[] strBytes = Encoding.UTF8.GetBytes(str);
                writer.Write((uint)strBytes.Length);
                writer.Write(strBytes);
            }
        }

        private string FormatOutput(byte[] bytes)
        {
            return Config.NumberFormat?.ToLower() switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
                "binary" => FormatAsBinary(bytes),
                _ => Convert.ToBase64String(bytes)
            };
        }

        private string FormatAsHex(byte[] bytes)
        {
            var hex = Convert.ToHexString(bytes);

            if (Config.PrettyPrint && !Config.Minify)
            {
                return string.Join(" ",
                    Enumerable.Range(0, hex.Length / 2)
                    .Select(i => hex.Substring(i * 2, 2)));
            }

            return hex.ToLowerInvariant();
        }

        private string FormatAsBinary(byte[] bytes)
        {
            if (Config.PrettyPrint && !Config.Minify)
            {
                return string.Join(" ", bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
            }

            return string.Concat(bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        }
    }
}