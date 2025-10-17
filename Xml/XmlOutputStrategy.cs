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
        private XNamespace _currentNamespace = XNamespace.None;

        public override string Serialize(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = PreprocessToken(data);

            try
            {
                return SerializeToXml(processed);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                return CreateErrorXml(ex.Message);
            }
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            var needsPretty = NeedsPretty();
            var chunkSize = GetChunkSize();
            var rootName = SanitizeElementName(Config.XmlRootElement ?? "root");
            var settings = CreateXmlWriterSettings();

            _currentNamespace = string.IsNullOrEmpty(Config.XmlNamespace)
                ? XNamespace.None
                : XNamespace.Get(Config.XmlNamespace);

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);

            if (Config.XmlIncludeDeclaration)
            {
                var standalone = Config.XmlStandalone ? " standalone=\"yes\"" : "";
                writer.Write($"<?xml version=\"1.0\" encoding=\"{Config.Encoding.WebName}\"{standalone}?>");
                if (needsPretty) writer.Write("\n");
            }

            writer.Write(CreateOpeningTag(rootName));
            if (needsPretty) writer.Write("\n");

            var buffer = new List<JToken>();

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    WriteChunkToStream(buffer, writer, settings, cancellationToken);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStream(buffer, writer, settings, cancellationToken);
            }

            if (needsPretty) writer.Write("\n");
            writer.Write($"</{rootName}>");

            writer.Flush();
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
        }

        private void WriteChunkToStream(List<JToken> items, StreamWriter writer, XmlWriterSettings settings, CancellationToken ct)
        {
            if (items.Count == 0) return;

            var needsPretty = NeedsPretty();
            var indent = needsPretty ? new string(' ', Config.IndentSize ?? 2) : "";

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (needsPretty) writer.Write(indent);

                try
                {
                    var itemXml = SerializeTokenToXml(items[i], "item", settings);
                    writer.Write(itemXml);
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    var errorXml = CreateErrorElement($"item_{i}", ex.Message, ex.GetType().Name);
                    writer.Write(errorXml);
                }

                if (needsPretty) writer.Write("\n");
            }

            writer.Flush();
        }

        private string SerializeToXml(JToken data)
        {
            var rootName = SanitizeElementName(Config.XmlRootElement ?? "root");

            _currentNamespace = string.IsNullOrEmpty(Config.XmlNamespace)
                ? XNamespace.None
                : XNamespace.Get(Config.XmlNamespace);

            var root = new XElement(_currentNamespace + rootName);

            ConfigureNamespace(root);
            ConvertJTokenToXElement(data, root);

            var result = CreateXmlDocument(root);

            if (Config.StrictMode)
            {
                ValidateXml(result);
            }

            return result;
        }

        private void ValidateXml(string xml)
        {
            try
            {
                var doc = XDocument.Parse(xml);

                foreach (var element in doc.Descendants())
                {
                    try
                    {
                        XmlConvert.VerifyName(element.Name.LocalName);
                    }
                    catch (XmlException ex)
                    {
                        throw new InvalidOperationException(
                            $"Invalid XML element name '{element.Name.LocalName}': {ex.Message}", ex);
                    }
                }

                foreach (var attribute in doc.Descendants().SelectMany(e => e.Attributes()))
                {
                    try
                    {
                        XmlConvert.VerifyName(attribute.Name.LocalName);
                    }
                    catch (XmlException ex)
                    {
                        throw new InvalidOperationException(
                            $"Invalid XML attribute name '{attribute.Name.LocalName}': {ex.Message}", ex);
                    }
                }
            }
            catch when (!Config.StrictMode) { }
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
            var element = new XElement(_currentNamespace + SanitizeElementName(elementName));
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
                    var childElement = new XElement(_currentNamespace + SanitizeElementName(prop.Name));
                    ConvertJTokenToXElement(prop.Value, childElement);
                    parent.Add(childElement);
                    break;

                default:
                    if (token is JValue jValue)
                    {
                        AddValueToElement(parent, jValue.Value!);
                    }
                    break;
            }
        }

        private void ConvertJObjectToXElement(JObject jObject, XElement parent)
        {
            foreach (var property in jObject.Properties())
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
                else if (Config.XmlUseAttributes && IsSimpleValue(property.Value))
                {
                    var attrValue = FormatValue(property.Value);
                    parent.SetAttributeValue(SanitizeElementName(property.Name), attrValue);
                }
                else
                {
                    var element = new XElement(_currentNamespace + SanitizeElementName(property.Name));

                    if (property.Value.Type == JTokenType.Array)
                    {
                        element.SetAttributeValue("type", "array");

                        var arrayType = DetectArrayType((JArray)property.Value);
                        if (!string.IsNullOrEmpty(arrayType))
                        {
                            element.SetAttributeValue("itemType", arrayType);
                        }
                    }
                    else if (IsSimpleValue(property.Value))
                    {
                        var valueType = GetValueType(property.Value);
                        if (!string.IsNullOrEmpty(valueType))
                        {
                            element.SetAttributeValue("type", valueType);
                        }
                    }

                    ConvertJTokenToXElement(property.Value, element);
                    parent.Add(element);
                }
            }
        }

        private void ConvertJArrayToXElement(JArray jArray, XElement parent)
        {
            foreach (var item in jArray)
            {
                var itemElement = new XElement(_currentNamespace + "item");

                if (IsSimpleValue(item))
                {
                    var itemType = GetValueType(item);
                    if (!string.IsNullOrEmpty(itemType))
                    {
                        itemElement.SetAttributeValue("type", itemType);
                    }
                }

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

        private static string? DetectArrayType(JArray array)
        {
            if (array.Count == 0) return "empty";

            var firstType = array[0].Type;
            bool isHomogeneous = array.All(item => item.Type == firstType);

            if (!isHomogeneous) return "mixed";

            return firstType switch
            {
                JTokenType.String => "string",
                JTokenType.Integer => "integer",
                JTokenType.Float => "number",
                JTokenType.Boolean => "boolean",
                JTokenType.Object => "object",
                JTokenType.Array => "array",
                JTokenType.Null => "null",
                _ => null
            };
        }

        private static string? GetValueType(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => "string",
                JTokenType.Integer => "integer",
                JTokenType.Float => "number",
                JTokenType.Boolean => "boolean",
                JTokenType.Null => "null",
                JTokenType.Date => "date",
                _ => null
            };
        }

        private static bool IsSimpleValue(JToken token)
        {
            return token.Type == JTokenType.String ||
                   token.Type == JTokenType.Integer ||
                   token.Type == JTokenType.Float ||
                   token.Type == JTokenType.Boolean ||
                   token.Type == JTokenType.Null ||
                   token.Type == JTokenType.Date;
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

            if (!string.IsNullOrEmpty(Config.XmlNamespacePrefix))
            {
                root.Add(new XAttribute(XNamespace.Xmlns + Config.XmlNamespacePrefix, Config.XmlNamespace));
            }
            else
            {
                root.Add(new XAttribute("xmlns", Config.XmlNamespace));
            }
        }

        private string CreateErrorElement(string elementName, string errorMessage, string errorType)
        {
            var element = new XElement(
                _currentNamespace + SanitizeElementName(elementName),
                new XAttribute("error", errorMessage),
                new XAttribute("error_type", errorType),
                new XAttribute("timestamp", DateTime.UtcNow.ToString("o"))
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
            var errorElement = new XElement(_currentNamespace + "error",
                new XElement("message", errorMessage),
                new XElement("timestamp", DateTime.UtcNow.ToString("o"))
            );

            return CreateXmlDocument(errorElement);
        }

        private static bool ContainsXmlSpecialCharacters(string text)
            => text.IndexOfAny(['<', '>', '&', '"', '\'']) >= 0;

        private static string EscapeCDataContent(string text)
            => text.Replace("]]>", "]]]]><![CDATA[>");

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