using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;

namespace FormatConverter.Cbor
{
    public class CborOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            try
            {
                var cborObj = ConvertJTokenToCbor(data);

                if (cborObj == null)
                {
                    throw new FormatException("Failed to convert JSON to CBOR object");
                }

                var options = GetCborEncodeOptions();
                var bytes = cborObj.EncodeToBytes(options);

                return FormatOutput(bytes);
            }
            catch (CBORException ex)
            {
                throw new FormatException($"CBOR serialization failed: {ex.Message}", ex);
            }
            catch (Exception ex) when (!(ex is FormatException))
            {
                throw new FormatException($"CBOR serialization error: {ex.Message}", ex);
            }
        }

        private CBORObject ConvertJTokenToCbor(JToken token)
        {
            if (token == null) return CBORObject.Null;

            return token.Type switch
            {
                JTokenType.Object => ConvertJObjectToCborMap((JObject)token),
                JTokenType.Array => ConvertJArrayToCborArray((JArray)token),
                JTokenType.String => ConvertStringToCbor(token.Value<string>()),
                JTokenType.Integer => CBORObject.FromObject(token.Value<long>()),
                JTokenType.Float => CBORObject.FromObject(FormatNumberValue(token.Value<double>())),
                JTokenType.Boolean => CBORObject.FromObject(token.Value<bool>()),
                JTokenType.Date => ConvertDateToCbor(token.Value<DateTime>()),
                JTokenType.Null => CBORObject.Null,
                JTokenType.Undefined => CBORObject.Undefined,
                _ => CBORObject.FromObject(token.ToString())
            };
        }

        private CBORObject ConvertJObjectToCborMap(JObject jObject)
        {
            var cborMap = CBORObject.NewMap();

            var properties = Config.SortKeys
                ? jObject.Properties().OrderBy(p => p.Name)
                : jObject.Properties();

            foreach (var property in properties)
            {
                var key = CBORObject.FromObject(property.Name);
                var value = ConvertJTokenToCbor(property.Value);
                cborMap[key] = value;
            }

            return cborMap;
        }

        private CBORObject ConvertJArrayToCborArray(JArray jArray)
        {
            var cborArray = CBORObject.NewArray();

            foreach (var item in jArray)
            {
                cborArray.Add(ConvertJTokenToCbor(item));
            }

            return cborArray;
        }

        private CBORObject ConvertStringToCbor(string? value)
        {
            if (value == null) return CBORObject.Null;

            if (IsBase64String(value))
            {
                try
                {
                    var bytes = Convert.FromBase64String(value);
                    return CBORObject.FromObject(bytes);
                }
                catch
                {
                    //if Base64 decoding fails, treat as regular string
                }
            }

            return CBORObject.FromObject(value);
        }

        private CBORObject ConvertDateToCbor(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                var formattedDate = Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds().ToString(),
                    _ => dateTime.ToString(Config.DateFormat)
                };
                return CBORObject.FromObject(formattedDate);
            }
            return CBORObject.FromObject(dateTime);
        }

        private double FormatNumberValue(double number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "scientific" => double.Parse(number.ToString("E")),
                    _ => number
                };
            }
            return number;
        }

        private CBOREncodeOptions GetCborEncodeOptions()
        {
            var opts = new List<string>();

            if (Config.Minify)
            {
                opts.Add("ctap2canonical=true");
            }

            if (Config.NoMetadata)
            {
                opts.Add("allowduplicatekeys=false");
            }

            return new CBOREncodeOptions(string.Join(",", opts));
        }

        private string FormatOutput(byte[] bytes)
        {
            return Config.NumberFormat?.ToLower() switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
                _ => Convert.ToBase64String(bytes)
            };
        }

        private string FormatAsHex(byte[] bytes)
        {
            var hex = Convert.ToHexString(bytes);

            if (Config.PrettyPrint && !Config.Minify)
            {
                // Add spacing for readability
                return string.Join(" ",
                    Enumerable.Range(0, hex.Length / 2)
                    .Select(i => hex.Substring(i * 2, 2)));
            }

            return hex;
        }

        private static bool IsBase64String(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length % 4 != 0)
                return false;

            try
            {
                Convert.FromBase64String(value);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}