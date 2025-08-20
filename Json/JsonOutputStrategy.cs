using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FormatConverter.Json
{
    public class JsonOutputStrategy : IOutputFormatStrategy
    {
        public string Serialize(JObject data)
        {
            return data.ToString(Formatting.Indented);
        }
    }
}
