using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text;

namespace FormatConverter.Toml
{
    public class TomlOutputStrategy : BaseOutputStrategy
    {
        private readonly StringBuilder _output = new();
        private readonly HashSet<string> _writtenTables = new();

        public override string Serialize(JToken data)
        {
            if (Config.UseStreaming)
            {
                return string.Join("", SerializeStream([data]));
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

            if (processed.Type == JTokenType.Array)
            {
                return StreamArray((JArray)processed);
            }
            else if (processed.Type == JTokenType.Object)
            {
                return StreamObject((JObject)processed);
            }
            else
            {
                return [SerializeRegular(processed)];
            }
        }

        private IEnumerable<string> StreamArray(JArray array)
        {
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var buffer = new List<JToken>();
            var currentBufferSize = 0;
            var totalProcessed = 0;
            var totalItems = array.Count;

            foreach (var item in array)
            {
                if (item.Type != JTokenType.Object)
                {
                    yield return SerializeRegular(item);
                    continue;
                }

                var serializedItem = SerializeTomlObject((JObject)item, string.Empty);
                var itemSizeInBytes = Encoding.UTF8.GetByteCount(serializedItem);

                if (buffer.Count > 0 && currentBufferSize + itemSizeInBytes > bufferSize)
                {
                    yield return SerializeBufferedItems(buffer);
                    buffer.Clear();
                    currentBufferSize = 0;

                    if (totalItems > 0 && totalProcessed % Math.Max(1, totalItems / 10) == 0)
                    {
                        var progress = (double)totalProcessed / totalItems * 100;
                        Console.WriteLine($"Serialization progress: {progress:F1}%");
                    }
                }

                buffer.Add(item);
                currentBufferSize += itemSizeInBytes;
                totalProcessed++;
            }

            if (buffer.Count > 0)
            {
                yield return SerializeBufferedItems(buffer);
            }
        }

        private IEnumerable<string> StreamObject(JObject obj)
        {
            var properties = obj.Properties().ToList();
            var bufferSize = Config.BufferSize > 0 ? Config.BufferSize : 4096;
            var maxPropertiesPerChunk = Math.Max(5, bufferSize / 1024);

            var currentChunk = new JObject();
            var processedCount = 0;
            var totalProperties = properties.Count;

            foreach (var prop in properties)
            {
                currentChunk[prop.Name] = prop.Value;
                processedCount++;

                if (processedCount >= maxPropertiesPerChunk)
                {
                    yield return SerializeRegular(currentChunk);
                    currentChunk = new JObject();
                    processedCount = 0;

                    var progress = (double)processedCount / totalProperties * 100;
                    if (progress % 10 < 1)
                    {
                        Console.WriteLine($"Object streaming progress: {progress:F1}%");
                    }
                }
            }

            if (processedCount > 0)
            {
                yield return SerializeRegular(currentChunk);
            }
        }

        private string SerializeBufferedItems(List<JToken> items)
        {
            var sb = new StringBuilder();

            foreach (var item in items)
            {
                if (item.Type == JTokenType.Object)
                {
                    sb.Append(SerializeTomlObject((JObject)item, string.Empty));
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private string SerializeRegular(JToken data)
        {
            var processed = ProcessDataBeforeSerialization(data);

            try
            {
                _output.Clear();
                _writtenTables.Clear();

                if (processed is JObject obj)
                {
                    return SerializeTomlObject(obj, string.Empty);
                }
                else
                {
                    throw new FormatException("TOML root must be an object/table");
                }
            }
            catch (Exception ex)
            {
                return HandleSerializationError(ex, processed);
            }
        }

        private string SerializeTomlObject(JObject obj, string sectionPath)
        {
            _output.Clear();
            _writtenTables.Clear();
            WriteObject(obj, sectionPath, 0);
            return _output.ToString().TrimEnd();
        }

        private JToken ProcessDataBeforeSerialization(JToken data)
        {
            if (Config.SortKeys)
                data = SortKeysRecursively(data);

            if (Config.ArrayWrap && data.Type != JTokenType.Array)
                return new JArray(data);

            return data;
        }

        private string HandleSerializationError(Exception ex, JToken data)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: TOML serialization error ignored: {ex.Message}");
                return CreateErrorToml(ex.Message, data);
            }
            throw new FormatException($"TOML serialization failed: {ex.Message}", ex);
        }

        private static string CreateErrorToml(string errorMessage, JToken data)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Error serializing to TOML: {errorMessage}");
            sb.AppendLine($"# Timestamp: {DateTime.UtcNow:O}");
            sb.AppendLine();
            sb.AppendLine("# Original data (as comment):");

            var dataStr = data.ToString();
            if (dataStr.Length > 500)
            {
                dataStr = string.Concat(dataStr.AsSpan(0, 500), "...");
            }

            foreach (var line in dataStr.Split('\n'))
            {
                sb.AppendLine($"# {line}");
            }

            return sb.ToString();
        }

        private void WriteObject(JObject obj, string sectionPath, int depth)
        {
            var simpleProperties = new List<JProperty>();
            var complexProperties = new List<JProperty>();
            var arrayOfTablesProperties = new List<JProperty>();

            foreach (var prop in obj.Properties())
            {
                try
                {
                    if (prop.Value.Type == JTokenType.Array &&
                        Config.TomlArrayOfTables &&
                        IsArrayOfTables((JArray)prop.Value))
                    {
                        arrayOfTablesProperties.Add(prop);
                    }
                    else if (IsSimpleValue(prop.Value))
                    {
                        simpleProperties.Add(prop);
                    }
                    else
                    {
                        complexProperties.Add(prop);
                    }
                }
                catch (FormatException) when (!Config.TomlStrictTypes)
                {
                    if (Config.IgnoreErrors)
                    {
                        Console.WriteLine($"Warning: Skipping property '{prop.Name}' - incompatible with TOML format");
                        continue;
                    }
                    throw;
                }
            }

            if (Config.SortKeys)
            {
                simpleProperties = simpleProperties.OrderBy(p => p.Name).ToList();
                complexProperties = complexProperties.OrderBy(p => p.Name).ToList();
                arrayOfTablesProperties = arrayOfTablesProperties.OrderBy(p => p.Name).ToList();
            }

            foreach (var prop in simpleProperties)
            {
                WriteKeyValue(prop.Name, prop.Value, depth);
            }

            if (simpleProperties.Count != 0 && (complexProperties.Count != 0 || arrayOfTablesProperties.Count != 0))
            {
                _output.AppendLine();
            }

            foreach (var prop in complexProperties)
            {
                var newSectionPath = string.IsNullOrEmpty(sectionPath) ? prop.Name : $"{sectionPath}.{prop.Name}";

                if (prop.Value.Type == JTokenType.Object)
                {
                    WriteTable(newSectionPath, (JObject)prop.Value, depth);
                }
                else if (prop.Value.Type == JTokenType.Array && !Config.TomlArrayOfTables)
                {
                    WriteKeyValue(prop.Name, prop.Value, depth);
                }
            }

            foreach (var prop in arrayOfTablesProperties)
            {
                var newSectionPath = string.IsNullOrEmpty(sectionPath) ? prop.Name : $"{sectionPath}.{prop.Name}";
                WriteArrayOfTables(newSectionPath, (JArray)prop.Value);
            }
        }

        private void WriteTable(string sectionPath, JObject obj, int depth)
        {
            if (_writtenTables.Contains(sectionPath))
                return;

            _writtenTables.Add(sectionPath);

            if (_output.Length > 0 && !_output.ToString().EndsWith("\n\n"))
            {
                _output.AppendLine();
            }

            _output.AppendLine($"[{sectionPath}]");
            WriteObject(obj, sectionPath, depth + 1);
        }

        private void WriteArrayOfTables(string sectionPath, JArray array)
        {
            foreach (JObject item in array.Cast<JObject>())
            {
                if (_output.Length > 0 && !_output.ToString().EndsWith("\n\n"))
                {
                    _output.AppendLine();
                }

                _output.AppendLine($"[[{sectionPath}]]");
                WriteObject(item, sectionPath, 0);
            }
        }

        private void WriteKeyValue(string key, JToken value, int depth)
        {
            var indent = Config.PrettyPrint ? new string(' ', depth * (Config.IndentSize ?? 2)) : string.Empty;
            var formattedKey = NeedsQuoting(key) ? $"\"{key}\"" : key;
            var formattedValue = FormatValue(value);

            _output.AppendLine($"{indent}{formattedKey} = {formattedValue}");
        }

        private string? FormatValue(JToken value)
        {
            return value.Type switch
            {
                JTokenType.String => FormatString(value.Value<string>()),
                JTokenType.Boolean => value.Value<bool>().ToString().ToLower(),
                JTokenType.Integer => FormatNumber(value.Value<long>()),
                JTokenType.Float => FormatNumber(value.Value<double>()),
                JTokenType.Date => FormatDateTime(value.Value<DateTime>()),
                JTokenType.Array => FormatArray((JArray)value),
                JTokenType.Object => FormatInlineTable((JObject)value),
                JTokenType.Null => Config.TomlStrictTypes ?
                    throw new FormatException("TOML does not support null values. Disable TomlStrictTypes to ignore nulls.") :
                    "\"\"",
                _ => $"\"{value}\""
            };
        }

        private string FormatString(string str)
        {
            if (string.IsNullOrEmpty(str)) return "\"\"";

            if (Config.TomlMultilineStrings && (str.Contains('\n') || str.Length > 80))
            {
                return $"\"\"\"\n{str}\n\"\"\"";
            }

            return $"\"{EscapeString(str)}\"";
        }

        private string? FormatNumber(object number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" when number is long l => $"0x{l:X}",
                    "scientific" when number is double d => d.ToString("E", CultureInfo.InvariantCulture),
                    _ => number.ToString()
                };
            }
            return number.ToString();
        }

