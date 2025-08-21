using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Bxml
{
    public class BxmlOutputStrategy : IOutputFormatStrategy
    {
        public string Serialize(JObject data)
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

                writer.Write((uint)stringTable.Count);

                var sortedStrings = stringTable.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key).ToArray();
                foreach (var str in sortedStrings)
                {
                    byte[] strBytes = Encoding.UTF8.GetBytes(str);
                    writer.Write((uint)strBytes.Length);
                    writer.Write(strBytes);
                }

                return Convert.ToBase64String(buffer.ToArray());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize to BXML: {ex.Message}", ex);
            }
        }

        private static BxmlElement ConvertJsonToBxmlElement(string name, JToken token, Func<string, uint> addString)
        {
            var element = new BxmlElement
            {
                NameIndex = addString(name)
            };

            switch (token.Type)
            {
                case JTokenType.Object:
                    element.Attributes[addString("type")] = addString("object");
                    foreach (var property in ((JObject)token).Properties())
                    {
                        var child = ConvertJsonToBxmlElement(property.Name, property.Value, addString);
                        element.Children.Add(child);
                    }
                    break;

                case JTokenType.Array:
                    element.Attributes[addString("type")] = addString("array");
                    foreach (var item in (JArray)token)
                    {
                        var child = ConvertJsonToBxmlElement("item", item, addString);
                        element.Children.Add(child);
                    }
                    break;

                case JTokenType.String:
                    element.Attributes[addString("type")] = addString("string");
                    element.TextIndex = addString(token.Value<string>() ?? "");
                    break;

                case JTokenType.Integer:
                    element.Attributes[addString("type")] = addString("int");
                    element.TextIndex = addString(token.ToString());
                    break;

                case JTokenType.Float:
                    element.Attributes[addString("type")] = addString("float");
                    element.TextIndex = addString(token.ToString());
                    break;

                case JTokenType.Boolean:
                    element.Attributes[addString("type")] = addString("bool");
                    element.TextIndex = addString(token.ToString().ToLower());
                    break;

                case JTokenType.Null:
                    element.Attributes[addString("type")] = addString("null");
                    break;

                default:
                    element.Attributes[addString("type")] = addString("string");
                    element.TextIndex = addString(token.ToString());
                    break;
            }

            return element;
        }

        private static void WriteElement(BinaryWriter writer, BxmlElement element)
        {
            writer.Write((byte)1);

            writer.Write(element.NameIndex);

            writer.Write((uint)element.Attributes.Count);
            foreach (var attr in element.Attributes)
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
    }
}