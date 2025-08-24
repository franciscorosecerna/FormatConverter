using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FormatConverter.Json
{
    public class JsonInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            var settings = new JsonLoadSettings
            {
                CommentHandling = Config.NoMetadata ? CommentHandling.Ignore : CommentHandling.Load,
                DuplicatePropertyNameHandling = Config.StrictMode ? DuplicatePropertyNameHandling.Error 
                : DuplicatePropertyNameHandling.Replace
            };

            try
            {
                var token = JToken.Parse(input, settings);

                if (Config.SortKeys)
                {
                    token = SortKeysRecursively(token);
                }

                return token;
            }
            catch (JsonReaderException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: JSON parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"Invalid JSON: {ex.Message}", ex);
            }
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
