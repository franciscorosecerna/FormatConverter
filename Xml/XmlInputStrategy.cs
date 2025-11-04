using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FormatConverter.Xml
{
    public class XmlInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            Logger.WriteTrace("Parse: Starting XML parsing");

            if (string.IsNullOrWhiteSpace(input))
            {
                Logger.WriteWarning("Parse: Input is null or empty");
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("XML input cannot be null or empty", nameof(input));
            }

            Logger.WriteDebug($"Parse: Input length: {input.Length} characters");
            var settings = CreateXmlReaderSettings();

            try
            {
                var result = ParseXmlDocument(input, settings);
                Logger.WriteSuccess("Parse: XML parsed successfully");
                return result;
            }
            catch (XmlException ex)
            {
                Logger.WriteError($"Parse: XML exception occurred - {ex.Message}");
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo($"ParseStream: Starting stream parsing for '{path}'");

            if (string.IsNullOrWhiteSpace(path))
            {
                Logger.WriteError("ParseStream: Path is null or empty");
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));
            }

            if (!File.Exists(path))
            {
                Logger.WriteError($"ParseStream: File not found at '{path}'");
                throw new FileNotFoundException("Input file not found.", path);
            }

            Logger.WriteDebug($"ParseStream: File found, size: {new FileInfo(path).Length} bytes");
            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken)
        {
            Logger.WriteTrace("ParseStreamInternal: Opening file stream");

            using var fileStream = File.OpenRead(path);
            using var streamReader = new StreamReader(fileStream, Config.Encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var xmlReader = XmlReader.Create(streamReader, CreateXmlReaderSettings());

            var fileSize = fileStream.Length;
            var showProgress = fileSize > 10_485_760;
            var elementsProcessed = 0;

            Logger.WriteDebug("ParseStreamInternal: Starting element iteration");

            while (xmlReader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (xmlReader.NodeType == XmlNodeType.Element && !xmlReader.IsEmptyElement)
                {
                    var token = ReadXmlElement(xmlReader, path);

                    if (token != null)
                    {
                        elementsProcessed++;

                        if (showProgress && elementsProcessed % 100 == 0)
                        {
                            var progress = (double)fileStream.Position / fileSize * 100;
                            Logger.WriteInfo($"Processing: {progress:F1}% ({elementsProcessed} elements)");
                        }

                        yield return token;
                    }
                }
            }

            if (showProgress)
            {
                Logger.WriteInfo($"Completed: {elementsProcessed} elements processed");
            }

            Logger.WriteSuccess($"ParseStreamInternal: Stream parsing completed. Total elements: {elementsProcessed}");
        }

        private JToken? ReadXmlElement(XmlReader xmlReader, string path)
        {
            Logger.WriteTrace($"ReadXmlElement: Reading element '{xmlReader.Name}'");

            try
            {
                if (XElement.ReadFrom(xmlReader) is XElement element)
                {
                    var result = ConvertXElementToJToken(element);
                    Logger.WriteTrace($"ReadXmlElement: Element '{element.Name.LocalName}' converted successfully");
                    return result;
                }
                Logger.WriteTrace("ReadXmlElement: No element to read");
                return null;
            }
            catch (XmlException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"XML error at line {ex.LineNumber}, " +
                                      $"position {ex.LinePosition}: {ex.Message}");
                    return CreateErrorToken(ex, xmlReader as IXmlLineInfo);
                }

                Logger.WriteError($"ReadXmlElement: Fatal XML error at line {ex.LineNumber}, position {ex.LinePosition}");
                throw new FormatException(
                    $"Invalid XML at line {ex.LineNumber}, position {ex.LinePosition}: {ex.Message}",
                    ex);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"Unexpected error in {path}: {ex.Message}");
                return CreateErrorToken(ex, path);
            }
        }

        private XmlReaderSettings CreateXmlReaderSettings()
        {
            Logger.WriteTrace("CreateXmlReaderSettings: Creating XML reader settings");

            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = !Config.PrettyPrint,
                IgnoreComments = Config.NoMetadata,
                IgnoreProcessingInstructions = Config.NoMetadata,
                DtdProcessing = Config.StrictMode ? DtdProcessing.Parse : DtdProcessing.Ignore,
                ValidationFlags = Config.StrictMode
                    ? System.Xml.Schema.XmlSchemaValidationFlags.ReportValidationWarnings
                    : System.Xml.Schema.XmlSchemaValidationFlags.None,
                MaxCharactersInDocument = 0
            };

            if (Config.MaxDepth.HasValue)
            {
                settings.MaxCharactersInDocument = Config.MaxDepth.Value * 1000;
                Logger.WriteDebug($"CreateXmlReaderSettings: MaxCharactersInDocument set to {settings.MaxCharactersInDocument}");
            }

            Logger.WriteDebug($"CreateXmlReaderSettings: StrictMode={Config.StrictMode}, PrettyPrint={Config.PrettyPrint}");
            return settings;
        }

        private JToken ParseXmlDocument(string input, XmlReaderSettings settings)
        {
            Logger.WriteTrace("ParseXmlDocument: Parsing XML document");

            var loadOptions = Config.PrettyPrint
                ? LoadOptions.PreserveWhitespace
                : LoadOptions.None;

            var doc = XDocument.Parse(input, loadOptions);

            if (doc.Root == null)
            {
                Logger.WriteError("ParseXmlDocument: XML document has no root element");
                throw new FormatException("XML document has no root element");
            }

            Logger.WriteDebug($"ParseXmlDocument: Root element '{doc.Root.Name.LocalName}' found");
            return ConvertXElementToJToken(doc.Root);
        }

        private JObject HandleParsingError(XmlException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"XML parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

            Logger.WriteError($"HandleParsingError: Fatal XML parsing error - {ex.Message}");
            throw new FormatException($"Invalid XML: {ex.Message}", ex);
        }

        private JToken ConvertXElementToJToken(XElement element)
        {
            Logger.WriteTrace($"ConvertXElementToJToken: Converting element '{element.Name.LocalName}'");

            var typeAttr = element.Attribute("type")?.Value;
            var itemTypeAttr = element.Attribute("itemType")?.Value;

            if (typeAttr == "array")
            {
                Logger.WriteTrace($"ConvertXElementToJToken: Processing array with itemType='{itemTypeAttr}'");

                if (itemTypeAttr == "empty")
                {
                    Logger.WriteTrace("ConvertXElementToJToken: Returning empty array");
                    return new JArray();
                }

                if (!element.Elements().Any())
                {
                    var text = element.Value?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        Logger.WriteTrace("ConvertXElementToJToken: Array has no elements, returning empty array");
                        return new JArray();
                    }
                }

                return ConvertArrayElement(element, itemTypeAttr);
            }

            if (element.Name.LocalName == "item" && typeAttr != null)
            {
                Logger.WriteTrace($"ConvertXElementToJToken: Processing typed item of type '{typeAttr}'");
                return ConvertTypedValue(element.Value?.Trim() ?? string.Empty, typeAttr);
            }

            var obj = new JObject();

            var attributes = element.Attributes().Where(a =>
                !a.IsNamespaceDeclaration &&
                a.Name.LocalName != "type" &&
                a.Name.LocalName != "itemType").ToList();

            if (attributes.Count != 0)
            {
                Logger.WriteTrace($"ConvertXElementToJToken: Processing {attributes.Count} attributes");
            }

            foreach (var attr in attributes)
            {
                var attrName = DecodeXmlName(attr.Name.LocalName);
                obj[$"@{attrName}"] = attr.Value;
            }

            var childElements = element.Elements().ToList();

            if (childElements.Count > 0)
            {
                Logger.WriteTrace($"ConvertXElementToJToken: Processing {childElements.Count} child elements");

                var grouped = childElements.GroupBy(e => e.Name.LocalName);

                foreach (var group in grouped)
                {
                    var propertyName = DecodeXmlName(group.Key);

                    if (group.Key == "item" && group.All(e => e.Name.LocalName == "item"))
                    {
                        Logger.WriteTrace($"ConvertXElementToJToken: Creating array from {group.Count()} item elements");
                        var array = new JArray();
                        foreach (var item in group)
                        {
                            array.Add(ConvertXElementToJToken(item));
                        }
                        return array;
                    }
                    else if (group.Count() == 1)
                    {
                        var child = group.First();
                        obj[propertyName] = ConvertXElementToJToken(child);
                    }
                    else
                    {
                        Logger.WriteTrace($"ConvertXElementToJToken: Creating array from {group.Count()} '{propertyName}' elements");
                        var array = new JArray(group.Select(ConvertXElementToJToken));
                        obj[propertyName] = array;
                    }
                }
            }

            if (childElements.Count == 0)
            {
                var text = element.Value?.Trim();

                if (!string.IsNullOrEmpty(text))
                {
                    if (typeAttr != null)
                    {
                        Logger.WriteTrace($"ConvertXElementToJToken: Converting text value with type '{typeAttr}'");
                        return ConvertTypedValue(text, typeAttr);
                    }
                    else
                    {
                        obj["#text"] = text;
                    }
                }
                else if (typeAttr == "null")
                {
                    Logger.WriteTrace("ConvertXElementToJToken: Returning null value");
                    return JValue.CreateNull();
                }
            }

            if (obj.Count == 1 && obj.ContainsKey("#text"))
            {
                Logger.WriteTrace("ConvertXElementToJToken: Simplifying object to text value");
                return obj["#text"]!;
            }

            return obj;
        }

        private static string DecodeXmlName(string encodedName)
        {
            try
            {
                return XmlConvert.DecodeName(encodedName);
            }
            catch
            {
                return encodedName;
            }
        }

        private JArray ConvertArrayElement(XElement element, string? itemType)
        {
            Logger.WriteTrace($"ConvertArrayElement: Converting array element with itemType='{itemType}'");

            var array = new JArray();
            var items = element.Elements("item");
            var itemCount = items.Count();

            Logger.WriteDebug($"ConvertArrayElement: Processing {itemCount} array items");

            foreach (var item in items)
            {
                var itemTypeAttr = item.Attribute("type")?.Value ?? itemType;

                if (itemTypeAttr != null && !item.Elements().Any())
                {
                    var value = item.Value?.Trim() ?? string.Empty;
                    array.Add(ConvertTypedValue(value, itemTypeAttr));
                }
                else
                {
                    array.Add(ConvertXElementToJToken(item));
                }
            }

            Logger.WriteTrace($"ConvertArrayElement: Array converted with {array.Count} items");
            return array;
        }

        private JToken ConvertTypedValue(string value, string type)
        {
            Logger.WriteTrace($"ConvertTypedValue: Converting value '{value}' to type '{type}'");

            if (string.IsNullOrEmpty(value) && type != "string")
            {
                Logger.WriteTrace($"ConvertTypedValue: Empty value for type '{type}'");
                return type == "null" ? JValue.CreateNull() : new JValue(string.Empty);
            }

            try
            {
                JToken result = type.ToLowerInvariant() switch
                {
                    "null" => JValue.CreateNull(),
                    "boolean" => new JValue(ParseBoolean(value)),
                    "integer" => new JValue(ParseInteger(value)),
                    "number" => new JValue(ParseNumber(value)),
                    "string" => new JValue(value),
                    "date" => new JValue(ParseDate(value)),
                    "bytes" => new JValue(value),
                    "guid" => new JValue(ParseGuid(value)),
                    "uri" => new JValue(new Uri(value)),
                    "timespan" => new JValue(TimeSpan.Parse(value)),
                    "empty" => new JArray(),
                    "mixed" => new JValue(value),
                    _ => new JValue(value)
                };

                Logger.WriteTrace($"ConvertTypedValue: Successfully converted to type '{type}'");
                return result;
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"Failed to convert '{value}' to type '{type}': {ex.Message}");
                return new JValue(value);
            }
        }

        private static bool ParseBoolean(string value)
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("True", StringComparison.Ordinal) ||
                   value == "1";
        }

        private static long ParseInteger(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                return result;

            return long.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture);
        }

        private static double ParseNumber(string value)
        {
            var normalized = value.Replace(',', '.');

            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                return result;

            return double.Parse(value, NumberStyles.Float, CultureInfo.CurrentCulture);
        }

        private static DateTime ParseDate(string value)
        {
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
                return result;

            return DateTime.Parse(value, CultureInfo.CurrentCulture, DateTimeStyles.RoundtripKind);
        }

        private static Guid ParseGuid(string value)
            => Guid.Parse(value);

        private static JObject CreateErrorToken(XmlException ex, IXmlLineInfo? lineInfo)
        {
            return new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["line"] = lineInfo?.LineNumber ?? ex.LineNumber,
                ["position"] = lineInfo?.LinePosition ?? ex.LinePosition,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static JObject CreateErrorToken(XmlException ex, string input)
        {
            var snippet = input.Length > 1000
                ? string.Concat(input.AsSpan(0, 1000), "...")
                : input;

            return new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["line"] = ex.LineNumber,
                ["position"] = ex.LinePosition,
                ["raw_snippet"] = snippet,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static JObject CreateErrorToken(Exception ex, string path)
        {
            return new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["source"] = path,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }
    }
}