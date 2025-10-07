using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System.Text;

namespace FormatConverter.Cbor
{
    public class CborInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            if (Config.UseStreaming)
            {
                var firstToken = ParseStream(input).FirstOrDefault();
                return firstToken ?? new JObject();
            }

            try
            {
                var token = ParseCborDocument(input);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                return token;
            }
            catch (Exception ex) when (ex is CBORException or FormatException)
            {
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string input)
        {
            if (!Config.UseStreaming)
            {
                yield return Parse(input);
                yield break;
            }

            var bytes = DecodeInput(input);
            var totalSize = bytes.Length;

            if (PeekForArray(bytes))
            {
                foreach (var item in StreamCborArray(bytes, totalSize))
                    yield return item;
            }
            else if (PeekForMap(bytes))
            {
                foreach (var chunk in StreamCborMap(bytes, totalSize))
                    yield return chunk;
            }
            else
            {
                foreach (var token in StreamSimpleValues(bytes, totalSize))
                    yield return token;
            }
        }

        private IEnumerable<JToken> StreamCborArray(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            IEnumerable<JToken> Iterator()
            {
                var cborObj = CBORObject.DecodeFromBytes(bytes);

                if (cborObj?.Type != CBORType.Array)
                    yield break;

                var itemBuffer = new List<JToken>();
                var currentBufferSize = 0;
                var processedBytes = 0L;

                for (int i = 0; i < cborObj.Count; i++)
                {
                    var item = ConvertCborToJToken(cborObj[i]);

                    if (Config.SortKeys)
                        item = SortKeysRecursively(item);

                    itemBuffer.Add(item);
                    var tokenBytes = EstimateTokenSize(item);
                    currentBufferSize += tokenBytes;
                    processedBytes += tokenBytes;

                    if (currentBufferSize >= bufferSize)
                    {
                        foreach (var bufferedItem in itemBuffer)
                            yield return bufferedItem;

                        itemBuffer.Clear();
                        currentBufferSize = 0;

                        if (totalSize > bufferSize * 10)
                        {
                            var progress = (double)processedBytes / totalSize * 100;
                            if (progress % 10 < 1)
                                Console.WriteLine($"Array streaming progress: {progress:F1}%");
                        }
                    }
                }

                foreach (var bufferedItem in itemBuffer)
                    yield return bufferedItem;
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex) when (ex is CBORException or FormatException)
            {
                return HandleStreamingError(ex, bytes);
            }
        }

        private IEnumerable<JToken> StreamCborMap(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            IEnumerable<JToken> Iterator()
            {
                var cborObj = CBORObject.DecodeFromBytes(bytes);

                if (cborObj?.Type != CBORType.Map)
                    yield break;

                var memoryThreshold = bufferSize > 0 ? bufferSize : 1024 * 1024;
                var maxPropertiesPerChunk = Math.Max(10, bufferSize / 1024);

                var currentChunk = new JObject();
                int propertyCount = 0;
                var currentChunkSize = 0;
                var processedBytes = 0L;
                var keys = cborObj.Keys.ToList();

                foreach (var key in keys)
                {
                    var propertyName = ConvertCborKeyToString(key);
                    var value = ConvertCborToJToken(cborObj[key]);

                    if (Config.SortKeys)
                        value = SortKeysRecursively(value);

                    currentChunk[propertyName] = value;
                    propertyCount++;

                    var propertyBytes = Encoding.UTF8.GetBytes(propertyName).Length + 3; // +3 CBOR overhead
                    var valueBytes = EstimateTokenSize(value);
                    var totalPropertyBytes = propertyBytes + valueBytes;

                    currentChunkSize += totalPropertyBytes;
                    processedBytes += totalPropertyBytes;

                    if (propertyCount >= maxPropertiesPerChunk || currentChunkSize >= memoryThreshold)
                    {
                        yield return Config.SortKeys ? SortJObject(currentChunk) : currentChunk;

                        currentChunk = new JObject();
                        propertyCount = 0;
                        currentChunkSize = 0;

                        if (totalSize > bufferSize * 10)
                        {
                            var progress = (double)processedBytes / totalSize * 100;
                            if (progress % 10 < 1)
                                Console.WriteLine($"Map streaming progress: {progress:F1}%");
                        }
                    }
                }

                if (propertyCount > 0)
                    yield return Config.SortKeys ? SortJObject(currentChunk) : currentChunk;
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex) when (ex is CBORException or FormatException)
            {
                return HandleStreamingError(ex, bytes);
            }
        }

        private IEnumerable<JToken> StreamSimpleValues(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            IEnumerable<JToken> Iterator()
            {
                var tokenBuffer = new List<JToken>();
                var currentBufferSize = 0;
                var maxBufferSize = bufferSize > 0 ? bufferSize : 4096;
                var processedBytes = 0L;

                var token = ParseCborDocument(Convert.ToBase64String(bytes));

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                tokenBuffer.Add(token);
                var tokenBytes = EstimateTokenSize(token);
                currentBufferSize += tokenBytes;
                processedBytes += tokenBytes;

                if (totalSize > maxBufferSize * 5)
                {
                    var progress = (double)processedBytes / totalSize * 100;
                    Console.WriteLine($"Simple values streaming progress: {progress:F1}%");
                }

                foreach (var bufferedToken in tokenBuffer)
                    yield return bufferedToken;
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex) when (ex is CBORException or FormatException)
            {
                return HandleStreamingError(ex, bytes);
            }
        }

