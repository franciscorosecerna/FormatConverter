using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;

namespace FormatConverter.Cbor
{
    public class CborInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            try
            {
                if (string.IsNullOrEmpty(input))
                {
                    throw new FormatException("CBOR input is empty or null");
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

                var cborObj = CBORObject.DecodeFromBytes(bytes) 
                    ?? throw new FormatException("CBOR object is null after decoding");

                var result = ConvertCborToJToken(cborObj);

                if (Config.SortKeys && result is JObject)
                {
                    result = SortKeysRecursively(result);
                }

                return result;
            }
            catch (CBORException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: CBOR parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"Invalid CBOR: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: CBOR parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"CBOR parsing failed: {ex.Message}", ex);
            }
        }

        private static byte[] ParseHexString(string hex)
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

        private JToken ConvertCborToJToken(CBORObject cbor)
        {
            if (cbor == null) return JValue.CreateNull();

            switch (cbor.Type)
            {
                case CBORType.Map:
                    return ConvertCborMapToJObject(cbor);

                case CBORType.Array:
                    return ConvertCborArrayToJArray(cbor);

                case CBORType.TextString:
                    return new JValue(cbor.AsString());

                case CBORType.Integer:
                    if (cbor.CanValueFitInInt64())
                        return new JValue(cbor.AsInt64Value());
                    return new JValue(cbor.AsNumber().ToInt64Checked().ToString());

                case CBORType.FloatingPoint:
                    return new JValue(cbor.AsDoubleValue());

                case CBORType.Boolean:
                    return new JValue(cbor.AsBoolean());

                case CBORType.SimpleValue:
                    if (cbor.IsNull) return JValue.CreateNull();
                    if (cbor.IsUndefined) return JValue.CreateUndefined();
                    return new JValue(cbor.SimpleValue);

                case CBORType.ByteString:
                    var bytes = cbor.GetByteString();
                    return new JValue(Convert.ToBase64String(bytes));

                default:
                    return new JValue(cbor.ToString());
            }
        }

        private JObject ConvertCborMapToJObject(CBORObject cborMap)
        {
            var result = new JObject();

            foreach (var key in cborMap.Keys)
            {
                var keyString = ConvertCborKeyToString(key);
                var value = ConvertCborToJToken(cborMap[key]);
                result[keyString] = value;
            }

            return result;
        }

        private JArray ConvertCborArrayToJArray(CBORObject cborArray)
        {
            var result = new JArray();

            for (int i = 0; i < cborArray.Count; i++)
            {
                result.Add(ConvertCborToJToken(cborArray[i]));
            }

            return result;
        }

        private static string ConvertCborKeyToString(CBORObject key)
        {
            return key.Type switch
            {
                CBORType.TextString => key.AsString(),
                CBORType.Integer => key.AsInt64Value().ToString(),
                CBORType.ByteString => Convert.ToBase64String(key.GetByteString()),
                _ => key.ToString()
            };
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