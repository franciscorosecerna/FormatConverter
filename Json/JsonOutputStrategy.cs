using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Json
{
    public class JsonOutputStrategy : BaseOutputStrategy
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
            var settings = CreateJsonSerializerSettings();

            return processed.Type switch
            {
                JTokenType.Array => StreamChunked(((JArray)processed).Children(), settings, "[", "]"),
                JTokenType.Object => StreamChunked(((JObject)processed).Properties(), settings, "{", "}"),
                _ => StreamSingle(processed, settings)
            };
        }

        private IEnumerable<string> StreamChunked<T>(
            IEnumerable<T> items,
            JsonSerializerSettings settings,
            string open,
            string close) where T : JToken
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var serializer = JsonSerializer.Create(settings);

            yield return NeedsPretty() ? open + "\n" : open;

            bool first = true;
            var buffer = new List<T>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var itemCount = items is ICollection<T> collection ? collection.Count : -1;

            foreach (var item in items)
            {
                var serializedItem = SerializeToken(item, serializer);
                var itemSizeInBytes = Encoding.UTF8.GetByteCount(serializedItem);

                if (buffer.Count > 0 && currentBufferSize + itemSizeInBytes > bufferSize)
                {
                    yield return SerializeChunk(buffer, serializer, ref first);
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
                yield return SerializeChunk(buffer, serializer, ref first);
            }

            yield return NeedsPretty() ? "\n" + close : close;
        }

        private string SerializeChunk<T>(
            List<T> items,
            JsonSerializer serializer,
            ref bool first) where T : JToken
        {
            var sb = new StringBuilder();

            for (int i = 0; i < items.Count; i++)
            {
                if (!first || i > 0)
                {
                    sb.Append(",");
                    if (NeedsPretty())
                    {
                        sb.Append("\n").Append(new string(' ', Config.IndentSize ?? 2));
                    }
                }
                else if (NeedsPretty())
                {
                    sb.Append(new string(' ', Config.IndentSize ?? 2));
                }

                try
                {
                    var serializedItem = SerializeToken(items[i], serializer);
                    sb.Append(serializedItem);
                }
                catch (Exception ex)
                {
                    if (Config.IgnoreErrors)
                    {
                        sb.Append(CreateErrorJsonForChunk(ex.Message, items.Count));
                    }
                    else
                    {
                        throw new FormatException($"Error serializing item {i} in chunk: {ex.Message}", ex);
                    }
                }
            }

            first = false;
            return sb.ToString();
        }

        private IEnumerable<string> StreamSingle(JToken token, JsonSerializerSettings settings)
        {
            IEnumerable<string> Iterator()
            {
                var serialized = SerializeToken(token, JsonSerializer.Create(settings));
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
                    return [CreateErrorJsonForChunk(ex.Message, 1)];
                }
                else
                {
                    throw new FormatException($"Error serializing single token: {ex.Message}", ex);
                }
            }
        }

        private string SerializeRegular(JToken data)
        {
            var processed = ProcessDataBeforeSerialization(data);
            var settings = CreateJsonSerializerSettings();

            return SerializeToken(processed, JsonSerializer.Create(settings));
        }

        private string SerializeToken(JToken token, JsonSerializer serializer)
        {
            using var sw = new StringWriter();
            using var writer = new JsonTextWriter(sw);

            ConfigureJsonWriter(writer);
            serializer.Serialize(writer, token);

            var result = sw.ToString();
            return (Config.JsonSingleQuotes && !Config.Minify)
                ? ConvertToSingleQuotes(result)
                : result;
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys)
                data = SortKeysRecursively(data);

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
                return new JArray(data);

            return data;
        }

        private bool NeedsPretty() => !Config.Minify && Config.PrettyPrint;

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

        private JsonSerializerSettings CreateJsonSerializerSettings()
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Config.Minify ? Formatting.None :
                            Config.PrettyPrint ? Formatting.Indented : Formatting.None,
                StringEscapeHandling = Config.JsonEscapeUnicode ?
                                       StringEscapeHandling.EscapeNonAscii :
                                       StringEscapeHandling.Default
            };

            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                if (Config.DateFormat.Equals("iso8601", StringComparison.OrdinalIgnoreCase))
                    settings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
                else
                    settings.DateFormatString = Config.DateFormat;
            }

            if (Config.IgnoreErrors)
            {
                settings.Error = (s, e) => e.ErrorContext.Handled = true;
            }

            return settings;
        }

        private void ConfigureJsonWriter(JsonTextWriter writer)
        {
            if (Config.IndentSize.HasValue && NeedsPretty())
            {
                writer.Indentation = Config.IndentSize.Value;
                writer.IndentChar = Config.IndentSize.Value == 0 ? '\t' : ' ';
            }

            writer.QuoteChar = Config.JsonSingleQuotes ? '\'' : '"';
        }

        private static string ConvertToSingleQuotes(string jsonString)
        {
            return jsonString.Replace("\"", "'");
        }

        private string CreateErrorJsonForChunk(string errorMessage, int count)
        {
            var errorObj = new JObject
            {
                ["error"] = errorMessage,
                ["itemCount"] = count,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            return Config.Minify
                ? errorObj.ToString(Formatting.None)
                : errorObj.ToString(Formatting.Indented);
        }
    }
}