using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FormatConverter.Xml
{
    public class XmlOutputStrategy : BaseOutputStrategy
    {
        private const int DEFAULT_CHUNK_SIZE = 100;

        public override string Serialize(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = ProcessDataBeforeSerialization(data);

            try
            {
                return SerializeToXml(processed);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                return CreateErrorXml(ex.Message);
            }
        }

        public override IEnumerable<string> SerializeStream(IEnumerable<JToken> data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            foreach (var chunk in StreamTokens(data))
                yield return chunk;
        }

        private IEnumerable<string> StreamTokens(IEnumerable<JToken> tokens)
        {
            var chunkSize = GetChunkSize();
            var needsPretty = NeedsPretty();
            var buffer = new List<JToken>();
            var firstChunk = true;
            var rootName = SanitizeElementName(Config.XmlRootElement ?? "root");
            var settings = CreateXmlWriterSettings();

            yield return CreateXmlHeader();
            yield return CreateOpeningTag(rootName);
            if (needsPretty) yield return "\n";

            foreach (var token in tokens)
            {
                buffer.Add(token);

                if (buffer.Count >= chunkSize)
                {
                    yield return SerializeChunk(buffer, !firstChunk, settings);
                    buffer.Clear();
                    firstChunk = false;
                }
            }

            if (buffer.Count > 0)
            {
                yield return SerializeChunk(buffer, !firstChunk, settings);
            }

            if (needsPretty) yield return "\n";
            yield return $"</{rootName}>";
        }

        private string SerializeChunk(List<JToken> items, bool includeNewline, XmlWriterSettings settings)
        {
            if (items.Count == 0) return string.Empty;

            var chunkBuilder = new StringBuilder();
            var needsPretty = NeedsPretty();
            var indent = needsPretty ? new string(' ', Config.IndentSize ?? 2) : "";

            for (int i = 0; i < items.Count; i++)
            {
                if ((includeNewline || i > 0) && needsPretty)
                {
                    chunkBuilder.Append('\n');
                }

                if (needsPretty) chunkBuilder.Append(indent);

                try
                {
                    var itemXml = SerializeTokenToXml(items[i], "item", settings);
                    chunkBuilder.Append(itemXml);
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    var errorXml = CreateSafeErrorXmlElement($"item_{i}", ex.Message);
                    chunkBuilder.Append(errorXml);
                }
            }

            return chunkBuilder.ToString();
        }

        private string SerializeToXml(JToken data)
        {
            var rootName = SanitizeElementName(Config.XmlRootElement ?? "root");
            var root = new XElement(rootName);

            ConfigureNamespace(root);
            ConvertJTokenToXElement(data, root);

            return CreateXmlDocument(root);
        }

        private string CreateXmlDocument(XElement root)
        {
            var doc = new XDocument();

            if (Config.XmlIncludeDeclaration)
            {
                doc.Declaration = new XDeclaration(
                    "1.0",
                    Config.Encoding.WebName,
                    Config.XmlStandalone ? "yes" : null
                );
            }

            doc.Add(root);

            var settings = CreateXmlWriterSettings();
            using var stringWriter = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(stringWriter, settings))
            {
                doc.Save(xmlWriter);
                xmlWriter.Flush();
            }

            return stringWriter.ToString();
        }

        private string SerializeTokenToXml(JToken token, string elementName, XmlWriterSettings settings)
        {
            var element = new XElement(SanitizeElementName(elementName));
            ConvertJTokenToXElement(token, element);

            var writerSettings = settings ?? CreateXmlWriterSettings();
            writerSettings.OmitXmlDeclaration = true;

            using var stringWriter = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(stringWriter, writerSettings))
            {
                element.WriteTo(xmlWriter);
                xmlWriter.Flush();
            }

            return stringWriter.ToString();
        }

        private void ConvertJTokenToXElement(JToken token, XElement parent)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    ConvertJObjectToXElement((JObject)token, parent);
                    break;

                case JTokenType.Array:
                    ConvertJArrayToXElement((JArray)token, parent);
                    break;

                case JTokenType.Property:
                    var prop = (JProperty)token;
                    var childElement = new XElement(SanitizeElementName(prop.Name));
                    ConvertJTokenToXElement(prop.Value, childElement);
                    parent.Add(childElement);
                    break;

                default:
                    if (token is JValue jValue)
                    {
                        AddValueToElement(parent, jValue.Value);
                    }
                    break;
            }
        }

        private void ConvertJObjectToXElement(JObject jObject, XElement parent)
        {
            var properties = Config.SortKeys
                ? jObject.Properties().OrderBy(p => p.Name)
                : jObject.Properties();

            foreach (var property in properties)
            {
                if (property.Name.StartsWith("@"))
                {
                    var attrName = SanitizeElementName(property.Name.Substring(1));
                    var attrValue = FormatValue(property.Value);
                    parent.SetAttributeValue(attrName, attrValue);
                }
                else if (property.Name == "#text")
                {
                    AddValueToElement(parent, property.Value);
                }
                else
                {
                    var element = new XElement(SanitizeElementName(property.Name));
                    ConvertJTokenToXElement(property.Value, element);
                    parent.Add(element);
                }
            }
        }

        private void ConvertJArrayToXElement(JArray jArray, XElement parent)
        {
            foreach (var item in jArray)
            {
                var itemElement = new XElement("item");
                ConvertJTokenToXElement(item, itemElement);
                parent.Add(itemElement);
            }
        }

        private void AddValueToElement(XElement parent, object value)
        {
            if (value == null) return;

            var textValue = FormatValue(value);

            if (Config.XmlUseCData && ContainsXmlSpecialCharacters(textValue))
            {
                parent.Add(new XCData(EscapeCDataContent(textValue)));
            }
            else
            {
                parent.Value = textValue;
            }
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            var result = data;

            if (Config.SortKeys)
                result = SortKeysRecursively(result);

            if (Config.ArrayWrap && result.Type != JTokenType.Array)
                result = new JArray(result);

            return result;
        }

        private bool NeedsPretty() => !Config.Minify && Config.PrettyPrint;

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : DEFAULT_CHUNK_SIZE;

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

        private string CreateXmlHeader()
        {
            if (!Config.XmlIncludeDeclaration) return string.Empty;

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
                    sb.Append($" xmlns:{Config.XmlNamespacePrefix}=\"{SecurityElement.Escape(Config.XmlNamespace)}\"");
                }
                else
                {
                    sb.Append($" xmlns=\"{SecurityElement.Escape(Config.XmlNamespace)}\"");
                }
            }

            sb.Append('>');
            return sb.ToString();
        }

        private void ConfigureNamespace(XElement root)
        {
            if (string.IsNullOrEmpty(Config.XmlNamespace)) return;

            var ns = XNamespace.Get(Config.XmlNamespace);

            if (!string.IsNullOrEmpty(Config.XmlNamespacePrefix))
            {
                root.Add(new XAttribute(XNamespace.Xmlns + Config.XmlNamespacePrefix, Config.XmlNamespace));
            }
            else
            {
                root.Add(new XAttribute("xmlns", Config.XmlNamespace));
            }

            root.Name = ns + root.Name.LocalName;
        }

        private string CreateSafeErrorXmlElement(string elementName, string errorMessage)
        {
            var element = new XElement(
                SanitizeElementName(elementName),
                new XAttribute("error", errorMessage)
            );

            using var stringWriter = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                element.WriteTo(xmlWriter);
            }

            return stringWriter.ToString();
        }

        private string CreateErrorXml(string errorMessage)
        {
            var errorElement = new XElement("error",
                new XElement("message", errorMessage),
                new XElement("timestamp", FormatDateTime(DateTime.UtcNow))
            );

            return CreateXmlDocument(errorElement);
        }

        private static bool ContainsXmlSpecialCharacters(string text)
            => text.IndexOfAny(['<', '>', '&', '"', '\'']) >= 0;

        private static string EscapeCDataContent(string text)
        {
            return text.Replace("]]>", "]]]]><![CDATA[>");
        }

        private static string SanitizeElementName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "element";

            try
            {
                var encoded = XmlConvert.EncodeLocalName(name);

                if (encoded.Length > 0 && char.IsDigit(encoded[0]))
                    encoded = "_" + encoded;

                return encoded;
            }
            catch
            {
                var sanitized = Regex.Replace(name, @"[^\w\.-]", "_");

                if (string.IsNullOrEmpty(sanitized) || char.IsDigit(sanitized[0]))
                    sanitized = "_" + sanitized;

                return string.IsNullOrEmpty(sanitized) ? "element" : sanitized;
            }
        }
    }
}