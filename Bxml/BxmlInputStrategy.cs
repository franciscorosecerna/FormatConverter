using FormatConverter.Bxml.BxmlReader;
using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Bxml
{
    public class BxmlInputStrategy : BaseInputStrategy
    {
        private const int MAX_ELEMENT_COUNT = 50000;
        private const int MAX_STRING_COUNT = 50000;
        private const int MAX_ATTRIBUTE_COUNT = 5000;
        private const int MAX_CHILD_COUNT = 5000;
        private const int MAX_STRING_LENGTH = 1000000;

        public override JToken Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("BXML input cannot be null or empty", nameof(input));
            }

            try
            {
                var bytes = DecodeInput(input);
                var token = ParseBxmlData(bytes);

                return token;
            }
            catch (Exception ex) when (ex is not FormatException && ex is not ArgumentException)
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

            var fileSize = fileStream.Length;
            var showProgress = fileSize > 10_485_760;
            int tokensProcessed = 0;

            using var reader = new BxmlStreamReader(
                fileStream,
                strictMode: Config.StrictMode,
                maxDepth: Config.MaxDepth!.Value);

            if (showProgress)
                Console.Error.Write("\rInitializing BXML stream reader...");

            reader.Initialize();

            if (showProgress)
                Console.Error.Write($"\rReading file: 100.0% - Processing {reader.ElementCount} elements...");

            string[]? stringTable = null;

            try
            {
                stringTable = reader.GetStringTable();
            }
            catch { }

            var batchSize = Math.Max(10, 8192 / 512);

            foreach (var batch in SafeEnumerate(() => reader.EnumerateBatches(batchSize, cancellationToken)))
            {
                foreach (var element in batch)
                {
                    JToken? json = null;

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        stringTable ??= reader.GetStringTable();

                        json = ConvertBxmlElementToJson(element, stringTable);
                        tokensProcessed++;

                        if (showProgress && tokensProcessed % 100 == 0)
                        {
                            var progress = (double)tokensProcessed / reader.ElementCount * 100;
                            Console.Error.Write($"\rProcessing: {progress:F1}% ({tokensProcessed} elements)");
                        }
                    }
                    catch (Exception ex) when (
                        ex is not FormatException &&
                        ex is not ArgumentException &&
                        ex is not OperationCanceledException)
                    {
                        if (Config.IgnoreErrors)
                        {
                            Console.Error.WriteLine($"\nWarning: Error processing BXML element: {ex.Message}");
                            json = CreateErrorToken(ex, $"File: {path}, Element #{tokensProcessed}");
                        }
                        else
                        {
                            throw new FormatException($"Invalid BXML element at index {tokensProcessed}: {ex.Message}", ex);
                        }
                    }

                    if (json != null)
                        yield return json;
                }
            }

            if (showProgress)
                Console.Error.WriteLine($"\rCompleted: {tokensProcessed} objects processed");
        }

        private IEnumerable<T> SafeEnumerate<T>(Func<IEnumerable<T>> enumeratorFactory)
        {
            IEnumerator<T>? enumerator = null;

            try
            {
                enumerator = enumeratorFactory().GetEnumerator();
                while (true)
                {
                    T current;
                    try
                    {
                        if (!enumerator.MoveNext())
                            yield break;

                        current = enumerator.Current;
                    }
                    catch (Exception ex)
                    {
                        if (Config.IgnoreErrors)
                        {
                            Console.Error.WriteLine($"\nWarning: BXML streaming error: {ex.Message}");
                            yield break;
                        }

                        throw new FormatException($"Invalid BXML during streaming: {ex.Message}", ex);
                    }

                    yield return current;
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }

        private JToken HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.Error.WriteLine($"Warning: BXML parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

            throw new FormatException($"Invalid BXML: {ex.Message}", ex);
        }

        private static JObject CreateErrorToken(Exception ex, string context)
        {
            var snippet = context.Length > 1000
                ? string.Concat(context.AsSpan(0, 1000), "...")
                : context;

            return new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["raw_snippet"] = snippet,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static byte[] DecodeInput(string input)
        {
            try
            {
                return Convert.FromBase64String(input);
            }
            catch (FormatException)
            {
                return ParseHexString(input);
            }
        }

        private static byte[] ParseHexString(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("-", "");

            if (hex.Length % 2 != 0)
                throw new FormatException("Invalid hex string length");

            var bytes = new byte[hex.Length / 2];
            var hexSpan = hex.AsSpan();

            for (int i = 0; i < bytes.Length; i++)
            {
                var slice = hexSpan.Slice(i * 2, 2);
                bytes[i] = (byte)((GetHexValue(slice[0]) << 4) | GetHexValue(slice[1]));
            }

            return bytes;
        }

        private static int GetHexValue(char c)
        {
            return c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'F' => c - 'A' + 10,
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => throw new FormatException($"Invalid hex character: {c}")
            };
        }

        private JToken ParseBxmlData(byte[] bxmlData)
        {
            if (bxmlData.Length < 12)
                throw new FormatException($"BXML data too short: {bxmlData.Length} bytes (minimum 12 required)");

            using var buffer = new MemoryStream(bxmlData);
            using var reader = new BinaryReader(buffer);

            ValidateSignature(reader);

            uint elementCount = reader.ReadUInt32();

            if (elementCount == 0)
                return new JObject();

            var maxElements = Config.StrictMode ? 1000 : MAX_ELEMENT_COUNT;
            if (elementCount > maxElements)
                throw new FormatException($"Element count {elementCount} exceeds maximum allowed {maxElements}");

            var elements = new List<BxmlElement>();
            for (int i = 0; i < elementCount; i++)
            {
                try
                {
                    elements.Add(ReadElement(reader, 0));
                }
                catch (Exception ex)
                {
                    throw new FormatException($"Failed to read element {i} at position {reader.BaseStream.Position}: {ex.Message}", ex);
                }
            }

            uint stringCount = reader.ReadUInt32();
            var maxStrings = Config.StrictMode ? 1000 : MAX_STRING_COUNT;
            if (stringCount > maxStrings)
                throw new FormatException($"String count {stringCount} exceeds maximum allowed {maxStrings}");

            var stringTable = ReadStringTable(reader, stringCount);

            if (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
                if (Config.IgnoreErrors)
                {
                    Console.Error.WriteLine($"\nWarning: {remaining} bytes of incomplete BXML data at end of file");
                }
                else
                {
                    throw new FormatException($"Incomplete BXML object at end of file ({remaining} bytes remaining)");
                }
            }

            if (elements.Count > 0)
                return ConvertBxmlElementToJson(elements[0], stringTable);

            return new JObject();
        }

        private static void ValidateSignature(BinaryReader reader)
        {
            var signature = reader.ReadBytes(4);
            string sigStr = Encoding.ASCII.GetString(signature);

            if (sigStr != "BXML")
                throw new FormatException($"Invalid BXML signature: '{sigStr}', expected 'BXML'");
        }

        private BxmlElement ReadElement(BinaryReader reader, int depth)
        {
            var maxDepth = Config.MaxDepth ?? (Config.StrictMode ? 10 : 100);
            if (depth > maxDepth)
                throw new FormatException($"Maximum nesting depth {maxDepth} exceeded");

            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                throw new FormatException("Unexpected end of stream while reading element");

            byte nodeType = reader.ReadByte();

            if (nodeType != 1)
                throw new FormatException($"Expected element type 1, got {nodeType} at position {reader.BaseStream.Position - 1}");

            uint nameIndex = reader.ReadUInt32();
            var element = new BxmlElement
            {
                NameIndex = nameIndex
            };

            uint attrCount = reader.ReadUInt32();
            var maxAttrs = Config.StrictMode ? 100 : MAX_ATTRIBUTE_COUNT;
            if (attrCount > maxAttrs)
                throw new FormatException($"Attribute count {attrCount} exceeds maximum allowed {maxAttrs}");

            for (int i = 0; i < attrCount; i++)
            {
                uint attrNameIndex = reader.ReadUInt32();
                uint attrValueIndex = reader.ReadUInt32();
                element.Attributes[attrNameIndex] = attrValueIndex;
            }

            byte hasText = reader.ReadByte();
            if (hasText == 1)
            {
                uint textIndex = reader.ReadUInt32();
                element.TextIndex = textIndex;
            }
            else if (hasText != 0)
            {
                throw new FormatException($"Invalid hasText flag: {hasText}, expected 0 or 1");
            }

            uint childCount = reader.ReadUInt32();
            var maxChildren = Config.StrictMode ? 100 : MAX_CHILD_COUNT;
            if (childCount > maxChildren)
                throw new FormatException($"Child count {childCount} exceeds maximum allowed {maxChildren}");

            for (int i = 0; i < childCount; i++)
            {
                element.Children.Add(ReadElement(reader, depth + 1));
            }

            return element;
        }

        private string[] ReadStringTable(BinaryReader reader, uint stringCount)
        {
            var stringTable = new string[stringCount];

            for (int i = 0; i < stringCount; i++)
            {
                if (reader.BaseStream.Position >= reader.BaseStream.Length)
                    throw new FormatException($"Unexpected end of stream while reading string {i}");

                uint stringLength = reader.ReadUInt32();
                var maxLength = Config.StrictMode ? 10000 : MAX_STRING_LENGTH;
                if (stringLength > maxLength)
                    throw new FormatException($"String {i} length {stringLength} exceeds maximum allowed {maxLength}");

                if (reader.BaseStream.Position + stringLength > reader.BaseStream.Length)
                    throw new FormatException($"String {i} extends beyond stream boundary");

                byte[] stringBytes = reader.ReadBytes((int)stringLength);
                stringTable[i] = Encoding.UTF8.GetString(stringBytes);
            }

            return stringTable;
        }

        private static JObject ConvertBxmlElementToJson(BxmlElement element, string[] stringTable)
        {
            string elementName = GetStringFromTable(element.NameIndex, stringTable, "unknown");

            if (elementName == "Root")
            {
                var rootContent = new JObject();
                foreach (var child in element.Children)
                {
                    var childJson = ConvertBxmlElementToJson(child, stringTable);
                    if (childJson is JObject childObj)
                    {
                        foreach (var prop in childObj.Properties())
                        {
                            rootContent[prop.Name] = prop.Value;
                        }
                    }
                }
                return rootContent;
            }

            var json = new JObject();
            string type = GetElementType(element, stringTable);

            switch (type)
            {
                case "object":
                    var obj = new JObject();
                    foreach (var child in element.Children)
                    {
                        var childJson = ConvertBxmlElementToJson(child, stringTable);
                        string childName = GetStringFromTable(child.NameIndex, stringTable, "unknown");

                        if (childJson is JObject childObj && childObj.Properties().Any())
                        {
                            obj[childName] = childObj.Properties().First().Value;
                        }
                        else
                        {
                            obj[childName] = childJson;
                        }
                    }
                    json[elementName] = obj;
                    break;

                case "array":
                    var array = new JArray();
                    foreach (var child in element.Children)
                    {
                        var childJson = ConvertBxmlElementToJson(child, stringTable);
                        if (childJson is JObject childObj && childObj.Properties().Any())
                        {
                            array.Add(childObj.Properties().First().Value);
                        }
                        else
                        {
                            array.Add(childJson);
                        }
                    }
                    json[elementName] = array;
                    break;

                default:
                    var value = GetElementValue(element, stringTable, type);
                    json[elementName] = value;
                    break;
            }

            return json;
        }

        private static string GetElementType(BxmlElement element, string[] stringTable)
        {
            foreach (var attr in element.Attributes)
            {
                string attrName = GetStringFromTable(attr.Key, stringTable, "");
                if (attrName == "type")
                {
                    return GetStringFromTable(attr.Value, stringTable, "string");
                }
            }
            return "string";
        }

        private static JValue GetElementValue(BxmlElement element, string[] stringTable, string type)
        {
            if (element.TextIndex.HasValue)
            {
                string value = GetStringFromTable(element.TextIndex.Value, stringTable, "");
                return ConvertValueByType(value, type);
            }

            return type == "null" ? JValue.CreateNull() : new JValue("");
        }

        private static JValue ConvertValueByType(string value, string type)
        {
            return type switch
            {
                "int" => int.TryParse(value, out var i) ? new JValue(i) : new JValue(0),
                "long" => long.TryParse(value, out var l) ? new JValue(l) : new JValue(0L),
                "float" => double.TryParse(value, out var d) ? new JValue(d) : new JValue(0.0),
                "double" => double.TryParse(value, out var db) ? new JValue(db) : new JValue(0.0),
                "bool" => bool.TryParse(value, out var b) ? new JValue(b) : new JValue(false),
                "null" => JValue.CreateNull(),
                "date" => DateTime.TryParse(value, out var dt) ? new JValue(dt) : new JValue(value),
                _ => new JValue(value)
            };
        }

        private static string GetStringFromTable(uint index, string[] stringTable, string defaultValue)
            => index < stringTable.Length ? stringTable[index] : defaultValue;
    }
}