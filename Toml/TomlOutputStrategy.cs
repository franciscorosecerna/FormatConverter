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

        private const string DefaultArrayWrapperKey = "items";

        public override string Serialize(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = PreprocessToken(data);

            try
            {
                _output.Clear();
                _writtenTables.Clear();

                if (processed is JArray arr)
                {
                    var wrapperKey = Config.TomlArrayWrapperKey ?? DefaultArrayWrapperKey;
                    processed = new JObject { [wrapperKey] = arr };

                    if (!Config.Minify)
                    {
                        _output.AppendLine($"# Note: Root array automatically wrapped under '{wrapperKey}'");
                        _output.AppendLine();
                    }
                }

                if (processed is JObject obj)
                {
                    WriteObject(obj, string.Empty, 0);
                    var result = _output.ToString().TrimEnd();

                    if (Config.StrictMode)
                    {
                        ValidateToml(result);
                    }

                    return result;
                }
                else
                {
                    throw new FormatException("TOML root must be an object/table");
                }
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                return CreateErrorToml(ex.Message, ex.GetType().Name, processed);
            }
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            var chunkSize = GetChunkSize();

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);
            ConfigureTomlWriter(writer);

            var buffer = new List<JToken>();
            var isFirst = true;

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    WriteChunkToStream(buffer, writer, cancellationToken, ref isFirst);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStream(buffer, writer, cancellationToken, ref isFirst);
            }

            writer.Flush();
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
        }

        private void WriteChunkToStream(List<JToken> items, StreamWriter writer, CancellationToken ct, ref bool isFirst)
        {
            if (items.Count == 0) return;

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (!isFirst && !Config.Minify)
                    {
                        writer.WriteLine();
                        writer.WriteLine("# ---");
                        writer.WriteLine();
                    }
                    isFirst = false;

                    _output.Clear();
                    _writtenTables.Clear();

                    var item = items[i];

                    if (item is JArray arr)
                    {
                        var wrapperKey = Config.TomlArrayWrapperKey ?? DefaultArrayWrapperKey;
                        item = new JObject { [wrapperKey] = arr };

                        if (!Config.Minify && i == 0)
                        {
                            writer.WriteLine($"# Note: Root array automatically wrapped under '{wrapperKey}'");
                            writer.WriteLine();
                        }
                    }

                    if (item is JObject obj)
                    {
                        WriteObject(obj, string.Empty, 0);
                        writer.WriteLine(_output.ToString().TrimEnd());
                    }
                    else
                    {
                        throw new FormatException($"TOML documents must be objects or arrays (got {item.Type})");
                    }
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    var errorToml = CreateErrorToml(ex.Message, ex.GetType().Name, items[i]);
                    writer.WriteLine(errorToml);
                }
            }

            writer.Flush();
        }

        private void ConfigureTomlWriter(StreamWriter writer)
            => writer.NewLine = Config.Minify ? "\n" : Environment.NewLine;

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;

        private void ValidateToml(string toml)
        {
            if (!Config.StrictMode) return;

            try
            {
                var result = Tomlyn.Toml.Parse(toml);
                if (result.HasErrors)
                {
                    var errors = string.Join(", ", result.Diagnostics.Select(d => d.ToString()));
                    throw new FormatException($"Generated TOML is invalid: {errors}");
                }
            }
            catch when (!Config.StrictMode) { }
        }

        private static string CreateErrorToml(string errorMessage, string errorType, JToken originalToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Error: {errorMessage}");
            sb.AppendLine($"# Error Type: {errorType}");
            sb.AppendLine($"# Original Type: {originalToken.Type}");
            sb.AppendLine($"# Timestamp: {DateTime.UtcNow:O}");
            sb.AppendLine();
            sb.AppendLine("# Original data (as comment):");

            var dataStr = originalToken.ToString();
            if (dataStr.Length > 500)
            {
                dataStr = string.Concat(dataStr.AsSpan(0, 500), "...");
            }

            foreach (var line in dataStr.Split('\n'))
            {
                sb.AppendLine($"# {line.TrimEnd()}");
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
                        Console.Error.WriteLine($"Warning: Skipping property '{prop.Name}' - incompatible with TOML format");
                        continue;
                    }
                    throw;
                }
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
                var newSectionPath = string.IsNullOrEmpty(sectionPath)
                    ? EscapeTomlKey(prop.Name)
                    : $"{sectionPath}.{EscapeTomlKey(prop.Name)}";

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
                var newSectionPath = string.IsNullOrEmpty(sectionPath)
                    ? EscapeTomlKey(prop.Name)
                    : $"{sectionPath}.{EscapeTomlKey(prop.Name)}";
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
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not JObject item)
                    continue;

                if (_output.Length > 0 && !_output.ToString().EndsWith("\n\n"))
                {
                    _output.AppendLine();
                }

                _output.AppendLine($"[[{sectionPath}]]");

                var itemPath = $"{sectionPath}[{i}]";

                var simpleProperties = new List<JProperty>();
                var complexProperties = new List<JProperty>();
                var nestedArrayOfTables = new List<JProperty>();

                foreach (var prop in item.Properties())
                {
                    try
                    {
                        if (prop.Value.Type == JTokenType.Array &&
                            Config.TomlArrayOfTables &&
                            IsArrayOfTables((JArray)prop.Value))
                        {
                            nestedArrayOfTables.Add(prop);
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
                            Console.Error.WriteLine($"Warning: Skipping property '{prop.Name}' - incompatible with TOML format");
                            continue;
                        }
                        throw;
                    }
                }

                foreach (var prop in simpleProperties)
                {
                    WriteKeyValue(prop.Name, prop.Value, 0);
                }

                foreach (var prop in complexProperties)
                {
                    var nestedPath = $"{sectionPath}.{EscapeTomlKey(prop.Name)}";

                    if (prop.Value.Type == JTokenType.Object)
                    {
                        if (_output.Length > 0 && !_output.ToString().EndsWith("\n\n"))
                        {
                            _output.AppendLine();
                        }
                        _output.AppendLine($"[{nestedPath}]");
                        WriteObjectProperties((JObject)prop.Value, nestedPath, 1);
                    }
                    else if (prop.Value.Type == JTokenType.Array && !Config.TomlArrayOfTables)
                    {
                        WriteKeyValue(prop.Name, prop.Value, 0);
                    }
                }

                foreach (var prop in nestedArrayOfTables)
                {
                    var nestedPath = $"{sectionPath}.{EscapeTomlKey(prop.Name)}";
                    WriteArrayOfTables(nestedPath, (JArray)prop.Value);
                }
            }
        }

        private void WriteObjectProperties(JObject obj, string sectionPath, int depth)
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
                        Console.Error.WriteLine($"Warning: Skipping property '{prop.Name}' - incompatible with TOML format");
                        continue;
                    }
                    throw;
                }
            }

            foreach (var prop in simpleProperties)
            {
                WriteKeyValue(prop.Name, prop.Value, depth);
            }

            foreach (var prop in complexProperties)
            {
                var newSectionPath = $"{sectionPath}.{EscapeTomlKey(prop.Name)}";

                if (prop.Value.Type == JTokenType.Object)
                {
                    if (_output.Length > 0 && !_output.ToString().EndsWith("\n\n"))
                    {
                        _output.AppendLine();
                    }
                    _output.AppendLine($"[{newSectionPath}]");
                    WriteObjectProperties((JObject)prop.Value, newSectionPath, depth + 1);
                }
                else if (prop.Value.Type == JTokenType.Array && !Config.TomlArrayOfTables)
                {
                    WriteKeyValue(prop.Name, prop.Value, depth);
                }
            }

            foreach (var prop in arrayOfTablesProperties)
            {
                var newSectionPath = $"{sectionPath}.{EscapeTomlKey(prop.Name)}";
                WriteArrayOfTables(newSectionPath, (JArray)prop.Value);
            }
        }

        private void WriteKeyValue(string key, JToken value, int depth)
        {
            var indent = Config.PrettyPrint ? new string(' ', depth * (Config.IndentSize ?? 2)) : string.Empty;
            var formattedKey = NeedsQuoting(key) ? $"\"{key}\"" : key;
            var formattedValue = FormatTomlValue(value);

            _output.AppendLine($"{indent}{formattedKey} = {formattedValue}");
        }

        private string? FormatTomlValue(JToken value)
        {
            return value.Type switch
            {
                JTokenType.Null => HandleNullValue(),
                JTokenType.String => FormatStringValue(value),
                JTokenType.Boolean => value.Value<bool>().ToString().ToLower(),
                JTokenType.Integer => FormatInteger(value.Value<long>()),
                JTokenType.Float => FormatFloat(value.Value<double>()),
                JTokenType.Date => FormatDateTime(value.Value<DateTime>()),
                JTokenType.Array => FormatArray((JArray)value),
                JTokenType.Object => FormatInlineTable((JObject)value),
                _ => $"\"{value}\""
            };
        }

        private string FormatStringValue(JToken value)
        {
            var str = value.Value<string>()!;

            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dateTime))
            {
                if (str.Contains('T') && (str.Contains('-') || str.Contains(':')))
                {
                    return FormatDateTime(dateTime);
                }
            }

            return FormatString(str);
        }

        private string HandleNullValue()
        {
            if (Config.TomlStrictTypes)
            {
                throw new FormatException("TOML does not support null values. Disable TomlStrictTypes to convert nulls to empty strings.");
            }
            return "\"\"";
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

        private string FormatInteger(long number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat) &&
                Config.NumberFormat.Equals("hexadecimal", StringComparison.OrdinalIgnoreCase))
            {
                return $"0x{number:X}";
            }
            return number.ToString(CultureInfo.InvariantCulture);
        }

        private string FormatFloat(double number)
        {
            if (Config.TomlStrictTypes && (double.IsNaN(number) || double.IsInfinity(number)))
            {
                throw new FormatException($"TOML does not support {(double.IsNaN(number) ? "NaN" : "Infinity")} values. Disable TomlStrictTypes to convert to string.");
            }

            if (double.IsNaN(number))
                return "\"NaN\"";
            if (double.IsPositiveInfinity(number))
                return "\"Infinity\"";
            if (double.IsNegativeInfinity(number))
                return "\"-Infinity\"";

            if (!string.IsNullOrEmpty(Config.NumberFormat) &&
                Config.NumberFormat.Equals("scientific", StringComparison.OrdinalIgnoreCase))
            {
                return number.ToString("E", CultureInfo.InvariantCulture);
            }

            return number.ToString("G", CultureInfo.InvariantCulture);
        }

        protected override string FormatDateTime(DateTime dateTime)
        {
            if (!string.IsNullOrEmpty(Config.DateFormat))
            {
                return Config.DateFormat.ToLower() switch
                {
                    "iso8601" => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    "rfc3339" => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                    _ => dateTime.ToString(Config.DateFormat, CultureInfo.InvariantCulture)
                };
            }

            return dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private string FormatArray(JArray array)
        {
            if (!array.Any()) return "[]";

            var items = array.Select(FormatTomlValue).ToArray();

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
                return $"{key} = {FormatTomlValue(p.Value)}";
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
            => array.Count > 0 && array.All(item => item.Type == JTokenType.Object);

        private static string EscapeTomlKey(string key)
            => key.Contains('.') || NeedsQuoting(key) ? $"\"{key}\"" : key;

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
    }
}