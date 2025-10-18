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
            if (string.IsNullOrWhiteSpace(input))
            {
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("TOML input cannot be null or empty");
            }

            try
            {
                var result = Tomlyn.Toml.Parse(input);

                if (result.HasErrors)
                {
                    if (!Config.IgnoreErrors)
                        throw new FormatException(string.Join("\n", result.Diagnostics));

                    Logger.WriteWarning("TOML parse errors ignored:");
                    foreach (var diag in result.Diagnostics)
                        Logger.WriteWarning($"  {diag}");
                }

                var model = result.ToModel();
                var token = ConvertToJToken(model);

                if (token is JObject obj && obj.Count == 1)
                {
                    var firstProp = obj.Properties().First();
                    if (firstProp.Value is JArray)
                    {
                        return firstProp.Value;
                    }
                }

                return token;
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Input file not found.", path);

            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken)
        {
            FileStream? fileStream = null;
            StreamReader? reader = null;

            try
            {
                fileStream = File.OpenRead(path);
                var fileSize = fileStream.Length;

                if (fileSize > 100_000_000)
                {
                    throw new FormatException(
                        $"TOML file too large ({fileSize / 1_048_576}MB). " +
                        "Maximum supported size is 100MB for memory safety.");
                }

                var showProgress = fileSize > 10_485_760;
                reader = new StreamReader(fileStream, Config.Encoding, detectEncodingFromByteOrderMarks: true);
                var content = reader.ReadToEnd();

                if (fileSize < 1_048_576)
                {
                    yield return Parse(content);
                    yield break;
                }

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
            }
            finally
            {
                reader?.Dispose();
                fileStream?.Dispose();
            }
        }

        private IEnumerable<JToken> StreamLargeTomlFile(string tomlContent, CancellationToken cancellationToken)
        {
            var result = Tomlyn.Toml.Parse(tomlContent);

            if (result.HasErrors && !Config.IgnoreErrors)
            {
                throw new FormatException(string.Join("\n", result.Diagnostics));
            }

            if (result.HasErrors && Config.IgnoreErrors)
            {
                Logger.WriteWarning("TOML parse errors ignored:");
                foreach (var diag in result.Diagnostics)
                    Logger.WriteWarning($"  {diag}");
            }

            var model = result.ToModel();
            if (model is TomlTable rootTable)
            {
                if (rootTable.Count == 1)
                {
                    var singleEntry = rootTable.First();
                    if (singleEntry.Value is TomlTableArray tableArray)
                    {
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
                        yield break;
                    }
                }

                var hasArrayOfTables = rootTable.Values.OfType<TomlTableArray>().Any();
                if (hasArrayOfTables)
                {
                    foreach (var (key, value) in rootTable)
                    {
                        if (value is TomlTableArray tableArray)
                        {
                            foreach (var table in tableArray)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var token = ConvertToJToken(table);

                                if (Config.MaxDepth > 0)
                                {
                                    ValidateDepth(token, Config.MaxDepth.Value, $"{key}[]");
                                }

                                yield return token;
                            }
                        }
                        else
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var token = ConvertToJToken(value);

                            if (Config.MaxDepth > 0)
                            {
                                ValidateDepth(token, Config.MaxDepth.Value, key);
                            }

                            yield return new JObject { [key] = token };
                        }
                    }
                }
                else if (rootTable.Count > 3)
                {
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
                }
                else
                {
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
            var obj = new JObject();

            foreach (var kvp in table)
                obj[kvp.Key] = ConvertToJToken(kvp.Value);

            return obj;
        }

        private JArray ConvertTableArrayToJArray(TomlTableArray tableArray)
        {
            var arr = new JArray();

            foreach (var table in tableArray)
                arr.Add(ConvertTableToJObject(table));

            return arr;
        }

        private JArray ConvertArrayToJArray(TomlArray array)
        {
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
                throw new FormatException($"Maximum nesting depth ({maxDepth}) exceeded at path '{path}'");
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
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"TOML parsing error ignored: {ex.Message}");
                return CreateErrorToken(ex, input);
            }
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