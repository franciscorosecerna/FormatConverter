using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Bxml
{
    public class BxmlInputStrategy : IInputFormatStrategy
    {
        public JObject Parse(string input)
        {
            try
            {
                byte[] bxmlData = Convert.FromBase64String(input);

                using var buffer = new MemoryStream(bxmlData);
                using var reader = new BinaryReader(buffer);
                var signature = reader.ReadBytes(4);
                if (Encoding.ASCII.GetString(signature) != "BXML")
                    throw new InvalidOperationException("Invalid BXML signature");

                uint elementCount = reader.ReadUInt32();

                var elements = new List<BxmlElement>();
                for (int i = 0; i < elementCount; i++)
                {
                    elements.Add(ReadElement(reader));
                }

                uint stringCount = reader.ReadUInt32();
                var stringTable = new string[stringCount];

                for (int i = 0; i < stringCount; i++)
                {
                    uint stringLength = reader.ReadUInt32();
                    if (stringLength > 100000)
                        throw new InvalidOperationException($"String too long: {stringLength}");

                    byte[] stringBytes = reader.ReadBytes((int)stringLength);
                    stringTable[i] = Encoding.UTF8.GetString(stringBytes);
                }

                if (elements.Count > 0)
                {
                    return ConvertBxmlElementToJson(elements[0], stringTable);
                }

                return [];
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize BXML: {ex.Message}", ex);
            }
        }

        private static BxmlElement ReadElement(BinaryReader reader)
        {
            byte nodeType = reader.ReadByte();
            if (nodeType != 1)
                throw new InvalidOperationException($"Expected element type, got {nodeType}");

            var element = new BxmlElement
            {
                NameIndex = reader.ReadUInt32()
            };

            uint attrCount = reader.ReadUInt32();
            for (int i = 0; i < attrCount; i++)
            {
                uint nameIndex = reader.ReadUInt32();
                uint valueIndex = reader.ReadUInt32();
                element.Attributes[nameIndex] = valueIndex;
            }

            byte hasText = reader.ReadByte();
            if (hasText == 1)
            {
                element.TextIndex = reader.ReadUInt32();
            }

            uint childCount = reader.ReadUInt32();
            for (int i = 0; i < childCount; i++)
            {
                element.Children.Add(ReadElement(reader));
            }

            return element;
        }

        private static JObject ConvertBxmlElementToJson(BxmlElement element, string[] stringTable)
        {
            string elementName = element.NameIndex < stringTable.Length ? stringTable[element.NameIndex] : "unknown";

            if (elementName == "Root")
            {
                var rootContent = new JObject();
                foreach (var child in element.Children)
                {
                    var childJson = ConvertBxmlElementToJson(child, stringTable);
                    foreach (var prop in childJson.Properties())
                    {
                        rootContent[prop.Name] = prop.Value;
                    }
                }
                return rootContent;
            }

            var json = new JObject();

            string type = "string";
            foreach (var attr in element.Attributes)
            {
                if (attr.Key < stringTable.Length && stringTable[attr.Key] == "type")
                {
                    if (attr.Value < stringTable.Length)
                    {
                        type = stringTable[attr.Value];
                    }
                    break;
                }
            }

            switch (type)
            {
                case "object":
                    var obj = new JObject();
                    foreach (var child in element.Children)
                    {
                        var childJson = ConvertBxmlElementToJson(child, stringTable);
                        string childName = child.NameIndex < stringTable.Length ? stringTable[child.NameIndex] : "unknown";
                        obj[childName] = childJson.Properties().FirstOrDefault()?.Value;
                    }
                    json[elementName] = obj;
                    break;

                case "array":
                    var array = new JArray();
                    foreach (var child in element.Children)
                    {
                        var childJson = ConvertBxmlElementToJson(child, stringTable);
                        array.Add(childJson.Properties().FirstOrDefault()?.Value);
                    }
                    json[elementName] = array;
                    break;

                default:
                    if (element.TextIndex.HasValue && element.TextIndex.Value < stringTable.Length)
                    {
                        string value = stringTable[element.TextIndex.Value];
                        json[elementName] = type switch
                        {
                            "int" => (JToken)(int.TryParse(value, out var i) ? i : 0),
                            "float" => (JToken)(double.TryParse(value, out var d) ? d : 0.0),
                            "bool" => (JToken)(bool.TryParse(value, out var b) && b),
                            "null" => null,
                            _ => (JToken)value,
                        };
                    }
                    else
                    {
                        json[elementName] = type == "null" ? null : "";
                    }
                    break;
            }

            return json;
        }
    }
}
