using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public abstract class BaseOutputStrategy : IOutputFormatStrategy
    {
        protected FormatConfig Config { get; private set; } = new FormatConfig();

        public virtual void Configure(FormatConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public abstract string Serialize(JToken data);

        public abstract void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default);

        protected JToken SortKeysRecursively(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Object => SortJObject((JObject)token),
                JTokenType.Array => new JArray(((JArray)token).Select(SortKeysRecursively)),
                _ => token
            };
        }

        protected JObject SortJObject(JObject obj)
        {
            var properties = obj.Properties().ToList();

            bool isAlreadySorted = true;
            for (int i = 1; i < properties.Count; i++)
            {
                if (string.Compare(properties[i - 1].Name, properties[i].Name, StringComparison.Ordinal) > 0)
                {
                    isAlreadySorted = false;
                    break;
                }
            }

            if (isAlreadySorted)
            {
                bool needsRecursiveSort = properties.Any(p =>
                    p.Value.Type == JTokenType.Object || p.Value.Type == JTokenType.Array);

                if (!needsRecursiveSort)
                    return obj;

                return new JObject(properties.Select(p =>
                    new JProperty(p.Name, SortKeysRecursively(p.Value))));
            }

            return new JObject(properties
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new JProperty(p.Name, SortKeysRecursively(p.Value))));
        }

        protected static JToken FlattenArraysRecursively(JToken token)
        {
            if (token.Type != JTokenType.Array)
                return token;

            var array = (JArray)token;
            var flattened = new JArray();

            foreach (var item in array)
            {
                if (item.Type == JTokenType.Array)
                {
                    var flattenedChild = FlattenArraysRecursively(item);
                    foreach (var child in (JArray)flattenedChild)
                    {
                        flattened.Add(child);
                    }
                }
                else
                {
                    flattened.Add(item);
                }
            }

            return flattened;
        }

        protected string FormatNumber(double number)
        {
            return Config.NumberFormat?.ToLower() switch
            {
                "hexadecimal" => $"0x{(long)number:X}",
                "scientific" => number.ToString("E"),
                _ => number.ToString()
            };
        }

        protected string FormatValue(object value)
        {
            if (value == null) return string.Empty;

            return value switch
            {
                DateTime dt => FormatDateTime(dt),
                double d when !string.IsNullOrEmpty(Config.NumberFormat) => FormatNumber(d),
                float f when !string.IsNullOrEmpty(Config.NumberFormat) => FormatNumber(f),
                decimal m when !string.IsNullOrEmpty(Config.NumberFormat) => FormatNumber((double)m),
                _ => value.ToString() ?? string.Empty
            };
        }

        protected string FormatDateTime(DateTime dateTime)
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

        protected static JToken LimitDepth(JToken token, int maxDepth, int currentDepth = 0)
        {
            if (currentDepth >= maxDepth)
            {
                return token.Type switch
                {
                    JTokenType.Object => new JObject(),
                    JTokenType.Array => new JArray(),
                    _ => token
                };
            }

            return token.Type switch
            {
                JTokenType.Object => new JObject(
                    ((JObject)token).Properties()
                        .Select(p => new JProperty(p.Name, LimitDepth(p.Value, maxDepth, currentDepth + 1)))
                ),
                JTokenType.Array => new JArray(
                    ((JArray)token).Select(item => LimitDepth(item, maxDepth, currentDepth + 1))
                ),
                _ => token
            };
        }

        protected JToken RemoveMetadata(JToken token)
        {
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var metadataKeys = new[] { "$schema", "$id", "$comment", "$ref", "_metadata", "__meta__", "__type" };
                var cleaned = new JObject();

                foreach (var prop in obj.Properties())
                {
                    if (!metadataKeys.Contains(prop.Name))
                    {
                        cleaned[prop.Name] = RemoveMetadata(prop.Value);
                    }
                }

                return cleaned;
            }
            else if (token.Type == JTokenType.Array)
            {
                return new JArray(((JArray)token).Select(RemoveMetadata));
            }

            return token;
        }

        protected virtual JToken PreprocessToken(JToken data)
        {
            var result = data;

            if (Config.FlattenArrays)
                result = FlattenArraysRecursively(result);

            if (Config.SortKeys)
                result = SortKeysRecursively(result);

            if (Config.ArrayWrap && result.Type != JTokenType.Array)
                result = new JArray(result);

            if (Config.NoMetadata)
                result = RemoveMetadata(result);

            if (Config.MaxDepth.HasValue)
                result = LimitDepth(result, Config.MaxDepth.Value);

            return result;
        }
    }
}