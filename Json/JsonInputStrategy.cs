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
            if (string.IsNullOrWhiteSpace(json))
            {
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("JSON input cannot be null or empty", nameof(json));
            }

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

                var token = JToken.ReadFrom(jsonReader, settings);

                if (Config.MaxDepth.HasValue)
                {
                    ValidateDepth(token, Config.MaxDepth.Value);
                }

                return token;
            }
            catch (JsonReaderException ex)
            {
                return HandleParsingError(ex, json);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Input file not found.", path);

            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken)
        {
            using var fileStream = File.OpenRead(path);
            using var streamReader = new StreamReader(fileStream, Config.Encoding, detectEncodingFromByteOrderMarks: true);
            using var jsonReader = CreateJsonTextReader(streamReader);

            var fileSize = fileStream.Length;
            var showProgress = fileSize > 10_485_760;
            var tokensProcessed = 0;
            var settings = CreateJsonLoadSettings();

            while (jsonReader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (jsonReader.TokenType == JsonToken.StartObject ||
                    jsonReader.TokenType == JsonToken.StartArray)
                {
                    var token = ReadToken(jsonReader, settings, path);

                    if (token != null)
                    {
                        tokensProcessed++;

                        if (showProgress && tokensProcessed % 100 == 0)
                        {
                            var progress = (double)fileStream.Position / fileSize * 100;
                            Logger.WriteInfo($"Processing: {progress:F1}% ({tokensProcessed} elements)");
                        }

                        yield return token;
                    }
                }
            }

            if (showProgress)
            {
                Logger.WriteInfo($"Completed: {tokensProcessed} objects processed");
            }
        }

        private JToken? ReadToken(JsonTextReader jsonReader, JsonLoadSettings settings, string path)
        {
            try
            {
                var token = JToken.ReadFrom(jsonReader, settings);

                if (Config.MaxDepth.HasValue)
                {
                    ValidateDepth(token, Config.MaxDepth.Value);
                }

                return token;
            }
            catch (JsonReaderException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"JSON streaming error at line {jsonReader.LineNumber}, " +
                                      $"position {jsonReader.LinePosition}: {ex.Message}");
                    return CreateErrorToken(ex, jsonReader);
                }

                throw new FormatException(
                    $"Invalid JSON at line {jsonReader.LineNumber}, position {jsonReader.LinePosition}: {ex.Message}",
                    ex);
            }
        }

        private void ValidateDepth(JToken token, int maxDepth)
        {
            var actualDepth = CalculateDepth(token);

            if (actualDepth > maxDepth)
            {
                var message = $"JSON depth ({actualDepth}) exceeds maximum allowed depth ({maxDepth})";

                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(message);
                }
                else
                {
                    throw new JsonReaderException(message);
                }
            }
        }

        private static int CalculateDepth(JToken token, int currentDepth = 1)
        {
            if (token is JObject obj)
            {
                if (!obj.HasValues) return currentDepth;

                return obj.Properties()
                    .Select(p => CalculateDepth(p.Value, currentDepth + 1))
                    .DefaultIfEmpty(currentDepth)
                    .Max();
            }
            else if (token is JArray arr)
            {
                if (!arr.HasValues) return currentDepth;

                return arr.Children()
                    .Select(child => CalculateDepth(child, currentDepth + 1))
                    .DefaultIfEmpty(currentDepth)
                    .Max();
            }

            return currentDepth;
        }

        private JsonTextReader CreateJsonTextReader(StreamReader streamReader)
        {
            return new JsonTextReader(streamReader)
            {
                SupportMultipleContent = true,
                DateParseHandling = DateParseHandling.None,
                MaxDepth = Config.MaxDepth
            };
        }

        private JsonLoadSettings CreateJsonLoadSettings()
        {
            return new JsonLoadSettings
            {
                CommentHandling = Config.NoMetadata ? CommentHandling.Ignore : CommentHandling.Load,
                DuplicatePropertyNameHandling = Config.StrictMode
                    ? DuplicatePropertyNameHandling.Error
                    : DuplicatePropertyNameHandling.Replace,
                LineInfoHandling = Config.StrictMode ? LineInfoHandling.Load : LineInfoHandling.Ignore
            };
        }

        private string PreprocessJson(string json)
        {
            if (Config.JsonAllowSingleQuotes)
            {
                json = ConvertSingleQuotesToDouble(json);
            }

            if (Config.JsonAllowTrailingCommas)
            {
                json = RemoveTrailingCommas(json);
            }

            if (!Config.JsonStrictPropertyNames)
            {
                json = AddQuotesToPropertyNames(json);
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
            => Regex.Replace(json, @",(\s*[\]}])", "$1");

        private static string AddQuotesToPropertyNames(string json)
            => Regex.Replace(json, @"(\{|\,)\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*:", "$1\"$2\":");

        private JObject HandleParsingError(JsonReaderException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"JSON parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

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