using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;

namespace FormatConverter.Json
{
    public class JsonInputStrategy : IInputFormatStrategy
    {
        public JObject Parse(string input)
        {
            return JObject.Parse(input);
        }
    }
}
