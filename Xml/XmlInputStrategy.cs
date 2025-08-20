using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;

namespace FormatConverter.Xml
{
    public class XmlInputStrategy : IInputFormatStrategy
    {
        public JObject Parse(string input)
        {
            var doc = XDocument.Parse(input);
            var json = new JObject();

            foreach (var elem in doc.Root.Elements())
            {
                var jvalue = ConvertXmlElementToJToken(elem);
                json[elem.Name.LocalName] = jvalue;
            }

            return json;
        }

        private static JToken ConvertXmlElementToJToken(XElement element)
        {
            string? type = element.Attribute("type")?.Value ?? "string";

            if (type == "object")
            {
                var childObj = new JObject();
                foreach (var child in element.Elements())
                {
                    childObj[child.Name.LocalName] = ConvertXmlElementToJToken(child);
                }
                return childObj;
            }
            else if (type == "array")
            {
                var array = new JArray();
                foreach (var item in element.Elements())
                {
                    array.Add(ConvertXmlElementToJToken(item));
                }
                return array;
            }

            if (type == "null")
                return JValue.CreateNull();

            string value = element.Value;

            return type switch
            {
                "int" => int.TryParse(value, out var i) ? new JValue(i) : JValue.CreateNull(),
                "float" => double.TryParse(value, out var d) ? new JValue(d) : JValue.CreateNull(),
                "bool" => bool.TryParse(value, out var b) ? new JValue(b) : JValue.CreateNull(),
                _ => new JValue(value)
            };
        }
    }
}
