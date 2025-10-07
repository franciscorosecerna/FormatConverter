using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System.Text;

namespace FormatConverter.Cbor
{
    public class CborOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            if (Config.UseStreaming)
            {
                var allBytes = SerializeStream([data])
                    .SelectMany(chunk => Convert.FromBase64String(chunk))
                    .ToArray();
                return FormatOutput(allBytes);
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
                JTokenType.Array => StreamChunked(((JArray)processed).Children()),
                JTokenType.Object => StreamChunkedObject((JObject)processed),
                _ => StreamSingle(processed)
            };
        }

        private IEnumerable<string> StreamChunked(IEnumerable<JToken> items)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var buffer = new List<JToken>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var itemCount = items is ICollection<JToken> collection ? collection.Count : -1;

            foreach (var item in items)
            {
                var cborItem = ConvertJTokenToCbor(item);
                var itemBytes = cborItem.EncodeToBytes();
                var itemSizeInBytes = itemBytes.Length;

                if (buffer.Count > 0 && currentBufferSize + itemSizeInBytes > bufferSize)
                {
                    yield return SerializeChunk(buffer);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (itemCount > 0 && totalProcessed % Math.Max(1, itemCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / itemCount * 100;
                        Console.WriteLine($"Serialization progress: {progress:F1}%");
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

        private IEnumerable<string> StreamChunkedObject(JObject obj)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var buffer = new List<JProperty>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var properties = Config.SortKeys
                ? obj.Properties().OrderBy(p => p.Name)
                : obj.Properties();
            var propCount = obj.Properties().Count();

            foreach (var prop in properties)
            {
                var cborKey = CBORObject.FromObject(prop.Name);
                var cborValue = ConvertJTokenToCbor(prop.Value);
                var propBytes = cborKey.EncodeToBytes().Length + cborValue.EncodeToBytes().Length;

                if (buffer.Count > 0 && currentBufferSize + propBytes > bufferSize)
                {
                    yield return SerializeChunkProperties(buffer);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (totalProcessed % Math.Max(1, propCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / propCount * 100;
                        Console.WriteLine($"Serialization progress: {progress:F1}%");
                    }
                }

                buffer.Add(prop);
                currentBufferSize += propBytes;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeChunkProperties(buffer);
            }
        }

        private string SerializeChunk(List<JToken> items)
        {
            try
            {
                var cborArray = CBORObject.NewArray();
                foreach (var item in items)
                {
                    cborArray.Add(ConvertJTokenToCbor(item));
                }

                var options = GetCborEncodeOptions();
                var bytes = cborArray.EncodeToBytes(options);
                return FormatOutput(bytes);
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return FormatOutput(CreateErrorCbor(ex.Message, items.Count).EncodeToBytes());
                }
                else
                {
                    throw new FormatException($"Error serializing chunk: {ex.Message}", ex);
                }
            }
        }

        private string SerializeChunkProperties(List<JProperty> properties)
        {
            try
            {
                var cborMap = CBORObject.NewMap();
                foreach (var prop in properties)
                {
                    var key = CBORObject.FromObject(prop.Name);
                    var value = ConvertJTokenToCbor(prop.Value);
                    cborMap[key] = value;
                }

                var options = GetCborEncodeOptions();
                var bytes = cborMap.EncodeToBytes(options);
                return FormatOutput(bytes);
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return FormatOutput(CreateErrorCbor(ex.Message, properties.Count).EncodeToBytes());
                }
                else
                {
                    throw new FormatException($"Error serializing properties chunk: {ex.Message}", ex);
                }
            }
        }

        private IEnumerable<string> StreamSingle(JToken token)
        {
            IEnumerable<string> Iterator()
            {
                var cborObj = ConvertJTokenToCbor(token);
                var options = GetCborEncodeOptions();
                var bytes = cborObj.EncodeToBytes(options);
                yield return FormatOutput(bytes);
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return [FormatOutput(CreateErrorCbor(ex.Message, 1).EncodeToBytes())];
                }
                else
                {
                    throw new FormatException($"Error serializing single token: {ex.Message}", ex);
                }
            }
        }

        private string SerializeRegular(JToken data)
        {
            try
            {
                var processed = ProcessDataBeforeSerialization(data);
                var cborObj = ConvertJTokenToCbor(processed);

                if (cborObj == null)
                    throw new FormatException("Failed to convert JSON to CBOR object");

                var options = GetCborEncodeOptions();
                var bytes = cborObj.EncodeToBytes(options);

                return FormatOutput(bytes);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                throw new FormatException($"CBOR serialization error: {ex.Message}", ex);
            }
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys)
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

        private CBORObject ConvertJTokenToCbor(JToken token)
        {
            if (token == null) return CBORObject.Null;

            return token.Type switch
            {
                JTokenType.Object => ConvertJObjectToCborMap((JObject)token),
                JTokenType.Array => ConvertJArrayToCborArray((JArray)token),
                JTokenType.String => ConvertStringToCbor(token.Value<string>()),
                JTokenType.Integer => CBORObject.FromObject(token.Value<long>()),
                JTokenType.Float => CBORObject.FromObject(FormatNumberValue(token.Value<double>())),
                JTokenType.Boolean => CBORObject.FromObject(token.Value<bool>()),
                JTokenType.Date => ConvertDateToCbor(token.Value<DateTime>()),
                JTokenType.Null => CBORObject.Null,
                JTokenType.Undefined => CBORObject.Undefined,
                _ => CBORObject.FromObject(token.ToString())
            };
        }

        private CBORObject ConvertJObjectToCborMap(JObject jObject)
        {
            var cborMap = CBORObject.NewMap();

            var properties = Config.SortKeys
                ? jObject.Properties().OrderBy(p => p.Name)
                : jObject.Properties();

            foreach (var property in properties)
            {
                var key = CBORObject.FromObject(property.Name);
                var value = ConvertJTokenToCbor(property.Value);
                cborMap[key] = value;
            }

            return cborMap;
        }

        private CBORObject ConvertJArrayToCborArray(JArray jArray)
        {
            var cborArray = CBORObject.NewArray();

            foreach (var item in jArray)
            {
                cborArray.Add(ConvertJTokenToCbor(item));
            }

            return cborArray;
        }

        private static CBORObject ConvertStringToCbor(string? value)
        {
            if (value == null) return CBORObject.Null;

            if (IsBase64String(value))
            {
                try
                {
                    var bytes = Convert.FromBase64String(value);
                    return CBORObject.FromObject(bytes);
                }
                finally { }
            }

            return CBORObject.FromObject(value);
        }

        private CBORObject ConvertDateToCbor(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                var formattedDate = Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds().ToString(),
                    _ => dateTime.ToString(Config.DateFormat)
                };
                return CBORObject.FromObject(formattedDate);
            }
            return CBORObject.FromObject(dateTime);
        }

        private double FormatNumberValue(double number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "scientific" => double.Parse(number.ToString("E")),
                    _ => number
                };
            }
            return number;
        }

        private CBOREncodeOptions GetCborEncodeOptions()
        {
            var opts = new List<string>();

            if (Config.Minify)
            {
                opts.Add("ctap2canonical=true");
            }

            if (Config.NoMetadata)
            {
                opts.Add("allowduplicatekeys=false");
            }

            return new CBOREncodeOptions(string.Join(",", opts));
        }

        private string FormatOutput(byte[] bytes)
        {
            return Config.NumberFormat?.ToLower() switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
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

            return hex;
        }

        private static bool IsBase64String(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length % 4 != 0)
                return false;

            try
            {
                Convert.FromBase64String(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static CBORObject CreateErrorCbor(string errorMessage, int count)
        {
            var errorMap = CBORObject.NewMap();
            errorMap[CBORObject.FromObject("error")] = CBORObject.FromObject(errorMessage);
            errorMap[CBORObject.FromObject("itemCount")] = CBORObject.FromObject(count);
            errorMap[CBORObject.FromObject("timestamp")] = CBORObject.FromObject(DateTime.UtcNow.ToString("O"));
            return errorMap;
        }
    }
}