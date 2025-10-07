using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Json
{
    public class JsonInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            if (Config.UseStreaming)
            {
                var firstToken = ParseStream(input).FirstOrDefault();
                return firstToken ?? new JObject();
            }

            var settings = CreateJsonLoadSettings();

            try
            {
                var token = JToken.Parse(input, settings);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                return token;
            }
            catch (JsonReaderException ex)
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

            if (PeekForArray(input))
            {
                foreach (var item in StreamJsonArray(bytes, totalSize))
                    yield return item;
            }
            else if (PeekForObject(input))
            {
                foreach (var chunk in StreamJsonObject(bytes, totalSize))
                    yield return chunk;
            }
            else
            {
                foreach (var token in StreamSimpleValues(bytes, totalSize))
                    yield return token;
            }
        }

        private IEnumerable<JToken> StreamJsonArray(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            using var stream = new MemoryStream(bytes);
            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            using var jsonReader = new JsonTextReader(streamReader)
            {
                SupportMultipleContent = true,
                CloseInput = false,
                ArrayPool = null
            };

            var settings = CreateJsonLoadSettings();

            IEnumerable<JToken> Iterator()
            {
                while (jsonReader.Read() && jsonReader.TokenType != JsonToken.StartArray)
                {
                    if (jsonReader.TokenType == JsonToken.Comment && Config.NoMetadata)
                        continue;
                }

                if (jsonReader.TokenType != JsonToken.StartArray)
                    yield break;

                var itemBuffer = new List<JToken>();
                var currentBufferSize = 0;
                var processedBytes = 0L;

                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.EndArray)
                    {
                        foreach (var bufferedItem in itemBuffer)
                            yield return bufferedItem;
                        break;
                    }

                    if (jsonReader.TokenType == JsonToken.Comment && Config.NoMetadata)
                        continue;

                    var item = JToken.ReadFrom(jsonReader, settings);

                    if (Config.SortKeys)
                        item = SortKeysRecursively(item);

                    itemBuffer.Add(item);
                    var tokenBytes = GetTokenSizeInBytes(item);
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
            }

            try
            {
                return Iterator();
            }
            catch (JsonReaderException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> StreamJsonObject(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            using var stream = new MemoryStream(bytes);
            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            using var jsonReader = new JsonTextReader(streamReader)
            {
                SupportMultipleContent = true,
                CloseInput = false,
                ArrayPool = null
            };

            var settings = CreateJsonLoadSettings();

            IEnumerable<JToken> Iterator()
            {
                while (jsonReader.Read() && jsonReader.TokenType != JsonToken.StartObject)
                {
                    if (jsonReader.TokenType == JsonToken.Comment && Config.NoMetadata)
                        continue;
                }

                if (jsonReader.TokenType != JsonToken.StartObject)
                    yield break;

                var memoryThreshold = bufferSize > 0 ? bufferSize : 1024 * 1024;
                var maxPropertiesPerChunk = Math.Max(10, bufferSize / 1024);

                var currentChunk = new JObject();
                int propertyCount = 0;
                var currentChunkSize = 0;
                var processedBytes = 0L;

                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.EndObject)
                    {
                        if (propertyCount > 0)
                            yield return Config.SortKeys ? SortJObject(currentChunk) : currentChunk;
                        break;
                    }

                    if (jsonReader.TokenType == JsonToken.Comment && Config.NoMetadata)
                        continue;

                    if (jsonReader.TokenType == JsonToken.PropertyName)
                    {
                        string propertyName = jsonReader.Value?.ToString();
                        if (string.IsNullOrEmpty(propertyName))
                            continue;

                        if (jsonReader.Read())
                        {
                            var value = JToken.ReadFrom(jsonReader, settings);

                            if (Config.SortKeys)
                                value = SortKeysRecursively(value);

                            currentChunk[propertyName] = value;
                            propertyCount++;

                            var propertyBytes = Encoding.UTF8.GetBytes(propertyName).Length;
                            var valueBytes = GetTokenSizeInBytes(value);
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
                                        Console.WriteLine($"Object streaming progress: {progress:F1}%");
                                }
                            }
                        }
                    }
                }
            }

            try
            {
                return Iterator();
            }
            catch (JsonReaderException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> StreamSimpleValues(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            using var stream = new MemoryStream(bytes);
            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            using var jsonReader = new JsonTextReader(streamReader)
            {
                SupportMultipleContent = true,
                CloseInput = false,
                ArrayPool = null
            };

            var settings = CreateJsonLoadSettings();

            IEnumerable<JToken> Iterator()
            {
                var tokenBuffer = new List<JToken>();
                var currentBufferSize = 0;
                var maxBufferSize = bufferSize > 0 ? bufferSize : 4096;
                var processedBytes = 0L;

                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.Comment && Config.NoMetadata)
                        continue;

                    var token = JToken.ReadFrom(jsonReader, settings);

                    if (Config.SortKeys)
                        token = SortKeysRecursively(token);

                    tokenBuffer.Add(token);
                    var tokenBytes = GetTokenSizeInBytes(token);
                    currentBufferSize += tokenBytes;
                    processedBytes += tokenBytes;

                    if (currentBufferSize >= maxBufferSize)
                    {
                        foreach (var bufferedToken in tokenBuffer)
                            yield return bufferedToken;

                        tokenBuffer.Clear();
                        currentBufferSize = 0;

                        if (totalSize > maxBufferSize * 5)
                        {
                            var progress = (double)processedBytes / totalSize * 100;
                            if (progress % 20 < 1)
                                Console.WriteLine($"Simple values streaming progress: {progress:F1}%");
                        }
                    }

                    if (!jsonReader.SupportMultipleContent)
                        break;
                }

                foreach (var bufferedToken in tokenBuffer)
                    yield return bufferedToken;
            }

            try
            {
                return Iterator();
            }
            catch (JsonReaderException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> HandleStreamingError(JsonReaderException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: JSON streaming error ignored: {ex.Message}");
                return [HandleParsingError(ex, input)];
            }
            else
            {
                throw new FormatException($"Invalid JSON during streaming: {ex.Message}", ex);
            }
        }

        private JsonLoadSettings CreateJsonLoadSettings()
        {
            return new JsonLoadSettings
            {
                CommentHandling = Config.NoMetadata ? CommentHandling.Ignore : CommentHandling.Load,
                DuplicatePropertyNameHandling = Config.StrictMode ? DuplicatePropertyNameHandling.Error
                    : DuplicatePropertyNameHandling.Replace
            };
        }

        private static bool PeekForArray(string input) => input.TrimStart().StartsWith('[');

        private static bool PeekForObject(string input) => input.TrimStart().StartsWith('{');

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

        private JObject HandleParsingError(JsonReaderException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: JSON parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000 ? string.Concat(input.AsSpan(0, 1000), "...") : input
                };
            }
            throw new FormatException($"Invalid JSON: {ex.Message}", ex);
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