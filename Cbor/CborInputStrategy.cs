using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;

namespace FormatConverter.Cbor
{
    public class CborInputStrategy : IInputFormatStrategy
    {
        public JObject Parse(string input)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(input);
                CBORObject cborObj = CBORObject.DecodeFromBytes(bytes);
                string jsonString = cborObj.ToJSONString();
                return JObject.Parse(jsonString);
            }
            catch (Exception ex)
            {
                throw new CBORException($"Failed to deserialize CBOR: {ex.Message}");
            }
        }
    }
}
