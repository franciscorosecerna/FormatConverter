using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;

namespace FormatConverter.Json
{
    public class JsonOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = PreprocessToken(data);
            var settings = CreateJsonSerializerSettings();

            var result = SerializeToken(processed, JsonSerializer.Create(settings));

            if (Config.StrictMode)
            {
                ValidateJson(result);
            }

            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            var settings = CreateJsonSerializerSettings();
            var serializer = JsonSerializer.Create(settings);
            var chunkSize = GetChunkSize();

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);
            using var jsonWriter = new JsonTextWriter(writer);

            ConfigureJsonWriter(jsonWriter);

            var buffer = new List<JToken>();

            jsonWriter.WriteStartArray();

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
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

                try
                {
                    serializer.Serialize(jsonWriter, items[i]);
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"JSON serialization error in item {i}: {ex.Message}");
                    var errorObj = new JObject
                    {
                        ["error"] = ex.Message,
                        ["error_type"] = ex.GetType().Name,
                        ["original_type"] = items[i].Type.ToString(),
                        ["timestamp"] = DateTime.UtcNow.ToString("o")
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
                Logger.WriteWarning($"JSON serialization error ignored: {ex.Message}");
                return CreateErrorJson(ex.Message, ex.GetType().Name, token);
            }
        }

        private void ValidateJson(string json)
        {
            try
            {
                JToken.Parse(json);
            }
            catch when (!Config.StrictMode) { }
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
                    Logger.WriteWarning($"JSON serialization error: {e.ErrorContext.Error.Message}");
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

        private string CreateErrorJson(string errorMessage, string errorType, JToken originalToken)
        {
            var errorObj = new JObject
            {
                ["error"] = errorMessage,
                ["error_type"] = errorType,
                ["original_type"] = originalToken.Type.ToString(),
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            return Config.Minify
                ? errorObj.ToString(Formatting.None)
                : errorObj.ToString(Formatting.Indented);
        }
    }
}