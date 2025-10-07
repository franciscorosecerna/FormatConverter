using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FormatConverter.Yaml
{
    public class YamlInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            if (Config.UseStreaming)
            {
                var firstToken = ParseStream(input).FirstOrDefault();
                return firstToken ?? new JObject();
            }

            var deserializer = CreateDeserializer();

            try
            {
                var token = ParseYamlDocument(input, deserializer);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                return token;
            }
            catch (Exception ex) when (ex is not FormatException)
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

            var bytes = Encoding.UTF8.GetBytes(input);
            var totalSize = bytes.Length;

            if (HasMultipleDocuments(input))
            {
                foreach (var token in StreamMultipleDocuments(bytes, totalSize))
                    yield return token;
            }
            else if (HasLargeArrays(input))
            {
                foreach (var chunk in StreamLargeArrays(bytes, totalSize))
                    yield return chunk;
            }
            else if (HasManyTopLevelProperties(input))
            {
                foreach (var chunk in StreamTopLevelProperties(bytes, totalSize))
                    yield return chunk;
            }
            else
            {
                foreach (var token in StreamSingleDocument(bytes, totalSize))
                    yield return token;
            }
        }

        private IEnumerable<JToken> StreamMultipleDocuments(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var input = Encoding.UTF8.GetString(bytes);
            var deserializer = CreateDeserializer();

            IEnumerable<JToken> Iterator()
            {
                var documents = input.Split(["---"], StringSplitOptions.RemoveEmptyEntries);
                var tokenBuffer = new List<JToken>();
                var currentBufferSize = 0;
                var processedBytes = 0L;

                for (int i = 0; i < documents.Length; i++)
                {
                    var trimmedDoc = documents[i].Trim();
                    if (string.IsNullOrEmpty(trimmedDoc)) continue;

                    var yamlObject = deserializer.Deserialize(new StringReader(trimmedDoc));
                    if (yamlObject != null)
                    {
                        var token = ConvertObjectToJToken(yamlObject);

                        if (Config.SortKeys)
                            token = SortKeysRecursively(token);

                        tokenBuffer.Add(token);
                        var tokenBytes = GetTokenSizeInBytes(token);
                        currentBufferSize += tokenBytes;
                        processedBytes += tokenBytes;

                        if (currentBufferSize >= bufferSize)
                        {
                            foreach (var bufferedToken in tokenBuffer)
                                yield return bufferedToken;

                            tokenBuffer.Clear();
                            currentBufferSize = 0;

                            if (totalSize > bufferSize * 10)
                            {
                                var progress = (double)processedBytes / totalSize * 100;
                                if (progress % 10 < 1)
                                    Console.WriteLine($"YAML multi-document streaming progress: {progress:F1}%");
                            }
                        }
                    }
                }
                foreach (var bufferedToken in tokenBuffer)
                    yield return bufferedToken;
            }

            try
            {
                return Iterator();
            }
            catch (YamlException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> StreamLargeArrays(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var input = Encoding.UTF8.GetString(bytes);
            var deserializer = CreateDeserializer();

            IEnumerable<JToken> Iterator()
            {
                var yamlObject = deserializer.Deserialize(new StringReader(input));
                var maxItemsPerChunk = Math.Max(10, bufferSize / 1024);

                var currentChunk = new JObject();
                int itemCount = 0;
                var currentChunkSize = 0;
                var processedBytes = 0L;

                if (yamlObject is Dictionary<object, object> dict)
                {
                    foreach (var kvp in dict)
                    {
                        var key = kvp.Key?.ToString() ?? "null";

                        if (kvp.Value is List<object> list && list.Count > maxItemsPerChunk)
                        {
                            for (int i = 0; i < list.Count; i += maxItemsPerChunk)
                            {
                                var chunk = list.Skip(i).Take(maxItemsPerChunk).ToList();
                                var chunkObject = new JObject();
                                chunkObject[key] = ConvertListToJArray(chunk);

                                if (!Config.NoMetadata)
                                {
                                    chunkObject["_chunk_info"] = new JObject
                                    {
                                        ["array_key"] = key,
                                        ["chunk_start"] = i,
                                        ["chunk_size"] = chunk.Count,
                                        ["total_items"] = list.Count
                                    };
                                }

                                var chunkBytes = GetTokenSizeInBytes(chunkObject);
                                processedBytes += chunkBytes;

                                yield return Config.SortKeys ? SortKeysRecursively(chunkObject) : chunkObject;

                                if (totalSize > bufferSize * 10 && i % (maxItemsPerChunk * 5) == 0)
                                {
                                    var progress = (double)processedBytes / totalSize * 100;
                                    Console.WriteLine($"YAML large array streaming progress: {progress:F1}%");
                                }
                            }
                        }
                        else
                        {
                            currentChunk[key] = ConvertObjectToJToken(kvp.Value);
                            itemCount++;

                            var itemBytes = GetTokenSizeInBytes(currentChunk[key]);
                            currentChunkSize += itemBytes;
                            processedBytes += itemBytes;

                            if (itemCount >= maxItemsPerChunk || currentChunkSize >= bufferSize)
                            {
                                yield return Config.SortKeys ? SortKeysRecursively(currentChunk) : currentChunk;

                                currentChunk = new JObject();
                                itemCount = 0;
                                currentChunkSize = 0;
                            }
                        }
                    }
                }
                else if (yamlObject is List<object> rootList)
                {
                    for (int i = 0; i < rootList.Count; i += maxItemsPerChunk)
                    {
                        var chunk = rootList.Skip(i).Take(maxItemsPerChunk).ToList();
                        var chunkArray = ConvertListToJArray(chunk);

                        var chunkBytes = GetTokenSizeInBytes(chunkArray);
                        processedBytes += chunkBytes;

                        yield return chunkArray;

                        if (totalSize > bufferSize * 10 && i % (maxItemsPerChunk * 5) == 0)
                        {
                            var progress = (double)processedBytes / totalSize * 100;
                            Console.WriteLine($"YAML root array streaming progress: {progress:F1}%");
                        }
                    }
                }

                if (itemCount > 0 || currentChunk.Count > 0)
                {
                    yield return Config.SortKeys ? SortKeysRecursively(currentChunk) : currentChunk;
                }
            }

            try
            {
                return Iterator();
            }
            catch (YamlException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> StreamTopLevelProperties(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var input = Encoding.UTF8.GetString(bytes);
            var deserializer = CreateDeserializer();

            IEnumerable<JToken> Iterator()
            {
                var yamlObject = deserializer.Deserialize(new StringReader(input));

                if (yamlObject is Dictionary<object, object> dict)
                {
                    var maxPropertiesPerChunk = Math.Max(5, bufferSize / 1024);
                    var currentChunk = new JObject();
                    int propertyCount = 0;
                    var currentChunkSize = 0;
                    var processedBytes = 0L;

                    foreach (var kvp in dict)
                    {
                        var key = kvp.Key?.ToString() ?? "null";
                        var value = ConvertObjectToJToken(kvp.Value);

                        if (Config.SortKeys)
                            value = SortKeysRecursively(value);

                        currentChunk[key] = value;
                        propertyCount++;

                        var propertyBytes = Encoding.UTF8.GetBytes(key).Length;
                        var valueBytes = GetTokenSizeInBytes(value);
                        var totalPropertyBytes = propertyBytes + valueBytes;

                        currentChunkSize += totalPropertyBytes;
                        processedBytes += totalPropertyBytes;

                        if (propertyCount >= maxPropertiesPerChunk || currentChunkSize >= bufferSize)
                        {
                            yield return Config.SortKeys ? SortJObject(currentChunk) : currentChunk;

                            currentChunk = new JObject();
                            propertyCount = 0;
                            currentChunkSize = 0;

                            if (totalSize > bufferSize * 10)
                            {
                                var progress = (double)processedBytes / totalSize * 100;
                                if (progress % 10 < 1)
                                    Console.WriteLine($"YAML top-level properties streaming progress: {progress:F1}%");
                            }
                        }
                    }

                    if (propertyCount > 0 || currentChunk.Count > 0)
                    {
                        yield return Config.SortKeys ? SortJObject(currentChunk) : currentChunk;
                    }
                }
            }

            try
            {
                return Iterator();
            }
            catch (YamlException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> StreamSingleDocument(byte[] bytes, int totalSize)
        {
            var input = Encoding.UTF8.GetString(bytes);
            var deserializer = CreateDeserializer();

            IEnumerable<JToken> Iterator()
            {
                var token = ParseYamlDocument(input, deserializer);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                yield return token;

                if (totalSize > 1024)
                {
                    Console.WriteLine("YAML single document streaming progress: 100.0%");
                }
            }

            try
            {
                return Iterator();
            }
            catch (YamlException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> HandleStreamingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: YAML streaming error ignored: {ex.Message}");
                return [HandleParsingError(ex, input)];
            }
            else
            {
                var exceptionType = ex is YamlException ? "YAML" : "streaming";
                throw new FormatException($"Invalid YAML during {exceptionType}: {ex.Message}", ex);
            }
        }

        private IDeserializer CreateDeserializer()
        {
            var deserializerBuilder = new DeserializerBuilder();

            if (Config.NoMetadata)
            {
                deserializerBuilder.IgnoreUnmatchedProperties();
            }

            if (Config.StrictMode)
            {
                deserializerBuilder.WithDuplicateKeyChecking();
            }

            return deserializerBuilder.Build();
        }

        private JToken ParseYamlDocument(string input, IDeserializer deserializer)
        {
            var yamlObject = deserializer.Deserialize(new StringReader(input))
                ?? throw new FormatException("YAML document is empty or null");

            return ConvertObjectToJToken(yamlObject);
        }

        private static bool HasMultipleDocuments(string input)
        {
            var lines = input.Split('\n');
            return lines.Count(line => line.Trim() == "---") > 1;
        }

        private static bool HasLargeArrays(string input)
        {
            var lines = input.Split('\n');
            var arrayItemLines = lines.Count(line => line.TrimStart().StartsWith("- "));
            return arrayItemLines > 20;
        }

        private bool HasManyTopLevelProperties(string input)
        {
            try
            {
                var deserializer = CreateDeserializer();
                var yamlObject = deserializer.Deserialize(new StringReader(input));

                if (yamlObject is Dictionary<object, object> dict)
                {
                    return dict.Count > 15;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static int GetTokenSizeInBytes(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => Encoding.UTF8.GetByteCount(token.ToString()),
                JTokenType.Integer => Encoding.UTF8.GetByteCount(token.ToString()),
                JTokenType.Float => Encoding.UTF8.GetByteCount(token.ToString()),
                JTokenType.Boolean => token.ToString().Length,
                JTokenType.Null => 4,
                JTokenType.Date => Encoding.UTF8.GetByteCount(token.ToString()),
                JTokenType.Object => GetObjectSizeInBytes((JObject)token),
                JTokenType.Array => GetArraySizeInBytes((JArray)token),
                _ => Encoding.UTF8.GetByteCount(token.ToString())
            };
        }

        private static int GetObjectSizeInBytes(JObject obj)
        {
            var totalSize = 2;
            foreach (var property in obj.Properties())
            {
                totalSize += Encoding.UTF8.GetByteCount($"\"{property.Name}\":");
                totalSize += GetTokenSizeInBytes(property.Value);
                totalSize += 1;
            }
            return totalSize;
        }

        private static int GetArraySizeInBytes(JArray array)
        {
            var totalSize = 2;
            foreach (var item in array)
            {
                totalSize += GetTokenSizeInBytes(item);
                totalSize += 1;
            }
            return totalSize;
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: YAML parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000 ? string.Concat(input.AsSpan(0, 1000), "...") : input
                };
            }
            throw new FormatException($"Invalid YAML: {ex.Message}", ex);
        }

        private JToken ConvertObjectToJToken(object obj)
        {
            if (obj == null) return JValue.CreateNull();

            return obj switch
            {
                Dictionary<object, object> dict => ConvertDictionaryToJObject(dict),
                List<object> list => ConvertListToJArray(list),
                Array array => ConvertArrayToJArray(array),
                string str => new JValue(str),
                bool b => new JValue(b),
                byte b => new JValue(b),
                short s => new JValue(s),
                int i => new JValue(i),
                long l => new JValue(l),
                float f => new JValue(f),
                double d => new JValue(d),
                decimal m => new JValue(m),
                DateTime dt => new JValue(dt),
                _ => new JValue(obj.ToString())
            };
        }

        private JObject ConvertDictionaryToJObject(Dictionary<object, object> dict)
        {
            var result = new JObject();

            foreach (var kvp in dict)
            {
                var key = kvp.Key?.ToString() ?? "null";
                result[key] = ConvertObjectToJToken(kvp.Value);
            }

            return result;
        }

        private JArray ConvertListToJArray(List<object> list)
        {
            var result = new JArray();

            foreach (var item in list)
            {
                result.Add(ConvertObjectToJToken(item));
            }

            return result;
        }

        private JArray ConvertArrayToJArray(Array array)
        {
            var result = new JArray();

            foreach (var item in array)
            {
                result.Add(ConvertObjectToJToken(item));
            }

            return result;
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