using FormatConverter.Logger;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace FormatConverter.Interfaces
{
    public abstract class BaseOutputStrategy : IOutputFormatStrategy
    {

        private static readonly HashSet<string> DefaultMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "$schema", "$id", "$comment", "$ref", "_metadata", "__meta__", "__type"
        };

        protected FormatConfig Config { get; private set; } = new FormatConfig();
        protected ILogger Logger { get; private set; } = new ConsoleLogger();

        /// <summary>
        /// Configures the strategy with the specified formatting configuration
        /// </summary>
        /// <param name="config">The formatting configuration to apply</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null</exception>
        public virtual void Configure(FormatConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Logger.Verbosity = config.Verbosity;
        }

        /// <summary>
        /// Serializes the provided JSON token to a string representation
        /// </summary>
        /// <param name="data">The JSON token to serialize</param>
        /// <returns>A string representing the serialized data</returns>
        public abstract string Serialize(JToken data);

        /// <summary>
        /// Serializes a sequence of JSON tokens to the specified output stream
        /// </summary>
        /// <param name="data">The sequence of JSON tokens to serialize</param>
        /// <param name="output">The output stream to write the serialized data to</param>
        /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
        public abstract void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default);

        /// <summary>
        /// Recursively sorts the keys of JSON objects in alphabetical order
        /// </summary>
        /// <param name="token">The JSON token to sort</param>
        /// <returns>A new JSON token with all object keys sorted recursively</returns>
        private JToken SortKeysRecursively(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Object => SortJObject((JObject)token),
                JTokenType.Array => new JArray(((JArray)token).Select(SortKeysRecursively)),
                _ => token
            };
        }

        /// <summary>
        /// Sorts the properties of a JObject alphabetically by name
        /// </summary>
        /// <param name="obj">The JObject to sort</param>
        /// <returns>A new JObject with properties sorted by name</returns>
        private JObject SortJObject(JObject obj)
        {
            return new JObject(
                obj.Properties()
                   .OrderBy(p => p.Name, StringComparer.Ordinal)
                   .Select(p => new JProperty(p.Name, SortKeysRecursively(p.Value)))
            );
        }

        /// <summary>
        /// Recursively flattens nested arrays into a single-level array
        /// </summary>
        /// <param name="token">The JSON token to flatten</param>
        /// <returns>A flattened JArray containing all non-array elements from the input</returns>
        private static JToken FlattenArraysRecursively(JToken token)
        {
            if (token.Type != JTokenType.Array)
                return token;

            var result = new JArray();
            var stack = new Stack<JToken>();
            stack.Push(token);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                if (current.Type == JTokenType.Array)
                {
                    var array = (JArray)current;
                    for (int i = array.Count - 1; i >= 0; i--)
                    {
                        stack.Push(array[i]);
                    }
                }
                else
                {
                    result.Add(current);
                }
            }

            return result;
        }

        /// <summary>
        /// Limits the depth of nested JSON structures by truncating beyond the specified maximum depth
        /// </summary>
        /// <param name="token">The JSON token to process</param>
        /// <param name="maxDepth">The maximum allowed depth</param>
        /// <param name="currentDepth">The current depth in the recursion (default: 0)</param>
        /// <returns>A JSON token with depth limited to the specified maximum</returns>
        private static JToken LimitDepth(JToken token, int maxDepth, int currentDepth = 0)
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

        /// <summary>
        /// Removes metadata properties from JSON objects based on the default metadata keys
        /// </summary>
        /// <param name="token">The JSON token to clean</param>
        /// <returns>A JSON token with metadata properties removed</returns>
        private JToken RemoveMetadata(JToken token)
        {
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var metadataKeys = DefaultMetadataKeys;
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

        /// <summary>
        /// Recursively formats numeric and date values according to the configuration settings
        /// </summary>
        /// <param name="token">The JSON token to format</param>
        /// <returns>A JSON token with formatted numeric and date values</returns>
        private JToken FormatValuesRecursively(JToken token)
        {
            if (token is JValue value)
            {
                if (!string.IsNullOrEmpty(Config.NumberFormat))
                {
                    switch (value.Type)
                    {
                        case JTokenType.Integer:
                            {
                                long num = value.Value<long>();
                                return FormatNumberAsJToken(num);
                            }
                        case JTokenType.Float:
                            {
                                double num = value.Value<double>();
                                return FormatNumberAsJToken(num);
                            }
                    }
                }

                if (!string.IsNullOrEmpty(Config.DateFormat) && value.Type == JTokenType.Date)
                {
                    DateTime dateTime = value.Value<DateTime>();
                    return FormatDateTimeAsJToken(dateTime);
                }

                return token;
            }

            if (token is JObject obj)
            {
                var clone = new JObject();
                foreach (var prop in obj.Properties())
                    clone[prop.Name] = FormatValuesRecursively(prop.Value);
                return clone;
            }

            if (token is JArray array)
            {
                var clone = new JArray();
                foreach (var item in array)
                    clone.Add(FormatValuesRecursively(item));
                return clone;
            }

            return token;
        }

        /// <summary>
        /// Formats a numeric value according to the configured number format
        /// </summary>
        /// <typeparam name="T">The numeric type (must implement INumber&lt;T&gt;)</typeparam>
        /// <param name="number">The numeric value to format</param>
        /// <returns>A JToken containing the formatted number</returns>
        private JValue FormatNumberAsJToken<T>(T number) where T : INumber<T>
        {
            var format = Config.NumberFormat?.ToLower();

            switch (format)
            {
                case "hexadecimal":
                    try
                    {
                        long value = Convert.ToInt64(number);
                        return new JValue($"0x{value:X}");
                    }
                    catch (OverflowException)
                    {
                        Logger?.WriteWarning(() => $"Number too large for hexadecimal format: {number}");
                        return new JValue(number.ToString());
                    }
                case "binary":
                    try
                    {
                        long value = Convert.ToInt64(number);
                        string binary = Convert.ToString(value, 2);
                        return new JValue($"0b{binary}");
                    }
                    catch (OverflowException)
                    {
                        Logger?.WriteWarning(() => $"Number too large for binary format: {number}");
                        return new JValue(number.ToString());
                    }
                case "scientific":
                    try
                    {
                        double value = Convert.ToDouble(number);
                        return new JValue(value.ToString("E", CultureInfo.InvariantCulture));
                    }
                    catch (OverflowException)
                    {
                        Logger?.WriteWarning(() => $"Number conversion error for scientific format: {number}");
                        return new JValue(number.ToString());
                    }
                default:
                    return new JValue(number);
            }
        }

        /// <summary>
        /// Formats a DateTime value according to the configured date format
        /// </summary>
        /// <param name="dateTime">The DateTime value to format</param>
        /// <returns>A JToken containing the formatted date/time</returns>
        private JValue FormatDateTimeAsJToken(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                try
                {
                    string formatted = Config.DateFormat.ToLower() switch
                    {
                        "iso8601" => dateTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                        "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds().ToString(),
                        "unixms" => ((DateTimeOffset)dateTime).ToUnixTimeMilliseconds().ToString(),
                        _ => dateTime.ToString(Config.DateFormat, CultureInfo.InvariantCulture)
                    };
                    return new JValue(formatted);
                }
                catch (FormatException ex)
                {
                    Logger?.WriteWarning(() => $"Invalid date format '{Config.DateFormat}': {ex.Message}");
                    return new JValue(dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
                }
            }

            return new JValue(dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Applies all configured preprocessing operations to the input data
        /// </summary>
        /// <param name="data">The JSON token to preprocess</param>
        /// <returns>A preprocessed JSON token ready for serialization</returns>
        /// <exception cref="ArgumentNullException">Thrown when data is null</exception>
        protected virtual JToken PreprocessToken(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (Config.FlattenArrays)
                data = FlattenArraysRecursively(data);

            if (Config.NoMetadata)
                data = RemoveMetadata(data);

            if (Config.MaxDepth.HasValue)
                data = LimitDepth(data, Config.MaxDepth.Value);

            if (!string.IsNullOrEmpty(Config.NumberFormat) || !string.IsNullOrEmpty(Config.DateFormat))
                data = FormatValuesRecursively(data);

            if (Config.SortKeys)
                data = SortKeysRecursively(data);

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
                data = new JArray(data);

            return data;
        }
    }
}