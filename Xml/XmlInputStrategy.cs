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
            if (string.IsNullOrWhiteSpace(input))
            {
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("XML input cannot be null or empty", nameof(input));
            }

            var settings = CreateXmlReaderSettings();

            try
            {
                return ParseXmlDocument(input, settings);
            }
            catch (XmlException ex)
            {
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Input file not found.", path);

            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken)
        {
            using var fileStream = File.OpenRead(path);
            using var streamReader = new StreamReader(fileStream, Config.Encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var xmlReader = XmlReader.Create(streamReader, CreateXmlReaderSettings());

            var fileSize = fileStream.Length;
            var showProgress = fileSize > 10_485_760;
            var elementsProcessed = 0;

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
        }

        private JToken? ReadXmlElement(XmlReader xmlReader, string path)
        {
            try
            {
                if (XElement.ReadFrom(xmlReader) is XElement element)
                {
                    return ConvertXElementToJToken(element);
                }
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
            }

            return settings;
        }

        private JToken ParseXmlDocument(string input, XmlReaderSettings settings)
        {
            var loadOptions = Config.PrettyPrint
                ? LoadOptions.PreserveWhitespace
                : LoadOptions.None;

            var doc = XDocument.Parse(input, loadOptions);

            if (doc.Root == null)
            {
                throw new FormatException("XML document has no root element");
            }

            return ConvertXElementToJToken(doc.Root);
        }

        private JObject HandleParsingError(XmlException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"XML parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

            throw new FormatException($"Invalid XML: {ex.Message}", ex);
        }

        private JToken ConvertXElementToJToken(XElement element)
        {
            var typeAttr = element.Attribute("type")?.Value;
            var itemTypeAttr = element.Attribute("itemType")?.Value;

            if (typeAttr == "array")
            {
                if (itemTypeAttr == "empty")
                {
                    return new JArray();
                }

                if (!element.Elements().Any())
                {
                    var text = element.Value?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        return new JArray();
                    }
                }

                return ConvertArrayElement(element, itemTypeAttr);
            }

            if (element.Name.LocalName == "item" && typeAttr != null)
            {
                return ConvertTypedValue(element.Value?.Trim() ?? string.Empty, typeAttr);
            }

            var obj = new JObject();

            foreach (var attr in element.Attributes().Where(a =>
                !a.IsNamespaceDeclaration &&
                a.Name.LocalName != "type" &&
                a.Name.LocalName != "itemType"))
            {
                var attrName = DecodeXmlName(attr.Name.LocalName);
                obj[$"@{attrName}"] = attr.Value;
            }

            var childElements = element.Elements().ToList();

            if (childElements.Count > 0)
            {
                var grouped = childElements.GroupBy(e => e.Name.LocalName);

                foreach (var group in grouped)
                {
                    var propertyName = DecodeXmlName(group.Key);

                    if (group.Key == "item" && group.All(e => e.Name.LocalName == "item"))
                    {
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
                        return ConvertTypedValue(text, typeAttr);
                    }
                    else
                    {
                        obj["#text"] = text;
                    }
                }
                else if (typeAttr == "null")
                {
                    return JValue.CreateNull();
                }
            }

            if (obj.Count == 1 && obj.ContainsKey("#text"))
            {
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
            var array = new JArray();
            var items = element.Elements("item");

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

            return array;
        }

        private JToken ConvertTypedValue(string value, string type)
        {
            if (string.IsNullOrEmpty(value) && type != "string")
            {
                return type == "null" ? JValue.CreateNull() : new JValue(string.Empty);
            }

            try
            {
                return type.ToLowerInvariant() switch
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