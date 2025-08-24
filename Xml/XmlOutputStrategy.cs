using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Xml;
using System.Xml.Linq;

namespace FormatConverter.Xml
{
    public class XmlOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            try
            {
                var rootName = Config.XmlRootElement ?? "root";
                var root = new XElement(rootName);

                if (!string.IsNullOrEmpty(Config.XmlNamespace))
                {
                    var ns = XNamespace.Get(Config.XmlNamespace);
                    if (!string.IsNullOrEmpty(Config.XmlNamespacePrefix))
                    {
                        root.Add(new XAttribute(XNamespace.Xmlns + Config.XmlNamespacePrefix, Config.XmlNamespace));
                        root.Name = ns + rootName;
                    }
                    else
                    {
                        root.Add(new XAttribute("xmlns", Config.XmlNamespace));
                        root.Name = ns + rootName;
                    }
                }

                ConvertObjectToXElement(data, root);

                var doc = new XDocument();

                if (Config.XmlIncludeDeclaration)
                {
                    doc.Declaration = new XDeclaration("1.0", Config.Encoding.WebName, Config.XmlStandalone ? "yes" : null);
                }

                doc.Add(root);

                var settings = new XmlWriterSettings
                {
                    Indent = Config.PrettyPrint && !Config.Minify,
                    IndentChars = Config.GetIndentString(),
                    Encoding = Config.Encoding,
                    OmitXmlDeclaration = !Config.XmlIncludeDeclaration,
                    NewLineHandling = Config.Minify ? NewLineHandling.None : NewLineHandling.Replace
                };

                using var stringWriter = new StringWriter();
                using var xmlWriter = XmlWriter.Create(stringWriter, settings);

                doc.Save(xmlWriter);
                return stringWriter.ToString();
            }
            catch (Exception ex)
            {
                throw new FormatException($"XML serialization failed: {ex.Message}", ex);
            }
        }

        private void ConvertObjectToXElement(object obj, XElement parent)
        {
            if (obj == null) return;

            switch (obj)
            {
                case Dictionary<string, object> dict:
                    ConvertDictionaryToXml(dict, parent);
                    break;

                case List<object> list:
                    ConvertListToXml(list, parent);
                    break;

                case Array array:
                    ConvertArrayToXml(array, parent);
                    break;

                default:
                    var textValue = FormatValue(obj);
                    if (Config.XmlUseCData && ContainsSpecialCharacters(textValue))
                    {
                        parent.Add(new XCData(textValue));
                    }
                    else
                    {
                        parent.Value = textValue;
                    }
                    break;
            }
        }

        private void ConvertDictionaryToXml(Dictionary<string, object> dict, XElement parent)
        {
            var entries = Config.SortKeys ? dict.OrderBy(kvp => kvp.Key) : dict.AsEnumerable();

            foreach (var kvp in entries)
            {
                if (kvp.Key.StartsWith("@"))
                {
                    var attrName = kvp.Key.Substring(1);
                    parent.SetAttributeValue(attrName, FormatValue(kvp.Value));
                }
                else if (kvp.Key == "#text")
                {
                    var textValue = FormatValue(kvp.Value);
                    if (Config.XmlUseCData && ContainsSpecialCharacters(textValue))
                    {
                        parent.Add(new XCData(textValue));
                    }
                    else
                    {
                        parent.Value = textValue;
                    }
                }
                else
                {
                    var element = new XElement(SanitizeElementName(kvp.Key));
                    ConvertObjectToXElement(kvp.Value, element);
                    parent.Add(element);
                }
            }
        }

        private void ConvertListToXml(List<object> list, XElement parent)
        {
            foreach (var item in list)
            {
                var itemElement = new XElement("item");
                ConvertObjectToXElement(item, itemElement);
                parent.Add(itemElement);
            }
        }

        private void ConvertArrayToXml(Array array, XElement parent)
        {
            foreach (var item in array)
            {
                var itemElement = new XElement("item");
                ConvertObjectToXElement(item, itemElement);
                parent.Add(itemElement);
            }
        }

        private string FormatValue(object value)
        {
            if (value == null) return string.Empty;

            return value switch
            {
                DateTime dt => FormatDateTime(dt),
                double d when !string.IsNullOrEmpty(Config.NumberFormat) => FormatNumber(d),
                float f when !string.IsNullOrEmpty(Config.NumberFormat) => FormatNumber(f),
                decimal m when !string.IsNullOrEmpty(Config.NumberFormat) => FormatNumber((double)m),
                _ => value.ToString() ?? string.Empty
            };
        }

        private string FormatDateTime(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }
            return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        }

        private string FormatNumber(double number)
        {
            return Config.NumberFormat?.ToLower() switch
            {
                "hexadecimal" => $"0x{(long)number:X}",
                "scientific" => number.ToString("E"),
                _ => number.ToString()
            };
        }

        private bool ContainsSpecialCharacters(string text)
        {
            return text.Contains('<') || text.Contains('>') || text.Contains('&') ||
                   text.Contains('\n') || text.Contains('\r');
        }

        private string SanitizeElementName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "element";

            if (char.IsDigit(name[0])) name = "_" + name;
            name = name.Replace(" ", "_").Replace("-", "_");

            return name;
        }
    }
}
