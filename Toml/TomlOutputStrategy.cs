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
            try
            {
                _output.Clear();
                _writtenTables.Clear();

                if (data is JObject obj)
                {
                    WriteObject(obj, string.Empty, 0);
                }
                else
                {
                    throw new FormatException("TOML root must be an object/table");
                }

                return _output.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    return $"# Error serializing to TOML: {ex.Message}\n{data}";
                }
                throw new FormatException($"TOML serialization failed: {ex.Message}", ex);
            }
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
                    Console.WriteLine($"Warning: Skipping property '{prop.Name}' - incompatible with TOML format");
                    continue;
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
                    "octal" when number is long l => $"0o{Convert.ToString(l, 8)}",
                    "binary" when number is long l => $"0b{Convert.ToString(l, 2)}",
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

        private bool IsSimpleArrayItem(JToken item)
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

        private bool IsArrayOfTables(JArray array)
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
    }
}