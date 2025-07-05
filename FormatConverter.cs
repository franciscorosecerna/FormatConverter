using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System.Text;
using System.Xml.Linq;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FormatConverter
{
    public static class FormatConverter
    {
        public static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "json", "yaml", "xml", "messagepack", "cbor", "protobuf", "bxml"
        };

        public static string ConvertFormat(string input, string fromFormat, string toFormat)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Input cannot be null or empty.", nameof(input));

            if (!SupportedFormats.Contains(fromFormat))
                throw new ArgumentException($"Unsupported input format: {fromFormat}. Supported formats: {string.Join(", ", SupportedFormats)}");

            if (!SupportedFormats.Contains(toFormat))
                throw new ArgumentException($"Unsupported output format: {toFormat}. Supported formats: {string.Join(", ", SupportedFormats)}");

            try
            {
                JObject data = ParseInput(input, fromFormat);
                return SerializeOutput(data, toFormat);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Invalid {fromFormat} input: {ex.Message}", ex);
            }
            catch (YamlException ex)
            {
                throw new FormatException($"Invalid YAML input: {ex.Message}", ex);
            }
            catch (MessagePackSerializationException ex)
            {
                throw new FormatException($"Invalid MessagePack input: {ex.Message}", ex);
            }
            catch (CBORException ex)
            {
                throw new FormatException($"Invalid CBOR input: {ex.Message}", ex);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new FormatException($"Invalid Protobuf input: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new FormatException($"Conversion failed: {ex.Message}", ex);
            }
        }

        private static JObject ParseInput(string input, string fromFormat)
        {
            return fromFormat.ToLowerInvariant() switch
            {
                "json" => JObject.Parse(input),
                "yaml" => YamlToJson(input),
                "xml" => XmlToJson(input),
                "messagepack" => MessagePackToJson(input),
                "cbor" => CborToJson(input),
                "protobuf" => ProtobufToJson(input),
                "bxml" => BxmlToJson(input),
                _ => throw new InvalidOperationException("Unreachable code (invalid input format)")
            };
        }

        private static string SerializeOutput(JObject data, string toFormat)
        {
            return toFormat.ToLowerInvariant() switch
            {
                "json" => data.ToString(Formatting.Indented),
                "yaml" => JsonToYaml(data),
                "xml" => JsonToXml(data),
                "messagepack" => JsonToMessagePack(data),
                "cbor" => JsonToCbor(data),
                "protobuf" => JsonToProtobuf(data),
                "bxml" => JsonToBxml(data),
                _ => throw new InvalidOperationException("Unreachable code (invalid output format)")
            };
        }

        private static JObject YamlToJson(string yaml)
        {
            var deserializer = new DeserializerBuilder().Build();
            var yamlObject = deserializer.Deserialize(new StringReader(yaml));
            string json = JsonConvert.SerializeObject(yamlObject);
            return JObject.Parse(json);
        }

        private static JObject XmlToJson(string xml)
        {
            var doc = XDocument.Parse(xml);

            var json = new JObject();
            foreach (var elem in doc.Root.Elements())
            {
                var jvalue = ConvertXmlElementToJToken(elem);
                json[elem.Name.LocalName] = jvalue;
            }

            return json;
        }

        private static JToken ConvertXmlElementToJToken(XElement element)
        {
            string? type = element.Attribute("type")?.Value ?? "string";

            if (type == "object")
            {
                var childObj = new JObject();
                foreach (var child in element.Elements())
                {
                    childObj[child.Name.LocalName] = ConvertXmlElementToJToken(child);
                }
                return childObj;
            }
            else if (type == "array")
            {
                var array = new JArray();
                foreach (var item in element.Elements())
                {
                    array.Add(ConvertXmlElementToJToken(item));
                }
                return array;
            }

            if (type == "null")
                return JValue.CreateNull();

            string value = element.Value;

            return type switch
            {
                "int" => int.TryParse(value, out var i) ? new JValue(i) : JValue.CreateNull(),
                "float" => double.TryParse(value, out var d) ? new JValue(d) : JValue.CreateNull(),
                "bool" => bool.TryParse(value, out var b) ? new JValue(b) : JValue.CreateNull(),
                "string" => new JValue(value),
                _ => new JValue(value)
            };
        }

        private static string JsonToYaml(JObject json)
        {
            ArgumentNullException.ThrowIfNull(json);

            var obj = ConvertJTokenToObject(json) ??
                throw new InvalidOperationException("Failed to convert JSON to object.");

            var serializer = new SerializerBuilder()
                .WithIndentedSequences()
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();

            return serializer.Serialize(obj);
        }

        private static object? ConvertJTokenToObject(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var property in token.Children<JProperty>())
                    {
                        dict[property.Name] = ConvertJTokenToObject(property.Value);
                    }
                    return dict;

                case JTokenType.Array:
                    var list = new List<object>();
                    foreach (var item in token.Children())
                    {
                        list.Add(ConvertJTokenToObject(item));
                    }
                    return list;

                case JTokenType.String:
                    return token.Value<string>();

                case JTokenType.Integer:
                    return token.Value<long>();

                case JTokenType.Float:
                    return token.Value<double>();

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Null:
                    return null;

                case JTokenType.Date:
                    return token.Value<DateTime>();

                default:
                    return token.ToString();
            }
        }

        private static string JsonToXml(JObject json)
        {
            XElement root = new("Root");

            foreach (var prop in json.Properties())
            {
                XElement elem = ConvertJsonTokenToXmlElement(prop.Name, prop.Value);
                root.Add(elem);
            }

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            return doc.ToString();
        }

        private static XElement ConvertJsonTokenToXmlElement(string name, JToken token)
        {
            var element = new XElement(name);

            switch (token.Type)
            {
                case JTokenType.Integer:
                    element.Value = token.ToString();
                    element.SetAttributeValue("type", "int");
                    break;
                case JTokenType.Float:
                    element.Value = token.ToString();
                    element.SetAttributeValue("type", "float");
                    break;
                case JTokenType.Boolean:
                    element.Value = token.ToString().ToLower();
                    element.SetAttributeValue("type", "bool");
                    break;
                case JTokenType.Null:
                    element.SetAttributeValue("type", "null");
                    break;
                case JTokenType.String:
                    element.Value = token.ToString();
                    element.SetAttributeValue("type", "string");
                    break;
                case JTokenType.Object:
                    element.SetAttributeValue("type", "object");
                    foreach (var child in ((JObject)token).Properties())
                    {
                        element.Add(ConvertJsonTokenToXmlElement(child.Name, child.Value));
                    }
                    break;
                case JTokenType.Array:
                    element.SetAttributeValue("type", "array");
                    foreach (var item in (JArray)token)
                    {
                        element.Add(ConvertJsonTokenToXmlElement("item", item));
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported token type: {token.Type}");
            }

            return element;
        }

        private static JObject MessagePackToJson(string base64Input)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64Input);

                var options = MessagePackSerializerOptions.Standard
                    .WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance);

                var obj = MessagePackSerializer.Deserialize<object>(bytes, options);
                string json = JsonConvert.SerializeObject(obj, Formatting.None);
                return JObject.Parse(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize MessagePack to JSON: {ex.Message}", ex);
            }
        }

        private static string JsonToMessagePack(JObject json)
        {
            try
            {
                var obj = ConvertJTokenToObject(json);

                var options = MessagePackSerializerOptions.Standard
                    .WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance);

                byte[] bytes = MessagePackSerializer.Serialize(obj, options);
                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize JSON to MessagePack: {ex.Message}", ex);
            }
        }

        private static JObject CborToJson(string base64Input)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64Input);

                CBORObject cborObj = CBORObject.DecodeFromBytes(bytes);

                string jsonString = cborObj.ToJSONString();

                return JObject.Parse(jsonString);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize CBOR to JSON: {ex.Message}", ex);
            }
        }

        private static string JsonToCbor(JObject json)
        {
            try
            {
                var obj = ConvertJTokenToObject(json);

                CBORObject cborObj = CBORObject.FromJSONString(json.ToString());

                byte[] bytes = cborObj.EncodeToBytes();

                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize JSON to CBOR: {ex.Message}", ex);
            }
        }

        private static JObject ProtobufToJson(string base64Input)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64Input);

                Struct protobufStruct = Struct.Parser.ParseFrom(bytes);

                string jsonString = protobufStruct.ToString();

                return JObject.Parse(jsonString);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize Protobuf to JSON: {ex.Message}", ex);
            }
        }

        private static string JsonToProtobuf(JObject json)
        {
            try
            {
                Struct protobufStruct = ConvertJObjectToStruct(json);

                byte[] bytes = protobufStruct.ToByteArray();

                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize JSON to Protobuf: {ex.Message}", ex);
            }
        }

        private static Struct ConvertJObjectToStruct(JObject json)
        {
            var structValue = new Struct();

            foreach (var property in json.Properties())
            {
                structValue.Fields[property.Name] = ConvertJTokenToValue(property.Value);
            }

            return structValue;
        }

        private static Value ConvertJTokenToValue(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => Value.ForString(token.Value<string>() ?? ""),
                JTokenType.Integer => Value.ForNumber(token.Value<double>()),
                JTokenType.Float => Value.ForNumber(token.Value<double>()),
                JTokenType.Boolean => Value.ForBool(token.Value<bool>()),
                JTokenType.Null => Value.ForNull(),
                JTokenType.Array => ConvertJArrayToValue(token as JArray),
                JTokenType.Object => ConvertJObjectToValue(token as JObject),
                JTokenType.Date => Value.ForString(token.Value<DateTime>().ToString("O")),
                _ => Value.ForString(token.ToString())
            };
        }

        private static Value ConvertJArrayToValue(JArray? array)
        {
            if (array == null) return Value.ForNull();

            var values = array.Select(ConvertJTokenToValue).ToArray();
            return Value.ForList(values);
        }

        private static Value ConvertJObjectToValue(JObject? obj)
        {
            if (obj == null) return Value.ForNull();

            return Value.ForStruct(ConvertJObjectToStruct(obj));
        }

        private static string JsonToBxml(JObject json)
        {
            try
            {
                using var buffer = new MemoryStream();
                using var writer = new BinaryWriter(buffer);
                writer.Write(Encoding.ASCII.GetBytes("BXML"));
                writer.Write((uint)1);

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

                var rootElement = ConvertJsonToBxmlElement("Root", json, AddString);
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
                throw new InvalidOperationException($"Failed to serialize JSON to BXML: {ex.Message}", ex);
            }
        }

        private static JObject BxmlToJson(string base64Input)
        {
            try
            {
                byte[] bxmlData = Convert.FromBase64String(base64Input);

                using var buffer = new MemoryStream(bxmlData);
                using var reader = new BinaryReader(buffer);
                var signature = reader.ReadBytes(4);
                if (Encoding.ASCII.GetString(signature) != "BXML")
                    throw new InvalidOperationException("Invalid BXML signature");

                uint version = reader.ReadUInt32();
                if (version != 1)
                    throw new InvalidOperationException($"Unsupported BXML version: {version}");

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
                throw new InvalidOperationException($"Failed to deserialize BXML to JSON: {ex.Message}", ex);
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
                            "bool" => (JToken)(bool.TryParse(value, out var b) ? b : false),
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