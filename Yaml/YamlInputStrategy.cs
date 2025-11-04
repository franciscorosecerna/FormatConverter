using FormatConverter.Interfaces;
using FormatConverter.Yaml.YamlParser;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace FormatConverter.Yaml
{
    public class YamlInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                Logger.WriteWarning("YAML input is null or empty");
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("YAML input cannot be null or empty");
            }

            Logger.WriteDebug("Starting YAML parsing");
            var deserializer = CreateDeserializer();

            try
            {
                var token = ParseYamlDocument(input, deserializer);
                Logger.WriteInfo("YAML parsing completed successfully");
                return token;
            }
            catch (YamlException ex)
            {
                Logger.WriteError($"YAML parsing failed: {ex.Message}");
                return HandleParsingError(ex, input);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                Logger.WriteError($"Unexpected error during YAML parsing: {ex.Message}");
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Logger.WriteError("Path is null or empty");
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));
            }

            if (!File.Exists(path))
            {
                Logger.WriteError($"Input file not found: {path}");
                throw new FileNotFoundException("Input file not found.", path);
            }

            Logger.WriteInfo($"Starting YAML stream parsing from: {path}");
            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken = default)
        {
            FileStream? fileStream = null;
            StreamReader? streamReader = null;

            try
            {
                fileStream = File.OpenRead(path);
                streamReader = new StreamReader(fileStream, Config.Encoding, detectEncodingFromByteOrderMarks: true);

                var fileSize = fileStream.Length;
                Logger.WriteDebug($"File size: {fileSize} bytes");
                var showProgress = fileSize > 10_485_760;
                var documentsProcessed = 0;

                var parser = CreateParser(streamReader);
                var deserializer = CreateDeserializer();

                parser.Consume<StreamStart>();
                Logger.WriteTrace("YAML stream started");

                while (parser.Accept<DocumentStart>(out _))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var token = ReadDocument(parser, deserializer, path);

                    if (token != null)
                    {
                        documentsProcessed++;
                        Logger.WriteTrace($"Document {documentsProcessed} parsed successfully");

                        if (showProgress && documentsProcessed % 100 == 0)
                        {
                            var progress = (double)fileStream.Position / fileSize * 100;
                            Logger.WriteInfo($"Processing: {progress:F1}% ({documentsProcessed} documents)");
                        }

                        yield return token;
                    }
                }

                parser.Consume<StreamEnd>();
                Logger.WriteTrace("YAML stream ended");

                if (showProgress)
                {
                    Logger.WriteInfo($"Completed: {documentsProcessed} documents processed");
                }
                else
                {
                    Logger.WriteInfo($"YAML stream parsing completed: {documentsProcessed} documents processed");
                }
            }
            finally
            {
                streamReader?.Dispose();
                fileStream?.Dispose();
                Logger.WriteDebug("Stream resources disposed");
            }
        }

        private JToken? ReadDocument(IParser parser, IDeserializer deserializer, string source)
        {
            try
            {
                parser.Consume<DocumentStart>();

                if (parser.Accept<DocumentEnd>(out _))
                {
                    parser.Consume<DocumentEnd>();
                    Logger.WriteTrace("Empty YAML document skipped");
                    return null;
                }

                var yamlObject = deserializer.Deserialize(parser);

                parser.Consume<DocumentEnd>();

                if (yamlObject == null)
                {
                    Logger.WriteTrace("Null YAML document skipped");
                    return null;
                }

                var token = ConvertObjectToJToken(yamlObject);

                return token;
            }
            catch (YamlException ex)
            {
                var errorLocation = $"{source}";
                if (ex.Start.Line > 0)
                    errorLocation += $" at line {ex.Start.Line}, column {ex.Start.Column}";

                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"YAML error ignored in {errorLocation}: {ex.Message}");
                    return CreateErrorToken(ex, errorLocation, (int)ex.Start.Line, (int)ex.Start.Column);
                }

                Logger.WriteError($"Invalid YAML in {errorLocation}: {ex.Message}");
                throw new FormatException($"Invalid YAML in {errorLocation}: {ex.Message}", ex);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"Unexpected error ignored in {source}: {ex.Message}");
                return CreateErrorToken(ex, source, 0, 0);
            }
            catch (Exception ex)
            {
                Logger.WriteError($"Critical error reading document from {source}: {ex.Message}");
                throw;
            }
        }

        private IParser CreateParser(StreamReader streamReader)
        {
            Logger.WriteTrace("Creating YAML parser");
            var baseParser = new YamlDotNet.Core.Parser(streamReader);

            if (Config.MaxDepth.HasValue)
            {
                Logger.WriteDebug($"Max depth validation enabled: {Config.MaxDepth.Value}");
                return new MaxDepthValidatingParser(
                    baseParser,
                    Config.MaxDepth.Value,
                    Config.IgnoreErrors);
            }

            return baseParser;
        }

        private IDeserializer CreateDeserializer()
        {
            Logger.WriteTrace("Creating YAML deserializer");
            var deserializerBuilder = new DeserializerBuilder();

            deserializerBuilder.WithAttemptingUnquotedStringTypeDeserialization();

            if (Config.NoMetadata)
            {
                Logger.WriteTrace("Ignoring unmatched properties");
                deserializerBuilder.IgnoreUnmatchedProperties();
            }

            if (Config.StrictMode || Config.YamlAllowDuplicateKeys == false)
            {
                Logger.WriteTrace("Duplicate key checking enabled");
                deserializerBuilder.WithDuplicateKeyChecking();
            }

            return deserializerBuilder.Build();
        }

        private JToken ParseYamlDocument(string input, IDeserializer deserializer)
        {
            Logger.WriteTrace($"Parsing YAML document of length: {input.Length}");
            using var reader = new StringReader(input);
            var parser = new YamlDotNet.Core.Parser(reader);

            var yamlObject = deserializer.Deserialize(parser)
                ?? throw new FormatException("YAML document is empty or null");

            Logger.WriteTrace("YAML document deserialized, converting to JToken");
            return ConvertObjectToJToken(yamlObject);
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"YAML parsing error ignored: {ex.Message}");

                var snippet = input.Length > 1000
                    ? string.Concat(input.AsSpan(0, 1000), "...")
                    : input;

                return new JObject
                {
                    ["error"] = ex.Message,
                    ["error_type"] = ex.GetType().Name,
                    ["raw_snippet"] = snippet,
                    ["timestamp"] = DateTime.UtcNow.ToString("o")
                };
            }

            Logger.WriteError($"YAML parsing error (not ignored): {ex.Message}");
            throw new FormatException($"Invalid YAML: {ex.Message}", ex);
        }

        private static JObject CreateErrorToken(Exception ex, string location, int line, int column)
        {
            var errorObj = new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["location"] = location,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            if (line > 0)
            {
                errorObj["line"] = line;
                errorObj["column"] = column;
            }

            return errorObj;
        }

        private JToken ConvertObjectToJToken(object obj, int currentDepth = 0)
        {
            if (Config.MaxDepth.HasValue && currentDepth > Config.MaxDepth.Value)
            {
                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"Maximum depth {Config.MaxDepth.Value} exceeded at level {currentDepth}");
                    return new JValue($"[Max depth exceeded at level {currentDepth}]");
                }
                Logger.WriteError($"Maximum depth {Config.MaxDepth.Value} exceeded");
                throw new FormatException($"Maximum depth of {Config.MaxDepth.Value} exceeded");
            }

            if (obj == null)
            {
                return JValue.CreateNull();
            }

            if (obj is Dictionary<object, object> dict)
            {
                Logger.WriteTrace($"Converting dictionary with {dict.Count} entries at depth {currentDepth}");
                return ConvertDictionaryToJObject(dict, currentDepth);
            }

            if (obj is List<object> list)
            {
                Logger.WriteTrace($"Converting list with {list.Count} items at depth {currentDepth}");
                return ConvertListToJArray(list, currentDepth);
            }

            if (obj is Array array)
            {
                Logger.WriteTrace($"Converting array with {array.Length} items at depth {currentDepth}");
                return ConvertArrayToJArray(array, currentDepth);
            }

            if (obj is string str)
            {
                return ParseStringValue(str);
            }

            return obj switch
            {
                bool b => new JValue(b),
                byte b => new JValue(b),
                sbyte sb => new JValue(sb),
                short s => new JValue(s),
                ushort us => new JValue(us),
                int i => new JValue(i),
                uint ui => new JValue(ui),
                long l => new JValue(l),
                ulong ul => new JValue(ul),
                float f => new JValue(f),
                double d => new JValue(d),
                decimal m => new JValue(m),
                DateTime dt => new JValue(dt),
                Guid guid => new JValue(guid),
                TimeSpan ts => new JValue(ts),
                Uri uri => new JValue(uri),
                _ => new JValue(obj.ToString())
            };
        }

        private JValue ParseStringValue(string str)
        {
            if (string.IsNullOrEmpty(str))
                return new JValue(str);

            if (str.Equals("true", StringComparison.OrdinalIgnoreCase))
                return new JValue(true);
            if (str.Equals("false", StringComparison.OrdinalIgnoreCase))
                return new JValue(false);

            if (str.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                str.Equals("~", StringComparison.Ordinal))
                return JValue.CreateNull();

            if (Config.YamlPreserveLeadingZeros && str.Length > 1 && str[0] == '0' && char.IsDigit(str[1]))
            {
                Logger.WriteTrace("Preserving leading zeros in string value");
                return new JValue(str);
            }

            if (long.TryParse(str, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long longValue))
            {
                if (longValue >= int.MinValue && longValue <= int.MaxValue)
                    return new JValue((int)longValue);
                return new JValue(longValue);
            }

            if (decimal.TryParse(str, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out decimal decimalValue))
            {
                return new JValue(decimalValue);
            }

            if (double.TryParse(str, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double doubleValue))
            {
                return new JValue(doubleValue);
            }

            if (DateTime.TryParse(str, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dateValue))
            {
                return new JValue(dateValue);
            }

            return new JValue(str);
        }

        private JObject ConvertDictionaryToJObject(Dictionary<object, object> dict, int currentDepth)
        {
            var result = new JObject();
            var nullKeyCounter = 0;

            foreach (var kvp in dict)
            {
                var key = kvp.Key?.ToString();

                if (key == null)
                {
                    if (Config.StrictMode)
                    {
                        Logger.WriteError("Null key encountered in strict mode");
                        throw new FormatException("Null keys are not allowed in strict mode");
                    }

                    key = nullKeyCounter == 0 ? "null" : $"null_{nullKeyCounter}";
                    Logger.WriteWarning($"Null key replaced with '{key}'");
                    nullKeyCounter++;
                }

                result[key] = ConvertObjectToJToken(kvp.Value, currentDepth + 1);
            }

            return result;
        }

        private JArray ConvertListToJArray(List<object> list, int currentDepth)
        {
            var result = new JArray();

            foreach (var item in list)
            {
                result.Add(ConvertObjectToJToken(item, currentDepth + 1));
            }

            return result;
        }

        private JArray ConvertArrayToJArray(Array array, int currentDepth)
        {
            var result = new JArray();

            foreach (var item in array)
            {
                result.Add(ConvertObjectToJToken(item, currentDepth + 1));
            }

            return result;
        }
    }
}