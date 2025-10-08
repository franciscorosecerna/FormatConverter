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

        public abstract IEnumerable<string> SerializeStream(IEnumerable<JToken> data);

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
    }
}