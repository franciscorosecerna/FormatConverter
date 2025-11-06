using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FormatConverter.Json
{
    public class JsonOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            Logger.WriteTrace(() => "Serialize: Starting JSON serialization");

            ArgumentNullException.ThrowIfNull(data);

            Logger.WriteDebug(() => $"Serialize: Input token type: {data.Type}");
            var processed = PreprocessToken(data);
            var settings = CreateJsonSerializerSettings();

            var result = SerializeToken(processed, JsonSerializer.Create(settings));

            if (Config.StrictMode)
            {
                Logger.WriteDebug(() => "Serialize: Validating JSON in strict mode");
                ValidateJson(result);
            }

            Logger.WriteSuccess($"Serialize: JSON serialization completed ({result.Length} characters)");
            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo(() => "SerializeStream: Starting stream serialization");

            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(output);

            var settings = CreateJsonSerializerSettings();
            var serializer = JsonSerializer.Create(settings);
            var chunkSize = GetChunkSize();

            Logger.WriteDebug(() => $"SerializeStream: Chunk size: {chunkSize}");

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);
            using var jsonWriter = new JsonTextWriter(writer);

            ConfigureJsonWriter(jsonWriter);

            var buffer = new List<JToken>();
            var totalProcessed = 0;

            Logger.WriteTrace(() => "SerializeStream: Writing array start");
            jsonWriter.WriteStartArray();

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    Logger.WriteTrace(() => $"SerializeStream: Writing chunk of {buffer.Count} items");
                    WriteChunkToStream(buffer, serializer, jsonWriter, cancellationToken);
                    totalProcessed += buffer.Count;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.WriteTrace(() => $"SerializeStream: Writing final chunk of {buffer.Count} items");
                WriteChunkToStream(buffer, serializer, jsonWriter, cancellationToken);
                totalProcessed += buffer.Count;
            }

            Logger.WriteTrace(() => "SerializeStream: Writing array end");
            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
            writer.Flush();

            Logger.WriteSuccess($"SerializeStream: Completed. Total items: {totalProcessed}");
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo(() => $"SerializeStream: Writing to file '{outputPath}'");

            if (string.IsNullOrEmpty(outputPath))
            {
                Logger.WriteError(() => "SerializeStream: Output path is null or empty");
                throw new ArgumentNullException(nameof(outputPath));
            }

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);

            Logger.WriteSuccess($"SerializeStream: File written successfully to '{outputPath}'");
        }

        private void WriteChunkToStream(List<JToken> items, JsonSerializer serializer, JsonTextWriter jsonWriter, CancellationToken ct)
        {
            if (items.Count == 0)
            {
                Logger.WriteTrace(() => "WriteChunkToStream: Empty chunk, skipping");
                return;
            }

            Logger.WriteTrace(() => $"WriteChunkToStream: Processing {items.Count} items");

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    serializer.Serialize(jsonWriter, items[i]);
                    Logger.WriteTrace(() => $"WriteChunkToStream: Item {i} serialized successfully");
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => $"JSON serialization error in item {i}: {ex.Message}");
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
            Logger.WriteTrace(() => "WriteChunkToStream: Chunk flushed");
        }

        private string SerializeToken(JToken token, JsonSerializer serializer)
        {
            Logger.WriteTrace(() => $"SerializeToken: Serializing token type {token.Type}");

            using var sw = new StringWriter();
            using var writer = new JsonTextWriter(sw);

            ConfigureJsonWriter(writer);

            try
            {
                serializer.Serialize(writer, token);
                var result = sw.ToString();
                Logger.WriteDebug(() => $"SerializeToken: Generated {result.Length} characters");
                return result;
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning(() => $"JSON serialization error ignored: {ex.Message}");
                return CreateErrorJson(ex.Message, ex.GetType().Name, token);
            }
        }

        private void ValidateJson(string json)
        {
            Logger.WriteTrace(() => $"ValidateJson: Validating {json.Length} characters");

            try
            {
                JToken.Parse(json);
                Logger.WriteDebug(() => "ValidateJson: Validation successful");
            }
            catch (Exception ex) when (!Config.StrictMode)
            {
                Logger.WriteWarning(() => $"ValidateJson: Validation failed but ignored - {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.WriteError(() => $"ValidateJson: Validation failed - {ex.Message}");
                throw;
            }
        }

        private bool NeedsPretty() => !Config.Minify && Config.PrettyPrint;

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;

        private JsonSerializerSettings CreateJsonSerializerSettings()
        {
            Logger.WriteTrace(() => "CreateJsonSerializerSettings: Creating JSON serializer settings");

            var formatting = Config.Minify ? Formatting.None :
                            Config.PrettyPrint ? Formatting.Indented : Formatting.None;

            var escapeHandling = Config.JsonEscapeUnicode ?
                               StringEscapeHandling.EscapeNonAscii :
                               StringEscapeHandling.Default;

            Logger.WriteDebug(() => $"CreateJsonSerializerSettings: Formatting={formatting}, " +
                            $"EscapeHandling={escapeHandling}, StrictMode={Config.StrictMode}");

            var settings = new JsonSerializerSettings
            {
                Formatting = formatting,
                StringEscapeHandling = escapeHandling
            };

            if (Config.IgnoreErrors)
            {
                Logger.WriteDebug(() => "CreateJsonSerializerSettings: Error handling configured for IgnoreErrors mode");
                settings.Error = (s, e) =>
                {
                    Logger.WriteWarning(() => $"JSON serialization error: {e.ErrorContext.Error.Message}");
                    e.ErrorContext.Handled = true;
                };
            }

            if (Config.StrictMode)
            {
                Logger.WriteDebug(() => "CreateJsonSerializerSettings: MissingMemberHandling set to Error for strict mode");
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
                        Logger.WriteDebug(() => "ConfigureJsonWriter: Using tab indentation");
                        writer.Indentation = 1;
                        writer.IndentChar = '\t';
                    }
                    else
                    {
                        Logger.WriteDebug(() => $"ConfigureJsonWriter: Using {Config.IndentSize.Value} space indentation");
                        writer.Indentation = Config.IndentSize.Value;
                        writer.IndentChar = ' ';
                    }
                }
                else
                {
                    Logger.WriteDebug(() => "ConfigureJsonWriter: Using default 2 space indentation");
                    writer.Indentation = 2;
                    writer.IndentChar = ' ';
                }
            }
            else
            {
                Logger.WriteTrace(() => "ConfigureJsonWriter: Pretty print disabled, using compact format");
            }
        }

        private string CreateErrorJson(string errorMessage, string errorType, JToken originalToken)
        {
            Logger.WriteTrace(() => "CreateErrorJson: Creating error JSON");

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