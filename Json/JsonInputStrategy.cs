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

            var settings = CreateJsonLoadSettings();

            try
            {
                var token = JToken.Parse(json, settings);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                return token;
            }
            catch (JsonReaderException ex)
            {
                return HandleParsingError(ex, json);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken)
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
                jsonReader = new JsonTextReader(streamReader)
                {
                    SupportMultipleContent = true,
                    DateParseHandling = DateParseHandling.None
                };

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

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

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