using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FormatConverter.Xml
{
    public class XmlOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            if (Config.UseStreaming)
            {
                return string.Join("", SerializeStream([data]));
            }

            return SerializeRegular(data);
        }

        public override IEnumerable<string> SerializeStream(IEnumerable<JToken> data)
        {
            if (!Config.UseStreaming)
            {
                yield return Serialize(new JArray(data));
                yield break;
            }

            foreach (var token in data)
            {
                foreach (var chunk in StreamToken(token))
                {
                    yield return chunk;
                }
            }
        }

        private IEnumerable<string> StreamToken(JToken token)
        {
            var processed = ProcessDataBeforeSerialization(token);

            return processed.Type switch
            {
                JTokenType.Array => StreamArray((JArray)processed),
                JTokenType.Object => StreamObject((JObject)processed),
                _ => StreamSingle(processed)
            };
        }

        private IEnumerable<string> StreamArray(JArray array)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var rootName = Config.XmlRootElement ?? "root";

            yield return CreateXmlHeader();
            yield return $"<{rootName}>";
            if (NeedsPretty()) yield return "\n";

            var buffer = new List<JToken>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var itemCount = array.Count;

            foreach (var item in array)
            {
                var xmlContent = SerializeTokenToXmlContent(item, "item");
                var itemSizeInBytes = Encoding.UTF8.GetByteCount(xmlContent);

                if (buffer.Count > 0 && currentBufferSize + itemSizeInBytes > bufferSize)
                {
                    yield return SerializeArrayChunk(buffer);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (itemCount > 0 && totalProcessed % Math.Max(1, itemCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / itemCount * 100;
                        Console.WriteLine($"XML Array serialization progress: {progress:F1}%");
                    }
                }

                buffer.Add(item);
                currentBufferSize += itemSizeInBytes;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeArrayChunk(buffer);
            }

            if (NeedsPretty()) yield return "\n";
            yield return $"</{rootName}>";
        }

        private IEnumerable<string> StreamObject(JObject obj)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var rootName = Config.XmlRootElement ?? "root";
            var maxPropertiesPerChunk = Math.Max(10, bufferSize / 1024);

            yield return CreateXmlHeader();
            yield return CreateOpeningTag(rootName);
            if (NeedsPretty()) yield return "\n";

            var propertyBuffer = new List<JProperty>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var propertyCount = obj.Count;
            var properties = Config.SortKeys ? obj.Properties().OrderBy(p => p.Name) : obj.Properties();

            foreach (var property in properties)
            {
                var xmlContent = SerializePropertyToXml(property);
                var propertySizeInBytes = Encoding.UTF8.GetByteCount(xmlContent);

                if (propertyBuffer.Count > 0 &&
                    (currentBufferSize + propertySizeInBytes > bufferSize ||
                     propertyBuffer.Count >= maxPropertiesPerChunk))
                {
                    yield return SerializeObjectChunk(propertyBuffer);
                    propertyBuffer.Clear();
                    currentBufferSize = 0;

                    if (propertyCount > 0 && totalProcessed % Math.Max(1, propertyCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / propertyCount * 100;
                        Console.WriteLine($"XML Object serialization progress: {progress:F1}%");
                    }
                }

                propertyBuffer.Add(property);
                currentBufferSize += propertySizeInBytes;
                totalProcessed++;
            }

            if (propertyBuffer.Count > 0)
            {
                yield return SerializeObjectChunk(propertyBuffer);
            }

            if (NeedsPretty()) yield return "\n";
            yield return $"</{rootName}>";
        }

        private IEnumerable<string> StreamSingle(JToken token)
        {
            IEnumerable<string> Iterator()
            {
                var rootName = Config.XmlRootElement ?? "root";
                yield return CreateXmlHeader();
                yield return SerializeTokenToXmlContent(token, rootName);
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return [CreateErrorXml(ex.Message, 1)];
                }
                else
                {
                    throw new FormatException($"Error serializing single token to XML: {ex.Message}", ex);
                }
            }
        }

        private string SerializeArrayChunk(List<JToken> items)
        {
            var sb = new StringBuilder();
            var indent = NeedsPretty() ? new string(' ', Config.IndentSize ?? 2) : "";

            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    if (NeedsPretty() && i > 0) sb.Append("\n");
                    if (NeedsPretty()) sb.Append(indent);

                    sb.Append(SerializeTokenToXmlContent(items[i], "item"));
                }
                catch (Exception ex)
                {
                    if (Config.IgnoreErrors)
                    {
                        sb.Append(CreateErrorXmlElement($"item_{i}", ex.Message));
                    }
                    else
                    {
                        throw new FormatException($"Error serializing array item {i}: {ex.Message}", ex);
                    }
                }
            }

            return sb.ToString();
        }

        private string SerializeObjectChunk(List<JProperty> properties)
        {
            var sb = new StringBuilder();
            var indent = NeedsPretty() ? new string(' ', Config.IndentSize ?? 2) : "";

            for (int i = 0; i < properties.Count; i++)
            {
                try
                {
                    if (NeedsPretty() && i > 0) sb.Append("\n");
                    if (NeedsPretty()) sb.Append(indent);

                    sb.Append(SerializePropertyToXml(properties[i]));
                }
                catch (Exception ex)
                {
                    if (Config.IgnoreErrors)
                    {
                        sb.Append(CreateErrorXmlElement(SanitizeElementName(properties[i].Name), ex.Message));
                    }
                    else
                    {
                        throw new FormatException($"Error serializing property {properties[i].Name}: {ex.Message}", ex);
                    }
                }
            }

            return sb.ToString();
        }

        private string SerializeRegular(JToken data)
        {
            var processed = ProcessDataBeforeSerialization(data);

            try
            {
                var rootName = Config.XmlRootElement ?? "root";
                var root = new XElement(rootName);

                ConfigureNamespace(root);
                ConvertObjectToXElement(processed, root);

                var doc = new XDocument();
                if (Config.XmlIncludeDeclaration)
                {
                    doc.Declaration = new XDeclaration("1.0", Config.Encoding.WebName, Config.XmlStandalone ? "yes" : null);
                }

                doc.Add(root);
                return SerializeXDocument(doc);
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return CreateErrorXml(ex.Message, 1);
                }
                throw new FormatException($"XML serialization failed: {ex.Message}", ex);
            }
        }

        private string SerializeXDocument(XDocument doc)
        {
            var settings = CreateXmlWriterSettings();

            using var stringWriter = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(stringWriter, settings))
            {
                doc.Save(xmlWriter);
                xmlWriter.Flush();
            }
            return stringWriter.ToString();
        }

        private XmlWriterSettings CreateXmlWriterSettings()
        {
            return new XmlWriterSettings
            {
                Indent = NeedsPretty(),
                IndentChars = Config.GetIndentString(),
                Encoding = Config.Encoding,
                OmitXmlDeclaration = !Config.XmlIncludeDeclaration,
                NewLineHandling = Config.Minify ? NewLineHandling.None : NewLineHandling.Replace
            };
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys)
                data = SortKeysRecursively(data);

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
                return new JArray(data);

            return data;
        }

        private bool NeedsPretty() => !Config.Minify && Config.PrettyPrint;

        private string CreateXmlHeader()
        {
            if (!Config.XmlIncludeDeclaration) return "";

            var standalone = Config.XmlStandalone ? " standalone=\"yes\"" : "";
            return $"<?xml version=\"1.0\" encoding=\"{Config.Encoding.WebName}\"{standalone}?>";
        }

        private string CreateOpeningTag(string rootName)
        {
            var sb = new StringBuilder($"<{rootName}");

            if (!string.IsNullOrEmpty(Config.XmlNamespace))
            {
                if (!string.IsNullOrEmpty(Config.XmlNamespacePrefix))
                {
                    sb.Append($" xmlns:{Config.XmlNamespacePrefix}=\"{Config.XmlNamespace}\"");
                }
                else
                {
                    sb.Append($" xmlns=\"{Config.XmlNamespace}\"");
                }
            }

            sb.Append(">");
            return sb.ToString();
        }

        private void ConfigureNamespace(XElement root)
        {
            if (string.IsNullOrEmpty(Config.XmlNamespace)) return;

            var ns = XNamespace.Get(Config.XmlNamespace);
            if (!string.IsNullOrEmpty(Config.XmlNamespacePrefix))
            {
                root.Add(new XAttribute(XNamespace.Xmlns + Config.XmlNamespacePrefix, Config.XmlNamespace));
                root.Name = ns + root.Name.LocalName;
            }
            else
            {
                root.Add(new XAttribute("xmlns", Config.XmlNamespace));
                root.Name = ns + root.Name.LocalName;
            }
        }

        private string SerializeTokenToXmlContent(JToken token, string elementName)
        {
            var element = new XElement(SanitizeElementName(elementName));
            ConvertObjectToXElement(token, element);

            var settings = CreateXmlWriterSettings();
            settings.OmitXmlDeclaration = true;

            using var stringWriter = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(stringWriter, settings))
            {
                element.Save(xmlWriter);
                xmlWriter.Flush();
            }
            return stringWriter.ToString();
        }

        private string SerializePropertyToXml(JProperty property)
        {
            if (property.Name.StartsWith("@"))
                return "";

            return SerializeTokenToXmlContent(property.Value, property.Name);
        }

        private string CreateErrorXml(string errorMessage, int count)
        {
            var errorElement = new XElement("error",
                new XElement("message", errorMessage),
                new XElement("count", count),
                new XElement("timestamp", DateTime.UtcNow.ToString("O"))
            );

            var settings = CreateXmlWriterSettings();
            using var stringWriter = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(stringWriter, settings))
            {
                errorElement.Save(xmlWriter);
                xmlWriter.Flush();
            }
            return stringWriter.ToString();
        }

        private string CreateErrorXmlElement(string elementName, string errorMessage)
        {
            return $"<{elementName}><!-- Error: {errorMessage} --></{elementName}>";
        }

        private void ConvertObjectToXElement(object obj, XElement parent)
        {
            if (obj == null) return;

            if (obj is JValue jValue)
            {
                var textValue = FormatValue(jValue.Value);
                if (Config.XmlUseCData && ContainsSpecialCharacters(textValue))
                    parent.Add(new XCData(textValue));
                else
                    parent.Value = textValue;
            }
            else if (obj is JObject jObject)
            {
                var properties = Config.SortKeys ? jObject.Properties().OrderBy(p => p.Name) : jObject.Properties();

                foreach (var property in properties)
                {
                    if (property.Name.StartsWith("@"))
                    {
                        var attrName = property.Name.Substring(1);
                        parent.SetAttributeValue(attrName, FormatValue(property.Value));
                    }
                    else if (property.Name == "#text")
                    {
                        var textValue = FormatValue(property.Value);
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
                        var element = new XElement(SanitizeElementName(property.Name));
                        ConvertObjectToXElement(property.Value, element);
                        parent.Add(element);
                    }
                }
            }
            else if (obj is JArray jArray)
            {
                foreach (var item in jArray)
                {
                    var itemElement = new XElement("item");
                    ConvertObjectToXElement(item, itemElement);
                    parent.Add(itemElement);
                }
            }
            else
            {
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
                            parent.Add(new XCData(textValue));
                        else
                            parent.Value = textValue;
                        break;
                }
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

        private JToken SortKeysRecursively(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Object => SortJObject((JObject)token),
                JTokenType.Array => new JArray(((JArray)token).Select(SortKeysRecursively)),
                _ => token
            };
        }

        private JObject SortJObject(JObject obj)
        {
            var sorted = new JObject();
            foreach (var property in obj.Properties().OrderBy(p => p.Name))
            {
                sorted[property.Name] = SortKeysRecursively(property.Value);
            }
            return sorted;
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

        private static bool ContainsSpecialCharacters(string text)
        {
            return text.Contains('<') || text.Contains('>') || text.Contains('&') ||
                   text.Contains('\n') || text.Contains('\r');
        }

        private static string SanitizeElementName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "element";

            if (char.IsDigit(name[0])) name = "_" + name;
            name = name.Replace(" ", "_").Replace("-", "_");

            return name;
        }
    }
}