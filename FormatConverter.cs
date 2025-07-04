using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FormatConverter
{
    public static class FormatConverter
    {
        public static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "json", "yaml", "xml", "messagepack", "cbor"
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
    }
}