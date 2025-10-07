using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FormatConverter.Toml
{
    public class TomlInputStrategy : BaseInputStrategy
    {
        private string _currentArrayTableKey = string.Empty;

        public override JToken Parse(string input)
        {
            if (Config.UseStreaming)
            {
                var firstToken = ParseStream(input).FirstOrDefault();
                return firstToken ?? new JObject();
            }

            try
            {
                var token = ParseTomlDocument(input);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                return token;
            }
            catch (Exception ex) when (ex is not FormatException)
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

            IEnumerable<JToken> tokens;

            try
            {
                if (HasMultipleTables(input))
                {
                    tokens = StreamMultipleTables(input);
                }
                else if (HasLargeArrayTables(input))
                {
                    tokens = StreamLargeArrayTables(input);
                }
                else if (HasManyTopLevelProperties(input))
                {
                    tokens = StreamTopLevelProperties(input);
                }
                else
                {
                    tokens = [ParseTomlDocument(input)];
                }
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                tokens = HandleStreamingError(ex, input);
            }

            foreach (var token in tokens)
            {
                yield return token;
            }
        }

        private JToken ParseTomlDocument(string input)
        {
            var lines = input.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .ToArray();

            var result = new JObject();
            var currentObject = result;
            string currentSection = string.Empty;
            _currentArrayTableKey = string.Empty;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                if (line.StartsWith("[[") && line.EndsWith("]]"))
                {
                    var sectionName = line.Substring(2, line.Length - 4).Trim();
                    currentSection = sectionName;
                    _currentArrayTableKey = sectionName;

                    if (!result.ContainsKey(sectionName))
                    {
                        result[sectionName] = new JArray();
                    }

                    var newTableObject = new JObject();
                    ((JArray)result[sectionName]).Add(newTableObject);
                    currentObject = newTableObject;
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    var sectionName = line.Substring(1, line.Length - 2).Trim();
                    currentSection = sectionName;
                    _currentArrayTableKey = string.Empty;

                    var sections = sectionName.Split('.');
                    currentObject = result;

                    foreach (var section in sections)
                    {
                        if (!currentObject.ContainsKey(section))
                        {
                            currentObject[section] = new JObject();
                        }
                        currentObject = (JObject)currentObject[section];
                    }
                    continue;
                }

                var keyValueMatch = Regex.Match(line, @"^([^=]+)=(.*)$");
                if (keyValueMatch.Success)
                {
                    var key = keyValueMatch.Groups[1].Value.Trim();
                    var valueStr = keyValueMatch.Groups[2].Value.Trim();

                    if (valueStr.StartsWith("\"\"\""))
                    {
                        var multilineValue = ParseMultilineString(lines, ref i, valueStr);
                        SetValue(currentObject, key, multilineValue);
                    }
                    else
                    {
                        var value = ParseValue(valueStr);
                        SetValue(currentObject, key, value);
                    }
                }
            }

            if (Config.NoMetadata)
            {
                RemoveMetadataProperties(result);
            }

            return result;
        }

        private static bool HasMultipleTables(string input)
        {
            var lines = input.Split('\n');
            return lines.Count(line => line.Trim().StartsWith("[") && !line.Trim().StartsWith("[[")) > 3;
        }

        private static bool HasLargeArrayTables(string input)
        {
            var lines = input.Split('\n');
            var arrayTableLines = lines.Count(line => line.Trim().StartsWith("[["));
            return arrayTableLines > 10;
        }

        private static bool HasManyTopLevelProperties(string input)
        {
            try
            {
                var lines = input.Split('\n');
                var topLevelProperties = 0;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                        continue;

                    if (trimmedLine.StartsWith("["))
                        break;

                    if (trimmedLine.Contains('='))
                        topLevelProperties++;
                }

                return topLevelProperties > 10;
            }
            catch
            {
                return false;
            }
        }

        private IEnumerable<JToken> StreamMultipleTables(string input)
        {
            var tokens = new List<JToken>();
            var lines = input.Split('\n');
            var currentSection = new List<string>();
            var topLevelProps = new List<string>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                {
                    currentSection.Add(line);
                    continue;
                }

                if (trimmedLine.StartsWith("["))
                {
                    if (currentSection.Count > 1)
                    {
                        var sectionToml = string.Join('\n', topLevelProps.Concat(currentSection));
                        var token = ParseTomlDocument(sectionToml);
                        tokens.Add(Config.SortKeys ? SortKeysRecursively(token) : token);
                    }

                    currentSection.Clear();
                    currentSection.Add(line);
                }
                else
                {
                    if (currentSection.Count == 0 && !line.Contains('['))
                    {
                        topLevelProps.Add(line);
                    }
                    else
                    {
                        currentSection.Add(line);
                    }
                }
            }

            if (currentSection.Count > 0)
            {
                var sectionToml = string.Join('\n', topLevelProps.Concat(currentSection));
                var token = ParseTomlDocument(sectionToml);
                tokens.Add(Config.SortKeys ? SortKeysRecursively(token) : token);
            }

            return tokens;
        }

        private IEnumerable<JToken> StreamLargeArrayTables(string input)
        {
            var chunks = new List<JToken>();
            const int chunkSize = 5;

            try
            {
                var lines = input.Split('\n');
                var currentChunk = new List<string>();
                var arrayTableCount = 0;
                var topLevelProps = new List<string>();
                var inArrayTable = false;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("[["))
                    {
                        inArrayTable = true;
                        arrayTableCount++;

                        if (arrayTableCount > chunkSize)
                        {
                            var chunkToml = string.Join('\n', topLevelProps.Concat(currentChunk));
                            var chunkToken = ParseTomlDocument(chunkToml);

                            var chunkObject = new JObject();
                            foreach (var prop in ((JObject)chunkToken).Properties())
                            {
                                chunkObject[prop.Name] = prop.Value;
                            }

                            chunkObject["_chunk_info"] = new JObject
                            {
                                ["chunk_start"] = arrayTableCount - chunkSize,
                                ["chunk_size"] = chunkSize,
                                ["type"] = "array_tables"
                            };

                            chunks.Add(Config.SortKeys ? SortKeysRecursively(chunkObject) : chunkObject);

                            currentChunk.Clear();
                            arrayTableCount = 1;
                        }
                    }
                    else if (!inArrayTable && !trimmedLine.StartsWith("["))
                    {
                        topLevelProps.Add(line);
                        continue;
                    }

                    currentChunk.Add(line);
                }

                if (currentChunk.Count > 0)
                {
                    var chunkToml = string.Join('\n', topLevelProps.Concat(currentChunk));
                    var token = ParseTomlDocument(chunkToml);
                    chunks.Add(Config.SortKeys ? SortKeysRecursively(token) : token);
                }
            }
            catch
            {
                chunks.Add(ParseTomlDocument(input));
            }

            return chunks;
        }

        private IEnumerable<JToken> StreamTopLevelProperties(string input)
        {
            var chunks = new List<JToken>();
            const int chunkSize = 5;

            try
            {
                var lines = input.Split('\n');
                var currentChunk = new JObject();
                var processedCount = 0;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                        continue;

                    if (trimmedLine.StartsWith("["))
                        break;

                    if (trimmedLine.Contains('='))
                    {
                        var keyValueMatch = Regex.Match(trimmedLine, @"^([^=]+)=(.*)$");
                        if (keyValueMatch.Success)
                        {
                            var key = keyValueMatch.Groups[1].Value.Trim();
                            var valueStr = keyValueMatch.Groups[2].Value.Trim();
                            var value = ParseValue(valueStr);

                            currentChunk[key] = JToken.FromObject(value);
                            processedCount++;

                            if (processedCount >= chunkSize)
                            {
                                chunks.Add(Config.SortKeys ? SortKeysRecursively(currentChunk) : currentChunk);
                                currentChunk = [];
                                processedCount = 0;
                            }
                        }
                    }
                }

                if (processedCount > 0)
                {
                    chunks.Add(Config.SortKeys ? SortKeysRecursively(currentChunk) : currentChunk);
                }
            }
            catch
            {
                chunks.Add(ParseTomlDocument(input));
            }

            return chunks;
        }

        private IEnumerable<JToken> HandleStreamingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: TOML streaming error ignored: {ex.Message}");
                return [HandleParsingError(ex, input)];
            }
            else
            {
                throw new FormatException($"TOML streaming failed: {ex.Message}", ex);
            }
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: TOML parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000 ? string.Concat(input.AsSpan(0, 1000), "...") : input
                };
            }
            throw new FormatException($"Invalid TOML: {ex.Message}", ex);
        }

        private static void RemoveMetadataProperties(JObject obj)
        {
            var keysToRemove = obj.Properties()
                .Where(p => p.Name.StartsWith("_"))
                .Select(p => p.Name)
                .ToList();

            foreach (var key in keysToRemove)
            {
                obj.Remove(key);
            }
        }

        private static string ParseMultilineString(string[] lines, ref int currentIndex, string firstLine)
        {
            var result = firstLine.Substring(3);

            if (result.EndsWith("\"\"\""))
            {
                return result.Substring(0, result.Length - 3);
            }

            for (int i = currentIndex + 1; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.TrimEnd().EndsWith("\"\"\""))
                {
                    result += string.Concat("\n", line.AsSpan(0, line.LastIndexOf("\"\"\"")));
                    currentIndex = i;
                    break;
                }
                result += "\n" + line;
            }

            return result;
        }

        private object ParseValue(string value)
        {
            value = value.Trim();

            if (value.StartsWith("\"") && value.EndsWith("\""))
            {
                return UnescapeString(value.Substring(1, value.Length - 2));
            }

            if (value.StartsWith("'") && value.EndsWith("'"))
            {
                return value.Substring(1, value.Length - 2);
            }

            if (value.StartsWith("[") && value.EndsWith("]"))
            {
                return ParseArray(value);
            }

            if (value.StartsWith("{") && value.EndsWith("}"))
            {
                return ParseInlineTable(value);
            }

            if (value == "true") return true;
            if (value == "false") return false;

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
                return intVal;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleVal))
                return doubleVal;

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dateVal))
                return dateVal;

            return value;
        }

        private JArray ParseArray(string arrayStr)
        {
            var content = arrayStr.Substring(1, arrayStr.Length - 2).Trim();
            if (string.IsNullOrEmpty(content))
                return [];

            var items = new List<string>();
            var current = string.Empty;
            int depth = 0;
            var inString = false;
            char stringChar = '"';

            foreach (char c in content)
            {
                if (!inString && (c == '"' || c == '\''))
                {
                    inString = true;
                    stringChar = c;
                    current += c;
                }
                else if (inString && c == stringChar)
                {
                    inString = false;
                    current += c;
                }
                else if (!inString && (c == '[' || c == '{'))
                {
                    depth++;
                    current += c;
                }
                else if (!inString && (c == ']' || c == '}'))
                {
                    depth--;
                    current += c;
                }
                else if (!inString && c == ',' && depth == 0)
                {
                    items.Add(current.Trim());
                    current = string.Empty;
                }
                else
                {
                    current += c;
                }
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                items.Add(current.Trim());
            }

            var array = new JArray();
            foreach (var item in items)
            {
                array.Add(JToken.FromObject(ParseValue(item)));
            }
            return array;
        }

        private JObject ParseInlineTable(string tableStr)
        {
            var content = tableStr.Substring(1, tableStr.Length - 2).Trim();
            var result = new JObject();

            if (string.IsNullOrEmpty(content))
                return result;

            var pairs = new List<string>();
            var current = string.Empty;
            int depth = 0;
            var inString = false;
            char stringChar = '"';

            foreach (char c in content)
            {
                if (!inString && (c == '"' || c == '\''))
                {
                    inString = true;
                    stringChar = c;
                    current += c;
                }
                else if (inString && c == stringChar)
                {
                    inString = false;
                    current += c;
                }
                else if (!inString && (c == '[' || c == '{'))
                {
                    depth++;
                    current += c;
                }
                else if (!inString && (c == ']' || c == '}'))
                {
                    depth--;
                    current += c;
                }
                else if (!inString && c == ',' && depth == 0)
                {
                    pairs.Add(current.Trim());
                    current = string.Empty;
                }
                else
                {
                    current += c;
                }
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                pairs.Add(current.Trim());
            }

            foreach (var pair in pairs)
            {
                var equalIndex = pair.IndexOf('=');
                if (equalIndex > 0)
                {
                    var key = pair.Substring(0, equalIndex).Trim();
                    var value = pair.Substring(equalIndex + 1).Trim();
                    result[key] = JToken.FromObject(ParseValue(value));
                }
            }

            return result;
        }

        private static void SetValue(JObject obj, string key, object value)
        {
            if (key.Contains('.'))
            {
                var keys = key.Split('.');
                var current = obj;

                for (int i = 0; i < keys.Length - 1; i++)
                {
                    if (!current.ContainsKey(keys[i]))
                    {
                        current[keys[i]] = new JObject();
                    }
                    current = (JObject)current[keys[i]];
                }

                current[keys.Last()] = JToken.FromObject(value);
            }
            else
            {
                obj[key] = JToken.FromObject(value);
            }
        }

        private static string UnescapeString(string str)
        {
            return str.Replace("\\\"", "\"")
                     .Replace("\\\\", "\\")
                     .Replace("\\n", "\n")
                     .Replace("\\r", "\r")
                     .Replace("\\t", "\t");
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