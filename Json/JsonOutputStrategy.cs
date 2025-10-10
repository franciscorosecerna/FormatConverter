using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;
using System.Text;

namespace FormatConverter.Json
{
    public class JsonOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = ProcessDataBeforeSerialization(data);
            var settings = CreateJsonSerializerSettings();

            return SerializeToken(processed, JsonSerializer.Create(settings));
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            var settings = CreateJsonSerializerSettings();
            var serializer = JsonSerializer.Create(settings);
            var needsPretty = NeedsPretty();
            var chunkSize = GetChunkSize();

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);
            using var jsonWriter = new JsonTextWriter(writer);

            ConfigureJsonWriter(jsonWriter);

            var buffer = new List<JToken>();

            jsonWriter.WriteStartArray();

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = ProcessDataBeforeSerialization(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    WriteChunkToStream(buffer, serializer, jsonWriter, cancellationToken);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStream(buffer, serializer, jsonWriter, cancellationToken);
            }

            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
            writer.Flush();
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
        }

        private void WriteChunkToStream(List<JToken> items, JsonSerializer serializer, JsonTextWriter jsonWriter, CancellationToken ct)
        {
            if (items.Count == 0) return;

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (jsonWriter.WriteState == WriteState.Array && i > 0)
                    jsonWriter.WriteRaw(",");

                try
                {
                    serializer.Serialize(jsonWriter, items[i]);
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    var errorObj = new JObject
                    {
                        ["error"] = ex.Message,
                        ["original_type"] = items[i].Type.ToString(),
                        ["timestamp"] = DateTime.UtcNow
                    };
                    serializer.Serialize(jsonWriter, errorObj);
                }
            }

            jsonWriter.Flush();
        }

        private string SerializeToken(JToken token, JsonSerializer serializer)
        {
            using var sw = new StringWriter();
            using var writer = new JsonTextWriter(sw);

            ConfigureJsonWriter(writer);

            try
            {
                serializer.Serialize(writer, token);
                return sw.ToString();
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                return CreateErrorJson(ex.Message, token);
            }
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            var result = data;

            if (Config.FlattenArrays)
                result = FlattenArraysRecursively(result);

            if (Config.SortKeys)
                result = SortKeysRecursively(result);

            if (Config.ArrayWrap && result.Type != JTokenType.Array)
                result = new JArray(result);

            if (Config.NoMetadata)
                result = RemoveMetadata(result);

            if (Config.MaxDepth.HasValue)
                result = LimitDepth(result, Config.MaxDepth.Value);

            return result;
        }

        private JToken RemoveMetadata(JToken token)
        {
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var metadataKeys = new[] { "$schema", "$id", "$comment", "$ref", "_metadata", "__meta__", "__type" };
                var cleaned = new JObject();

                foreach (var prop in obj.Properties())
                {
                    if (!metadataKeys.Contains(prop.Name))
                    {
                        cleaned[prop.Name] = RemoveMetadata(prop.Value);
                    }
                }

                return cleaned;
            }
            else if (token.Type == JTokenType.Array)
            {
                return new JArray(((JArray)token).Select(RemoveMetadata));
            }

            return token;
        }

        private static JToken LimitDepth(JToken token, int maxDepth, int currentDepth = 0)
        {
            if (currentDepth >= maxDepth)
            {
                return token.Type switch
                {
                    JTokenType.Object => new JObject(),
                    JTokenType.Array => new JArray(),
                    _ => token
                };
            }

            return token.Type switch
            {
                JTokenType.Object => new JObject(
                    ((JObject)token).Properties()
                        .Select(p => new JProperty(p.Name, LimitDepth(p.Value, maxDepth, currentDepth + 1)))
                ),
                JTokenType.Array => new JArray(
                    ((JArray)token).Select(item => LimitDepth(item, maxDepth, currentDepth + 1))
                ),
                _ => token
            };
        }

        private bool NeedsPretty() => !Config.Minify && Config.PrettyPrint;

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;

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
                {
                    settings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
                    settings.DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ";
                }
                else if (Config.DateFormat.Equals("unix", StringComparison.OrdinalIgnoreCase))
                {
                    settings.Converters.Add(new UnixDateTimeConverter());
                    settings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
                }
                else if (Config.DateFormat.Equals("rfc3339", StringComparison.OrdinalIgnoreCase))
                {
                    settings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
                    settings.DateFormatString = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
                }
                else
                {
                    settings.DateFormatString = Config.DateFormat;
                }
            }

            if (Config.IgnoreErrors)
            {
                settings.Error = (s, e) =>
                {
                    e.ErrorContext.Handled = true;
                };
            }

            if (Config.StrictMode)
            {
                settings.MissingMemberHandling = MissingMemberHandling.Error;
            }

            return settings;
        }

        private void ConfigureJsonWriter(JsonTextWriter writer)
        {
            if (NeedsPretty())
            {
                if (Config.IndentSize.HasValue)
                {
                    if (Config.IndentSize.Value == 0)
                    {
                        writer.Indentation = 1;
                        writer.IndentChar = '\t';
                    }
                    else
                    {
                        writer.Indentation = Config.IndentSize.Value;
                        writer.IndentChar = ' ';
                    }
                }
                else
                {
                    writer.Indentation = 2;
                    writer.IndentChar = ' ';
                }
            }
        }

        private string CreateErrorJson(string errorMessage, JToken originalToken)
        {
            var errorObj = new JObject
            {
                ["error"] = errorMessage,
                ["original_type"] = originalToken.Type.ToString(),
                ["timestamp"] = DateTime.UtcNow
            };

            return Config.Minify
                ? errorObj.ToString(Formatting.None)
                : errorObj.ToString(Formatting.Indented);
        }
    }
}