using FormatConverter.Interfaces;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MessagePack.Resolvers;

namespace FormatConverter.MessagePack
{
    public class MessagePackInputStrategy : IInputFormatStrategy
    {
        public JObject Parse(string input)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(input);

                var options = MessagePackSerializerOptions.Standard
                    .WithResolver(ContractlessStandardResolver.Instance);

                var obj = MessagePackSerializer.Deserialize<object>(bytes, options);
                string json = JsonConvert.SerializeObject(obj, Formatting.None);
                return JObject.Parse(json);
            }
            catch (Exception ex)
            {
                throw new MessagePackSerializationException($"Failed to deserialize MessagePack: {ex.Message}", ex);
            }
        }
    }
}
