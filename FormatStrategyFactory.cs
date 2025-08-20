using FormatConverter.Bxml;
using FormatConverter.Cbor;
using FormatConverter.Interfaces;
using FormatConverter.Json;
using FormatConverter.MessagePack;
using FormatConverter.Protobuf;
using FormatConverter.Xml;
using FormatConverter.Yaml;

namespace FormatConverter
{
    public static class FormatStrategyFactory
    {
        private static readonly Dictionary<string, Func<IInputFormatStrategy>> InputStrategies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["json"] = () => new JsonInputStrategy(),
            ["yaml"] = () => new YamlInputStrategy(),
            ["xml"] = () => new XmlInputStrategy(),
            ["messagepack"] = () => new MessagePackInputStrategy(),
            ["cbor"] = () => new CborInputStrategy(),
            ["protobuf"] = () => new ProtobufInputStrategy(),
            ["bxml"] = () => new BxmlInputStrategy()
        };

        private static readonly Dictionary<string, Func<IOutputFormatStrategy>> OutputStrategies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["json"] = () => new JsonOutputStrategy(),
            ["yaml"] = () => new YamlOutputStrategy(),
            ["xml"] = () => new XmlOutputStrategy(),
            ["messagepack"] = () => new MessagePackOutputStrategy(),
            ["cbor"] = () => new CborOutputStrategy(),
            ["protobuf"] = () => new ProtobufOutputStrategy(),
            ["bxml"] = () => new BxmlOutputStrategy()
        };

        public static IInputFormatStrategy CreateInputStrategy(string format)
        {
            if (InputStrategies.TryGetValue(format, out var factory))
            {
                return factory();
            }
            throw new ArgumentException($"Unsupported input format: {format}. Supported formats: {string.Join(", ", InputStrategies.Keys)}");
        }

        public static IOutputFormatStrategy CreateOutputStrategy(string format)
        {
            if (OutputStrategies.TryGetValue(format, out var factory))
            {
                return factory();
            }
            throw new ArgumentException($"Unsupported output format: {format}. Supported formats: {string.Join(", ", OutputStrategies.Keys)}");
        }

        public static IEnumerable<string> GetSupportedFormats()
        {
            return InputStrategies.Keys;
        }
    }
}
