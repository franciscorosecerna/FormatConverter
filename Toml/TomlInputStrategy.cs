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
            try
            {
                var lines = input.Split('\n')
                    .Select(line => line.TrimEnd('\r'))
                    .ToArray();

                var result = new JObject();
                var currentObject = result;
                string currentSection = string.Empty;

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

                if (Config.SortKeys)
                {
                    result = (JObject)SortKeysRecursively(result);
                }

                return result;
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: TOML parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"Invalid TOML: {ex.Message}", ex);
            }
        }

        private string ParseMultilineString(string[] lines, ref int currentIndex, string firstLine)
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
                return new JArray();

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

        private void SetValue(JObject obj, string key, object value)
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
