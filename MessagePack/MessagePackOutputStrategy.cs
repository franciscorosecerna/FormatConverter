using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using MessagePack;
using MessagePack.Resolvers;
using System.Text;

namespace FormatConverter.MessagePack
{
    public class MessagePackOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            if (Config.UseStreaming)
            {
                return string.Join("", SerializeStream([data]));
            }

            return SerializeRegular(data);
        }

        public override IEnumerable<string> SerializeStream(IEnumerable<JToken> data)
        {
            if (!Config.UseStreaming)
            {
                yield return Serialize(new JArray(data));
                yield break;
            }

            foreach (var token in data)
            {
                foreach (var chunk in StreamToken(token))
                {
                    yield return chunk;
                }
            }
        }

        private IEnumerable<string> StreamToken(JToken token)
        {
            var processed = ProcessDataBeforeSerialization(token);

            return processed.Type switch
            {
                JTokenType.Array => StreamArray((JArray)processed),
                JTokenType.Object => StreamObject((JObject)processed),
                _ => StreamSingle(processed)
            };
        }

        private IEnumerable<string> StreamArray(JArray array)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var buffer = new List<JToken>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var itemCount = array.Count;

            foreach (var item in array)
            {
                var serializedItem = SerializeTokenToBytes(item);
                var itemSizeInBytes = serializedItem.Length;

                if (buffer.Count > 0 && currentBufferSize + itemSizeInBytes > bufferSize)
                {
                    yield return SerializeChunk(buffer);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (itemCount > 0 && totalProcessed % Math.Max(1, itemCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / itemCount * 100;
                        Console.WriteLine($"Array serialization progress: {progress:F1}%");
                    }
                }

                buffer.Add(item);
                currentBufferSize += itemSizeInBytes;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeChunk(buffer);
            }
        }

        private IEnumerable<string> StreamObject(JObject obj)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var properties = Config.SortKeys
                ? obj.Properties().OrderBy(p => p.Name).ToList()
                : obj.Properties().ToList();

            var buffer = new Dictionary<string, JToken>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var propertyCount = properties.Count;

            foreach (var property in properties)
            {
                if (Config.NoMetadata && IsMetadataField(property.Name))
                    continue;

                var serializedValue = SerializeTokenToBytes(property.Value);
                var propertySizeInBytes = Encoding.UTF8.GetByteCount(property.Name) + serializedValue.Length;

                if (buffer.Count > 0 && currentBufferSize + propertySizeInBytes > bufferSize)
                {
                    yield return SerializeChunk(new JObject(buffer.Select(kvp =>
                        new JProperty(kvp.Key, kvp.Value))));
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (propertyCount > 0 && totalProcessed % Math.Max(1, propertyCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / propertyCount * 100;
                        Console.WriteLine($"Object serialization progress: {progress:F1}%");
                    }
                }

                buffer[property.Name] = property.Value;
                currentBufferSize += propertySizeInBytes;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeChunk(new JObject(buffer.Select(kvp =>
                    new JProperty(kvp.Key, kvp.Value))));
            }
        }

        private IEnumerable<string> StreamSingle(JToken token)
        {
            IEnumerable<string> Iterator()
            {
                yield return SerializeChunk(token);
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return [CreateErrorOutput(ex.Message)];
                }
                else
                {
                    throw new FormatException($"Error serializing single token: {ex.Message}", ex);
                }
            }
        }

        private string SerializeChunk(JToken token)
        {
            try
            {
                var obj = ConvertJTokenToObject(token);

                if (obj == null && token.Type != JTokenType.Null)
                {
                    if (Config.IgnoreErrors)
                    {
                        return CreateErrorOutput("Failed to convert token to object");
                    }
                    throw new FormatException("Failed to convert JSON to object for MessagePack serialization");
                }

                var options = GetMessagePackOptions();
                var bytes = MessagePackSerializer.Serialize(obj, options);

                return FormatOutput(bytes);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                if (Config.IgnoreErrors)
                {
                    return CreateErrorOutput(ex.Message);
                }
                throw new FormatException($"MessagePack serialization error: {ex.Message}", ex);
            }
        }

        private string SerializeChunk(List<JToken> items)
        {
            var array = new JArray(items);
            return SerializeChunk(array);
        }

        private string SerializeRegular(JToken data)
        {
            var processed = ProcessDataBeforeSerialization(data);
            return SerializeChunk(processed);
        }

        private byte[] SerializeTokenToBytes(JToken token)
        {
            var obj = ConvertJTokenToObject(token);
            var options = GetMessagePackOptions();
            return MessagePackSerializer.Serialize(obj, options);
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys && data is JObject)
                data = SortKeysRecursively(data);

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
                return new JArray(data);

            return data;
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

        private MessagePackSerializerOptions GetMessagePackOptions()
        {
            var options = MessagePackSerializerOptions.Standard
                .WithResolver(ContractlessStandardResolver.Instance);

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
                finally { }
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
                "raw" => Encoding.UTF8.GetString(bytes),
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

        private string CreateErrorOutput(string errorMessage)
        {
            var errorObj = new JObject
            {
                ["error"] = errorMessage,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            var errorBytes = Encoding.UTF8.GetBytes(errorObj.ToString(Newtonsoft.Json.Formatting.None));
            return FormatOutput(errorBytes);
        }
    }
}