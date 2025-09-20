using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Bxml
{
    public class BxmlInputStrategy : BaseInputStrategy
    {
        private const int MAX_ELEMENT_COUNT = 50000;
        private const int MAX_STRING_COUNT = 50000;
        private const int MAX_ATTRIBUTE_COUNT = 5000;
        private const int MAX_CHILD_COUNT = 5000;
        private const int MAX_STRING_LENGTH = 1000000;

        public override JToken Parse(string input)
        {
            try
            {
                if (string.IsNullOrEmpty(input))
                {
                    throw new FormatException("BXML input is empty or null");
                }

                byte[] bxmlData;

                try
                {
                    bxmlData = Convert.FromBase64String(input);
                }
                catch (FormatException)
                {
                    bxmlData = ParseHexString(input);
                }

                if (bxmlData.Length < 12)
                {
                    throw new FormatException($"BXML data too short: {bxmlData.Length} bytes (minimum 12 required)");
                }

                var result = ParseBxmlData(bxmlData);

                if (Config.SortKeys && result is JObject)
                {
                    result = SortKeysRecursively(result);
                }

                return result;
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: BXML parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"BXML parsing failed: {ex.Message}", ex);
            }
        }

        private byte[] ParseHexString(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("-", "");

            if (hex.Length % 2 != 0)
            {
                throw new FormatException("Invalid hex string length");
            }

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        private JToken ParseBxmlData(byte[] bxmlData)
        {
            using var buffer = new MemoryStream(bxmlData);
            using var reader = new BinaryReader(buffer);

            var signature = reader.ReadBytes(4);
            string sigStr = Encoding.ASCII.GetString(signature);

            if (sigStr != "BXML")
            {
                throw new FormatException($"Invalid BXML signature: '{sigStr}', expected 'BXML'");
            }

            uint elementCount = reader.ReadUInt32();

            if (elementCount == 0)
            {
                return new JObject();
            }

            var maxElements = Config.StrictMode ? 1000 : MAX_ELEMENT_COUNT;
            if (elementCount > maxElements)
            {
                throw new FormatException($"Element count {elementCount} exceeds maximum allowed {maxElements}");
            }

            var elements = new List<BxmlElement>();
            for (int i = 0; i < elementCount; i++)
            {
                try
                {
                    elements.Add(ReadElement(reader, 0));
                }
                catch (Exception ex)
                {
                    throw new FormatException($"Failed to read element {i} at position {reader.BaseStream.Position}: {ex.Message}", ex);
                }
            }

            uint stringCount = reader.ReadUInt32();
            var maxStrings = Config.StrictMode ? 1000 : MAX_STRING_COUNT;
            if (stringCount > maxStrings)
            {
                throw new FormatException($"String count {stringCount} exceeds maximum allowed {maxStrings}");
            }

            var stringTable = ReadStringTable(reader, stringCount);

            if (elements.Count > 0)
            {
                return ConvertBxmlElementToJson(elements[0], stringTable);
            }

            return new JObject();
        }

        private BxmlElement ReadElement(BinaryReader reader, int depth)
        {
            var maxDepth = Config.MaxDepth ?? (Config.StrictMode ? 10 : 100);
            if (depth > maxDepth)
            {
                throw new FormatException($"Maximum nesting depth {maxDepth} exceeded");
            }

            if (reader.BaseStream.Position >= reader.BaseStream.Length)
            {
                throw new FormatException("Unexpected end of stream while reading element");
            }

            byte nodeType = reader.ReadByte();

            if (nodeType != 1)
            {
                throw new FormatException($"Expected element type 1, got {nodeType} at position {reader.BaseStream.Position - 1}");
            }

            uint nameIndex = reader.ReadUInt32();
            var element = new BxmlElement
            {
                NameIndex = nameIndex
            };

            uint attrCount = reader.ReadUInt32();
            var maxAttrs = Config.StrictMode ? 100 : MAX_ATTRIBUTE_COUNT;
            if (attrCount > maxAttrs)
            {
                throw new FormatException($"Attribute count {attrCount} exceeds maximum allowed {maxAttrs}");
            }

            for (int i = 0; i < attrCount; i++)
            {
                uint attrNameIndex = reader.ReadUInt32();
                uint attrValueIndex = reader.ReadUInt32();
                element.Attributes[attrNameIndex] = attrValueIndex;
            }

            byte hasText = reader.ReadByte();
            if (hasText == 1)
            {
                uint textIndex = reader.ReadUInt32();
                element.TextIndex = textIndex;
            }
            else if (hasText != 0)
            {
                throw new FormatException($"Invalid hasText flag: {hasText}, expected 0 or 1");
            }

            uint childCount = reader.ReadUInt32();
            var maxChildren = Config.StrictMode ? 100 : MAX_CHILD_COUNT;
            if (childCount > maxChildren)
            {
                throw new FormatException($"Child count {childCount} exceeds maximum allowed {maxChildren}");
            }

            for (int i = 0; i < childCount; i++)
            {
                element.Children.Add(ReadElement(reader, depth + 1));
            }

            return element;
        }

        private string[] ReadStringTable(BinaryReader reader, uint stringCount)
        {
            var stringTable = new string[stringCount];

            for (int i = 0; i < stringCount; i++)
            {
                if (reader.BaseStream.Position >= reader.BaseStream.Length)
                {
                    throw new FormatException($"Unexpected end of stream while reading string {i}");
                }

                uint stringLength = reader.ReadUInt32();
                var maxLength = Config.StrictMode ? 10000 : MAX_STRING_LENGTH;
                if (stringLength > maxLength)
                {
                    throw new FormatException($"String {i} length {stringLength} exceeds maximum allowed {maxLength}");
                }

                if (reader.BaseStream.Position + stringLength > reader.BaseStream.Length)
                {
                    throw new FormatException($"String {i} extends beyond stream boundary");
                }

                byte[] stringBytes = reader.ReadBytes((int)stringLength);
                stringTable[i] = Encoding.UTF8.GetString(stringBytes);
            }

            return stringTable;
        }

        private JToken ConvertBxmlElementToJson(BxmlElement element, string[] stringTable)
        {
            string elementName = GetStringFromTable(element.NameIndex, stringTable, "unknown");

            if (elementName == "Root")
            {
                var rootContent = new JObject();
                foreach (var child in element.Children)
                {
                    var childJson = ConvertBxmlElementToJson(child, stringTable);
                    if (childJson is JObject childObj)
                    {
                        foreach (var prop in childObj.Properties())
                        {
                            rootContent[prop.Name] = prop.Value;
                        }
                    }
                }
                return rootContent;
            }

            var json = new JObject();
            string type = GetElementType(element, stringTable);

            switch (type)
            {
                case "object":
                    var obj = new JObject();
                    foreach (var child in element.Children)
                    {
                        var childJson = ConvertBxmlElementToJson(child, stringTable);
                        string childName = GetStringFromTable(child.NameIndex, stringTable, "unknown");

                        if (childJson is JObject childObj && childObj.Properties().Any())
                        {
                            obj[childName] = childObj.Properties().First().Value;
                        }
                        else
                        {
                            obj[childName] = childJson;
                        }
                    }
                    json[elementName] = obj;
                    break;

                case "array":
                    var array = new JArray();
                    foreach (var child in element.Children)
                    {
                        var childJson = ConvertBxmlElementToJson(child, stringTable);
                        if (childJson is JObject childObj && childObj.Properties().Any())
                        {
                            array.Add(childObj.Properties().First().Value);
                        }
                        else
                        {
                            array.Add(childJson);
                        }
                    }
                    json[elementName] = array;
                    break;

                default:
                    var value = GetElementValue(element, stringTable, type);
                    json[elementName] = value;
                    break;
            }

            return json;
        }

        private string GetElementType(BxmlElement element, string[] stringTable)
        {
            foreach (var attr in element.Attributes)
            {
                string attrName = GetStringFromTable(attr.Key, stringTable, "");
                if (attrName == "type")
                {
                    return GetStringFromTable(attr.Value, stringTable, "string");
                }
            }
            return "string";
        }

        private JToken GetElementValue(BxmlElement element, string[] stringTable, string type)
        {
            if (element.TextIndex.HasValue)
            {
                string value = GetStringFromTable(element.TextIndex.Value, stringTable, "");
                return ConvertValueByType(value, type);
            }

            return type == "null" ? JValue.CreateNull() : new JValue("");
        }

        private JToken ConvertValueByType(string value, string type)
        {
            return type switch
            {
                "int" => int.TryParse(value, out var i) ? new JValue(i) : new JValue(0),
                "long" => long.TryParse(value, out var l) ? new JValue(l) : new JValue(0L),
                "float" => double.TryParse(value, out var d) ? new JValue(FormatNumber(d)) : new JValue(0.0),
                "double" => double.TryParse(value, out var db) ? new JValue(FormatNumber(db)) : new JValue(0.0),
                "bool" => bool.TryParse(value, out var b) ? new JValue(b) : new JValue(false),
                "null" => JValue.CreateNull(),
                "date" => DateTime.TryParse(value, out var dt) ? new JValue(FormatDateTime(dt)) : new JValue(value),
                _ => new JValue(value)
            };
        }

        private string GetStringFromTable(uint index, string[] stringTable, string defaultValue)
        {
            return index < stringTable.Length ? stringTable[index] : defaultValue;
        }

        private object FormatNumber(double number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{(long)number:X}",
                    "scientific" => number.ToString("E"),
                    _ => number
                };
            }
            return number;
        }

        private object FormatDateTime(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds(),
                    "rfc3339" => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }
            return dateTime;
        }

        private JToken SortKeysRecursively(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Object => SortJObject((JObject)token),
                JTokenType.Array => new JArray(((JArray)token).Select(SortKeysRecursively)),
                _ => token
            };
        }

        private JObject SortJObject(JObject obj)
        {
            var sorted = new JObject();
            foreach (var property in obj.Properties().OrderBy(p => p.Name))
            {
                sorted[property.Name] = SortKeysRecursively(property.Value);
            }
            return sorted;
        }
    }
}