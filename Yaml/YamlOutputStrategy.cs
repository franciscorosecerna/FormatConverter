using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FormatConverter.Yaml
{
    public class YamlOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            ArgumentNullException.ThrowIfNull(data);
            Logger.WriteDebug("Starting YAML serialization");

            var processed = PreprocessToken(data);
            var result = SerializeToken(processed);

            if (Config.StrictMode)
            {
                Logger.WriteDebug("Validating YAML in strict mode");
                ValidateYaml(result);
            }

            Logger.WriteInfo("YAML serialization completed successfully");
            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(output);

            Logger.WriteInfo("Starting YAML stream serialization");
            var serializer = CreateYamlSerializer();
            var chunkSize = GetChunkSize();
            Logger.WriteDebug($"Using chunk size: {chunkSize}");

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);

            var buffer = new List<JToken>();
            bool isFirstDocument = true;
            int totalProcessed = 0;

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    Logger.WriteTrace($"Writing chunk of {buffer.Count} items to stream");
                    WriteChunkToStream(buffer, serializer, writer, ref isFirstDocument, cancellationToken);
                    totalProcessed += buffer.Count;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.WriteTrace($"Writing final chunk of {buffer.Count} items to stream");
                WriteChunkToStream(buffer, serializer, writer, ref isFirstDocument, cancellationToken);
                totalProcessed += buffer.Count;
            }

            writer.Flush();
            Logger.WriteInfo($"YAML stream serialization completed. Total items processed: {totalProcessed}");
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            Logger.WriteInfo($"Serializing YAML stream to file: {outputPath}");
            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
            Logger.WriteInfo($"YAML file created successfully: {outputPath}");
        }

        private void WriteChunkToStream(List<JToken> items, ISerializer serializer, StreamWriter writer, ref bool isFirstDocument, CancellationToken ct)
        {
            if (items.Count == 0) return;

            Logger.WriteTrace($"Processing chunk with {items.Count} items");

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (!isFirstDocument || Config.YamlExplicitStart)
                    {
                        writer.WriteLine("---");
                    }
                    isFirstDocument = false;

                    var obj = ConvertJTokenToObject(items[i]);
                    var yamlContent = serializer.Serialize(obj);

                    writer.Write(yamlContent.TrimEnd());

                    if (Config.YamlExplicitEnd)
                    {
                        writer.WriteLine();
                        writer.WriteLine("...");
                    }
                    else
                    {
                        writer.WriteLine();
                    }

                    Logger.WriteTrace($"Item {i + 1}/{items.Count} serialized successfully");
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"YAML serialization error ignored: {ex.Message}");
                    var errorYaml = CreateErrorYaml(ex.Message, ex.GetType().Name, items[i]);
                    writer.WriteLine(errorYaml);
                }
                catch (Exception ex)
                {
                    Logger.WriteError($"Critical YAML serialization error at item {i + 1}: {ex.Message}");
                    throw;
                }
            }

            writer.Flush();
        }

        private string SerializeToken(JToken token)
        {
            try
            {
                Logger.WriteTrace($"Serializing token of type: {token.Type}");
                var serializer = CreateYamlSerializer();
                var obj = ConvertJTokenToObject(token);
                var yamlContent = serializer.Serialize(obj);

                yamlContent = ApplyYamlFormatting(yamlContent);

                Logger.WriteTrace("Token serialized successfully");
                return yamlContent;
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"YAML serialization error ignored: {ex.Message}");
                return CreateErrorYaml(ex.Message, ex.GetType().Name, token);
            }
            catch (Exception ex)
            {
                Logger.WriteError($"Critical error serializing token: {ex.Message}");
                throw;
            }
        }

        private void ValidateYaml(string yaml)
        {
            try
            {
                var deserializer = new DeserializerBuilder().Build();
                deserializer.Deserialize(new StringReader(yaml));
                Logger.WriteDebug("YAML validation passed");
            }
            catch (Exception ex) when (!Config.StrictMode)
            {
                Logger.WriteWarning($"YAML validation failed (ignored): {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.WriteError($"YAML validation failed: {ex.Message}");
                throw;
            }
        }

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;

        private ISerializer CreateYamlSerializer()
        {
            Logger.WriteTrace("Creating YAML serializer with configured options");
            var serializerBuilder = new SerializerBuilder();

            if (Config.YamlFlowStyle)
            {
                Logger.WriteTrace("Applying JSON-compatible flow style");
                serializerBuilder.JsonCompatible();
            }

            if (Config.YamlCanonical)
            {
                Logger.WriteTrace("Applying canonical naming convention");
                serializerBuilder.WithNamingConvention(CamelCaseNamingConvention.Instance);
            }

            if (Config.YamlQuoteStrings)
            {
                Logger.WriteTrace("Enabling string quoting");
                serializerBuilder.WithQuotingNecessaryStrings();
            }

            if (Config.PrettyPrint && !Config.Minify)
            {
                Logger.WriteTrace("Enabling indented sequences");
                serializerBuilder.WithIndentedSequences();
            }

            serializerBuilder.ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull);

            if (Config.NoMetadata)
            {
                Logger.WriteTrace("Disabling YAML aliases");
                serializerBuilder.DisableAliases();
            }

            return serializerBuilder.Build();
        }

        private string ApplyYamlFormatting(string yamlContent)
        {
            Logger.WriteTrace("Applying YAML formatting");
            var sb = new StringBuilder();

            if (Config.YamlExplicitStart)
            {
                sb.AppendLine("---");
            }

            if (Config.Minify)
            {
                Logger.WriteTrace("Minifying YAML output");
                yamlContent = MinifyYaml(yamlContent);
            }

            sb.Append(yamlContent.TrimEnd());

            if (Config.YamlExplicitEnd)
            {
                sb.AppendLine();
                sb.Append("...");
            }

            return sb.ToString();
        }

        private object? ConvertJTokenToObject(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    var dict = new Dictionary<string, object?>();
                    var jObject = (JObject)token;

                    foreach (var property in jObject.Properties())
                    {
                        dict[property.Name] = ConvertJTokenToObject(property.Value);
                    }
                    return dict;

                case JTokenType.Array:
                    var list = new List<object?>();
                    foreach (var item in token.Children())
                    {
                        list.Add(ConvertJTokenToObject(item));
                    }
                    return list;

                case JTokenType.String:
                    return token.Value<string>() ?? string.Empty;

                case JTokenType.Integer:
                    if (Config.YamlPreserveLeadingZeros)
                    {
                        var originalString = ((JValue)token).ToString();

                        if (originalString.StartsWith("0") && originalString.Length > 1)
                        {
                            Logger.WriteTrace("Preserving leading zeros in integer");
                            return originalString;
                        }
                    }
                    return token.Value<long>();

                case JTokenType.Float:
                    return token.Value<double>();

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Null:
                    return null;

                case JTokenType.Date:
                    return token.Value<DateTime>();

                default:
                    Logger.WriteWarning($"Unexpected JToken type: {token.Type}, converting to string");
                    return token.ToString();
            }
        }

        private static string MinifyYaml(string yaml)
        {
            var lines = yaml.Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim());

            return string.Join("\n", lines);
        }

        private string CreateErrorYaml(string errorMessage, string errorType, JToken originalToken)
        {
            Logger.WriteDebug($"Creating error YAML for {errorType}");
            var errorDict = new Dictionary<string, object>
            {
                ["error"] = errorMessage,
                ["error_type"] = errorType,
                ["original_type"] = originalToken.Type.ToString(),
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            var serializer = CreateYamlSerializer();
            var errorYaml = serializer.Serialize(errorDict);

            return Config.Minify
                ? MinifyYaml(errorYaml)
                : errorYaml;
        }
    }
}