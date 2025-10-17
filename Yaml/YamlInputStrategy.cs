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
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("YAML input cannot be null or empty");
            }

            var deserializer = CreateDeserializer();

            try
            {
                var token = ParseYamlDocument(input, deserializer);
                return token;
            }
            catch (YamlException ex)
            {
                return HandleParsingError(ex, input);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                return HandleParsingError(ex, input);
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

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken = default)
        {
            FileStream? fileStream = null;
            StreamReader? streamReader = null;

            try
            {
                fileStream = File.OpenRead(path);
                streamReader = new StreamReader(fileStream, Config.Encoding, detectEncodingFromByteOrderMarks: true);

                var fileSize = fileStream.Length;
                var showProgress = fileSize > 10_485_760;
                var documentsProcessed = 0;

                var parser = CreateParser(streamReader);
                var deserializer = CreateDeserializer();

                parser.Consume<StreamStart>();

                while (parser.Accept<DocumentStart>(out _))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var token = ReadDocument(parser, deserializer, path);

                    if (token != null)
                    {
                        documentsProcessed++;

                        if (showProgress && documentsProcessed % 100 == 0)
                        {
                            var progress = (double)fileStream.Position / fileSize * 100;
                            Console.Error.Write($"\rProcessing: {progress:F1}% ({documentsProcessed} documents)");
                        }

                        yield return token;
                    }
                }

                parser.Consume<StreamEnd>();

                if (showProgress)
                {
                    Console.Error.WriteLine($"\rCompleted: {documentsProcessed} documents processed");
                }
            }
            finally
            {
                streamReader?.Dispose();
                fileStream?.Dispose();
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
                    return null;
                }

                var yamlObject = deserializer.Deserialize(parser);

                parser.Consume<DocumentEnd>();

                if (yamlObject == null)
                    return null;

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
                    Console.Error.WriteLine($"Warning: YAML error ignored in {errorLocation}: {ex.Message}");
                    return CreateErrorToken(ex, errorLocation, (int)ex.Start.Line, (int)ex.Start.Column);
                }

                throw new FormatException($"Invalid YAML in {errorLocation}: {ex.Message}", ex);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Console.Error.WriteLine($"Warning: Unexpected error ignored in {source}: {ex.Message}");
                return CreateErrorToken(ex, source, 0, 0);
            }
        }

        private IParser CreateParser(StreamReader streamReader)
        {
            var baseParser = new YamlDotNet.Core.Parser(streamReader);

            if (Config.MaxDepth.HasValue)
            {
                return new MaxDepthValidatingParser(
                    baseParser,
                    Config.MaxDepth.Value,
                    Config.IgnoreErrors);
            }

            return baseParser;
        }

        private IDeserializer CreateDeserializer()
        {
            var deserializerBuilder = new DeserializerBuilder();

            deserializerBuilder.WithAttemptingUnquotedStringTypeDeserialization();

            if (Config.NoMetadata)
            {
                deserializerBuilder.IgnoreUnmatchedProperties();
            }

            if (Config.StrictMode || Config.YamlAllowDuplicateKeys == false)
            {
                deserializerBuilder.WithDuplicateKeyChecking();
            }

            return deserializerBuilder.Build();
        }

        private JToken ParseYamlDocument(string input, IDeserializer deserializer)
        {
            using var reader = new StringReader(input);
            var parser = new YamlDotNet.Core.Parser(reader);

            var yamlObject = deserializer.Deserialize(parser)
                ?? throw new FormatException("YAML document is empty or null");

            return ConvertObjectToJToken(yamlObject);
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.Error.WriteLine($"Warning: YAML parsing error ignored: {ex.Message}");

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
                    Console.Error.WriteLine($"Warning: Maximum depth {Config.MaxDepth.Value} exceeded");
                    return new JValue($"[Max depth exceeded at level {currentDepth}]");
                }
                throw new FormatException($"Maximum depth of {Config.MaxDepth.Value} exceeded");
            }

            if (obj == null)
            {
                return JValue.CreateNull();
            }

            if (obj is Dictionary<object, object> dict)
                return ConvertDictionaryToJObject(dict, currentDepth);

            if (obj is List<object> list)
                return ConvertListToJArray(list, currentDepth);

            if (obj is Array array)
                return ConvertArrayToJArray(array, currentDepth);

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

        private static JValue ParseStringValue(string str)
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

            if (long.TryParse(str, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long longValue))
            {
                if (longValue >= int.MinValue && longValue <= int.MaxValue)
                    return new JValue((int)longValue);
                return new JValue(longValue);
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
                        throw new FormatException("Null keys are not allowed in strict mode");

                    key = nullKeyCounter == 0 ? "null" : $"null_{nullKeyCounter}";
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