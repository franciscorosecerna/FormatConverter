using FormatConverter.Interfaces;
using MessagePack;
using MessagePack.Resolvers;
using Newtonsoft.Json.Linq;
using System.Buffers;
using System.Text;

namespace FormatConverter.MessagePack
{
    public class MessagePackInputStrategy : BaseInputStrategy
    {
        private const int DEFAULT_MAX_DEPTH = 32;
        private const int DEFAULT_ARRAY_CHUNK_SIZE = 100;
        private const int DEFAULT_MAP_CHUNK_SIZE = 50;
        private const int LARGE_ARRAY_THRESHOLD = 50;
        private const int LARGE_MAP_THRESHOLD = 30;

        public override JToken Parse(string input)
        {
            ValidateInput(input);

            try
            {
                var bytes = DecodeInput(input);
                var options = GetMessagePackOptions();
                var obj = MessagePackSerializer.Deserialize<object>(bytes, options) 
                    ?? throw new FormatException("MessagePack deserialization returned null");

                var result = ConvertObjectToJToken(obj);
                return ApplyPostProcessing(result);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string input)
        {
            ValidateInput(input);

            if (!Config.UseStreaming)
            {
                yield return Parse(input);
                yield break;
            }

            var bytes = DecodeInput(input);

            if (TryParseAsMessageSequence(bytes, out var messages))
            {
                foreach (var message in messages)
                {
                    yield return ApplyPostProcessing(message);
                }
                yield break;
            }

            var rootObject = DeserializeSafely(bytes);
            if (rootObject == null)
            {
                yield return new JObject { ["error"] = "Failed to deserialize MessagePack data" };
                yield break;
            }

            var streamingResult = StreamLargeStructures(rootObject, bytes.Length);

            foreach (var chunk in streamingResult)
            {
                yield return ApplyPostProcessing(chunk);
            }
        }

        private bool TryParseAsMessageSequence(byte[] bytes, out List<JToken> messages)
        {
            messages = [];

            try
            {
                var options = GetMessagePackOptions();
                var sequence = new ReadOnlySequence<byte>(bytes);
                var reader = new MessagePackReader(sequence);
                var lastPosition = 0L;

                while (!reader.End)
                {
                    var positionBefore = reader.Consumed;

                    try
                    {
                        var obj = MessagePackSerializer.Deserialize<object>(ref reader, options);

                        if (obj != null)
                        {
                            messages.Add(ConvertObjectToJToken(obj));
                        }

                        if (reader.Consumed == positionBefore)
                        {
                            break;
                        }

                        lastPosition = reader.Consumed;
                    }
                    catch (MessagePackSerializationException)
                    {
                        if (messages.Count == 0)
                        {
                            return false;
                        }
                        break;
                    }
                }

                if (messages.Count > 1)
                {
                    AddSequenceMetadata(messages);
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (!Config.IgnoreErrors)
                {
                    Console.WriteLine($"Debug: Failed to parse as message sequence: {ex.Message}");
                }
            }

            messages.Clear();
            return false;
        }

        private static void AddSequenceMetadata(List<JToken> messages)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] is JObject obj)
                {
                    obj["_sequence_info"] = new JObject
                    {
                        ["message_index"] = i,
                        ["total_messages"] = messages.Count,
                        ["is_sequence"] = true
                    };
                }
            }
        }

        private IEnumerable<JToken> StreamLargeStructures(object rootObject, int totalSize)
        {
            var hasLargeArrays = ContainsLargeArrays(rootObject, 0);
            var hasLargeMaps = ContainsLargeMaps(rootObject, 0);

            if (!hasLargeArrays && !hasLargeMaps)
            {
                yield return ConvertObjectToJToken(rootObject);
                yield break;
            }

            if (hasLargeArrays)
            {
                var arrayChunks = ExtractAndChunkArrays(rootObject, "root", 0).ToList();
                if (arrayChunks.Count > 0)
                {
                    AddGlobalChunkMetadata(arrayChunks, totalSize);
                    foreach (var chunk in arrayChunks)
                        yield return chunk;
                    yield break;
                }
            }

            if (hasLargeMaps)
            {
                var mapChunks = ExtractAndChunkMaps(rootObject, 0).ToList();
                if (mapChunks.Count > 0)
                {
                    AddGlobalChunkMetadata(mapChunks, totalSize);
                    foreach (var chunk in mapChunks)
                        yield return chunk;
                    yield break;
                }
            }
            yield return ConvertObjectToJToken(rootObject);
        }

        private IEnumerable<JToken> ExtractAndChunkArrays(object obj, string path, int depth)
        {
            if (depth > GetMaxDepth())
            {
                yield return ConvertObjectToJToken(obj);
                yield break;
            }

            switch (obj)
            {
                case List<object> list2 when list2.Count > LARGE_ARRAY_THRESHOLD:
                    foreach (var chunk in CreateArrayChunks(list2, path))
                        yield return chunk;
                    break;

                case Array array when array.Length > LARGE_ARRAY_THRESHOLD:
                    var list = array.Cast<object>().ToList();
                    foreach (var chunk in CreateArrayChunks(list, path))
                        yield return chunk;
                    break;

                case Dictionary<object, object> dict:
                    var chunks = ProcessDictionaryForArrays(dict, path, depth);
                    if (chunks.Any())
                    {
                        foreach (var chunk in chunks)
                            yield return chunk;
                    }
                    else
                    {
                        yield return ConvertObjectToJToken(obj);
                    }
                    break;

                case Dictionary<string, object> stringDict:
                    var stringChunks = ProcessStringDictionaryForArrays(stringDict, path, depth);
                    if (stringChunks.Any())
                    {
                        foreach (var chunk in stringChunks)
                            yield return chunk;
                    }
                    else
                    {
                        yield return ConvertObjectToJToken(obj);
                    }
                    break;

                default:
                    yield return ConvertObjectToJToken(obj);
                    break;
            }
        }

        private IEnumerable<JToken> ProcessDictionaryForArrays(
            Dictionary<object, object> dict, string path, int depth)
        {
            var allChunks = new List<JToken>();
            var nonChunkedProps = new JObject();

            foreach (var kvp in dict)
            {
                var key = ConvertKeyToString(kvp.Key);
                var newPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";

                var subChunks = ExtractAndChunkArrays(kvp.Value, newPath, depth + 1).ToList();

                if (subChunks.Count > 1 || HasChunkInfo(subChunks.FirstOrDefault()))
                {
                    allChunks.AddRange(subChunks);
                }
                else
                {
                    nonChunkedProps[key] = ConvertObjectToJToken(kvp.Value);
                }
            }

            if (nonChunkedProps.Count > 0)
            {
                allChunks.Insert(0, nonChunkedProps);
            }

            return allChunks;
        }

        private IEnumerable<JToken> ProcessStringDictionaryForArrays(
            Dictionary<string, object> dict, string path, int depth)
        {
            var allChunks = new List<JToken>();
            var nonChunkedProps = new JObject();

            foreach (var kvp in dict)
            {
                var newPath = string.IsNullOrEmpty(path) ? kvp.Key : $"{path}.{kvp.Key}";

                var subChunks = ExtractAndChunkArrays(kvp.Value, newPath, depth + 1).ToList();

                if (subChunks.Count > 1 || HasChunkInfo(subChunks.FirstOrDefault()))
                {
                    allChunks.AddRange(subChunks);
                }
                else
                {
                    nonChunkedProps[kvp.Key] = ConvertObjectToJToken(kvp.Value);
                }
            }

            if (nonChunkedProps.Count > 0)
            {
                allChunks.Insert(0, nonChunkedProps);
            }

            return allChunks;
        }

        private IEnumerable<JToken> ExtractAndChunkMaps(object obj, int depth)
        {
            if (depth > GetMaxDepth())
            {
                yield return ConvertObjectToJToken(obj);
                yield break;
            }

            switch (obj)
            {
                case Dictionary<object, object> dict when dict.Count > LARGE_MAP_THRESHOLD:
                    foreach (var chunk in CreateMapChunks(dict))
                        yield return chunk;
                    break;

                case Dictionary<string, object> stringDict when stringDict.Count > LARGE_MAP_THRESHOLD:
                    foreach (var chunk in CreateMapChunks(stringDict))
                        yield return chunk;
                    break;

                default:
                    yield return ConvertObjectToJToken(obj);
                    break;
            }
        }

        private IEnumerable<JToken> CreateArrayChunks(List<object> list, string arrayKey)
        {
            var chunkSize = DEFAULT_ARRAY_CHUNK_SIZE;

            for (int i = 0; i < list.Count; i += chunkSize)
            {
                var chunkItems = list.Skip(i).Take(chunkSize).ToList();
                var chunkObject = new JObject
                {
                    [arrayKey] = ConvertListToJArray(chunkItems),
                    ["_chunk_info"] = new JObject
                    {
                        ["type"] = "array_chunk",
                        ["array_key"] = arrayKey,
                        ["chunk_start"] = i,
                        ["chunk_size"] = chunkItems.Count,
                        ["total_items"] = list.Count
                    }
                };

                yield return chunkObject;
            }
        }

        private IEnumerable<JToken> CreateMapChunks(Dictionary<object, object> dict)
        {
            var chunkSize = DEFAULT_MAP_CHUNK_SIZE;
            var currentChunk = new JObject();
            var processedKeys = 0;
            var chunkIndex = 0;

            foreach (var kvp in dict)
            {
                var key = ConvertKeyToString(kvp.Key);
                currentChunk[key] = ConvertObjectToJToken(kvp.Value);
                processedKeys++;

                if (processedKeys >= chunkSize)
                {
                    AddMapChunkInfo(currentChunk, chunkIndex++, dict.Count);
                    yield return currentChunk;
                    currentChunk = new JObject();
                    processedKeys = 0;
                }
            }

            if (processedKeys > 0)
            {
                AddMapChunkInfo(currentChunk, chunkIndex, dict.Count);
                yield return currentChunk;
            }
        }

        private IEnumerable<JToken> CreateMapChunks(Dictionary<string, object> dict)
        {
            var chunkSize = DEFAULT_MAP_CHUNK_SIZE;
            var currentChunk = new JObject();
            var processedKeys = 0;
            var chunkIndex = 0;

            foreach (var kvp in dict)
            {
                currentChunk[kvp.Key] = ConvertObjectToJToken(kvp.Value);
                processedKeys++;

                if (processedKeys >= chunkSize)
                {
                    AddMapChunkInfo(currentChunk, chunkIndex++, dict.Count);
                    yield return currentChunk;
                    currentChunk = new JObject();
                    processedKeys = 0;
                }
            }

            if (processedKeys > 0)
            {
                AddMapChunkInfo(currentChunk, chunkIndex, dict.Count);
                yield return currentChunk;
            }
        }

        private static void AddMapChunkInfo(JObject chunk, int chunkIndex, int totalKeys)
        {
            chunk["_chunk_info"] = new JObject
            {
                ["type"] = "map_chunk",
                ["chunk_index"] = chunkIndex,
                ["total_keys"] = totalKeys
            };
        }

        private static void AddGlobalChunkMetadata(List<JToken> chunks, int totalSize)
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i] is JObject chunkObj && chunkObj["_chunk_info"] is JObject chunkInfo)
                {
                    chunkInfo["chunk_number"] = i + 1;
                    chunkInfo["total_chunks"] = chunks.Count;
                    chunkInfo["original_size_bytes"] = totalSize;
                }
            }
        }

        private static bool HasChunkInfo(JToken token)
        {
            return token is JObject obj && obj["_chunk_info"] != null;
        }

        private bool ContainsLargeArrays(object obj, int depth)
        {
            if (depth > GetMaxDepth()) return false;

            return obj switch
            {
                List<object> list => list.Count > LARGE_ARRAY_THRESHOLD ||
                                     list.Any(item => ContainsLargeArrays(item, depth + 1)),
                Array array => array.Length > LARGE_ARRAY_THRESHOLD ||
                              array.Cast<object>().Any(item => ContainsLargeArrays(item, depth + 1)),
                Dictionary<object, object> dict => dict.Values.Any(v => ContainsLargeArrays(v, depth + 1)),
                Dictionary<string, object> stringDict => stringDict.Values.Any(v => ContainsLargeArrays(v, depth + 1)),
                _ => false
            };
        }

        private bool ContainsLargeMaps(object obj, int depth)
        {
            if (depth > GetMaxDepth()) return false;

            return obj switch
            {
                Dictionary<object, object> dict => dict.Count > LARGE_MAP_THRESHOLD ||
                                                    dict.Values.Any(v => ContainsLargeMaps(v, depth + 1)),
                Dictionary<string, object> stringDict => stringDict.Count > LARGE_MAP_THRESHOLD ||
                                                         stringDict.Values.Any(v => ContainsLargeMaps(v, depth + 1)),
                List<object> list => list.Any(item => ContainsLargeMaps(item, depth + 1)),
                Array array => array.Cast<object>().Any(item => ContainsLargeMaps(item, depth + 1)),
                _ => false
            };
        }

        private static void ValidateInput(string input)
        {
            if (string.IsNullOrEmpty(input))
                throw new FormatException("MessagePack input is empty or null");
        }

        private object DeserializeSafely(byte[] bytes)
        {
            try
            {
                var options = GetMessagePackOptions();
                return MessagePackSerializer.Deserialize<object>(bytes, options);
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: MessagePack deserialization error ignored: {ex.Message}");
                    return null;
                }
                throw new FormatException($"Invalid MessagePack: {ex.Message}", ex);
            }
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: MessagePack parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["error_type"] = ex.GetType().Name,
                    ["raw"] = input.Length > 1000 ? string.Concat(input.AsSpan(0, 1000), "...") : input
                };
            }
            throw new FormatException($"Invalid MessagePack: {ex.Message}", ex);
        }

        private JToken ApplyPostProcessing(JToken token)
        {
            var result = token;

            if (Config.NoMetadata)
                result = RemoveMetadataProperties(result);

            if (Config.SortKeys && result is JObject)
                result = SortKeysRecursively(result);

            return result;
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

        private static byte[] ParseHexString(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("-", "");

            if (hex.Length % 2 != 0)
                throw new FormatException("Invalid hex string length");

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

            return bytes;
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
                options = options.WithSecurity(MessagePackSecurity.UntrustedData);

            return options;
        }

        private JToken ConvertObjectToJToken(object obj)
        {
            if (obj == null) return JValue.CreateNull();

            return obj switch
            {
                Dictionary<object, object> dict => ConvertDictionaryToJObject(dict),
                Dictionary<string, object> stringDict => ConvertStringDictionaryToJObject(stringDict),
                List<object> list => ConvertListToJArray(list),
                byte[] bytes => new JValue(Convert.ToBase64String(bytes)),
                Array array => ConvertArrayToJArray(array),
                string str => new JValue(str),
                bool b => new JValue(b),
                byte b => new JValue(b),
                sbyte sb => new JValue(sb),
                short s => new JValue(s),
                ushort us => new JValue(us),
                int i => new JValue(i),
                uint ui => new JValue(ui),
                long l => new JValue(l),
                ulong ul => ul > long.MaxValue
                    ? new JValue((decimal)ul)
                    : new JValue((long)ul),
                float f => new JValue(FormatNumberValue(f)),
                double d => new JValue(FormatNumberValue(d)),
                decimal m => new JValue(m),
                DateTime dt => new JValue(FormatDateTime(dt)),
                DateTimeOffset dto => new JValue(FormatDateTime(dto.DateTime)),
                _ when obj.GetType().IsArray => ConvertArrayToJArray((Array)obj),
                _ => ConvertComplexObjectToJToken(obj)
            };
        }

        private JObject ConvertDictionaryToJObject(Dictionary<object, object> dict)
        {
            var result = new JObject();
            foreach (var kvp in dict)
            {
                var key = ConvertKeyToString(kvp.Key);
                result[key] = ConvertObjectToJToken(kvp.Value);
            }
            return result;
        }

        private JObject ConvertStringDictionaryToJObject(Dictionary<string, object> dict)
        {
            var result = new JObject();
            foreach (var kvp in dict)
                result[kvp.Key] = ConvertObjectToJToken(kvp.Value);
            return result;
        }

        private JArray ConvertListToJArray(List<object> list)
        {
            var result = new JArray();
            foreach (var item in list)
                result.Add(ConvertObjectToJToken(item));
            return result;
        }

        private JArray ConvertArrayToJArray(Array array)
        {
            var result = new JArray();
            foreach (var item in array)
                result.Add(ConvertObjectToJToken(item));
            return result;
        }

        private JToken ConvertComplexObjectToJToken(object obj)
        {
            try
            {
                var type = obj.GetType();
                if (type.IsClass && type != typeof(string))
                {
                    var result = new JObject();
                    var properties = type.GetProperties();

                    foreach (var prop in properties)
                    {
                        if (prop.CanRead)
                        {
                            var value = prop.GetValue(obj);
                            result[prop.Name] = ConvertObjectToJToken(value);
                        }
                    }

                    return result;
                }
            }
            finally { }

            return new JValue(obj.ToString());
        }

        private static string ConvertKeyToString(object key)
        {
            return key switch
            {
                string str => str,
                null => "null",
                byte[] bytes => Convert.ToBase64String(bytes),
                _ => key.ToString() ?? "unknown"
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

        private object FormatDateTime(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds(),
                    "rfc3339" => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }
            return dateTime;
        }

        private int GetMaxDepth() => (int)(Config.MaxDepth > 0 ? Config.MaxDepth : DEFAULT_MAX_DEPTH);
    }
}