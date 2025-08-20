using FormatConverter.Interfaces;
using MessagePack;
using MessagePack.Resolvers;
using Newtonsoft.Json.Linq;

namespace FormatConverter.MessagePack
{
    public class MessagePackOutputStrategy : IOutputFormatStrategy
    {
        public string Serialize(JObject data)
        {
            try
            {
                var obj = ConvertJTokenToObject(data);

                var options = MessagePackSerializerOptions.Standard
                    .WithResolver(ContractlessStandardResolver.Instance);

                byte[] bytes = MessagePackSerializer.Serialize(obj, options);
                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                throw new MessagePackSerializationException($"Failed to serialize to MessagePack: {ex.Message}", ex);
            }
        }

        private static object? ConvertJTokenToObject(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var property in token.Children<JProperty>())
                    {
                        dict[property.Name] = ConvertJTokenToObject(property.Value);
                    }
                    return dict;

                case JTokenType.Array:
                    var list = new List<object>();
                    foreach (var item in token.Children())
                    {
                        list.Add(ConvertJTokenToObject(item));
                    }
                    return list;

                case JTokenType.String:
                    return token.Value<string>();

                case JTokenType.Integer:
                    return token.Value<long>();

                case JTokenType.Float:
                    return token.Value<double>();

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Null:
                    return null;

                case JTokenType.Date:
                    return token.Value<DateTime>();

                default:
                    return token.ToString();
            }
        }
    }
}
