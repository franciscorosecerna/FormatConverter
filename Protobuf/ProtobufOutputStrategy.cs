using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using System.Text;

namespace FormatConverter.Protobuf
{
    public class ProtobufOutputStrategy : BaseOutputStrategy
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

            if (processed.Type == JTokenType.Array)
            {
                return StreamArray((JArray)processed);
            }
            else if (processed.Type == JTokenType.Object)
            {
                return StreamObject((JObject)processed);
            }
            else
            {
                return StreamSingle(processed);
            }
        }

        private IEnumerable<string> StreamArray(JArray array)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            IEnumerable<string> Iterator()
            {
                var buffer = new List<JToken>();
                var currentBufferSize = 0;
                var totalProcessed = 0;
                var totalItems = array.Count;

                foreach (var item in array)
                {
                    var serialized = SerializeToProtobuf(item);
                    var itemSizeInBytes = serialized.Length;

                    if (buffer.Count > 0 && currentBufferSize + itemSizeInBytes > bufferSize)
                    {
                        yield return SerializeBufferedItems(buffer);
                        buffer.Clear();
                        currentBufferSize = 0;

                        if (totalItems > 0 && totalProcessed % Math.Max(1, totalItems / 10) == 0)
                        {
                            var progress = (double)totalProcessed / totalItems * 100;
                            Console.WriteLine($"Serialization progress: {progress:F1}%");
                        }
                    }

                    buffer.Add(item);
                    currentBufferSize += itemSizeInBytes;
                    totalProcessed++;
                }

                if (buffer.Count > 0)
                {
                    yield return SerializeBufferedItems(buffer);
                }
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: Protobuf array streaming error ignored: {ex.Message}");
                    return [CreateErrorProtobuf(ex.Message)];
                }
                else
                {
                    throw new FormatException($"Protobuf array streaming failed: {ex.Message}", ex);
                }
            }
        }

        private IEnumerable<string> StreamObject(JObject obj)
        {
            var properties = obj.Properties().ToList();
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var maxPropertiesPerChunk = Math.Max(5, bufferSize / 1024);

            IEnumerable<string> Iterator()
            {
                var currentChunk = new JObject();
                var processedCount = 0;
                var totalProperties = properties.Count;

                foreach (var prop in properties)
                {
                    currentChunk[prop.Name] = prop.Value;
                    processedCount++;

                    if (processedCount >= maxPropertiesPerChunk)
                    {
                        yield return SerializeRegular(currentChunk);
                        currentChunk = new JObject();
                        processedCount = 0;

                        if (totalProperties > 10)
                        {
                            var progress = (double)processedCount / totalProperties * 100;
                            if (progress % 10 < 1)
                            {
                                Console.WriteLine($"Object streaming progress: {progress:F1}%");
                            }
                        }
                    }
                }

                if (processedCount > 0)
                {
                    yield return SerializeRegular(currentChunk);
                }
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: Protobuf object streaming error ignored: {ex.Message}");
                    return [CreateErrorProtobuf(ex.Message)];
                }
                else
                {
                    throw new FormatException($"Protobuf object streaming failed: {ex.Message}", ex);
                }
            }
        }

        private IEnumerable<string> StreamSingle(JToken token)
        {
            IEnumerable<string> Iterator()
            {
                var serialized = SerializeToProtobuf(token);
                yield return serialized;
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: Protobuf single token serialization error ignored: {ex.Message}");
                    return [CreateErrorProtobuf(ex.Message)];
                }
                else
                {
                    throw new FormatException($"Error serializing single token: {ex.Message}", ex);
                }
            }
        }

        private string SerializeBufferedItems(List<JToken> items)
        {
            var combinedStruct = new Struct();

            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    var itemStruct = ConvertJTokenToStruct(items[i]);
                    combinedStruct.Fields[$"item_{i}"] = Value.ForStruct(itemStruct);
                }
                catch (Exception ex)
                {
                    if (Config.IgnoreErrors)
                    {
                        Console.WriteLine($"Warning: Protobuf buffer item serialization error ignored: {ex.Message}");
                        continue;
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            var bytes = combinedStruct.ToByteArray();
            return FormatOutput(bytes);
        }

        private string SerializeRegular(JToken data)
        {
            var processed = ProcessDataBeforeSerialization(data);

            try
            {
                return SerializeToProtobuf(processed);
            }
            catch (Exception ex)
            {
                return HandleSerializationError(ex, processed);
            }
        }

        private string SerializeToProtobuf(JToken data)
        {
            IMessage protobufMessage;

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
            return FormatOutput(bytes);
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys)
                data = SortKeysRecursively(data);

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
                return new JArray(data);

            return data;
        }

        private string HandleSerializationError(Exception ex, JToken data)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: Protobuf serialization error ignored: {ex.Message}");
                return CreateErrorProtobuf(ex.Message);
            }
            throw new FormatException($"Protobuf serialization failed: {ex.Message}", ex);
        }

        private string CreateErrorProtobuf(string errorMessage)
        {
            var errorStruct = new Struct();
            errorStruct.Fields["error"] = Value.ForString(errorMessage);
            errorStruct.Fields["timestamp"] = Value.ForString(DateTime.UtcNow.ToString("O"));

            var bytes = errorStruct.ToByteArray();
            return FormatOutput(bytes);
        }

        private static bool ShouldSerializeAsAny(JToken data)
        {
            return data is JObject obj &&
                   obj.ContainsKey("@type") &&
                   obj.ContainsKey("value");
        }

        private static bool ShouldSerializeAsValue(JToken data)
        {
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

        private static Any ConvertJTokenToAny(JToken data)
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

        private static Value ConvertStringToValue(string? str)
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