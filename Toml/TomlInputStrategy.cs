using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using Tomlyn;
using Tomlyn.Model;

namespace FormatConverter.Toml
{
    public class TomlInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            Logger.WriteTrace("Parse: Starting TOML parsing");

            if (string.IsNullOrWhiteSpace(input))
            {
                Logger.WriteWarning("Parse: Input is null or empty");
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("TOML input cannot be null or empty");
            }

            Logger.WriteDebug($"Parse: Input length: {input.Length} characters");

            try
            {
                var result = Tomlyn.Toml.Parse(input);

                if (result.HasErrors)
                {
                    Logger.WriteWarning($"Parse: Found {result.Diagnostics.Count} parse errors");

                    if (!Config.IgnoreErrors)
                    {
                        Logger.WriteError("Parse: Errors not ignored, throwing exception");
                        throw new FormatException(string.Join("\n", result.Diagnostics));
                    }

                    Logger.WriteWarning("TOML parse errors ignored:");
                    foreach (var diag in result.Diagnostics)
                        Logger.WriteWarning($"  {diag}");
                }

                var model = result.ToModel();
                Logger.WriteDebug($"Parse: Model type: {model?.GetType().Name ?? "null"}");

                var token = ConvertToJToken(model);
                Logger.WriteTrace($"Parse: Converted to JToken type: {token.Type}");

                if (token is JObject obj && obj.Count == 1)
                {
                    var firstProp = obj.Properties().First();
                    if (firstProp.Value is JArray)
                    {
                        Logger.WriteDebug($"Parse: Unwrapping single array property '{firstProp.Name}'");
                        return firstProp.Value;
                    }
                }

                Logger.WriteSuccess("Parse: TOML parsed successfully");
                return token;
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                Logger.WriteError($"Parse: Exception occurred - {ex.Message}");
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken)
        {
            Logger.WriteInfo($"ParseStream: Starting stream parsing for '{path}'");

            if (string.IsNullOrWhiteSpace(path))
            {
                Logger.WriteError("ParseStream: Path is null or empty");
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));
            }

            if (!File.Exists(path))
            {
                Logger.WriteError($"ParseStream: File not found at '{path}'");
                throw new FileNotFoundException("Input file not found.", path);
            }

            Logger.WriteDebug($"ParseStream: File found, size: {new FileInfo(path).Length} bytes");
            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken)
        {
            Logger.WriteTrace("ParseStreamInternal: Opening file stream");

            FileStream? fileStream = null;
            StreamReader? reader = null;

            try
            {
                fileStream = File.OpenRead(path);
                var fileSize = fileStream.Length;
                Logger.WriteDebug($"ParseStreamInternal: File size: {fileSize:N0} bytes ({fileSize / 1_048_576:F2} MB)");

                if (fileSize > 100_000_000)
                {
                    Logger.WriteError($"ParseStreamInternal: File too large ({fileSize / 1_048_576}MB)");
                    throw new FormatException(
                        $"TOML file too large ({fileSize / 1_048_576}MB). " +
                        "Maximum supported size is 100MB for memory safety.");
                }

                var showProgress = fileSize > 10_485_760;
                if (showProgress)
                {
                    Logger.WriteInfo("ParseStreamInternal: Large file detected, progress logging enabled");
                }

                reader = new StreamReader(fileStream, Config.Encoding, detectEncodingFromByteOrderMarks: true);
                var content = reader.ReadToEnd();
                Logger.WriteDebug($"ParseStreamInternal: Content read, length: {content.Length} characters");

                if (fileSize < 1_048_576)
                {
                    Logger.WriteDebug("ParseStreamInternal: Small file, using simple parse");
                    yield return Parse(content);
                    Logger.WriteSuccess("ParseStreamInternal: Small file parsed successfully");
                    yield break;
                }

                Logger.WriteDebug("ParseStreamInternal: Large file, using streaming parse");
                var tokens = StreamLargeTomlFile(content, cancellationToken);
                var count = 0;

                foreach (var token in tokens)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count++;

                    if (showProgress && count % 10 == 0)
                    {
                        var progress = (double)fileStream.Position / fileSize * 100;
                        Logger.WriteInfo($"Processing: {progress:F1}% ({count} items)");
                    }

                    yield return token;
                }

                if (showProgress)
                    Logger.WriteInfo($"Completed: {count} items processed");

                Logger.WriteSuccess($"ParseStreamInternal: Stream parsing completed. Total items: {count}");
            }
            finally
            {
                Logger.WriteTrace("ParseStreamInternal: Disposing resources");
                reader?.Dispose();
                fileStream?.Dispose();
            }
        }

        private IEnumerable<JToken> StreamLargeTomlFile(string tomlContent, CancellationToken cancellationToken)
        {
            Logger.WriteTrace($"StreamLargeTomlFile: Parsing content ({tomlContent.Length} characters)");

            var result = Tomlyn.Toml.Parse(tomlContent);

            if (result.HasErrors && !Config.IgnoreErrors)
            {
                Logger.WriteError($"StreamLargeTomlFile: Parse errors found ({result.Diagnostics.Count})");
                throw new FormatException(string.Join("\n", result.Diagnostics));
            }

            if (result.HasErrors && Config.IgnoreErrors)
            {
                Logger.WriteWarning($"StreamLargeTomlFile: Ignoring {result.Diagnostics.Count} parse errors");
                Logger.WriteWarning("TOML parse errors ignored:");
                foreach (var diag in result.Diagnostics)
                    Logger.WriteWarning($"  {diag}");
            }

            var model = result.ToModel();
            Logger.WriteDebug($"StreamLargeTomlFile: Model type: {model?.GetType().Name ?? "null"}");

            if (model is TomlTable rootTable)
            {
                Logger.WriteDebug($"StreamLargeTomlFile: Root table with {rootTable.Count} entries");

                if (rootTable.Count == 1)
                {
                    var singleEntry = rootTable.First();
                    if (singleEntry.Value is TomlTableArray tableArray)
                    {
                        Logger.WriteDebug($"StreamLargeTomlFile: Single table array '{singleEntry.Key}' with {tableArray.Count} items");

                        foreach (var table in tableArray)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var token = ConvertToJToken(table);

                            if (Config.MaxDepth > 0)
                            {
                                ValidateDepth(token, Config.MaxDepth.Value, $"{singleEntry.Key}[]");
                            }

                            yield return token;
                        }
                        Logger.WriteTrace($"StreamLargeTomlFile: Yielded {tableArray.Count} items from single table array");
                        yield break;
                    }
                }

                var hasArrayOfTables = rootTable.Values.OfType<TomlTableArray>().Any();
                Logger.WriteDebug($"StreamLargeTomlFile: Has array of tables: {hasArrayOfTables}");

                if (hasArrayOfTables)
                {
                    var itemCount = 0;
                    foreach (var (key, value) in rootTable)
                    {
                        if (value is TomlTableArray tableArray)
                        {
                            Logger.WriteTrace($"StreamLargeTomlFile: Processing table array '{key}' with {tableArray.Count} items");

                            foreach (var table in tableArray)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var token = ConvertToJToken(table);

                                if (Config.MaxDepth > 0)
                                {
                                    ValidateDepth(token, Config.MaxDepth.Value, $"{key}[]");
                                }

                                itemCount++;
                                yield return token;
                            }
                        }
                        else
                        {
                            Logger.WriteTrace($"StreamLargeTomlFile: Processing regular entry '{key}'");
                            cancellationToken.ThrowIfCancellationRequested();
                            var token = ConvertToJToken(value);

                            if (Config.MaxDepth > 0)
                            {
                                ValidateDepth(token, Config.MaxDepth.Value, key);
                            }

                            itemCount++;
                            yield return new JObject { [key] = token };
                        }
                    }
                    Logger.WriteTrace($"StreamLargeTomlFile: Yielded {itemCount} items from mixed content");
                }
                else if (rootTable.Count > 3)
                {
                    Logger.WriteDebug($"StreamLargeTomlFile: Splitting {rootTable.Count} root entries");

                    foreach (var (key, value) in rootTable)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var token = ConvertToJToken(value);

                        if (Config.MaxDepth > 0)
                        {
                            ValidateDepth(token, Config.MaxDepth.Value, key);
                        }

                        yield return new JObject { [key] = token };
                    }
                    Logger.WriteTrace($"StreamLargeTomlFile: Yielded {rootTable.Count} split entries");
                }
                else
                {
                    Logger.WriteDebug("StreamLargeTomlFile: Small root table, yielding as single token");
                    var token = ConvertToJToken(model);

                    if (Config.MaxDepth > 0)
                    {
                        ValidateDepth(token, Config.MaxDepth.Value, "root");
                    }

                    yield return token;
                }
            }
            else
            {
                Logger.WriteDebug("StreamLargeTomlFile: Non-table model, yielding as single token");
                var token = ConvertToJToken(model);

                if (Config.MaxDepth > 0)
                {
                    ValidateDepth(token, Config.MaxDepth.Value, "root");
                }

                yield return token;
            }
        }

        private JToken ConvertToJToken(object? value)
        {
            Logger.WriteTrace($"ConvertToJToken: Converting type {value?.GetType().Name ?? "null"}");

            return value switch
            {
                null => JValue.CreateNull(),
                "" => JValue.CreateNull(),
                TomlTable table => ConvertTableToJObject(table),
                TomlTableArray tableArray => ConvertTableArrayToJArray(tableArray),
                TomlArray array => ConvertArrayToJArray(array),
                string s => new JValue(s),
                bool b => new JValue(b),
                long l => new JValue(l),
                int i => new JValue(i),
                double d => new JValue(d),
                float f => new JValue(f),
                decimal dec => new JValue(dec),
                DateTimeOffset dto => new JValue(dto.DateTime.ToString("yyyy-MM-ddTHH:mm:ss")),
                DateTime dt => new JValue(dt.ToString("yyyy-MM-ddTHH:mm:ss")),
                TomlDateTime tdt => ConvertTomlDateTime(tdt),
                _ => throw new FormatException($"Unsupported TOML type: {value?.GetType().Name ?? "null"}")
            };
        }

        private static JValue ConvertTomlDateTime(TomlDateTime tomlDateTime)
        {
            try
            {
                var dt = tomlDateTime.DateTime;
                if (dt != default(DateTime))
                {
                    return new JValue(dt);
                }
            }
            catch { }

            var str = tomlDateTime.ToString();
            if (str.Length > 19 && (str[19] == '+' || str[19] == '-'))
            {
                str = str[..19];
            }
            return new JValue(str);
        }

        private JObject ConvertTableToJObject(TomlTable table)
        {
            Logger.WriteTrace($"ConvertTableToJObject: Converting table with {table.Count} entries");

            var obj = new JObject();

            foreach (var kvp in table)
                obj[kvp.Key] = ConvertToJToken(kvp.Value);

            return obj;
        }

        private JArray ConvertTableArrayToJArray(TomlTableArray tableArray)
        {
            Logger.WriteTrace($"ConvertTableArrayToJArray: Converting table array with {tableArray.Count} items");

            var arr = new JArray();

            foreach (var table in tableArray)
                arr.Add(ConvertTableToJObject(table));

            return arr;
        }

        private JArray ConvertArrayToJArray(TomlArray array)
        {
            Logger.WriteTrace($"ConvertArrayToJArray: Converting array with {array.Count} items");

            var arr = new JArray();

            foreach (var item in array)
            {
                if (item is DateTimeOffset dto)
                {
                    arr.Add(new JValue(dto.DateTime.ToString("yyyy-MM-ddTHH:mm:ss")));
                }
                else if (item is DateTime dt)
                {
                    arr.Add(new JValue(dt.ToString("yyyy-MM-ddTHH:mm:ss")));
                }
                else
                {
                    arr.Add(ConvertToJToken(item));
                }
            }

            return arr;
        }

        private void ValidateDepth(JToken token, int maxDepth, string path, int currentDepth = 0)
        {
            if (currentDepth > maxDepth)
            {
                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"Maximum depth ({maxDepth}) exceeded at path '{path}'");
                    return;
                }
                Logger.WriteError($"ValidateDepth: Maximum depth ({maxDepth}) exceeded at path '{path}'");
                throw new FormatException($"Maximum nesting depth ({maxDepth}) exceeded at path '{path}'");
            }

            if (currentDepth == 0)
            {
                Logger.WriteTrace($"ValidateDepth: Starting validation at '{path}' (max depth: {maxDepth})");
            }

            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    ValidateDepth(prop.Value, maxDepth, $"{path}.{prop.Name}", currentDepth + 1);
                }
            }
            else if (token is JArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    ValidateDepth(arr[i], maxDepth, $"{path}[{i}]", currentDepth + 1);
                }
            }

            if (currentDepth == 0)
            {
                Logger.WriteDebug($"ValidateDepth: Validation passed for '{path}'");
            }
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"TOML parsing error ignored: {ex.Message}");
                return CreateErrorToken(ex, input);
            }
            Logger.WriteError($"HandleParsingError: Fatal error - {ex.Message}");
            throw new FormatException(ex.Message, ex);
        }

        private static JObject CreateErrorToken(Exception ex, string input)
        {
            return new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["raw_snippet"] = input.Length > 1000
                    ? string.Concat(input.AsSpan(0, 1000), "...")
                    : input,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }
    }
}