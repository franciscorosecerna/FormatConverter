using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            string json = JsonConvert.SerializeXNode(doc, Formatting.None, omitRootObject: true);
            return JObject.Parse(json);
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

        private static object ConvertJTokenToObject(JToken token)
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
            var doc = JsonConvert.DeserializeXNode(json.ToString(), "Root");
            return doc?.ToString() ?? throw new InvalidOperationException("Failed to convert JSON to XML.");
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

        private static JObject BxmlToJson(string base64Input)
        {
            try
            {
                byte[] bxmlData = Convert.FromBase64String(base64Input);

                string xmlString = BxmlToXml(bxmlData);

                return XmlToJson(xmlString);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize BXML to JSON: {ex.Message}", ex);
            }
        }

        private static string JsonToBxml(JObject json)
        {
            try
            {
                string xmlString = JsonToXml(json);

                byte[] bxmlData = XmlToBxml(xmlString);

                return Convert.ToBase64String(bxmlData);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize JSON to BXML: {ex.Message}", ex);
            }
        }

        private static string BxmlToXml(byte[] bxmlData)
        {
            try
            {
                using var buffer = new MemoryStream(bxmlData);
                using var reader = new BinaryReader(buffer);
                var signature = reader.ReadBytes(4);
                if (Encoding.ASCII.GetString(signature) != "BXML")
                    throw new InvalidOperationException("Invalid BXML signature");

                uint version = reader.ReadUInt32();
                if (version != 1)
                    throw new InvalidOperationException($"Unsupported BXML version: {version}");

                uint stringTableOffset = reader.ReadUInt32();

                long currentPos = buffer.Position;
                buffer.Seek(stringTableOffset, SeekOrigin.Begin);

                uint stringCount = reader.ReadUInt32();
                var stringTable = new string[stringCount];

                for (int i = 0; i < stringCount; i++)
                {
                    uint stringLength = reader.ReadUInt32();
                    byte[] stringBytes = reader.ReadBytes((int)stringLength);
                    stringTable[i] = Encoding.UTF8.GetString(stringBytes);
                }

                buffer.Seek(currentPos, SeekOrigin.Begin);

                XElement root = ReadElement(reader, stringTable);

                var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

                return doc.ToString();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to convert BXML to XML: {ex.Message}", ex);
            }
        }

        private static byte[] XmlToBxml(string xmlString)
        {
            try
            {
                XDocument doc = XDocument.Parse(xmlString);

                using var buffer = new MemoryStream();
                using var writer = new BinaryWriter(buffer);

                var stringTable = new Dictionary<string, ushort>();
                ushort stringCounter = 0;

                ushort AddString(string s)
                {
                    if (string.IsNullOrEmpty(s)) s = "";
                    if (!stringTable.ContainsKey(s))
                    {
                        stringTable[s] = stringCounter++;
                    }
                    return stringTable[s];
                }

                writer.Write(Encoding.ASCII.GetBytes("BXML"));
                writer.Write((uint)1);

                long stringTableOffsetPosition = buffer.Position;
                writer.Write((uint)0);

                if (doc.Root != null)
                {
                    ProcessElement(doc.Root, writer, AddString);
                }

                long stringTableOffset = buffer.Position;
                writer.Write((uint)stringTable.Count);

                var sortedStrings = new string[stringTable.Count];
                foreach (var kvp in stringTable)
                {
                    sortedStrings[kvp.Value] = kvp.Key;
                }

                foreach (var str in sortedStrings)
                {
                    byte[] strBytes = Encoding.UTF8.GetBytes(str);
                    writer.Write((uint)strBytes.Length);
                    writer.Write(strBytes);
                }

                long currentPos = buffer.Position;
                buffer.Seek(stringTableOffsetPosition, SeekOrigin.Begin);
                writer.Write((uint)stringTableOffset);
                buffer.Seek(currentPos, SeekOrigin.Begin);

                return buffer.ToArray();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to convert XML to BXML: {ex.Message}", ex);
            }
        }

        private static XElement ReadElement(BinaryReader reader, string[] stringTable)
        {
            byte nodeType = reader.ReadByte();

            if (nodeType != 1)
                throw new InvalidOperationException($"Unexpected node type: {nodeType}");

            ushort tagIndex = reader.ReadUInt16();
            string tagName = stringTable[tagIndex];

            var element = new XElement(tagName);

            ushort attrCount = reader.ReadUInt16();
            for (int i = 0; i < attrCount; i++)
            {
                ushort nameIndex = reader.ReadUInt16();
                ushort valueIndex = reader.ReadUInt16();
                string attrName = stringTable[nameIndex];
                string attrValue = stringTable[valueIndex];
                element.SetAttributeValue(attrName, attrValue);
            }

            long position = reader.BaseStream.Position;
            if (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte nextByte = reader.ReadByte();
                if (nextByte == 2)
                {
                    ushort textIndex = reader.ReadUInt16();
                    element.Value = stringTable[textIndex];
                }
                else
                {
                    reader.BaseStream.Seek(position, SeekOrigin.Begin);
                }
            }

            ushort childCount = reader.ReadUInt16();
            for (int i = 0; i < childCount; i++)
            {
                XElement child = ReadElement(reader, stringTable);
                element.Add(child);
            }

            return element;
        }

        private static void ProcessElement(XElement element, BinaryWriter writer, Func<string, ushort> addString)
        {
            writer.Write((byte)1);

            ushort tagIndex = addString(element.Name.LocalName);
            writer.Write(tagIndex);

            var attrs = element.Attributes().ToArray();
            writer.Write((ushort)attrs.Length);

            foreach (var attr in attrs)
            {
                ushort nameIndex = addString(attr.Name.LocalName);
                ushort valueIndex = addString(attr.Value);
                writer.Write(nameIndex);
                writer.Write(valueIndex);
            }

            if (!string.IsNullOrEmpty(element.Value) && !element.HasElements)
            {
                writer.Write((byte)2);
                ushort textIndex = addString(element.Value);
                writer.Write(textIndex);
            }

            var children = element.Elements().ToArray();
            writer.Write((ushort)children.Length);

            foreach (var child in children)
            {
                ProcessElement(child, writer, addString);
            }
        }        
    }
}