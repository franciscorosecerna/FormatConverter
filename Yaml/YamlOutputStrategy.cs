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
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = PreprocessToken(data);
            var result = SerializeToken(processed);

            if (Config.StrictMode)
            {
                ValidateYaml(result);
            }

            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            var serializer = CreateYamlSerializer();
            var chunkSize = GetChunkSize();

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);

            var buffer = new List<JToken>();
            bool isFirstDocument = true;

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    WriteChunkToStream(buffer, serializer, writer, ref isFirstDocument, cancellationToken);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStream(buffer, serializer, writer, ref isFirstDocument, cancellationToken);
            }

            writer.Flush();
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
        }

        private void WriteChunkToStream(List<JToken> items, ISerializer serializer, StreamWriter writer, ref bool isFirstDocument, CancellationToken ct)
        {
            if (items.Count == 0) return;

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
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"YAML serialization error ignored: {ex.Message}");
                    var errorYaml = CreateErrorYaml(ex.Message, ex.GetType().Name, items[i]);
                    writer.WriteLine(errorYaml);
                }
            }

            writer.Flush();
        }

        private string SerializeToken(JToken token)
        {
            try
            {
                var serializer = CreateYamlSerializer();
                var obj = ConvertJTokenToObject(token);
                var yamlContent = serializer.Serialize(obj);

                yamlContent = ApplyYamlFormatting(yamlContent);

                return yamlContent;
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"YAML serialization error ignored: {ex.Message}");
                return CreateErrorYaml(ex.Message, ex.GetType().Name, token);
            }
        }

        private void ValidateYaml(string yaml)
        {
            try
            {
                var deserializer = new DeserializerBuilder().Build();
                deserializer.Deserialize(new StringReader(yaml));
            }
            catch when (!Config.StrictMode) { }
        }

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;

        private ISerializer CreateYamlSerializer()
        {
            var serializerBuilder = new SerializerBuilder();

            if (Config.YamlFlowStyle)
            {
                serializerBuilder.JsonCompatible();
            }

            if (Config.YamlCanonical)
            {
                serializerBuilder.WithNamingConvention(CamelCaseNamingConvention.Instance);
            }

            if (Config.YamlQuoteStrings)
            {
                serializerBuilder.WithQuotingNecessaryStrings();
            }

            if (Config.PrettyPrint && !Config.Minify)
            {
                serializerBuilder.WithIndentedSequences();
            }

            serializerBuilder.ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull);

            if (Config.NoMetadata)
            {
                serializerBuilder.DisableAliases();
            }

            return serializerBuilder.Build();
        }

        private string ApplyYamlFormatting(string yamlContent)
        {
            var sb = new StringBuilder();

            if (Config.YamlExplicitStart)
            {
                sb.AppendLine("---");
            }

            if (Config.Minify)
            {
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

        private object? ConvertJTokenToObject(JToken token, int currentDepth = 0)
        {
            if (Config.MaxDepth.HasValue && currentDepth > Config.MaxDepth.Value)
            {
                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"Maximum depth {Config.MaxDepth.Value} exceeded during serialization");
                    return $"[Max depth exceeded at level {currentDepth}]";
                }
                throw new FormatException($"Maximum depth of {Config.MaxDepth.Value} exceeded during serialization");
            }

            switch (token.Type)
            {
                case JTokenType.Object:
                    var dict = new Dictionary<string, object?>();
                    var jObject = (JObject)token;

                    var properties = Config.SortKeys
                        ? jObject.Properties().OrderBy(p => p.Name)
                        : jObject.Properties();

                    foreach (var property in properties)
                    {
                        dict[property.Name] = ConvertJTokenToObject(property.Value, currentDepth + 1);
                    }
                    return dict;

                case JTokenType.Array:
                    var list = new List<object?>();
                    foreach (var item in token.Children())
                    {
                        list.Add(ConvertJTokenToObject(item, currentDepth + 1));
                    }
                    return list;

                case JTokenType.String:
                    return token.Value<string>() ?? string.Empty;

                case JTokenType.Integer:
                    return token.Value<long>();

                case JTokenType.Float:
                    if (!string.IsNullOrEmpty(Config.NumberFormat))
                        return FormatNumber(token.Value<double>());
                    return token.Value<double>();

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Null:
                    return null;

                case JTokenType.Date:
                    if (!string.IsNullOrEmpty(Config.DateFormat))
                        return FormatDateTime(token.Value<DateTime>());
                    return token.Value<DateTime>();
                default:
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