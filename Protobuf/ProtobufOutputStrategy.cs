using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;

namespace FormatConverter.Protobuf
{
    public class ProtobufOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            try
            {
                IMessage protobufMessage;

                // Determine the appropriate Protobuf message type based on data structure
                if (ShouldSerializeAsAny(data))
                {
                    protobufMessage = ConvertJTokenToAny(data);
                }
                else if (ShouldSerializeAsValue(data))
                {
                    protobufMessage = ConvertJTokenToValue(data);
                }
                else
                {
                    protobufMessage = ConvertJTokenToStruct(data);
                }

                var bytes = protobufMessage.ToByteArray();

                // Return format based on configuration
                return FormatOutput(bytes);
            }
            catch (Exception ex)
            {
                throw new FormatException($"Protobuf serialization failed: {ex.Message}", ex);
            }
        }

        private bool ShouldSerializeAsAny(JToken data)
        {
            return data is JObject obj &&
                   obj.ContainsKey("@type") &&
                   obj.ContainsKey("value");
        }

        private bool ShouldSerializeAsValue(JToken data)
        {
            // Single primitive values should be serialized as Value
            return data.Type switch
            {
                JTokenType.String => true,
                JTokenType.Integer => true,
                JTokenType.Float => true,
                JTokenType.Boolean => true,
                JTokenType.Null => true,
                _ => false
            };
        }

        private Any ConvertJTokenToAny(JToken data)
        {
            if (data is not JObject obj)
                throw new FormatException("Any message must be a JSON object with @type and value fields");

            var typeUrl = obj["@type"]?.Value<string>() ??
                throw new FormatException("Any message missing @type field");

            var valueString = obj["value"]?.Value<string>() ??
                throw new FormatException("Any message missing value field");

            var valueBytes = Convert.FromBase64String(valueString);

            return new Any
            {
                TypeUrl = typeUrl,
                Value = ByteString.CopyFrom(valueBytes)
            };
        }

        private Value ConvertJTokenToValue(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => ConvertStringToValue(token.Value<string>()),
                JTokenType.Integer => Value.ForNumber(FormatNumberValue(token.Value<long>())),
                JTokenType.Float => Value.ForNumber(FormatNumberValue(token.Value<double>())),
                JTokenType.Boolean => Value.ForBool(token.Value<bool>()),
                JTokenType.Null => Value.ForNull(),
                JTokenType.Array => ConvertJArrayToValue((JArray)token),
                JTokenType.Object => ConvertJObjectToValue((JObject)token),
                JTokenType.Date => ConvertDateToValue(token.Value<DateTime>()),
                _ => Value.ForString(token.ToString())
            };
        }

        private Value ConvertStringToValue(string? str)
        {
            if (str == null) return Value.ForNull();
            return Value.ForString(str);
        }

        private Value ConvertDateToValue(DateTime dateTime)
        {
            var formattedDate = FormatDateTime(dateTime);
            return Value.ForString(formattedDate);
        }

        private Value ConvertJArrayToValue(JArray array)
        {
            var listValue = new ListValue();

            foreach (var item in array)
            {
                listValue.Values.Add(ConvertJTokenToValue(item));
            }

            return new Value { ListValue = listValue };
        }

        private Value ConvertJObjectToValue(JObject obj)
        {
            var structValue = ConvertJObjectToStruct(obj);
            return Value.ForStruct(structValue);
        }

        private Struct ConvertJTokenToStruct(JToken token)
        {
            if (token is not JObject obj)
                throw new FormatException("Cannot convert non-object JToken to Protobuf Struct");

            return ConvertJObjectToStruct(obj);
        }

        private Struct ConvertJObjectToStruct(JObject json)
        {
            var structValue = new Struct();

            var properties = Config.SortKeys
                ? json.Properties().OrderBy(p => p.Name)
                : json.Properties();

            foreach (var property in properties)
            {
                if (Config.NoMetadata && IsMetadataField(property.Name))
                    continue;

                structValue.Fields[property.Name] = ConvertJTokenToValue(property.Value);
            }

            return structValue;
        }

        private static bool IsMetadataField(string fieldName)
        {
            return fieldName.StartsWith("_") ||
                   fieldName.StartsWith("@") ||
                   fieldName.Equals("$schema", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("metadata", StringComparison.OrdinalIgnoreCase);
        }

        private double FormatNumberValue(double number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "scientific" => double.Parse(number.ToString("E")),
                    "hexadecimal" => Convert.ToDouble(Convert.ToInt64(number)),
                    _ => number
                };
            }
            return number;
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

            return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
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
            var hex = BitConverter.ToString(bytes).Replace("-", "");

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