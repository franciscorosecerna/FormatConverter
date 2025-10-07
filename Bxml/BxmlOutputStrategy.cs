using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Bxml
{
    public class BxmlOutputStrategy : BaseOutputStrategy
    {
        private const int MAX_STRING_TABLE_SIZE = 100000;

        public override string Serialize(JToken data)
        {
            if (Config.UseStreaming)
            {
                var allBytes = SerializeStream([data])
                    .SelectMany(chunk => Convert.FromBase64String(chunk))
                    .ToArray();
                return FormatOutput(allBytes);
            }

            return SerializeRegular(data);
        }

        public override IEnumerable<string> SerializeStream(IEnumerable<JToken> data)
        {
            if (!Config.UseStreaming)
            {
                yield return Serialize(new JArray(data));
                yield break;
            }

            foreach (var token in data)
            {
                foreach (var chunk in StreamToken(token))
                {
                    yield return chunk;
                }
            }
        }

        private IEnumerable<string> StreamToken(JToken token)
        {
            var processed = ProcessDataBeforeSerialization(token);

            return processed.Type switch
            {
                JTokenType.Array => StreamChunked(((JArray)processed).Children()),
                JTokenType.Object => StreamChunkedObject((JObject)processed),
                _ => StreamSingle(processed)
            };
        }

        private IEnumerable<string> StreamChunked(IEnumerable<JToken> items)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var buffer = new List<JToken>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var itemCount = items is ICollection<JToken> collection ? collection.Count : -1;

            foreach (var item in items)
            {
                var estimatedSize = EstimateTokenSize(item);

                if (buffer.Count > 0 && currentBufferSize + estimatedSize > bufferSize)
                {
                    yield return SerializeChunk(buffer);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (itemCount > 0 && totalProcessed % Math.Max(1, itemCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / itemCount * 100;
                        Console.WriteLine($"Serialization progress: {progress:F1}%");
                    }
                }

                buffer.Add(item);
                currentBufferSize += estimatedSize;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeChunk(buffer);
            }
        }

        private IEnumerable<string> StreamChunkedObject(JObject obj)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var buffer = new List<JProperty>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var properties = Config.SortKeys
                ? obj.Properties().OrderBy(p => p.Name)
                : obj.Properties();
            var propCount = obj.Properties().Count();

            foreach (var prop in properties)
            {
                if (Config.NoMetadata && IsMetadataField(prop.Name))
                    continue;

                var estimatedSize = EstimateTokenSize(prop.Value) + prop.Name.Length;

                if (buffer.Count > 0 && currentBufferSize + estimatedSize > bufferSize)
                {
                    yield return SerializeChunkProperties(buffer);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (totalProcessed % Math.Max(1, propCount / 10) == 0)
                    {
                        var progress = (double)totalProcessed / propCount * 100;
                        Console.WriteLine($"Serialization progress: {progress:F1}%");
                    }
                }

                buffer.Add(prop);
                currentBufferSize += estimatedSize;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeChunkProperties(buffer);
            }
        }

        private string SerializeChunk(List<JToken> items)
        {
            try
            {
                var arrayWrapper = new JArray(items);
                var bytes = SerializeToBxml("ChunkArray", arrayWrapper);
                return FormatOutput(bytes);
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return FormatOutput(CreateErrorBxml(ex.Message, items.Count));
                }
                else
                {
                    throw new FormatException($"Error serializing chunk: {ex.Message}", ex);
                }
            }
        }

        private string SerializeChunkProperties(List<JProperty> properties)
        {
            try
            {
                var objWrapper = new JObject();
                foreach (var prop in properties)
                {
                    objWrapper[prop.Name] = prop.Value;
                }
                var bytes = SerializeToBxml("ChunkObject", objWrapper);
                return FormatOutput(bytes);
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return FormatOutput(CreateErrorBxml(ex.Message, properties.Count));
                }
                else
                {
                    throw new FormatException($"Error serializing properties chunk: {ex.Message}", ex);
                }
            }
        }

        private IEnumerable<string> StreamSingle(JToken token)
        {
            IEnumerable<string> Iterator()
            {
                var bytes = SerializeToBxml("Root", token);
                yield return FormatOutput(bytes);
            }

            try
            {
                return Iterator();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return [FormatOutput(CreateErrorBxml(ex.Message, 1))];
                }
                else
                {
                    throw new FormatException($"Error serializing single token: {ex.Message}", ex);
                }
            }
        }

        private string SerializeRegular(JToken data)
        {
            try
            {
                var processed = ProcessDataBeforeSerialization(data);
                var bytes = SerializeToBxml("Root", processed);
                return FormatOutput(bytes);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                throw new FormatException($"BXML serialization failed: {ex.Message}", ex);
            }
        }

        private byte[] SerializeToBxml(string rootName, JToken data)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);

            writer.Write(Encoding.ASCII.GetBytes("BXML"));

            var elements = new List<BxmlElement>();
            var stringTable = new Dictionary<string, uint>();
            uint stringIndex = 0;

            uint AddString(string s)
            {
                if (string.IsNullOrEmpty(s)) s = "";

                if (!stringTable.TryGetValue(s, out uint value))
                {
                    if (stringTable.Count >= MAX_STRING_TABLE_SIZE)
                    {
                        throw new FormatException($"String table size limit {MAX_STRING_TABLE_SIZE} exceeded");
                    }

                    value = stringIndex++;
                    stringTable[s] = value;
                }
                return value;
            }

            var rootElement = ConvertJsonToBxmlElement(rootName, data, AddString);
            elements.Add(rootElement);

            writer.Write((uint)elements.Count);

            foreach (var element in elements)
            {
                WriteElement(writer, element);
            }

            WriteStringTable(writer, stringTable);

            return buffer.ToArray();
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys)
                data = SortKeysRecursively(data);

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
                return new JArray(data);

            return data;
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

        private static int EstimateTokenSize(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => token.ToString().Length + 20,
                JTokenType.Integer => 30,
                JTokenType.Float => 30,
                JTokenType.Boolean => 20,
                JTokenType.Null => 20,
                JTokenType.Date => 40,
                JTokenType.Object => ((JObject)token).Properties().Sum(p => EstimateTokenSize(p.Value) + p.Name.Length + 20),
                JTokenType.Array => ((JArray)token).Sum(EstimateTokenSize) + 20,
                _ => 50
            };
        }

        private BxmlElement ConvertJsonToBxmlElement(string name, JToken token, Func<string, uint> addString)
        {
            var element = new BxmlElement
            {
                NameIndex = addString(name)
            };

            switch (token.Type)
            {
                case JTokenType.Object:
                    ConvertObjectToElement(element, (JObject)token, addString);
                    break;

                case JTokenType.Array:
                    ConvertArrayToElement(element, (JArray)token, addString);
                    break;

                case JTokenType.String:
                    ConvertStringToElement(element, token.Value<string>(), addString);
                    break;

                case JTokenType.Integer:
                    ConvertIntegerToElement(element, token.Value<long>(), addString);
                    break;

                case JTokenType.Float:
                    ConvertFloatToElement(element, token.Value<double>(), addString);
                    break;

                case JTokenType.Boolean:
                    ConvertBooleanToElement(element, token.Value<bool>(), addString);
                    break;

                case JTokenType.Date:
                    ConvertDateToElement(element, token.Value<DateTime>(), addString);
                    break;

                case JTokenType.Null:
                    element.Attributes[addString("type")] = addString("null");
                    break;

                default:
                    ConvertStringToElement(element, token.ToString(), addString);
                    break;
            }

            return element;
        }

        private void ConvertObjectToElement(BxmlElement element, JObject obj, Func<string, uint> addString)
        {
            element.Attributes[addString("type")] = addString("object");

            var properties = Config.SortKeys
                ? obj.Properties().OrderBy(p => p.Name)
                : obj.Properties();

            foreach (var property in properties)
            {
                if (Config.NoMetadata && IsMetadataField(property.Name))
                    continue;

                var child = ConvertJsonToBxmlElement(property.Name, property.Value, addString);
                element.Children.Add(child);
            }
        }

        private void ConvertArrayToElement(BxmlElement element, JArray array, Func<string, uint> addString)
        {
            element.Attributes[addString("type")] = addString("array");

            foreach (var item in array)
            {
                var child = ConvertJsonToBxmlElement("item", item, addString);
                element.Children.Add(child);
            }
        }

        private static void ConvertStringToElement(BxmlElement element, string? value, Func<string, uint> addString)
        {
            element.Attributes[addString("type")] = addString("string");
            element.TextIndex = addString(value ?? "");
        }

        private void ConvertIntegerToElement(BxmlElement element, long value, Func<string, uint> addString)
        {
            var formattedValue = FormatIntegerValue(value);
            var typeString = DetermineIntegerType(value);

            element.Attributes[addString("type")] = addString(typeString);
            element.TextIndex = addString(formattedValue);
        }

        private void ConvertFloatToElement(BxmlElement element, double value, Func<string, uint> addString)
        {
            var formattedValue = FormatNumberValue(value);
            element.Attributes[addString("type")] = addString("float");
            element.TextIndex = addString(formattedValue);
        }

        private static void ConvertBooleanToElement(BxmlElement element, bool value, Func<string, uint> addString)
        {
            element.Attributes[addString("type")] = addString("bool");
            element.TextIndex = addString(value.ToString().ToLowerInvariant());
        }

        private void ConvertDateToElement(BxmlElement element, DateTime value, Func<string, uint> addString)
        {
            var formattedValue = FormatDateTime(value);
            element.Attributes[addString("type")] = addString("date");
            element.TextIndex = addString(formattedValue);
        }

        private static bool IsMetadataField(string fieldName)
        {
            return fieldName.StartsWith("_") ||
                   fieldName.StartsWith("@") ||
                   fieldName.StartsWith("$") ||
                   fieldName.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("version", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("schema", StringComparison.OrdinalIgnoreCase);
        }

        private static string DetermineIntegerType(long value)
        {
            if (value >= int.MinValue && value <= int.MaxValue)
                return "int";
            return "long";
        }

        private string FormatIntegerValue(long value)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{value:X}",
                    "scientific" => ((double)value).ToString("E"),
                    _ => value.ToString()
                };
            }
            return value.ToString();
        }

        private string FormatNumberValue(double value)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{(long)value:X}",
                    "scientific" => value.ToString("E"),
                    _ => value.ToString()
                };
            }
            return value.ToString();
        }

        private string FormatDateTime(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "unix" => ((DateTimeOffset)dateTime).ToUnixTimeSeconds().ToString(),
                    "rfc3339" => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }

            return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        }

        private void WriteElement(BinaryWriter writer, BxmlElement element)
        {
            writer.Write((byte)1);
            writer.Write(element.NameIndex);
            writer.Write((uint)element.Attributes.Count);

            var attributes = Config.SortKeys
                ? element.Attributes.OrderBy(kvp => kvp.Key)
                : element.Attributes.AsEnumerable();

            foreach (var attr in attributes)
            {
                writer.Write(attr.Key);
                writer.Write(attr.Value);
            }

            if (element.TextIndex.HasValue)
            {
                writer.Write((byte)1);
                writer.Write(element.TextIndex.Value);
            }
            else
            {
                writer.Write((byte)0);
            }

            writer.Write((uint)element.Children.Count);
            foreach (var child in element.Children)
            {
                WriteElement(writer, child);
            }
        }

        private static void WriteStringTable(BinaryWriter writer, Dictionary<string, uint> stringTable)
        {
            writer.Write((uint)stringTable.Count);

            var sortedStrings = stringTable.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key).ToArray();

            foreach (var str in sortedStrings)
            {
                byte[] strBytes = Encoding.UTF8.GetBytes(str);
                writer.Write((uint)strBytes.Length);
                writer.Write(strBytes);
            }
        }

        private string FormatOutput(byte[] bytes)
        {
            return Config.NumberFormat?.ToLower() switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
                "binary" => FormatAsBinary(bytes),
                _ => Convert.ToBase64String(bytes)
            };
        }

        private string FormatAsHex(byte[] bytes)
        {
            var hex = Convert.ToHexString(bytes);

            if (Config.PrettyPrint && !Config.Minify)
            {
                return string.Join(" ",
                    Enumerable.Range(0, hex.Length / 2)
                    .Select(i => hex.Substring(i * 2, 2)));
            }

            return hex.ToLowerInvariant();
        }

        private string FormatAsBinary(byte[] bytes)
        {
            if (Config.PrettyPrint && !Config.Minify)
            {
                return string.Join(" ", bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
            }

            return string.Concat(bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        }

        private byte[] CreateErrorBxml(string errorMessage, int count)
        {
            var errorObj = new JObject
            {
                ["error"] = errorMessage,
                ["itemCount"] = count,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            return SerializeToBxml("Error", errorObj);
        }
    }
}