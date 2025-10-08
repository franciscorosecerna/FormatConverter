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
                    : throw new ArgumentException("XML input cannot be null or empty");
            }

            var settings = CreateXmlReaderSettings();

            try
            {
                var token = ParseXmlDocument(input, settings);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                return token;
            }
            catch (XmlException ex)
            {
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Input file not found.", path);

            return ParseStreamInternal(path);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path)
        {
            FileStream? fileStream = null;
            StreamReader? streamReader = null;
            XmlReader? xmlReader = null;

            try
            {
                var settings = CreateXmlReaderSettings();

                fileStream = File.OpenRead(path);
                streamReader = new StreamReader(fileStream, Config.Encoding ?? Encoding.UTF8, true);
                xmlReader = XmlReader.Create(streamReader, settings);

                while (xmlReader.Read())
                {
                    if (xmlReader.NodeType == XmlNodeType.Element && !xmlReader.IsEmptyElement)
                    {
                        var token = ReadXmlElement(xmlReader, path);

                        if (token != null)
                            yield return token;
                    }
                }
            }
            finally
            {
                xmlReader?.Close();
                streamReader?.Dispose();
                fileStream?.Dispose();
            }
        }

        private JToken? ReadXmlElement(XmlReader xmlReader, string path)
        {
            try
            {
                if (XElement.ReadFrom(xmlReader) is XElement element)
                {
                    var token = ConvertXElementToJToken(element);

                    if (Config.SortKeys)
                        token = SortKeysRecursively(token);

                    return token;
                }
                return null;
            }
            catch (XmlException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: XML element read error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["file"] = path
                    };
                }

                throw new FormatException($"Invalid XML element at line {ex.LineNumber}, " +
                    $"position {ex.LinePosition}: {ex.Message}", ex);
            }
        }

        private XmlReaderSettings CreateXmlReaderSettings()
        {
            return new XmlReaderSettings
            {
                IgnoreWhitespace = !Config.PrettyPrint,
                IgnoreComments = Config.NoMetadata,
                IgnoreProcessingInstructions = Config.NoMetadata,
                DtdProcessing = Config.StrictMode ? DtdProcessing.Parse : DtdProcessing.Ignore,
                ValidationFlags = Config.StrictMode
                    ? System.Xml.Schema.XmlSchemaValidationFlags.ReportValidationWarnings
                    : System.Xml.Schema.XmlSchemaValidationFlags.None
            };
        }

        private JToken ParseXmlDocument(string input, XmlReaderSettings settings)
        {
            var doc = XDocument.Parse(input, LoadOptions.PreserveWhitespace);

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
                Console.WriteLine($"Warning: XML parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000
                        ? string.Concat(input.AsSpan(0, 1000), "...")
                        : input
                };
            }
            throw new FormatException($"Invalid XML: {ex.Message}", ex);
        }

        private JToken ConvertXElementToJToken(XElement element)
        {
            var result = new JObject();

            if (element.HasAttributes)
            {
                foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                {
                    var key = Config.XmlUseAttributes ? $"@{attr.Name.LocalName}" : attr.Name.LocalName;
                    result[key] = new JValue(ConvertValue(attr.Value));
                }
            }

            var childElements = element.Elements().ToList();
            var childGroups = childElements.GroupBy(e => e.Name.LocalName);

            foreach (var group in childGroups)
            {
                var children = group.ToList();
                result[group.Key] = CreateGroupToken(children);
            }

            if (!element.HasElements && !element.HasAttributes && !string.IsNullOrWhiteSpace(element.Value))
            {
                return new JValue(ConvertValue(element.Value));
            }

            if (!element.HasElements && element.HasAttributes && !string.IsNullOrWhiteSpace(element.Value))
            {
                result["#text"] = new JValue(ConvertValue(element.Value));
            }

            return result.Count == 1 && result["#text"] != null ? result["#text"]! : result;
        }

        private JToken CreateGroupToken(List<XElement> children)
        {
            if (children.Count == 1)
            {
                var child = children.First();
                return child.HasElements || child.HasAttributes
                    ? ConvertXElementToJToken(child)
                    : new JValue(ConvertValue(child.Value));
            }

            var array = new JArray();
            foreach (var child in children)
            {
                array.Add(ConvertXElementToJToken(child));
            }
            return array;
        }

        private static object ConvertValue(string value)
        {
            if (bool.TryParse(value, out bool boolVal)) return boolVal;
            if (int.TryParse(value, out int intVal)) return intVal;
            if (double.TryParse(value, out double doubleVal)) return doubleVal;
            if (DateTime.TryParse(value, out DateTime dateVal)) return dateVal;

            return value;
        }
    }
}