        private IEnumerable<JToken> HandleStreamingError(Exception ex, byte[] bytes)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: CBOR streaming error ignored: {ex.Message}");
                return [HandleParsingError(ex, Convert.ToBase64String(bytes))];
            }
            else
            {
                throw new FormatException($"Invalid CBOR during streaming: {ex.Message}", ex);
            }
        }

        private JToken ParseCborDocument(string input)
        {
            if (string.IsNullOrEmpty(input))
                throw new FormatException("CBOR input is empty or null");

            var bytes = DecodeInput(input);
            var cborObj = CBORObject.DecodeFromBytes(bytes)
                ?? throw new FormatException("CBOR object is null after decoding");

            var result = ConvertCborToJToken(cborObj);

            if (Config.NoMetadata)
                result = RemoveMetadataProperties(result);

            return result;
        }

        private static byte[] DecodeInput(string input)
        {
            try
            {
                return Convert.FromBase64String(input);
            }
            catch (FormatException)
            {
                return ParseHexString(input);
            }
        }

        private static bool PeekForArray(byte[] bytes)
        {
            if (bytes.Length == 0) return false;

            try
            {
                var cbor = CBORObject.DecodeFromBytes(bytes);
                return cbor?.Type == CBORType.Array;
            }
            catch
            {
                return false;
            }
        }

        private static bool PeekForMap(byte[] bytes)
        {
            if (bytes.Length == 0) return false;

            try
            {
                var cbor = CBORObject.DecodeFromBytes(bytes);
                return cbor?.Type == CBORType.Map;
            }
            catch
            {
                return false;
            }
        }

        private static int EstimateTokenSize(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => Encoding.UTF8.GetByteCount(token.ToString()) + 5,
                JTokenType.Integer => 9,
                JTokenType.Float => 9,
                JTokenType.Boolean => 1,
                JTokenType.Null => 1,
                JTokenType.Date => 20,
                JTokenType.Object => EstimateObjectSize((JObject)token),
                JTokenType.Array => EstimateArraySize((JArray)token),
                _ => 20
            };
        }

        private static int EstimateObjectSize(JObject obj)
        {
            var totalSize = 5;

            foreach (var property in obj.Properties())
            {
                totalSize += Encoding.UTF8.GetByteCount(property.Name) + 3;

                totalSize += EstimateTokenSize(property.Value);
            }

            return totalSize;
        }

        private static int EstimateArraySize(JArray array)
        {
            var totalSize = 5;

            foreach (var item in array)
            {
                totalSize += EstimateTokenSize(item);
            }

            return totalSize;
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: CBOR parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000 ? string.Concat(input.AsSpan(0, 1000), "...") : input
                };
            }
            throw new FormatException($"Invalid CBOR: {ex.Message}", ex);
        }

        private JToken RemoveMetadataProperties(JToken token)
        {
            if (token is JObject obj)
            {
                var result = new JObject();
                foreach (var prop in obj.Properties())
                {
                    if (!prop.Name.StartsWith("_"))
                        result[prop.Name] = RemoveMetadataProperties(prop.Value);
                }
                return result;
            }
            else if (token is JArray array)
            {
                return new JArray(array.Select(RemoveMetadataProperties));
            }

            return token;
        }

        private static byte[] ParseHexString(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("-", "");

            if (hex.Length % 2 != 0)
                throw new FormatException("Invalid hex string length");

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        private JToken ConvertCborToJToken(CBORObject cbor)
        {
            if (cbor == null) return JValue.CreateNull();

            return cbor.Type switch
            {
                CBORType.Map => ConvertCborMapToJObject(cbor),
                CBORType.Array => ConvertCborArrayToJArray(cbor),
                CBORType.TextString => new JValue(cbor.AsString()),
                CBORType.Integer => new JValue(cbor.CanValueFitInInt64()
                    ? FormatNumberValue(cbor.AsInt64Value())
                    : cbor.AsNumber().ToInt64Checked().ToString()),
                CBORType.FloatingPoint => new JValue(FormatNumberValue(cbor.AsDoubleValue())),
                CBORType.Boolean => new JValue(cbor.AsBoolean()),
                CBORType.SimpleValue => cbor.IsNull ? JValue.CreateNull()
                    : cbor.IsUndefined ? JValue.CreateUndefined()
                    : new JValue(cbor.SimpleValue),
                CBORType.ByteString => new JValue(Convert.ToBase64String(cbor.GetByteString())),
                _ => new JValue(cbor.ToString())
            };
        }

        private JObject ConvertCborMapToJObject(CBORObject cborMap)
        {
            var result = new JObject();

            foreach (var key in cborMap.Keys)
            {
                var keyString = ConvertCborKeyToString(key);
                var value = ConvertCborToJToken(cborMap[key]);
                result[keyString] = value;
            }

            return result;
        }

        private JArray ConvertCborArrayToJArray(CBORObject cborArray)
        {
            var result = new JArray();

            for (int i = 0; i < cborArray.Count; i++)
            {
                result.Add(ConvertCborToJToken(cborArray[i]));
            }

            return result;
        }

        private static string ConvertCborKeyToString(CBORObject key)
        {
            return key.Type switch
            {
                CBORType.TextString => key.AsString(),
                CBORType.Integer => key.AsInt64Value().ToString(),
                CBORType.ByteString => Convert.ToBase64String(key.GetByteString()),
                _ => key.ToString()
            };
        }

        private object FormatNumberValue(double number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{(long)number:X}",
                    "scientific" => number.ToString("E"),
                    _ => number
                };
            }
            return number;
        }

        private object FormatNumberValue(long number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{number:X}",
                    "scientific" => ((double)number).ToString("E"),
                    _ => number
                };
            }
            return number;
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
                sorted[property.Name] = SortKeysRecursively(property.Value);
            return sorted;
        }
    }
}