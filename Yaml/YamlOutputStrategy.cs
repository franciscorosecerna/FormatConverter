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
            if (Config.UseStreaming)
            {
                return string.Join("", SerializeStream([data]));
            }

            return SerializeRegular(data);
        }

        public override IEnumerable<string> SerializeStream(IEnumerable<JToken> data)
        {
            if (!Config.UseStreaming)
            {
                yield return Serialize(new JArray(data));
                yield break;
            }

            foreach (var token in data)
            {
                foreach (var chunk in StreamToken(token))
                {
                    yield return chunk;
                }
            }
        }

        private IEnumerable<string> StreamToken(JToken token)
        {
            var processed = ProcessDataBeforeSerialization(token);

            return processed.Type switch
            {
                JTokenType.Array => StreamArray((JArray)processed),
                JTokenType.Object => StreamObject((JObject)processed),
                _ => StreamSingle(processed)
            };
        }

        private IEnumerable<string> StreamArray(JArray array)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            if (Config.YamlExplicitStart)
            {
                yield return "---\n";
            }

            bool first = true;
            var buffer = new List<JToken>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var itemCount = array.Count;

            foreach (var item in array)
            {
                var obj = ConvertJTokenToObject(item);
                var serializedItem = SerializeYamlItem(obj);
                var itemSizeInBytes = Encoding.UTF8.GetByteCount(serializedItem);

                if (buffer.Count > 0 && currentBufferSize + itemSizeInBytes > bufferSize)
                {
                    yield return SerializeYamlChunk(buffer, ref first);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (itemCount > 0 && totalProcessed % Math.Max(1, itemCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / itemCount * 100;
                        Console.WriteLine($"Serialization progress: {progress:F1}%");
                    }
                }

                buffer.Add(item);
                currentBufferSize += itemSizeInBytes;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeYamlChunk(buffer, ref first);
            }

            if (Config.YamlExplicitEnd)
            {
                yield return "\n...";
            }
        }

        private IEnumerable<string> StreamObject(JObject obj)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;

            if (Config.YamlExplicitStart)
            {
                yield return "---\n";
            }

            var properties = Config.SortKeys
                ? obj.Properties().OrderBy(p => p.Name)
                : obj.Properties();

            bool first = true;
            var buffer = new List<JProperty>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var itemCount = obj.Count;

            foreach (var property in properties)
            {
                var serializedItem = SerializeYamlProperty(property);
                var itemSizeInBytes = Encoding.UTF8.GetByteCount(serializedItem);

                if (buffer.Count > 0 && currentBufferSize + itemSizeInBytes > bufferSize)
                {
                    yield return SerializeYamlPropertyChunk(buffer, ref first);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (itemCount > 0 && totalProcessed % Math.Max(1, itemCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / itemCount * 100;
                        Console.WriteLine($"Serialization progress: {progress:F1}%");
                    }
                }

                buffer.Add(property);
                currentBufferSize += itemSizeInBytes;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeYamlPropertyChunk(buffer, ref first);
            }

            if (Config.YamlExplicitEnd)
            {
                yield return "\n...";
            }
        }

        private string SerializeYamlChunk(List<JToken> items, ref bool first)
        {
            var sb = new StringBuilder();
            var indent = NeedsPretty() ? new string(' ', Config.IndentSize ?? 2) : "";

            foreach (var item in items)
            {
                try
                {
                    var obj = ConvertJTokenToObject(item);
                    var serialized = SerializeYamlItem(obj);

                    if (NeedsPretty())
                    {
                        sb.Append("- ");
                        var lines = serialized.Split('\n');
                        sb.AppendLine(lines[0]);
                        for (int i = 1; i < lines.Length; i++)
                        {
                            if (!string.IsNullOrWhiteSpace(lines[i]))
                            {
                                sb.Append(indent).AppendLine(lines[i]);
                            }
                        }
                    }
                    else
                    {
                        sb.Append("- ").AppendLine(serialized.Replace("\n", " "));
                    }
                }
                catch (Exception ex)
                {
                    if (Config.IgnoreErrors)
                    {
                        sb.AppendLine(CreateErrorYaml(ex.Message));
                    }
                    else
                    {
                        throw new FormatException($"Error serializing YAML item: {ex.Message}", ex);
                    }
                }
            }

            first = false;
            return sb.ToString();
        }

        private string SerializeYamlPropertyChunk(List<JProperty> properties, ref bool first)
        {
            var sb = new StringBuilder();

            foreach (var property in properties)
            {
                try
                {
                    var serialized = SerializeYamlProperty(property);
                    sb.AppendLine(serialized);
                }
                catch (Exception ex)
                {
                    if (Config.IgnoreErrors)
                    {
                        sb.AppendLine(CreateErrorYaml(ex.Message));
                    }
                    else
                    {
                        throw new FormatException($"Error serializing YAML property: {ex.Message}", ex);
                    }
                }
            }

            first = false;
            return sb.ToString();
        }

        private IEnumerable<string> StreamSingle(JToken token)
        {
            IEnumerable<string> Iterator()
            {
                if (Config.YamlExplicitStart)
                {
                    yield return "---\n";
                }

                var obj = ConvertJTokenToObject(token);
                var serialized = SerializeYamlItem(obj);
                yield return serialized;

                if (Config.YamlExplicitEnd)
                {
                    yield return "\n...";
                }
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return [CreateErrorYaml(ex.Message)];
                }
                else
                {
                    throw new FormatException($"Error serializing single YAML token: {ex.Message}", ex);
                }
            }
        }

        private string SerializeRegular(JToken data)
        {
            try
            {
                var processed = ProcessDataBeforeSerialization(data);
                var obj = ConvertJTokenToObject(processed)
                    ?? throw new FormatException("Failed to convert JSON to object for YAML serialization");

                var serializer = CreateYamlSerializer();
                var yamlContent = serializer.Serialize(obj);

                yamlContent = ApplyYamlFormatting(yamlContent);

                return yamlContent;
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return CreateErrorYaml(ex.Message);
                }
                throw new FormatException($"YAML serialization failed: {ex.Message}", ex);
            }
        }

        private string SerializeYamlItem(object? obj)
        {
            var serializer = CreateYamlSerializer();
            var yamlContent = serializer.Serialize(obj);

            if (Config.Minify)
            {
                yamlContent = MinifyYaml(yamlContent);
            }

            return yamlContent;
        }

        private string SerializeYamlProperty(JProperty property)
        {
            var obj = ConvertJTokenToObject(property.Value);
            var serializer = CreateYamlSerializer();
            var valueYaml = serializer.Serialize(obj);

            if (valueYaml.Contains('\n'))
            {
                return $"{property.Name}:\n{IndentYaml(valueYaml)}";
            }

            return $"{property.Name}: {valueYaml}";
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys)
                data = SortKeysRecursively(data);

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
                return new JArray(data);

            return data;
        }

        private bool NeedsPretty() => !Config.Minify && Config.PrettyPrint;

        private JToken SortKeysRecursively(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Object => SortJObject((JObject)token),
                JTokenType.Array => new JArray(((JArray)token).Select(SortKeysRecursively)),
                _ => token
            };
        }

        private JObject SortJObject(JObject obj)
        {
            var sorted = new JObject();
            foreach (var property in obj.Properties().OrderBy(p => p.Name))
            {
                sorted[property.Name] = SortKeysRecursively(property.Value);
            }
            return sorted;
        }

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
            if (Config.YamlExplicitStart && !yamlContent.StartsWith("---"))
            {
                yamlContent = "---\n" + yamlContent;
            }

            if (Config.YamlExplicitEnd && !yamlContent.EndsWith("..."))
            {
                yamlContent = yamlContent.TrimEnd() + "\n...";
            }

            if (Config.Minify)
            {
                yamlContent = MinifyYaml(yamlContent);
            }

            return yamlContent;
        }

        private object? ConvertJTokenToObject(JToken token)
        {
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
                    return FormatStringValue(token.Value<string>());

                case JTokenType.Integer:
                    return token.Value<long>();

                case JTokenType.Float:
                    return FormatNumberValue(token.Value<double>());

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Null:
                    return null;

                case JTokenType.Date:
                    return FormatDateTimeValue(token.Value<DateTime>());

                default:
                    return token.ToString();
            }
        }

        private static string FormatStringValue(string? value) => value ?? string.Empty;

        private object FormatNumberValue(double number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{(long)number:X}",
                    "scientific" => number.ToString("E"),
                    _ => number
                };
            }
            return number;
        }

        private string FormatDateTimeValue(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }
            return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        }

        private static string MinifyYaml(string yaml)
        {
            var lines = yaml.Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim());

            return string.Join("\n", lines);
        }

        private static string IndentYaml(string yaml)
        {
            var lines = yaml.Split('\n');
            var indent = "  ";
            return string.Join("\n", lines.Select(line =>
                string.IsNullOrWhiteSpace(line) ? line : indent + line));
        }

        private string CreateErrorYaml(string errorMessage)
        {
            var errorDict = new Dictionary<string, object>
            {
                ["error"] = errorMessage,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            var serializer = CreateYamlSerializer();
            return serializer.Serialize(errorDict);
        }
    }
}