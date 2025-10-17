using FormatConverter.Bxml.BxmlReader;
using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Bxml
{
    public class BxmlInputStrategy : BaseInputStrategy
    {
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

            using var reader = new BxmlStreamReader(
                fileStream,
                strictMode: Config.StrictMode,
                maxDepth: Config.MaxDepth!.Value);

            if (showProgress)
                Console.Error.Write("\rInitializing BXML stream reader...");

            reader.Initialize();

            if (showProgress)
                Console.Error.Write("\rReading BXML document...");

            string[] stringTable = reader.GetStringTable();

            BxmlElement? root = null;
            Exception? error = null;

            try
            {
                root = reader.ReadDocument();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (error != null)
            {
                if (Config.IgnoreErrors)
                {
                    Console.Error.WriteLine($"\nWarning: Error reading BXML document: {error.Message}");
                    yield return CreateErrorToken(error, $"File: {path}");
                    yield break;
                }
                throw new FormatException($"Invalid BXML document: {error.Message}", error);
            }

            if (showProgress)
                Console.Error.Write("\rConverting to JSON...");

            JToken? json = null;
            error = null;

            try
            {
                json = ConvertBxmlElementToJson(root!, stringTable);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (error != null)
            {
                if (Config.IgnoreErrors)
                {
                    Console.Error.WriteLine($"\nWarning: Error converting BXML to JSON: {error.Message}");
                    yield return CreateErrorToken(error, $"File: {path}");
                    yield break;
                }
                throw new FormatException($"Error converting BXML to JSON: {error.Message}", error);
            }

            yield return json!;

            if (showProgress)
                Console.Error.WriteLine("Completed");
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
            if (bxmlData.Length < 8) // BXML + version + flags + reserved = 8 bytes minimum
                throw new FormatException($"BXML data too short: {bxmlData.Length} bytes (minimum 8 required)");

            using var buffer = new MemoryStream(bxmlData);
            using var reader = new BxmlStreamReader(
                buffer,
                strictMode: Config.StrictMode,
                maxDepth: Config.MaxDepth ?? 100);

            reader.Initialize();

            var stringTable = reader.GetStringTable();
            var root = reader.ReadDocument();

            return ConvertBxmlElementToJson(root, stringTable);
        }

        private static JToken ConvertBxmlElementToJson(BxmlElement element, string[] stringTable)
        {
            string elementName = GetStringFromTable(element.NameIndex, stringTable, "unknown");
            string type = GetElementType(element, stringTable);

            // Handle root element specially
            if (elementName == "root" || elementName == "Root")
            {
                // If root has children, merge them into a single object
                if (element.Children.Count > 0)
                {
                    var rootContent = new JObject();
                    foreach (var child in element.Children)
                    {
                        string childName = GetStringFromTable(child.NameIndex, stringTable, "unknown");
                        rootContent[childName] = ConvertElementToJsonValue(child, stringTable);
                    }
                    return rootContent;
                }

                // If root has a value, return it directly
                if (element.Value != null)
                {
                    return ConvertValueToJToken(element.Value, type);
                }

                return new JObject();
            }

            return ConvertElementToJsonValue(element, stringTable);
        }

        private static JToken ConvertElementToJsonValue(BxmlElement element, string[] stringTable)
        {
            string type = GetElementType(element, stringTable);

            switch (type)
            {
                case "object":
                    var obj = new JObject();
                    foreach (var child in element.Children)
                    {
                        string childName = GetStringFromTable(child.NameIndex, stringTable, "unknown");
                        obj[childName] = ConvertElementToJsonValue(child, stringTable);
                    }
                    return obj;

                case "array":
                    var array = new JArray();
                    foreach (var child in element.Children)
                    {
                        array.Add(ConvertElementToJsonValue(child, stringTable));
                    }
                    return array;

                case "null":
                    return JValue.CreateNull();

                case "bool":
                case "boolean":
                    if (element.Value is bool b)
                        return new JValue(b);
                    if (element.Value is string s)
                        return new JValue(bool.TryParse(s, out var bv) && bv);
                    return new JValue(false);

                case "integer":
                case "int":
                case "long":
                    if (element.Value is long l)
                        return new JValue(l);
                    if (element.Value is string ss)
                        return new JValue(long.TryParse(ss, out var lv) ? lv : 0L);
                    return new JValue(0L);

                case "float":
                case "double":
                    if (element.Value is double d)
                        return new JValue(d);
                    if (element.Value is string sss)
                        return new JValue(double.TryParse(sss, out var dv) ? dv : 0.0);
                    return new JValue(0.0);

                case "string":
                default:
                    if (element.Value == null)
                        return new JValue("");
                    return new JValue(element.Value.ToString());
            }
        }

        private static JToken ConvertValueToJToken(object value, string type)
        {
            return type switch
            {
                "null" => JValue.CreateNull(),
                "bool" or "boolean" => value is bool b ? new JValue(b) : new JValue(false),
                "integer" or "int" or "long" => value is long l ? new JValue(l) : new JValue(0L),
                "float" or "double" => value is double d ? new JValue(d) : new JValue(0.0),
                _ => new JValue(value.ToString())
            };
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

        private static string GetStringFromTable(uint index, string[] stringTable, string defaultValue)
            => index < stringTable.Length ? stringTable[index] : defaultValue;
    }
}