using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;

namespace FormatConverter.Cbor
{
    public class CborOutputStrategy : IOutputFormatStrategy
    {
        public string Serialize(JObject data)
        {
            try
            {
                CBORObject cborObj = CBORObject.FromJSONString(data.ToString());
                byte[] bytes = cborObj.EncodeToBytes();
                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                throw new CBORException($"Failed to serialize to CBOR: {ex.Message}");
            }
        }
    }
}
