using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;

namespace FormatConverter.Xml
{
    public class XmlOutputStrategy : IOutputFormatStrategy
    {
        public string Serialize(JObject data)
        {
            XElement root = new("Root");

            foreach (var prop in data.Properties())
            {
                XElement elem = ConvertJsonTokenToXmlElement(prop.Name, prop.Value);
                root.Add(elem);
            }

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            return doc.ToString();
        }

        private static XElement ConvertJsonTokenToXmlElement(string name, JToken token)
        {
            var element = new XElement(name);

            switch (token.Type)
            {
                case JTokenType.Integer:
                    element.Value = token.ToString();
                    element.SetAttributeValue("type", "int");
                    break;
                case JTokenType.Float:
                    element.Value = token.ToString();
                    element.SetAttributeValue("type", "float");
                    break;
                case JTokenType.Boolean:
                    element.Value = token.ToString().ToLower();
                    element.SetAttributeValue("type", "bool");
                    break;
                case JTokenType.Null:
                    element.SetAttributeValue("type", "null");
                    break;
                case JTokenType.String:
                    element.Value = token.ToString();
                    element.SetAttributeValue("type", "string");
                    break;
                case JTokenType.Object:
                    element.SetAttributeValue("type", "object");
                    foreach (var child in ((JObject)token).Properties())
                    {
                        element.Add(ConvertJsonTokenToXmlElement(child.Name, child.Value));
                    }
                    break;
                case JTokenType.Array:
                    element.SetAttributeValue("type", "array");
                    foreach (var item in (JArray)token)
                    {
                        element.Add(ConvertJsonTokenToXmlElement("item", item));
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported token type: {token.Type}");
            }

            return element;
        }
    }
}
