using FormatConverter.Interfaces;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json.Linq;

namespace FormatConverter.Protobuf
{
    public class ProtobufInputStrategy : IInputFormatStrategy
    {
        public JObject Parse(string input)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(input);
                Struct protobufStruct = Struct.Parser.ParseFrom(bytes);
                string jsonString = protobufStruct.ToString();
                return JObject.Parse(jsonString);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize Protobuf: {ex.Message}");
            }
        }
    }
}
