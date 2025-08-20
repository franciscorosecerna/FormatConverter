using FormatConverter.Interfaces;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json.Linq;

namespace FormatConverter.Protobuf
{
    public class ProtobufOutputStrategy : IOutputFormatStrategy
    {
        public string Serialize(JObject data)
        {
            try
            {
                Struct protobufStruct = ConvertJObjectToStruct(data);
                byte[] bytes = protobufStruct.ToByteArray();
                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize to Protobuf: {ex.Message}");
            }
        }

        private static Struct ConvertJObjectToStruct(JObject json)
        {
            var structValue = new Struct();

            foreach (var property in json.Properties())
            {
                structValue.Fields[property.Name] = ConvertJTokenToValue(property.Value);
            }

            return structValue;
        }

        private static Value ConvertJTokenToValue(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => Value.ForString(token.Value<string>() ?? ""),
                JTokenType.Integer => Value.ForNumber(token.Value<double>()),
                JTokenType.Float => Value.ForNumber(token.Value<double>()),
                JTokenType.Boolean => Value.ForBool(token.Value<bool>()),
                JTokenType.Null => Value.ForNull(),
                JTokenType.Array => ConvertJArrayToValue(token as JArray),
                JTokenType.Object => ConvertJObjectToValue(token as JObject),
                JTokenType.Date => Value.ForString(token.Value<DateTime>().ToString("O")),
                _ => Value.ForString(token.ToString())
            };
        }

        private static Value ConvertJArrayToValue(JArray? array)
        {
            if (array == null) return Value.ForNull();

            var values = array.Select(ConvertJTokenToValue).ToArray();
            return Value.ForList(values);
        }

        private static Value ConvertJObjectToValue(JObject? obj)
        {
            if (obj == null) return Value.ForNull();

            return Value.ForStruct(ConvertJObjectToStruct(obj));
        }
    }
}
