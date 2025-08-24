using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FormatConverter.Json
{
    public class JsonOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            try
            {
                var processedData = ProcessDataBeforeSerialization(data);

                var settings = CreateJsonSerializerSettings();

                string jsonString;
                using (var stringWriter = new StringWriter())
                using (var jsonWriter = new JsonTextWriter(stringWriter))
                {
                    ConfigureJsonWriter(jsonWriter);

                    var serializer = JsonSerializer.Create(settings);
                    serializer.Serialize(jsonWriter, processedData);

                    jsonString = stringWriter.ToString();
                }

                if (Config.JsonSingleQuotes && !Config.Minify)
                {
                    jsonString = ConvertToSingleQuotes(jsonString);
                }

                return jsonString;
            }
            catch (Exception ex)
            {
                throw new FormatException($"JSON serialization failed: {ex.Message}", ex);
            }
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys)
            {
                data = SortKeysRecursively(data);
            }

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
            {
                return new JArray(data);
            }

            return data;
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

        private JsonSerializerSettings CreateJsonSerializerSettings()
        {
            var settings = new JsonSerializerSettings();

            settings.Formatting = Config.Minify ? Formatting.None :
                                 Config.PrettyPrint ? Formatting.Indented : Formatting.None;

            settings.StringEscapeHandling = Config.JsonEscapeUnicode ?
                StringEscapeHandling.EscapeNonAscii : StringEscapeHandling.Default;

            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                settings.FloatFormatHandling = Config.NumberFormat.ToLower() switch
                {
                    "string" => FloatFormatHandling.String,
                    _ => FloatFormatHandling.DefaultValue
                };
            }

            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                if (Config.DateFormat.Equals("iso8601", StringComparison.CurrentCultureIgnoreCase))
                {
                    settings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
                }
                else
                {
                    settings.DateFormatString = Config.DateFormat;
                }
            }

            if (Config.IgnoreErrors)
            {
                settings.Error = (sender, args) => args.ErrorContext.Handled = true;
            }

            return settings;
        }

        private void ConfigureJsonWriter(JsonTextWriter writer)
        {
            if (Config.IndentSize.HasValue && Config.PrettyPrint && !Config.Minify)
            {
                writer.Indentation = Config.IndentSize.Value;
                writer.IndentChar = Config.IndentSize.Value == 0 ? '\t' : ' ';
            }

            writer.QuoteChar = Config.JsonSingleQuotes ? '\'' : '"';
        }

        private string ConvertToSingleQuotes(string jsonString)
        {
            var result = new System.Text.StringBuilder();
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < jsonString.Length; i++)
            {
                char c = jsonString[i];

                if (escaped)
                {
                    result.Append(c);
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    result.Append(c);
                    continue;
                }
                if (c == '"' && !inString)
                {
                    result.Append('\'');
                    inString = true;
                }
                else if (c == '"' && inString)
                {
                    result.Append('\'');
                    inString = false;
                }
                else if (c == '\'' && inString)
                {
                    result.Append("\\'");
                }
                else
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }
    }
}
