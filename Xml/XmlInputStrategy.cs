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
            if (Config.UseStreaming)
            {
                var firstToken = ParseStream(input).FirstOrDefault();
                return firstToken ?? new JObject();
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

        public override IEnumerable<JToken> ParseStream(string input)
        {
            if (!Config.UseStreaming)
            {
                yield return Parse(input);
                yield break;
            }

            var bytes = Encoding.UTF8.GetBytes(input);
            var totalSize = bytes.Length;

            if (HasRepeatingRootElements(input))
            {
                foreach (var token in StreamRepeatingElements(bytes, totalSize))
                    yield return token;
            }
            else if (HasManyChildElements(input))
            {
                foreach (var chunk in StreamChildElements(bytes, totalSize))
                    yield return chunk;
            }
            else
            {
                foreach (var token in StreamSingleDocument(bytes, totalSize))
                    yield return token;
            }
        }

        private IEnumerable<JToken> StreamRepeatingElements(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var settings = CreateXmlReaderSettings();

            using var stream = new MemoryStream(bytes);
            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            using var xmlReader = XmlReader.Create(streamReader, settings);

            IEnumerable<JToken> Iterator()
            {
                var tokenBuffer = new List<JToken>();
                var currentBufferSize = 0;
                var processedBytes = 0L;

                while (xmlReader.Read())
                {
                    if (xmlReader.NodeType == XmlNodeType.Element && !xmlReader.IsEmptyElement)
                    {
                        if (XElement.ReadFrom(xmlReader) is XElement element)
                        {
                            var token = ConvertXElementToJToken(element);

                            if (Config.SortKeys)
                                token = SortKeysRecursively(token);

                            tokenBuffer.Add(token);
                            var tokenBytes = GetTokenSizeInBytes(token);
                            currentBufferSize += tokenBytes;
                            processedBytes += tokenBytes;

                            if (currentBufferSize >= bufferSize)
                            {
                                foreach (var bufferedToken in tokenBuffer)
                                    yield return bufferedToken;

                                tokenBuffer.Clear();
                                currentBufferSize = 0;

                                if (totalSize > bufferSize * 10)
                                {
                                    var progress = (double)processedBytes / totalSize * 100;
                                    if (progress % 10 < 1)
                                        Console.WriteLine($"XML repeating elements streaming progress: {progress:F1}%");
                                }
                            }
                        }
                    }
                }

                foreach (var bufferedToken in tokenBuffer)
                    yield return bufferedToken;
            }

            try
            {
                return Iterator();
            }
            catch (XmlException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> StreamChildElements(byte[] bytes, int totalSize)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var settings = CreateXmlReaderSettings();

            using var stream = new MemoryStream(bytes);
            var input = Encoding.UTF8.GetString(bytes);

            IEnumerable<JToken> Iterator()
            {
                var doc = XDocument.Parse(input, LoadOptions.PreserveWhitespace);
                if (doc.Root == null)
                    yield break;

                var maxPropertiesPerChunk = Math.Max(10, bufferSize / 1024);
                var currentChunk = new JObject();
                int propertyCount = 0;
                var currentChunkSize = 0;
                var processedBytes = 0L;

                if (doc.Root.HasAttributes)
                {
                    foreach (var attr in doc.Root.Attributes().Where(a => !a.IsNamespaceDeclaration))
                    {
                        var key = Config.XmlUseAttributes ? $"@{attr.Name.LocalName}" : attr.Name.LocalName;
                        currentChunk[key] = new JValue(ConvertValue(attr.Value));

                        var attrBytes = Encoding.UTF8.GetBytes(key).Length + Encoding.UTF8.GetBytes(attr.Value).Length;
                        currentChunkSize += attrBytes;
                        processedBytes += attrBytes;
                        propertyCount++;
                    }
                }

                var childElements = doc.Root.Elements().ToList();
                var childGroups = childElements.GroupBy(e => e.Name.LocalName);

                foreach (var group in childGroups)
                {
                    var children = group.ToList();
                    JToken groupToken;

                    if (children.Count == 1)
                    {
                        var child = children.First();
                        groupToken = child.HasElements || child.HasAttributes
                            ? ConvertXElementToJToken(child)
                            : new JValue(ConvertValue(child.Value));
                    }
                    else
                    {
                        var array = new JArray();
                        foreach (var child in children)
                        {
                            array.Add(ConvertXElementToJToken(child));
                        }
                        groupToken = array;
                    }

                    if (Config.SortKeys)
                        groupToken = SortKeysRecursively(groupToken);

                    currentChunk[group.Key] = groupToken;
                    propertyCount++;

                    var groupBytes = GetTokenSizeInBytes(groupToken);
                    currentChunkSize += groupBytes;
                    processedBytes += groupBytes;

                    if (propertyCount >= maxPropertiesPerChunk || currentChunkSize >= bufferSize)
                    {
                        yield return Config.SortKeys ? SortJObject(currentChunk) : currentChunk;

                        currentChunk = [];
                        propertyCount = 0;
                        currentChunkSize = 0;

                        if (totalSize > bufferSize * 10)
                        {
                            var progress = (double)processedBytes / totalSize * 100;
                            if (progress % 10 < 1)
                                Console.WriteLine($"XML child elements streaming progress: {progress:F1}%");
                        }
                    }
                }

                if (propertyCount > 0 || currentChunk.Count > 0)
                {
                    yield return Config.SortKeys ? SortJObject(currentChunk) : currentChunk;
                }
            }

            try
            {
                return Iterator();
            }
            catch (XmlException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> StreamSingleDocument(byte[] bytes, int totalSize)
        {
            var settings = CreateXmlReaderSettings();
            var input = Encoding.UTF8.GetString(bytes);

            IEnumerable<JToken> Iterator()
            {
                var token = ParseXmlDocument(input, settings);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                yield return token;

                if (totalSize > 1024)
                {
                    Console.WriteLine("XML single document streaming progress: 100.0%");
                }
            }

            try
            {
                return Iterator();
            }
            catch (XmlException ex)
            {
                return HandleStreamingError(ex, Encoding.UTF8.GetString(bytes));
            }
        }

        private IEnumerable<JToken> HandleStreamingError(XmlException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: XML streaming error ignored: {ex.Message}");
                return [HandleParsingError(ex, input)];
            }
            else
            {
                throw new FormatException($"Invalid XML during streaming: {ex.Message}", ex);
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
                ValidationFlags = Config.StrictMode ? System.Xml.Schema.XmlSchemaValidationFlags.ReportValidationWarnings : System.Xml.Schema.XmlSchemaValidationFlags.None
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

        private bool HasRepeatingRootElements(string input)
        {
            var settings = CreateXmlReaderSettings();

            using var reader = XmlReader.Create(new StringReader(input), settings);

            string? firstName = null;
            int count = 0;

            while (reader.Read() && count < 3)
            {
                if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
                {
                    if (firstName == null)
                    {
                        firstName = reader.LocalName;
                    }
                    else if (reader.LocalName == firstName)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                    count++;
                }
            }

            return false;
        }

        private static bool HasManyChildElements(string input)
        {
            try
            {
                var doc = XDocument.Parse(input);
                return doc.Root?.Elements().Count() > 20;
            }
            catch
            {
                return false;
            }
        }

        private static int GetTokenSizeInBytes(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => Encoding.UTF8.GetByteCount(token.ToString()),
                JTokenType.Integer => Encoding.UTF8.GetByteCount(token.ToString()),
                JTokenType.Float => Encoding.UTF8.GetByteCount(token.ToString()),
                JTokenType.Boolean => token.ToString().Length,
                JTokenType.Null => 4,
                JTokenType.Date => Encoding.UTF8.GetByteCount(token.ToString()),
                JTokenType.Object => GetObjectSizeInBytes((JObject)token),
                JTokenType.Array => GetArraySizeInBytes((JArray)token),
                _ => Encoding.UTF8.GetByteCount(token.ToString())
            };
        }

        private static int GetObjectSizeInBytes(JObject obj)
        {
            var totalSize = 2;
            foreach (var property in obj.Properties())
            {
                totalSize += Encoding.UTF8.GetByteCount($"\"{property.Name}\":");
                totalSize += GetTokenSizeInBytes(property.Value);
                totalSize += 1;
            }
            return totalSize;
        }

        private static int GetArraySizeInBytes(JArray array)
        {
            var totalSize = 2;
            foreach (var item in array)
            {
                totalSize += GetTokenSizeInBytes(item);
                totalSize += 1;
            }
            return totalSize;
        }

        private JObject HandleParsingError(XmlException ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: XML parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000 ? string.Concat(input.AsSpan(0, 1000), "...") : input
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
                if (children.Count == 1)
                {
                    var child = children.First();
                    if (child.HasElements || child.HasAttributes)
                    {
                        result[child.Name.LocalName] = ConvertXElementToJToken(child);
                    }
                    else
                    {
                        result[child.Name.LocalName] = new JValue(ConvertValue(child.Value));
                    }
                }
                else
                {
                    var array = new JArray();
                    foreach (var child in children)
                    {
                        array.Add(ConvertXElementToJToken(child));
                    }
                    result[group.Key] = array;
                }
            }

            if (!element.HasElements && !element.HasAttributes && !string.IsNullOrWhiteSpace(element.Value))
            {
                return new JValue(ConvertValue(element.Value));
            }

            if (!element.HasElements && element.HasAttributes && !string.IsNullOrWhiteSpace(element.Value))
            {
                result["#text"] = new JValue(ConvertValue(element.Value));
            }

            return result.Count == 1 && result["#text"] != null ? result["#text"] : result;
        }

        private static object ConvertValue(string value)
        {
            if (bool.TryParse(value, out bool boolVal)) return boolVal;
            if (int.TryParse(value, out int intVal)) return intVal;
            if (double.TryParse(value, out double doubleVal)) return doubleVal;
            if (DateTime.TryParse(value, out DateTime dateVal)) return dateVal;

            return value;
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
    }
}