using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;
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

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

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
                streamReader = new StreamReader(fileStream, Config.Encoding, true);

                var fileSize = fileStream.Length;
                var showProgress = fileSize > 10_485_760; // Show progress for files > 10MB
                var documentsProcessed = 0;

                var parser = new Parser(streamReader);
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

        private JToken? ReadDocument(Parser parser, IDeserializer deserializer, string source)
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

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

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
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["location"] = errorLocation
                    };
                }

                throw new FormatException($"Invalid YAML in {errorLocation}: {ex.Message}", ex);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Console.Error.WriteLine($"Warning: Unexpected error ignored in {source}: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["source"] = source
                };
            }
        }

        private IDeserializer CreateDeserializer()
        {
            var deserializerBuilder = new DeserializerBuilder();

            if (Config.NoMetadata)
            {
                deserializerBuilder.IgnoreUnmatchedProperties();
            }

            if (Config.StrictMode)
            {
                deserializerBuilder.WithDuplicateKeyChecking();
            }

            return deserializerBuilder.Build();
        }

        private JToken ParseYamlDocument(string input, IDeserializer deserializer)
        {
            var yamlObject = deserializer.Deserialize(new StringReader(input))
                ?? throw new FormatException("YAML document is empty or null");

            return ConvertObjectToJToken(yamlObject);
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.Error.WriteLine($"Warning: YAML parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000
                        ? string.Concat(input.AsSpan(0, 1000), "...")
                        : input
                };
            }

            throw new FormatException($"Invalid YAML: {ex.Message}", ex);
        }

        private JToken ConvertObjectToJToken(object obj)
        {
            if (obj == null) return JValue.CreateNull();

            return obj switch
            {
                Dictionary<object, object> dict => ConvertDictionaryToJObject(dict),
                List<object> list => ConvertListToJArray(list),
                Array array => ConvertArrayToJArray(array),
                string str => new JValue(str),
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

        private JObject ConvertDictionaryToJObject(Dictionary<object, object> dict)
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

                result[key] = ConvertObjectToJToken(kvp.Value);
            }

            return result;
        }

        private JArray ConvertListToJArray(List<object> list)
        {
            var result = new JArray();

            foreach (var item in list)
            {
                result.Add(ConvertObjectToJToken(item));
            }

            return result;
        }

        private JArray ConvertArrayToJArray(Array array)
        {
            var result = new JArray();

            foreach (var item in array)
            {
                result.Add(ConvertObjectToJToken(item));
            }

            return result;
        }
    }
}