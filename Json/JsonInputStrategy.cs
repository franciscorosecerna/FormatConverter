using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FormatConverter.Json
{
    public class JsonInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string json)
        {
            Logger.WriteTrace(() => "Parse: Starting JSON parsing");

            if (string.IsNullOrWhiteSpace(json))
            {
                Logger.WriteWarning(() => "Parse: Input is null or empty");
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("JSON input cannot be null or empty", nameof(json));
            }

            Logger.WriteDebug(() => $"Parse: Input length: {json.Length} characters");
            json = PreprocessJson(json);

            var settings = CreateJsonLoadSettings();

            try
            {
                using var stringReader = new StringReader(json);
                using var jsonReader = new JsonTextReader(stringReader)
                {
                    MaxDepth = Config.MaxDepth,
                    DateParseHandling = DateParseHandling.None
                };

                Logger.WriteTrace(() => "Parse: Reading JSON token");
                var token = JToken.ReadFrom(jsonReader, settings);

                Logger.WriteSuccess($"Parse: JSON parsed successfully as {token.Type}");
                return token;
            }
            catch (JsonReaderException ex)
            {
                Logger.WriteError(() => $"Parse: JsonReaderException at line {ex.LineNumber}, position {ex.LinePosition} - {ex.Message}");
                return HandleParsingError(ex, json);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo(() => $"ParseStream: Starting stream parsing for '{path}'");

            if (string.IsNullOrWhiteSpace(path))
            {
                Logger.WriteError(() => "ParseStream: Path is null or empty");
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));
            }

            if (!File.Exists(path))
            {
                Logger.WriteError(() => $"ParseStream: File not found at '{path}'");
                throw new FileNotFoundException("Input file not found.", path);
            }

            Logger.WriteDebug(() => $"ParseStream: File found, size: {new FileInfo(path).Length} bytes");
            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken)
        {
            Logger.WriteTrace(() => "ParseStreamInternal: Opening file stream");

            using var fileStream = File.OpenRead(path);
            using var streamReader = new StreamReader(fileStream, Config.Encoding, detectEncodingFromByteOrderMarks: true);
            using var jsonReader = CreateJsonTextReader(streamReader);

            var fileSize = fileStream.Length;
            var showProgress = fileSize > 10_485_760;
            var tokensProcessed = 0;
            var settings = CreateJsonLoadSettings();

            Logger.WriteDebug(() => $"ParseStreamInternal: File size: {fileSize:N0} bytes, progress logging: {showProgress}");
            Logger.WriteTrace(() => "ParseStreamInternal: Starting token iteration");

            while (jsonReader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (jsonReader.TokenType == JsonToken.StartObject ||
                    jsonReader.TokenType == JsonToken.StartArray)
                {
                    Logger.WriteTrace(() => $"ParseStreamInternal: Found {jsonReader.TokenType} at line {jsonReader.LineNumber}");
                    var token = ReadToken(jsonReader, settings, path);

                    if (token != null)
                    {
                        tokensProcessed++;

                        if (showProgress && tokensProcessed % 100 == 0)
                        {
                            var progress = (double)fileStream.Position / fileSize * 100;
                            Logger.WriteInfo(() => $"Processing: {progress:F1}% ({tokensProcessed} elements)");
                        }

                        Logger.WriteTrace(() => $"ParseStreamInternal: Token {tokensProcessed} parsed as {token.Type}");
                        yield return token;
                    }
                }
            }

            if (showProgress)
            {
                Logger.WriteInfo(() => $"Completed: {tokensProcessed} objects processed");
            }

            Logger.WriteSuccess($"ParseStreamInternal: Stream parsing completed. Total tokens: {tokensProcessed}");
        }

        private JToken? ReadToken(JsonTextReader jsonReader, JsonLoadSettings settings, string path)
        {
            Logger.WriteTrace(() => $"ReadToken: Reading token at line {jsonReader.LineNumber}, position {jsonReader.LinePosition}");

            try
            {
                var token = JToken.ReadFrom(jsonReader, settings);
                Logger.WriteTrace(() => $"ReadToken: Successfully read {token.Type}");
                return token;
            }
            catch (JsonReaderException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => $"JSON streaming error at line {jsonReader.LineNumber}, " +
                                      $"position {jsonReader.LinePosition}: {ex.Message}");
                    return CreateErrorToken(ex, jsonReader);
                }

                Logger.WriteError(() => $"ReadToken: Fatal error at line {jsonReader.LineNumber}, position {jsonReader.LinePosition}");
                throw new FormatException(
                    $"Invalid JSON at line {jsonReader.LineNumber}, position {jsonReader.LinePosition}: {ex.Message}",
                    ex);
            }
        }

        private JsonTextReader CreateJsonTextReader(StreamReader streamReader)
        {
            Logger.WriteTrace(() => "CreateJsonTextReader: Creating JsonTextReader with multiple content support");
            Logger.WriteDebug(() => $"CreateJsonTextReader: MaxDepth={Config.MaxDepth?.ToString() ?? "unlimited"}");

            return new JsonTextReader(streamReader)
            {
                SupportMultipleContent = true,
                DateParseHandling = DateParseHandling.None,
                MaxDepth = Config.MaxDepth
            };
        }

        private JsonLoadSettings CreateJsonLoadSettings()
        {
            Logger.WriteTrace(() => "CreateJsonLoadSettings: Creating JSON load settings");

            var commentHandling = Config.NoMetadata ? CommentHandling.Ignore : CommentHandling.Load;
            var duplicateHandling = Config.StrictMode
                ? DuplicatePropertyNameHandling.Error
                : DuplicatePropertyNameHandling.Replace;
            var lineInfoHandling = Config.StrictMode ? LineInfoHandling.Load : LineInfoHandling.Ignore;

            Logger.WriteDebug(() => $"CreateJsonLoadSettings: CommentHandling={commentHandling}, " +
                            $"DuplicateHandling={duplicateHandling}, LineInfoHandling={lineInfoHandling}");

            return new JsonLoadSettings
            {
                CommentHandling = commentHandling,
                DuplicatePropertyNameHandling = duplicateHandling,
                LineInfoHandling = lineInfoHandling
            };
        }

        private string PreprocessJson(string json)
        {
            Logger.WriteTrace(() => $"PreprocessJson: Starting preprocessing ({json.Length} characters)");
            var modified = false;

            if (Config.JsonAllowSingleQuotes)
            {
                Logger.WriteDebug(() => "PreprocessJson: Converting single quotes to double quotes");
                json = ConvertSingleQuotesToDouble(json);
                modified = true;
            }

            if (Config.JsonAllowTrailingCommas)
            {
                Logger.WriteDebug(() => "PreprocessJson: Removing trailing commas");
                json = RemoveTrailingCommas(json);
                modified = true;
            }

            if (!Config.JsonStrictPropertyNames)
            {
                Logger.WriteDebug(() => "PreprocessJson: Adding quotes to property names");
                json = AddQuotesToPropertyNames(json);
                modified = true;
            }

            if (modified)
            {
                Logger.WriteTrace(() => $"PreprocessJson: Preprocessing completed, new length: {json.Length} characters");
            }
            else
            {
                Logger.WriteTrace(() => "PreprocessJson: No preprocessing needed");
            }

            return json;
        }

        private static string ConvertSingleQuotesToDouble(string json)
        {
            var result = new StringBuilder(json.Length);
            var inString = false;
            var quoteChar = '\0';
            var escaped = false;

            for (int i = 0; i < json.Length; i++)
            {
                var c = json[i];

                if (escaped)
                {
                    result.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    result.Append(c);
                    escaped = true;
                    continue;
                }

                if ((c == '\'' || c == '"') && !inString)
                {
                    inString = true;
                    quoteChar = c;
                    result.Append('"');
                }
                else if (c == quoteChar && inString)
                {
                    inString = false;
                    quoteChar = '\0';
                    result.Append('"');
                }
                else
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        private static string RemoveTrailingCommas(string json)
            => Regex.Replace(json, @",(\s*[\]}])", "$1", RegexOptions.Compiled);

        private static string AddQuotesToPropertyNames(string json)
            => Regex.Replace(json, @"(\{|\,)\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*:", "$1\"$2\":", RegexOptions.Compiled);

        private JObject HandleParsingError(JsonReaderException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning(() => $"JSON parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

            Logger.WriteError(() => $"HandleParsingError: Fatal error - {ex.Message}");
            throw new FormatException($"Invalid JSON: {ex.Message}", ex);
        }

        private static JObject CreateErrorToken(JsonReaderException ex, JsonTextReader reader)
        {
            return new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["line"] = reader.LineNumber,
                ["position"] = reader.LinePosition,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static JObject CreateErrorToken(JsonReaderException ex, string input)
        {
            var snippet = input.Length > 1000
                ? string.Concat(input.AsSpan(0, 1000), "...")
                : input;

            return new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["line"] = ex.LineNumber,
                ["position"] = ex.LinePosition,
                ["raw_snippet"] = snippet,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }
    }
}