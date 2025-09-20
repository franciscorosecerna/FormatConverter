using FormatConverter.Bxml;
using FormatConverter.Cbor;
using FormatConverter.Interfaces;
using FormatConverter.Json;
using FormatConverter.MessagePack;
using FormatConverter.Protobuf;
using FormatConverter.Toml;
using FormatConverter.Xml;
using FormatConverter.Yaml;

namespace FormatConverter
{
    public static class FormatStrategyFactory
    {
        private static readonly Dictionary<string, Type> InputStrategies = new()
        {
            { "json", typeof(JsonInputStrategy) },
            { "xml", typeof(XmlInputStrategy) },
            { "yaml", typeof(YamlInputStrategy) },
            { "messagepack", typeof(MessagePackInputStrategy) },
            { "cbor", typeof(CborInputStrategy) },
            { "protobuf", typeof(ProtobufInputStrategy) },
            { "bxml", typeof(BxmlInputStrategy) },
            { "toml", typeof (TomlInputStrategy) },
        };

        private static readonly Dictionary<string, Type> OutputStrategies = new()
        {
            { "json", typeof(JsonOutputStrategy) },
            { "xml", typeof(XmlOutputStrategy) },
            { "yaml", typeof(YamlOutputStrategy) },
            { "messagepack", typeof(MessagePackOutputStrategy) },
            { "cbor", typeof(CborOutputStrategy) },
            { "protobuf", typeof(ProtobufOutputStrategy) },
            { "bxml", typeof(BxmlOutputStrategy) },
            { "toml", typeof(TomlOutputStrategy) },
        };

        public static IInputFormatStrategy CreateInputStrategy(string format, FormatConfig? config = null)
        {
            var normalizedFormat = format.ToLowerInvariant();

            if (!InputStrategies.TryGetValue(normalizedFormat, out var strategyType))
            {
                throw new NotSupportedException($"Input format '{format}' is not supported");
            }

            var strategy = (IInputFormatStrategy)Activator.CreateInstance(strategyType)!;

            if (config != null)
            {
                strategy.Configure(config);
            }

            return strategy;
        }

        public static IOutputFormatStrategy CreateOutputStrategy(string format, FormatConfig? config = null)
        {
            var normalizedFormat = format.ToLowerInvariant();

            if (!OutputStrategies.TryGetValue(normalizedFormat, out var strategyType))
            {
                throw new NotSupportedException($"Output format '{format}' is not supported");
            }

            var strategy = (IOutputFormatStrategy)Activator.CreateInstance(strategyType)!;

            if (config != null)
            {
                strategy.Configure(config);
            }

            return strategy;
        }

        public static IEnumerable<string> GetSupportedFormats()
        {
            return InputStrategies.Keys;
        }
    }
}
