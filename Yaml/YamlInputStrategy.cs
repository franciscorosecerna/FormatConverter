using FormatConverter.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace FormatConverter.Yaml
{
    public class YamlInputStrategy : IInputFormatStrategy
    {
        public JObject Parse(string input)
        {
            var deserializer = new DeserializerBuilder().Build();
            var yamlObject = deserializer.Deserialize(new StringReader(input));
            string json = JsonConvert.SerializeObject(yamlObject);
            return JObject.Parse(json);
        }
    }
}
