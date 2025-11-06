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
            ArgumentNullException.ThrowIfNull(data);
            Logger.WriteDebug(() => "Starting XML serialization");

            var processed = PreprocessToken(data);

            try
            {
                var result = SerializeToXml(processed);
                Logger.WriteInfo(() => "XML serialization completed successfully");
                return result;
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning(() => $"XML serialization error ignored: {ex.Message}");
                return CreateErrorXml(ex.Message);
            }
            catch (Exception ex)
            {
                Logger.WriteError(() => $"Critical XML serialization error: {ex.Message}");
                throw;
            }
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(output);

            Logger.WriteInfo(() => "Starting XML stream serialization");
            var needsPretty = NeedsPretty();
            var chunkSize = GetChunkSize();
            Logger.WriteDebug(() => $"Using chunk size: {chunkSize}, pretty print: {needsPretty}");

            var rootName = SanitizeElementName(Config.XmlRootElement ?? "root");
            var settings = CreateXmlWriterSettings();

            _currentNamespace = string.IsNullOrEmpty(Config.XmlNamespace)
                ? XNamespace.None
                : XNamespace.Get(Config.XmlNamespace);

            if (!string.IsNullOrEmpty(Config.XmlNamespace))
            {
                Logger.WriteDebug(() => $"Using XML namespace: {Config.XmlNamespace}");
            }

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);

            if (Config.XmlIncludeDeclaration)
            {
                var standalone = Config.XmlStandalone ? " standalone=\"yes\"" : "";
                writer.Write($"<?xml version=\"1.0\" encoding=\"{Config.Encoding.WebName}\"{standalone}?>");
                if (needsPretty) writer.Write("\n");
                Logger.WriteTrace(() => "XML declaration written");
            }

            writer.Write(CreateOpeningTag(rootName));
            if (needsPretty) writer.Write("\n");
            Logger.WriteTrace(() => $"Opening root tag written: {rootName}");

            var buffer = new List<JToken>();
            int totalProcessed = 0;

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    Logger.WriteTrace(() => $"Writing chunk of {buffer.Count} items to stream");
                    WriteChunkToStream(buffer, writer, settings, cancellationToken);
                    totalProcessed += buffer.Count;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.WriteTrace(() => $"Writing final chunk of {buffer.Count} items to stream");
                WriteChunkToStream(buffer, writer, settings, cancellationToken);
                totalProcessed += buffer.Count;
            }

            if (needsPretty) writer.Write("\n");
            writer.Write($"</{rootName}>");
            Logger.WriteTrace(() => $"Closing root tag written: {rootName}");

            writer.Flush();
            Logger.WriteInfo(() => $"XML stream serialization completed. Total items processed: {totalProcessed}");
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                Logger.WriteError(() => "Output path is null or empty");
                throw new ArgumentNullException(nameof(outputPath));
            }

            Logger.WriteInfo(() => $"Serializing XML stream to file: {outputPath}");
            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
            Logger.WriteInfo(() => $"XML file created successfully: {outputPath}");
        }

        private void WriteChunkToStream(List<JToken> items, StreamWriter writer, XmlWriterSettings settings, CancellationToken ct)
        {
            if (items.Count == 0) return;

            Logger.WriteTrace(() => $"Processing chunk with {items.Count} items");
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
                    Logger.WriteTrace(() => $"Item {i + 1}/{items.Count} serialized successfully");
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => $"XML serialization error in item {i}: {ex.Message}");
                    var errorXml = CreateErrorElement($"item_{i}", ex.Message, ex.GetType().Name);
                    writer.Write(errorXml);
                }
                catch (Exception ex)
                {
                    Logger.WriteError(() => $"Critical XML serialization error at item {i}: {ex.Message}");
                    throw;
                }

                if (needsPretty) writer.Write("\n");
            }

            writer.Flush();
        }

        private string SerializeToXml(JToken data)
        {
            Logger.WriteTrace(() => "Creating XML document structure");
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
                Logger.WriteDebug(() => "Validating XML in strict mode");
                ValidateXml(result);
            }

            return result;
        }

        private void ValidateXml(string xml)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                Logger.WriteTrace(() => "XML document parsed for validation");

                foreach (var element in doc.Descendants())
                {
                    try
                    {
                        XmlConvert.VerifyName(element.Name.LocalName);
                    }
                    catch (XmlException ex)
                    {
                        Logger.WriteError(() => $"Invalid XML element name '{element.Name.LocalName}': {ex.Message}");
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
                        Logger.WriteError(() => $"Invalid XML attribute name '{attribute.Name.LocalName}': {ex.Message}");
                        throw new InvalidOperationException(
                            $"Invalid XML attribute name '{attribute.Name.LocalName}': {ex.Message}", ex);
                    }
                }

                Logger.WriteDebug(() => "XML validation passed");
            }
            catch (Exception ex) when (!Config.StrictMode)
            {
                Logger.WriteWarning(() => $"XML validation failed (ignored): {ex.Message}");
            }
        }

        private string CreateXmlDocument(XElement root)
        {
            Logger.WriteTrace(() => "Creating XML document with declaration");
            var doc = new XDocument();

            if (Config.XmlIncludeDeclaration)
            {
                doc.Declaration = new XDeclaration(
                    "1.0",
                    Config.Encoding.WebName,
                    Config.XmlStandalone ? "yes" : null
                );
                Logger.WriteTrace(() => $"XML declaration added: {Config.Encoding.WebName}");
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
            Logger.WriteTrace(() => $"Serializing token to XML element: {elementName}");
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
            Logger.WriteTrace(() => $"Converting JObject with {jObject.Properties().Count()} properties to XML");

            foreach (var property in jObject.Properties())
            {
                if (property.Name.StartsWith("@"))
                {
                    var attrName = SanitizeElementName(property.Name.Substring(1));
                    var attrValue = GetValueAsString(property.Value);
                    parent.SetAttributeValue(attrName, attrValue);
                    Logger.WriteTrace(() => $"Attribute added: {attrName}");
                }
                else if (property.Name == "#text")
                {
                    AddValueToElement(parent, property.Value);
                }
                else if (Config.XmlUseAttributes && IsSimpleValue(property.Value))
                {
                    var attrValue = GetValueAsString(property.Value);
                    parent.SetAttributeValue(SanitizeElementName(property.Name), attrValue);
                    Logger.WriteTrace(() => $"Simple value as attribute: {property.Name}");
                }
                else
                {
                    var element = new XElement(_currentNamespace + SanitizeElementName(property.Name));

                    if (property.Value.Type != JTokenType.Array && IsSimpleValue(property.Value))
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
            Logger.WriteTrace(() => $"Converting JArray with {jArray.Count} items to XML");
            parent.SetAttributeValue("type", "array");

            if (jArray.Count == 0)
            {
                parent.SetAttributeValue("itemType", "empty");
                return;
            }

            var arrayType = DetectArrayType(jArray);
            if (!string.IsNullOrEmpty(arrayType))
            {
                parent.SetAttributeValue("itemType", arrayType);
                Logger.WriteTrace(() => $"Array type detected: {arrayType}");
            }

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

            var textValue = GetValueAsString(value);

            if (Config.XmlUseCData && ContainsXmlSpecialCharacters(textValue))
            {
                Logger.WriteTrace(() => "Using CDATA for value with special characters");
                parent.Add(new XCData(EscapeCDataContent(textValue)));
            }
            else
            {
                parent.Value = textValue;
            }
        }

        private static string GetValueAsString(object value)
        {
            if (value is JToken token)
            {
                return token.Type == JTokenType.String
                    ? token.Value<string>() ?? string.Empty
                    : token.ToString();
            }

            return value?.ToString() ?? string.Empty;
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
            Logger.WriteTrace(() => "Creating XML writer settings");
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
                    Logger.WriteTrace(() => $"Namespace with prefix added: {Config.XmlNamespacePrefix}");
                }
                else
                {
                    sb.Append($" xmlns=\"{SecurityElement.Escape(Config.XmlNamespace)}\"");
                    Logger.WriteTrace(() => "Default namespace added");
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
                Logger.WriteTrace(() => $"Namespace configured with prefix: {Config.XmlNamespacePrefix}");
            }
            else
            {
                root.Add(new XAttribute("xmlns", Config.XmlNamespace));
                Logger.WriteTrace(() => "Default namespace configured");
            }
        }

        private string CreateErrorElement(string elementName, string errorMessage, string errorType)
        {
            Logger.WriteDebug(() => $"Creating error element for {errorType}");
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
            Logger.WriteDebug(() => "Creating error XML document");
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
                var sanitized = Regex.Replace(name, @"[^\w\.-]", "_", RegexOptions.Compiled);

                if (string.IsNullOrEmpty(sanitized) || char.IsDigit(sanitized[0]))
                    sanitized = "_" + sanitized;

                return string.IsNullOrEmpty(sanitized) ? "element" : sanitized;
            }
        }
    }
}