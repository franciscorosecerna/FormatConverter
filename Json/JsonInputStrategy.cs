using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

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
                    : throw new ArgumentException("JSON input cannot be null or empty");
            }

            json = PreprocessJson(json);

            var settings = CreateJsonLoadSettings();

            try
            {
                var token = JToken.Parse(json, settings);

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
            FileStream? fileStream = null;
            StreamReader? streamReader = null;
            JsonTextReader? jsonReader = null;

            try
            {
                fileStream = File.OpenRead(path);
                streamReader = new StreamReader(fileStream, Config.Encoding, true);
                jsonReader = CreateJsonTextReader(streamReader);

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

                            if (showProgress)
                            {
                                if (showProgress && tokensProcessed % 100 == 0)
                                {
                                    var progress = (double)fileStream.Position / fileSize * 100;
                                    Console.Error.Write($"\rProcessing: {progress:F1}% ({tokensProcessed} elements)");
                                }
                            }

                            yield return token;
                        }
                    }
                }

                if (showProgress)
                {
                    Console.Error.WriteLine($"\rCompleted: {tokensProcessed} objects processed");
                }
            }
            finally
            {
                jsonReader?.Close();
                streamReader?.Dispose();
                fileStream?.Dispose();
            }
        }

        private JToken? ReadToken(JsonTextReader jsonReader, JsonLoadSettings settings, string path)
        {
            try
            {
                var token = JToken.ReadFrom(jsonReader, settings);

                return token;
            }
            catch (JsonReaderException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.Error.WriteLine($"Warning: JSON streaming error ignored: {ex.Message}");
                    return HandleParsingError(ex, path);
                }

                throw new FormatException($"Invalid JSON at line {jsonReader.LineNumber}," +
                    $" position {jsonReader.LinePosition}: {ex.Message}", ex);
            }
        }

        private JsonTextReader CreateJsonTextReader(StreamReader streamReader)
        {
            return new JsonTextReader(streamReader)
            {
                SupportMultipleContent = true,
                DateParseHandling = DateParseHandling.None
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
        {
            var pattern = @",(\s*[\]}])";
            return System.Text.RegularExpressions.Regex.Replace(json, pattern, "$1");
        }

        private static string AddQuotesToPropertyNames(string json)
        {
            var pattern = @"(\{|\,)\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*:";
            return System.Text.RegularExpressions.Regex.Replace(json, pattern, "$1\"$2\":");
        }

        private JObject HandleParsingError(JsonReaderException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.Error.WriteLine($"Warning: JSON parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000
                        ? string.Concat(input.AsSpan(0, 1000), "...")
                        : input
                };
            }

            throw new FormatException($"Invalid JSON: {ex.Message}", ex);
        }
    }
}