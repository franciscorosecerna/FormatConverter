using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
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
                            Console.Error.Write($"\rProcessing: {progress:F1}% ({elementsProcessed} elements)");
                        }

                        yield return token;
                    }
                }
            }

            if (showProgress)
            {
                Console.Error.WriteLine($"\rCompleted: {elementsProcessed} elements processed");
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
                    Console.Error.WriteLine($"Warning: XML error at line {ex.LineNumber}, " +
                                          $"position {ex.LinePosition}: {ex.Message}");
                    return CreateErrorToken(ex, xmlReader as IXmlLineInfo);
                }

                throw new FormatException(
                    $"Invalid XML at line {ex.LineNumber}, position {ex.LinePosition}: {ex.Message}",
                    ex);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Console.Error.WriteLine($"Warning: Unexpected error in {path}: {ex.Message}");
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
                Console.Error.WriteLine($"Warning: XML parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

            throw new FormatException($"Invalid XML: {ex.Message}", ex);
        }

        private JObject ConvertXElementToJToken(XElement element)
        {
            var obj = new JObject();

            foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                obj[$"@{attr.Name.LocalName}"] = attr.Value;
            }

            var childElements = element.Elements().ToList();

            if (childElements.Count > 0)
            {
                var grouped = childElements.GroupBy(e => e.Name.LocalName);

                foreach (var group in grouped)
                {
                    if (group.Count() == 1)
                    {
                        obj[group.Key] = ConvertXElementToJToken(group.First());
                    }
                    else
                    {
                        var array = new JArray(group.Select(ConvertXElementToJToken));
                        obj[group.Key] = array;
                    }
                }
            }

            if (childElements.Count == 0)
            {
                var text = element.Value?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    obj["#text"] = text;
                }
            }

            return obj;
        }

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