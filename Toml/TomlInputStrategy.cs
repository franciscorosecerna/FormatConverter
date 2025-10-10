using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

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

                    Console.Error.WriteLine("Warning: TOML parse errors ignored:");
                    foreach (var diag in result.Diagnostics)
                        Console.Error.WriteLine($"  {diag}");
                }

                var model = result.ToModel();

                var token = ConvertToJToken(model);

                if (Config.NoMetadata)
                    RemoveMetadataProperties(token);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

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
                reader = new StreamReader(fileStream, Config.Encoding, true);
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
                        Console.Error.Write($"\rProcessing: {count} tables");
                    }

                    yield return token;
                }

                if (showProgress)
                    Console.Error.WriteLine($"\rCompleted: {count} tables processed");
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
                Console.Error.WriteLine("Warning: TOML parse errors ignored:");
                foreach (var diag in result.Diagnostics)
                    Console.Error.WriteLine($"  {diag}");
            }

            var model = result.ToModel();

            if (model is TomlTable rootTable && rootTable.Count > 3)
            {
                foreach (var (key, value) in rootTable)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var token = ConvertToJToken(value);
                    token = ApplyConfiguration(token);

                    yield return new JObject { [key] = token };
                }
            }
            else
            {
                var token = ConvertToJToken(model);
                token = ApplyConfiguration(token);

                yield return token;
            }
        }

        private JToken ApplyConfiguration(JToken token)
        {
            if (Config.NoMetadata)
                RemoveMetadataProperties(token);

            if (Config.SortKeys)
                token = SortKeysRecursively(token);

            return token;
        }

        private JToken ConvertToJToken(object? value)
        {
            return value switch
            {
                null => JValue.CreateNull(),
                TomlTable table => ConvertTableToJObject(table),
                TomlArray array => ConvertArrayToJArray(array),
                string s => new JValue(s),
                bool b => new JValue(b),
                long l => new JValue(l),
                int i => new JValue(i),
                double d => new JValue(d),
                float f => new JValue(f),
                decimal dec => new JValue(dec),
                DateTime dt => new JValue(dt),
                DateTimeOffset dto => new JValue(dto),
                TomlDateTime tdt => new JValue(tdt.DateTime),
                _ => JToken.FromObject(value)
            };
        }

        private JObject ConvertTableToJObject(TomlTable table)
        {
            var obj = new JObject();

            foreach (var kvp in table)
                obj[kvp.Key] = ConvertToJToken(kvp.Value);

            return obj;
        }

        private JArray ConvertArrayToJArray(TomlArray array)
        {
            var arr = new JArray();

            foreach (var item in array)
                arr.Add(ConvertToJToken(item));

            return arr;
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.Error.WriteLine($"Warning: TOML parsing error ignored: {ex.Message}");

                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000
                        ? string.Concat(input.AsSpan(0, 1000), "...")
                        : input
                };
            }
            throw new FormatException(ex.Message, ex);
        }

        private static void RemoveMetadataProperties(JToken token)
        {
            if (token is JObject obj)
            {
                var toRemove = obj.Properties()
                    .Where(p => p.Name.StartsWith("_"))
                    .Select(p => p.Name)
                    .ToList();

                foreach (var key in toRemove)
                    obj.Remove(key);

                foreach (var prop in obj.Properties())
                    RemoveMetadataProperties(prop.Value);
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                    RemoveMetadataProperties(item);
            }
        }
    }
}