using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Json
{
    public class JsonOutputStrategy : BaseOutputStrategy
    {
        private const int DEFAULT_CHUNK_SIZE = 100;

        public override string Serialize(JToken data)
        {
            var processed = ProcessDataBeforeSerialization(data);
            var settings = CreateJsonSerializerSettings();

            return SerializeToken(processed, JsonSerializer.Create(settings));
        }

        public override IEnumerable<string> SerializeStream(IEnumerable<JToken> data)
        {
            var settings = CreateJsonSerializerSettings();
            var serializer = JsonSerializer.Create(settings);

            if (data is JArray array)
            {
                foreach (var chunk in StreamArray(array, serializer))
                    yield return chunk;
            }
            else if (data.All(t => t.Type == JTokenType.Object))
            {
                foreach (var chunk in StreamObjectSequence(data, serializer))
                    yield return chunk;
            }
            else
            {
                yield return Serialize(new JArray(data.ToArray()));
            }
        }

        private IEnumerable<string> StreamArray(JArray array, JsonSerializer serializer)
        {
            var chunkSize = GetChunkSize();
            var needsPretty = NeedsPretty();
            var items = array.Children().ToList();

            yield return needsPretty ? "[\n" : "[";

            for (int i = 0; i < items.Count; i += chunkSize)
            {
                var chunkItems = items.Skip(i).Take(chunkSize).ToList();
                var chunkJson = SerializeChunk(chunkItems, serializer, i > 0);

                if (!string.IsNullOrEmpty(chunkJson))
                {
                    yield return chunkJson;
                }
            }

            yield return needsPretty ? "\n]" : "]";
        }

        private IEnumerable<string> StreamObjectSequence(IEnumerable<JToken> objects, JsonSerializer serializer)
        {
            var chunkSize = GetChunkSize();
            var needsPretty = NeedsPretty();
            var buffer = new List<JToken>();
            var firstChunk = true;

            yield return needsPretty ? "[\n" : "[";

            foreach (var obj in objects)
            {
                buffer.Add(obj);

                if (buffer.Count >= chunkSize)
                {
                    yield return SerializeChunk(buffer, serializer, !firstChunk);
                    buffer.Clear();
                    firstChunk = false;
                }
            }

            if (buffer.Count > 0)
            {
                yield return SerializeChunk(buffer, serializer, !firstChunk);
            }

            yield return needsPretty ? "\n]" : "]";
        }

        private string SerializeChunk(List<JToken> items, JsonSerializer serializer, bool includeComma)
        {
            if (items.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            var needsPretty = NeedsPretty();
            var indent = needsPretty ? new string(' ', Config.IndentSize ?? 2) : "";

            if (includeComma)
            {
                sb.Append(",");
                if (needsPretty) sb.Append("\n");
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(",");
                    if (needsPretty) sb.Append("\n");
                }

                if (needsPretty) sb.Append(indent);

                try
                {
                    var itemJson = SerializeToken(items[i], serializer);
                    sb.Append(itemJson);
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    var errorJson = CreateErrorJson(ex.Message, items[i]);
                    sb.Append(errorJson);
                }
            }

            return sb.ToString();
        }

        private string SerializeToken(JToken token, JsonSerializer serializer)
        {
            using var sw = new StringWriter();
            using var writer = new JsonTextWriter(sw);

            ConfigureJsonWriter(writer);

            try
            {
                serializer.Serialize(writer, token);
                var result = sw.ToString();

                return Config.JsonSingleQuotes && !Config.Minify
                    ? ConvertToSingleQuotes(result)
                    : result;
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                return CreateErrorJson(ex.Message, token);
            }
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            var result = data;

            if (Config.SortKeys)
                result = SortKeysRecursively(result);

            if (Config.ArrayWrap && result.Type != JTokenType.Array)
                result = new JArray(result);

            return result;
        }

        private bool NeedsPretty() => !Config.Minify && Config.PrettyPrint;

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : DEFAULT_CHUNK_SIZE;

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
            => jsonString.Replace("\"", "'");

        private string CreateErrorJson(string errorMessage, JToken originalToken)
        {
            var errorObj = new JObject
            {
                ["error"] = errorMessage,
                ["original_type"] = originalToken.Type.ToString(),
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            return Config.Minify
                ? errorObj.ToString(Formatting.None)
                : errorObj.ToString(Formatting.Indented);
        }
    }
}