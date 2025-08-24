using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace FormatConverter.Yaml
{
    public class YamlInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            try
            {
                var deserializerBuilder = new DeserializerBuilder();

                if (Config.NoMetadata)
                {
                    deserializerBuilder.IgnoreUnmatchedProperties();
                }

                var deserializer = deserializerBuilder.Build();
                var yamlObject = deserializer.Deserialize(new StringReader(input));

                if (yamlObject == null)
                {
                    throw new FormatException("YAML document is empty or null");
                }

                var result = ConvertObjectToJToken(yamlObject);

                if (Config.SortKeys && result is JObject)
                {
                    result = SortKeysRecursively(result);
                }

                return result;
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: YAML parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"Invalid YAML: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: YAML parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"YAML parsing failed: {ex.Message}", ex);
            }
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
                short s => new JValue(s),
                int i => new JValue(i),
                long l => new JValue(l),
                float f => new JValue(f),
                double d => new JValue(d),
                decimal m => new JValue(m),
                DateTime dt => new JValue(dt),
                _ => new JValue(obj.ToString())
            };
        }

        private JObject ConvertDictionaryToJObject(Dictionary<object, object> dict)
        {
            var result = new JObject();

            foreach (var kvp in dict)
            {
                var key = kvp.Key?.ToString() ?? "null";
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
    }
}