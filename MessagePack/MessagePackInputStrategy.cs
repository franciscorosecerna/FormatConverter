using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using MessagePack;
using MessagePack.Resolvers;

namespace FormatConverter.MessagePack
{
    public class MessagePackInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            try
            {
                if (string.IsNullOrEmpty(input))
                {
                    throw new FormatException("MessagePack input is empty or null");
                }

                byte[] bytes;

                try
                {
                    bytes = Convert.FromBase64String(input);
                }
                catch (FormatException)
                {
                    bytes = ParseHexString(input);
                }

                var options = GetMessagePackOptions();
                var obj = MessagePackSerializer.Deserialize<object>(bytes, options);

                if (obj == null)
                {
                    throw new FormatException("MessagePack deserialization returned null");
                }

                var result = ConvertObjectToJToken(obj);

                if (Config.SortKeys && result is JObject)
                {
                    result = SortKeysRecursively(result);
                }

                return result;
            }
            catch (MessagePackSerializationException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: MessagePack parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"Invalid MessagePack: {ex.Message}", ex);
            }
            catch (Exception ex) when (!(ex is FormatException))
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: MessagePack parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"MessagePack parsing failed: {ex.Message}", ex);
            }
        }

        private byte[] ParseHexString(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("-", "");

            if (hex.Length % 2 != 0)
            {
                throw new FormatException("Invalid hex string length");
            }

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        private MessagePackSerializerOptions GetMessagePackOptions()
        {
            var options = MessagePackSerializerOptions.Standard;

            options = options.WithResolver(ContractlessStandardResolver.Instance);

            if (!string.IsNullOrEmpty(Config.Compression))
            {
                options = Config.Compression.ToLower() switch
                {
                    "lz4" => options.WithCompression(MessagePackCompression.Lz4Block),
                    "lz4array" => options.WithCompression(MessagePackCompression.Lz4BlockArray),
                    _ => options
                };
            }

            if (Config.StrictMode)
            {
                options = options.WithSecurity(MessagePackSecurity.UntrustedData);
            }

            return options;
        }

        private JToken ConvertObjectToJToken(object obj)
        {
            if (obj == null) return JValue.CreateNull();

            return obj switch
            {
                Dictionary<object, object> dict => ConvertDictionaryToJObject(dict),
                Dictionary<string, object> stringDict => ConvertStringDictionaryToJObject(stringDict),
                List<object> list => ConvertListToJArray(list),
                byte[] bytes => new JValue(Convert.ToBase64String(bytes)),
                Array array => ConvertArrayToJArray(array),
                string str => new JValue(str),
                bool b => new JValue(b),
                byte b => new JValue(b),
                sbyte sb => new JValue(sb),
                short s => new JValue(s),
                ushort us => new JValue(us),
                int i => new JValue(i),
                uint ui => new JValue(ui),
                long l => new JValue(l),
                ulong ul => new JValue((long)ul),
                float f => new JValue(FormatNumberValue(f)),
                double d => new JValue(FormatNumberValue(d)),
                decimal m => new JValue(m),
                DateTime dt => new JValue(FormatDateTime(dt)),
                DateTimeOffset dto => new JValue(FormatDateTime(dto.DateTime)),
                _ when obj.GetType().IsArray => ConvertArrayToJArray((Array)obj),
                _ => ConvertComplexObjectToJToken(obj)
            };
        }

        private JObject ConvertDictionaryToJObject(Dictionary<object, object> dict)
        {
            var result = new JObject();

            foreach (var kvp in dict)
            {
                var key = ConvertKeyToString(kvp.Key);
                result[key] = ConvertObjectToJToken(kvp.Value);
            }

            return result;
        }

        private JObject ConvertStringDictionaryToJObject(Dictionary<string, object> dict)
        {
            var result = new JObject();

            foreach (var kvp in dict)
            {
                result[kvp.Key] = ConvertObjectToJToken(kvp.Value);
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

        private JToken ConvertComplexObjectToJToken(object obj)
        {
            try
            {
                var type = obj.GetType();
                if (type.IsClass && type != typeof(string))
                {
                    var result = new JObject();
                    var properties = type.GetProperties();

                    foreach (var prop in properties)
                    {
                        if (prop.CanRead)
                        {
                            var value = prop.GetValue(obj);
                            result[prop.Name] = ConvertObjectToJToken(value);
                        }
                    }

                    return result;
                }
            }
            catch { }

            return new JValue(obj.ToString());
        }

        private static string ConvertKeyToString(object key)
        {
            return key switch
            {
                string str => str,
                null => "null",
                byte[] bytes => Convert.ToBase64String(bytes),
                _ => key.ToString() ?? "unknown"
            };
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

        private object FormatDateTime(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds(),
                    "rfc3339" => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }
            return dateTime;
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