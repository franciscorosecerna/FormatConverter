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
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = ProcessDataBeforeSerialization(data);
            var settings = CreateJsonSerializerSettings();

            return SerializeToken(processed, JsonSerializer.Create(settings));
        }

        public override IEnumerable<string> SerializeStream(IEnumerable<JToken> data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var settings = CreateJsonSerializerSettings();
            var serializer = JsonSerializer.Create(settings);

            foreach (var chunk in StreamTokens(data, serializer))
                yield return chunk;
        }

        private IEnumerable<string> StreamTokens(IEnumerable<JToken> tokens, JsonSerializer serializer)
        {
            var chunkSize = GetChunkSize();
            var needsPretty = NeedsPretty();
            var buffer = new List<JToken>();
            var firstChunk = true;

            yield return needsPretty ? "[\n" : "[";

            foreach (var token in tokens)
            {
                buffer.Add(token);

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

            var chunkBuilder = new StringBuilder();
            var needsPretty = NeedsPretty();
            var indent = needsPretty ? new string(' ', Config.IndentSize ?? 2) : "";

            if (includeComma)
            {
                chunkBuilder.Append(",");
                if (needsPretty) chunkBuilder.Append('\n');
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    chunkBuilder.Append(",");
                    if (needsPretty) chunkBuilder.Append('\n');
                }

                if (needsPretty) chunkBuilder.Append(indent);

                try
                {
                    var itemJson = SerializeToken(items[i], serializer);
                    chunkBuilder.Append(itemJson);
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    var errorJson = CreateErrorJson(ex.Message, items[i]);
                    chunkBuilder.Append(errorJson);
                }
            }

            return chunkBuilder.ToString();
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
            var sb = new StringBuilder(jsonString.Length);
            bool inString = false;
            char? prevChar = null;

            for (int i = 0; i < jsonString.Length; i++)
            {
                char c = jsonString[i];

                if (c == '"' && prevChar != '\\')
                {
                    sb.Append('\'');
                    inString = !inString;
                }
                else if (c == '\\' && prevChar == '\\')
                {
                    sb.Append(c);
                    prevChar = null;
                    continue;
                }
                else
                {
                    sb.Append(c);
                }

                prevChar = c;
            }

            return sb.ToString();
        }

        private string CreateErrorJson(string errorMessage, JToken originalToken)
        {
            var errorObj = new JObject
            {
                ["error"] = errorMessage,
                ["original_type"] = originalToken.Type.ToString(),
                ["timestamp"] = FormatDateTime(DateTime.UtcNow)
            };

            return Config.Minify
                ? errorObj.ToString(Formatting.None)
                : errorObj.ToString(Formatting.Indented);
        }
    }
}