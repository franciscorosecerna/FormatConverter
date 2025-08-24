using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Xml;
using System.Xml.Linq;

namespace FormatConverter.Xml
{
    public class XmlInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    IgnoreWhitespace = !Config.PrettyPrint,
                    IgnoreComments = Config.NoMetadata,
                    IgnoreProcessingInstructions = Config.NoMetadata
                };

                var doc = XDocument.Parse(input, LoadOptions.PreserveWhitespace);

                if (doc.Root == null)
                {
                    throw new FormatException("XML document has no root element");
                }

                var result = ConvertXElementToJToken(doc.Root);

                if (Config.SortKeys && result is JObject)
                {
                    result = SortKeysRecursively(result);
                }

                return result;
            }
            catch (XmlException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: XML parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"Invalid XML: {ex.Message}", ex);
            }
        }

        private JToken ConvertXElementToJToken(XElement element)
        {
            var result = new JObject();

            if (element.HasAttributes)
            {
                foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                {
                    var key = Config.XmlUseAttributes ? $"@{attr.Name.LocalName}" : attr.Name.LocalName;
                    result[key] = new JValue(ConvertValue(attr.Value));
                }
            }

            var childElements = element.Elements().ToList();
            var childGroups = childElements.GroupBy(e => e.Name.LocalName);

            foreach (var group in childGroups)
            {
                var children = group.ToList();
                if (children.Count == 1)
                {
                    var child = children.First();
                    if (child.HasElements || child.HasAttributes)
                    {
                        result[child.Name.LocalName] = ConvertXElementToJToken(child);
                    }
                    else
                    {
                        result[child.Name.LocalName] = new JValue(ConvertValue(child.Value));
                    }
                }
                else
                {
                    var array = new JArray();
                    foreach (var child in children)
                    {
                        array.Add(ConvertXElementToJToken(child));
                    }
                    result[group.Key] = array;
                }
            }

            if (!element.HasElements && !element.HasAttributes && !string.IsNullOrWhiteSpace(element.Value))
            {
                return new JValue(ConvertValue(element.Value));
            }

            if (!element.HasElements && element.HasAttributes && !string.IsNullOrWhiteSpace(element.Value))
            {
                result["#text"] = new JValue(ConvertValue(element.Value));
            }

            return result.Count == 1 && result["#text"] != null ? result["#text"] : result;
        }

        private static object ConvertValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            if (bool.TryParse(value, out bool boolVal)) return boolVal;
            if (int.TryParse(value, out int intVal)) return intVal;
            if (double.TryParse(value, out double doubleVal)) return doubleVal;
            if (DateTime.TryParse(value, out DateTime dateVal)) return dateVal;

            return value;
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