        private string FormatDateTime(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    _ => dateTime.ToString(Config.DateFormat)
                };
            }

            return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        }

        private string FormatArray(JArray array)
        {
            if (!array.Any()) return "[]";

            var items = array.Select(FormatValue).ToArray();

            if (array.All(item => IsSimpleArrayItem(item)))
            {
                if (Config.Minify)
                {
                    return "[" + string.Join(",", items) + "]";
                }
                else
                {
                    return "[" + string.Join(", ", items) + "]";
                }
            }

            var indent = new string(' ', (Config.IndentSize ?? 2));
            return "[\n" + indent + string.Join(",\n" + indent, items) + "\n]";
        }

        private static bool IsSimpleArrayItem(JToken item)
        {
            return item.Type switch
            {
                JTokenType.String or JTokenType.Boolean or JTokenType.Integer
                or JTokenType.Float or JTokenType.Date => true,
                _ => false
            };
        }

        private string FormatInlineTable(JObject obj)
        {
            if (!obj.Properties().Any()) return "{}";

            var properties = Config.SortKeys
                ? obj.Properties().OrderBy(p => p.Name)
                : obj.Properties();

            var pairs = properties.Select(p =>
            {
                var key = NeedsQuoting(p.Name) ? $"\"{p.Name}\"" : p.Name;
                return $"{key} = {FormatValue(p.Value)}";
            });

            return "{ " + string.Join(", ", pairs) + " }";
        }

        private bool IsSimpleValue(JToken value)
        {
            return value.Type switch
            {
                JTokenType.String or JTokenType.Boolean or JTokenType.Integer
                or JTokenType.Float or JTokenType.Date => true,
                JTokenType.Array when !Config.TomlArrayOfTables => true,
                JTokenType.Array when Config.TomlArrayOfTables => !IsArrayOfTables((JArray)value),
                JTokenType.Object => false,
                JTokenType.Null when Config.TomlStrictTypes => throw new FormatException("TOML does not support null values"),
                JTokenType.Null => true,
                _ => false
            };
        }

        private static bool IsArrayOfTables(JArray array)
        {
            return array.All(item => item.Type == JTokenType.Object);
        }

        private static bool NeedsQuoting(string key)
        {
            return key.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-') ||
                   key.Length == 0 ||
                   char.IsDigit(key[0]);
        }

        private static string EscapeString(string str)
        {
            return str.Replace("\\", "\\\\")
                     .Replace("\"", "\\\"")
                     .Replace("\n", "\\n")
                     .Replace("\r", "\\r")
                     .Replace("\t", "\\t");
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