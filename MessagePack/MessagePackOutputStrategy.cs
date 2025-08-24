using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using MessagePack;
using MessagePack.Resolvers;

namespace FormatConverter.MessagePack
{
    public class MessagePackOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            try
            {
                var obj = ConvertJTokenToObject(data);

                if (obj == null && data.Type != JTokenType.Null)
                {
                    throw new FormatException("Failed to convert JSON to object for MessagePack serialization");
                }

                var options = GetMessagePackOptions();
                var bytes = MessagePackSerializer.Serialize(obj, options);

                return FormatOutput(bytes);
            }
            catch (MessagePackSerializationException ex)
            {
                throw new FormatException($"MessagePack serialization failed: {ex.Message}", ex);
            }
            catch (Exception ex) when (!(ex is FormatException))
            {
                throw new FormatException($"MessagePack serialization error: {ex.Message}", ex);
            }
        }

        private MessagePackSerializerOptions GetMessagePackOptions()
        {
            var options = MessagePackSerializerOptions.Standard;

            options = options.WithResolver(ContractlessStandardResolver.Instance);

            if (!string.IsNullOrEmpty(Config.Compression))
            {
                options = Config.Compression.ToLower() switch
                {
                    "lz4" => options.WithCompression(MessagePackCompression.Lz4Block),
                    "lz4array" => options.WithCompression(MessagePackCompression.Lz4BlockArray),
                    _ => options
                };
            }

            if (Config.StrictMode)
            {
                options = options.WithSecurity(MessagePackSecurity.UntrustedData);
            }

            if (Config.MaxDepth.HasValue)
            {
                var security = MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(Config.MaxDepth.Value);
                options = options.WithSecurity(security);
            }

            return options;
        }

        private object? ConvertJTokenToObject(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    return ConvertJObjectToDictionary((JObject)token);

                case JTokenType.Array:
                    return ConvertJArrayToList((JArray)token);

                case JTokenType.String:
                    var stringValue = token.Value<string>();
                    return ProcessStringValue(stringValue);

                case JTokenType.Integer:
                    return ProcessIntegerValue(token.Value<long>());

                case JTokenType.Float:
                    return FormatNumberValue(token.Value<double>());

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Date:
                    return FormatDateTimeValue(token.Value<DateTime>());

                case JTokenType.Null:
                    return null;

                case JTokenType.Bytes:
                    return token.Value<byte[]>();

                default:
                    return token.ToString();
            }
        }

        private Dictionary<string, object?> ConvertJObjectToDictionary(JObject jObject)
        {
            var dict = new Dictionary<string, object?>();

            var properties = Config.SortKeys
                ? jObject.Properties().OrderBy(p => p.Name)
                : jObject.Properties();

            foreach (var property in properties)
            {
                if (Config.NoMetadata && IsMetadataField(property.Name))
                    continue;

                dict[property.Name] = ConvertJTokenToObject(property.Value);
            }

            return dict;
        }

        private List<object?> ConvertJArrayToList(JArray jArray)
        {
            var list = new List<object?>();

            foreach (var item in jArray)
            {
                list.Add(ConvertJTokenToObject(item));
            }

            if (Config.ArrayWrap && list.Count == 1)
            {
                return [list];
            }

            return list;
        }

        private static object ProcessStringValue(string? value)
        {
            if (value == null) return null!;

            if (ShouldTreatAsBinary(value))
            {
                try
                {
                    return Convert.FromBase64String(value);
                }
                catch { }
            }
            return value;
        }

        private static long ProcessIntegerValue(long value)
        {
            if (value >= byte.MinValue && value <= byte.MaxValue)
                return (byte)value;
            if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
                return (sbyte)value;
            if (value >= short.MinValue && value <= short.MaxValue)
                return (short)value;
            if (value >= ushort.MinValue && value <= ushort.MaxValue)
                return (ushort)value;
            if (value >= int.MinValue && value <= int.MaxValue)
                return (int)value;
            if (value >= uint.MinValue && value <= uint.MaxValue)
                return (uint)value;

            return value;
        }

        private static bool IsMetadataField(string fieldName)
        {
            return fieldName.StartsWith("_") ||
                   fieldName.StartsWith("@") ||
                   fieldName.StartsWith("$") ||
                   fieldName.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("version", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldTreatAsBinary(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 8 || value.Length % 4 != 0)
                return false;

            return value.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');
        }

        private double FormatNumberValue(double number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "scientific" => double.Parse(number.ToString("E")),
                    "hexadecimal" => (long)number,
                    _ => number
                };
            }
            return number;
        }

        private object FormatDateTimeValue(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds(),
                    "rfc3339" => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
                    "timestamp" => ((DateTimeOffset)dateTime).ToUnixTimeMilliseconds(),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }

            return dateTime;
        }

        private string FormatOutput(byte[] bytes)
        {
            return Config.NumberFormat?.ToLower() switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
                "binary" => FormatAsBinary(bytes),
                "raw" => System.Text.Encoding.UTF8.GetString(bytes),
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