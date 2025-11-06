using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text;

namespace FormatConverter.Toml
{
    public class TomlOutputStrategy : BaseOutputStrategy
    {
        private readonly StringBuilder _output = new();
        private readonly HashSet<string> _writtenTables = [];

        private const string DefaultArrayWrapperKey = "items";

        public override string Serialize(JToken data)
        {
            Logger.WriteTrace(() => "Serialize: Starting TOML serialization");

            if (data == null)
            {
                Logger.WriteError(() => "Serialize: Data is null");
                throw new ArgumentNullException(nameof(data));
            }

            Logger.WriteDebug(() => $"Serialize: Input token type: {data.Type}");
            var processed = PreprocessToken(data);

            try
            {
                _output.Clear();
                _writtenTables.Clear();

                if (processed is JArray arr)
                {
                    var wrapperKey = Config.TomlArrayWrapperKey ?? DefaultArrayWrapperKey;
                    Logger.WriteDebug(() => $"Serialize: Wrapping root array under '{wrapperKey}'");
                    processed = new JObject { [wrapperKey] = arr };

                    if (!Config.Minify)
                    {
                        _output.AppendLine($"# Note: Root array automatically wrapped under '{wrapperKey}'");
                        _output.AppendLine();
                    }
                }

                if (processed is JObject obj)
                {
                    Logger.WriteTrace(() => $"Serialize: Processing object with {obj.Count} properties");
                    WriteObject(obj, string.Empty, 0);
                    var result = _output.ToString().TrimEnd();

                    if (Config.StrictMode)
                    {
                        Logger.WriteDebug(() => "Serialize: Validating TOML in strict mode");
                        ValidateToml(result);
                    }

                    Logger.WriteSuccess($"Serialize: TOML serialization completed ({result.Length} characters)");
                    return result;
                }
                else
                {
                    Logger.WriteError(() => $"Serialize: Invalid root type - {processed.Type}");
                    throw new FormatException("TOML root must be an object/table");
                }
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning(() => $"TOML serialization error ignored: {ex.Message}");
                return CreateErrorToml(ex.Message, ex.GetType().Name, processed);
            }
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo(() => "SerializeStream: Starting stream serialization");

            if (data == null)
            {
                Logger.WriteError(() => "SerializeStream: Data is null");
                throw new ArgumentNullException(nameof(data));
            }
            if (output == null)
            {
                Logger.WriteError(() => "SerializeStream: Output stream is null");
                throw new ArgumentNullException(nameof(output));
            }

            var chunkSize = GetChunkSize();
            Logger.WriteDebug(() => $"SerializeStream: Using chunk size of {chunkSize}");

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);
            ConfigureTomlWriter(writer);

            var buffer = new List<JToken>();
            var isFirst = true;
            var totalProcessed = 0;

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    Logger.WriteTrace(() => $"SerializeStream: Writing chunk of {buffer.Count} items");
                    WriteChunkToStream(buffer, writer, cancellationToken, ref isFirst);
                    totalProcessed += buffer.Count;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.WriteTrace(() => $"SerializeStream: Writing final chunk of {buffer.Count} items");
                WriteChunkToStream(buffer, writer, cancellationToken, ref isFirst);
                totalProcessed += buffer.Count;
            }

            writer.Flush();
            Logger.WriteSuccess($"SerializeStream: Completed. Total items processed: {totalProcessed}");
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo(() => $"SerializeStream: Writing to file '{outputPath}'");

