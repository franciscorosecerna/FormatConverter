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
                if (string.IsNullOrEmpty(input))
                    throw new ArgumentException("Input cannot be null or empty");

                byte[] bxmlData = Convert.FromBase64String(input);

                if (bxmlData.Length < 12)
                    throw new InvalidOperationException($"BXML data too short: {bxmlData.Length} bytes");

                using var buffer = new MemoryStream(bxmlData);
                using var reader = new BinaryReader(buffer);

                var signature = reader.ReadBytes(4);
                string sigStr = Encoding.ASCII.GetString(signature);

                if (sigStr != "BXML")
                    throw new InvalidOperationException($"Invalid BXML signature: {sigStr}");

                uint elementCount = reader.ReadUInt32();

                if (elementCount == 0)
                {
                    return new JObject();
                }

                if (elementCount > 10000)
                    throw new InvalidOperationException($"Unreasonable element count: {elementCount}");

                var elements = new List<BxmlElement>();
                for (int i = 0; i < elementCount; i++)
                {
                    try
                    {
                        elements.Add(ReadElement(reader));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Failed to read element {i} at position {reader.BaseStream.Position}: {ex.Message}", ex);
                    }
                }

                uint stringCount = reader.ReadUInt32();
                if (stringCount > 10000)
                    throw new InvalidOperationException($"Unreasonable string count: {stringCount}");

                var stringTable = new string[stringCount];
                for (int i = 0; i < stringCount; i++)
                {
                    if (reader.BaseStream.Position >= reader.BaseStream.Length)
                        throw new InvalidOperationException($"Unexpected end of stream while reading string {i}");

                    uint stringLength = reader.ReadUInt32();
                    if (stringLength > 100000)
                        throw new InvalidOperationException($"String {i} too long: {stringLength}");

                    if (reader.BaseStream.Position + stringLength > reader.BaseStream.Length)
                        throw new InvalidOperationException($"String {i} extends beyond stream boundary");

                    byte[] stringBytes = reader.ReadBytes((int)stringLength);
                    stringTable[i] = Encoding.UTF8.GetString(stringBytes);
                }

                if (elements.Count > 0)
                {
                    return ConvertBxmlElementToJson(elements[0], stringTable);
                }

                return new JObject();
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"Invalid Base64 input: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize BXML: {ex.Message}", ex);
            }
        }

        private static BxmlElement ReadElement(BinaryReader reader)
        {
            long startPos = reader.BaseStream.Position;

            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                throw new InvalidOperationException("Unexpected end of stream while reading element");

            byte nodeType = reader.ReadByte();

            if (nodeType != 1)
            {
                throw new InvalidOperationException($"Expected element type 1, got {nodeType} at position {startPos}");
            }

            uint nameIndex = reader.ReadUInt32();
            var element = new BxmlElement
            {
                NameIndex = nameIndex
            };
            uint attrCount = reader.ReadUInt32();

            if (attrCount > 1000)
                throw new InvalidOperationException($"Unreasonable attribute count: {attrCount}");

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
                throw new InvalidOperationException($"Invalid hasText flag: {hasText}");
            }

            uint childCount = reader.ReadUInt32();

            if (childCount > 1000)
                throw new InvalidOperationException($"Unreasonable child count: {childCount}");

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