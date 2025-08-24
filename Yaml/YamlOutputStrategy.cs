using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FormatConverter.Yaml
{
    public class YamlOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            try
            {
                var obj = ConvertJTokenToObject(data) 
                    ?? throw new FormatException("Failed to convert JSON to object for YAML serialization");

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

                var serializer = serializerBuilder.Build();
                var yamlContent = serializer.Serialize(obj);

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
            catch (Exception ex)
            {
                throw new FormatException($"YAML serialization failed: {ex.Message}", ex);
            }
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
                    var stringValue = token.Value<string>();
                    return FormatStringValue(stringValue);

                case JTokenType.Integer:
                    return token.Value<long>();

                case JTokenType.Float:
                    var doubleValue = token.Value<double>();
                    return FormatNumberValue(doubleValue);

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Null:
                    return null;

                case JTokenType.Date:
                    var dateValue = token.Value<DateTime>();
                    return FormatDateTimeValue(dateValue);

                default:
                    return token.ToString();
            }
        }

        private static string FormatStringValue(string? value)
        {
            if (value == null) return string.Empty;
            return value;
        }

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
    }
}