            if (string.IsNullOrEmpty(outputPath))
            {
                Logger.WriteError(() => "SerializeStream: Output path is null or empty");
                throw new ArgumentNullException(nameof(outputPath));
            }

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);

            Logger.WriteSuccess($"SerializeStream: File written successfully to '{outputPath}'");
        }

        private void WriteChunkToStream(List<JToken> items, StreamWriter writer, CancellationToken ct, ref bool isFirst)
        {
            if (items.Count == 0)
            {
                Logger.WriteTrace(() => "WriteChunkToStream: Empty chunk, skipping");
                return;
            }

            Logger.WriteTrace(() => $"WriteChunkToStream: Processing {items.Count} items");

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
                        Logger.WriteTrace(() => $"WriteChunkToStream: Item {i} is array, wrapping under '{wrapperKey}'");
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
                        Logger.WriteTrace(() => $"WriteChunkToStream: Item {i} written successfully");
                    }
                    else
                    {
                        Logger.WriteError(() => $"WriteChunkToStream: Item {i} has invalid type - {item.Type}");
                        throw new FormatException($"TOML documents must be objects or arrays (got {item.Type})");
                    }
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => $"TOML serialization error in item {i}: {ex.Message}");
                    var errorToml = CreateErrorToml(ex.Message, ex.GetType().Name, items[i]);
                    writer.WriteLine(errorToml);
                }
            }

            writer.Flush();
            Logger.WriteDebug(() => $"WriteChunkToStream: Chunk flushed to stream");
        }

        private void ConfigureTomlWriter(StreamWriter writer)
        {
            writer.NewLine = Config.Minify ? "\n" : Environment.NewLine;
            Logger.WriteTrace(() => $"ConfigureTomlWriter: NewLine set to {(Config.Minify ? "\\n" : "Environment.NewLine")}");
        }

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;

        private void ValidateToml(string toml)
        {
            if (!Config.StrictMode) return;

            Logger.WriteTrace(() => "ValidateToml: Validating TOML syntax");

            try
            {
                var result = Tomlyn.Toml.Parse(toml);
                if (result.HasErrors)
                {
                    var errors = string.Join(", ", result.Diagnostics.Select(d => d.ToString()));
                    Logger.WriteError(() => $"ValidateToml: TOML validation failed - {errors}");
                    throw new FormatException($"Generated TOML is invalid: {errors}");
                }
                Logger.WriteDebug(() => "ValidateToml: TOML is valid");
            }
            catch when (!Config.StrictMode)
            {
                Logger.WriteWarning(() => "ValidateToml: Validation error ignored (StrictMode is off)");
            }
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
            Logger.WriteTrace(() => $"WriteObject: Processing object at path '{sectionPath}' (depth {depth}) with {obj.Count} properties");

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
                        Logger.WriteWarning(() => $"Skipping property '{prop.Name}' - incompatible with TOML format");
                        continue;
                    }
                    throw;
                }
            }

            Logger.WriteDebug(() => $"WriteObject: Categorized properties - Simple: {simpleProperties.Count}, Complex: {complexProperties.Count}, ArrayOfTables: {arrayOfTablesProperties.Count}");

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
            {
                Logger.WriteTrace(() => $"WriteTable: Skipping duplicate table '{sectionPath}'");
                return;
            }

            Logger.WriteTrace(() => $"WriteTable: Writing table '{sectionPath}'");
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
            Logger.WriteTrace(() => $"WriteArrayOfTables: Writing array of tables '{sectionPath}' with {array.Count} items");

            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not JObject item)
                {
                    Logger.WriteWarning(() => $"WriteArrayOfTables: Item {i} in '{sectionPath}' is not an object, skipping");
                    continue;
                }

                if (_output.Length > 0 && !_output.ToString().EndsWith("\n\n"))
                {
                    _output.AppendLine();
                }

                _output.AppendLine($"[[{sectionPath}]]");
                Logger.WriteTrace(() => $"WriteArrayOfTables: Processing item {i} of '{sectionPath}'");

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
                            Logger.WriteWarning(() => $"Skipping property '{prop.Name}' - incompatible with TOML format");
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
            Logger.WriteTrace(() => $"WriteObjectProperties: Processing properties at '{sectionPath}' (depth {depth})");

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
                        Logger.WriteWarning(() => $"Skipping property '{prop.Name}' - incompatible with TOML format");
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
            Logger.WriteTrace(() => $"WriteKeyValue: Writing key '{key}' with value type {value.Type}");

            var indent = Config.PrettyPrint ? new string(' ', depth * (Config.IndentSize ?? 2)) : string.Empty;
            var formattedKey = NeedsQuoting(key) ? $"\"{key}\"" : key;
            var formattedValue = FormatTomlValue(value);

            _output.AppendLine($"{indent}{formattedKey} = {formattedValue}");
        }

        private string? FormatTomlValue(JToken value)
        {
            Logger.WriteTrace(() => $"FormatTomlValue: Formatting value of type {value.Type}");

            return value.Type switch
            {
                JTokenType.Null => HandleNullValue(),
                JTokenType.String => FormatStringValue(value.Value<string>()!),
                JTokenType.Boolean => value.Value<bool>().ToString().ToLower(),
                JTokenType.Integer => value.Value<long>().ToString(CultureInfo.InvariantCulture),
                JTokenType.Float => FormatFloat(value.Value<double>()),
                JTokenType.Date => value.Value<DateTime>().ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
                JTokenType.Array => FormatArray((JArray)value),
                JTokenType.Object => FormatInlineTable((JObject)value),
                _ => $"\"{value}\""
            };
        }

        private string FormatStringValue(string str)
        {
            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dateTime))
            {
                if (str.Contains('T') && (str.Contains('-') || str.Contains(':')))
                {
                    return str.Contains('Z') || str.Contains('+') || str.Contains("offset")
                        ? str
                        : dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
                }
            }

            return FormatString(str);
        }

        private string HandleNullValue()
        {
            if (Config.TomlStrictTypes)
            {
                Logger.WriteError(() => "HandleNullValue: Null value encountered in strict mode");
                throw new FormatException("TOML does not support null values. Disable TomlStrictTypes to convert nulls to empty strings.");
            }
            Logger.WriteTrace(() => "HandleNullValue: Converting null to empty string");
            return "\"\"";
        }

        private string FormatString(string str)
        {
            if (string.IsNullOrEmpty(str)) return "\"\"";

            if (Config.TomlMultilineStrings && (str.Contains('\n') || str.Length > 80))
            {
                Logger.WriteTrace(() => $"FormatString: Using multiline format for string of length {str.Length}");
                return $"\"\"\"\n{str}\n\"\"\"";
            }

            return $"\"{EscapeString(str)}\"";
        }

        private string FormatFloat(double number)
        {
            if (Config.TomlStrictTypes && (double.IsNaN(number) || double.IsInfinity(number)))
            {
                Logger.WriteError(() => $"FormatFloat: Special float value {(double.IsNaN(number) ? "NaN" : "Infinity")} in strict mode");
                throw new FormatException($"TOML does not support {(double.IsNaN(number) ? "NaN" : "Infinity")} values. Disable TomlStrictTypes to convert to string.");
            }

            if (double.IsNaN(number))
            {
                Logger.WriteTrace(() => "FormatFloat: Converting NaN to string");
                return "\"NaN\"";
            }
            if (double.IsPositiveInfinity(number))
            {
                Logger.WriteTrace(() => "FormatFloat: Converting Infinity to string");
                return "\"Infinity\"";
            }
            if (double.IsNegativeInfinity(number))
            {
                Logger.WriteTrace(() => "FormatFloat: Converting -Infinity to string");
                return "\"-Infinity\"";
            }

            return number.ToString("G", CultureInfo.InvariantCulture);
        }

        private string FormatArray(JArray array)
        {
            Logger.WriteTrace(() => $"FormatArray: Formatting array with {array.Count} items");

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

            Logger.WriteTrace(() => "FormatArray: Using multiline format for complex array");
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
            Logger.WriteTrace(() => $"FormatInlineTable: Formatting inline table with {obj.Count} properties");

            if (!obj.Properties().Any()) return "{}";

            var pairs = obj.Properties().Select(p =>
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