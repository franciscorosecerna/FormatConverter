using System.Text;
using System.Globalization;

namespace FormatConverter
{
    public class FormatConfig
    {
        public int? IndentSize { get; set; }
        public bool Minify { get; set; }
        public bool PrettyPrint { get; set; } = true;
        public bool NoMetadata { get; set; }
        public bool SortKeys { get; set; }
        public Encoding Encoding { get; set; } = Encoding.UTF8;
        public bool JsonEscapeUnicode { get; set; }
        public bool JsonTrailingCommas { get; set; }
        public bool JsonQuoteNames { get; set; } = true;
        public bool JsonSingleQuotes { get; set; }
        public string? XmlRootElement { get; set; }
        public string? XmlNamespace { get; set; }
        public string? XmlNamespacePrefix { get; set; }
        public bool XmlIncludeDeclaration { get; set; } = true;
        public bool XmlStandalone { get; set; }
        public bool XmlUseCData { get; set; }
        public bool XmlUseAttributes { get; set; }
        public bool YamlFlowStyle { get; set; }
        public bool YamlExplicitStart { get; set; }
        public bool YamlExplicitEnd { get; set; }
        public bool YamlQuoteStrings { get; set; }
        public bool YamlCanonical { get; set; }
        public string? Compression { get; set; }
        public int CompressionLevel { get; set; } = 6;
        public string? SchemaFile { get; set; }
        public bool StrictMode { get; set; }
        public bool IgnoreErrors { get; set; }
        public bool UseStreaming { get; set; }
        public int BufferSize { get; set; } = 4096;
        public string? NumberFormat { get; set; }
        public string? DateFormat { get; set; }
        public string? Timezone { get; set; }
        public bool ArrayWrap { get; set; }
        public bool FlattenArrays { get; set; }
        public int? MaxDepth { get; set; }

        public static FormatConfig FromOptions(Options options)
        {
            var config = new FormatConfig
            {
                IndentSize = options.IndentSize,
                Minify = options.Minify,
                PrettyPrint = options.PrettyPrint && !options.Minify,
                NoMetadata = options.NoMetadata,
                SortKeys = options.SortKeys,
                JsonEscapeUnicode = options.JsonEscapeUnicode,
                JsonTrailingCommas = options.JsonTrailingCommas,
                JsonQuoteNames = options.JsonQuoteNames,
                JsonSingleQuotes = options.JsonSingleQuotes,
                XmlRootElement = options.XmlRootElement,
                XmlNamespace = options.XmlNamespace,
                XmlNamespacePrefix = options.XmlNamespacePrefix,
                XmlIncludeDeclaration = options.XmlIncludeDeclaration,
                XmlStandalone = options.XmlStandalone,
                XmlUseCData = options.XmlUseCData,
                XmlUseAttributes = options.XmlUseAttributes,
                YamlFlowStyle = options.YamlFlowStyle,
                YamlExplicitStart = options.YamlExplicitStart,
                YamlExplicitEnd = options.YamlExplicitEnd,
                YamlQuoteStrings = options.YamlQuoteStrings,
                YamlCanonical = options.YamlCanonical,
                Compression = options.Compression,
                CompressionLevel = options.CompressionLevel,
                SchemaFile = options.SchemaFile,
                StrictMode = options.StrictMode,
                IgnoreErrors = options.IgnoreErrors,
                UseStreaming = options.UseStreaming,
                BufferSize = options.BufferSize,
                NumberFormat = options.NumberFormat,
                DateFormat = options.DateFormat,
                Timezone = options.Timezone,
                ArrayWrap = options.ArrayWrap,
                FlattenArrays = options.FlattenArrays,
                MaxDepth = options.MaxDepth
            };

            try
            {
                config.Encoding = Encoding.GetEncoding(options.Encoding);
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"Warning: Unknown encoding '{options.Encoding}', using UTF-8");
                config.Encoding = Encoding.UTF8;
            }

            return config;
        }

        public string GetIndentString()
        {
            if (!PrettyPrint || Minify) return "";

            if (IndentSize == null) return "  ";
            if (IndentSize == 0) return "\t";
            return new string(' ', IndentSize.Value);
        }

        public CultureInfo GetCulture()
        {
            return CultureInfo.InvariantCulture;
        }

        public TimeZoneInfo GetTimeZone()
        {
            if (string.IsNullOrEmpty(Timezone))
                return TimeZoneInfo.Utc;

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(Timezone);
            }
            catch
            {
                Console.WriteLine($"Warning: Unknown timezone '{Timezone}', using UTC");
                return TimeZoneInfo.Utc;
            }
        }

        public void ValidateConfiguration()
        {
            if (Minify && PrettyPrint)
            {
                Console.WriteLine("Warning: Both --minify and --pretty specified. Minify takes precedence.");
            }

            if (IndentSize < 0)
            {
                throw new ArgumentException("Indent size cannot be negative");
            }

            if (BufferSize <= 0)
            {
                throw new ArgumentException("Buffer size must be positive");
            }

            if (CompressionLevel < 1 || CompressionLevel > 9)
            {
                throw new ArgumentException("Compression level must be between 1 and 9");
            }

            if (MaxDepth <= 0)
            {
                throw new ArgumentException("Max depth must be positive");
            }

            if (!string.IsNullOrEmpty(Compression))
            {
                var validCompressions = new[] { "gzip", "deflate", "brotli" };
                if (!Array.Exists(validCompressions, c => c.Equals(Compression, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException($"Unsupported compression format: {Compression}. Valid options: {string.Join(", ", validCompressions)}");
                }
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Format Configuration:");
            sb.AppendLine($"  Pretty Print: {PrettyPrint}");
            sb.AppendLine($"  Minify: {Minify}");
            sb.AppendLine($"  Indent: {(IndentSize == 0 ? "tabs" : IndentSize?.ToString() ?? "default")}");
            sb.AppendLine($"  Encoding: {Encoding.EncodingName}");

            if (SortKeys) sb.AppendLine("  Sort Keys: enabled");
            if (NoMetadata) sb.AppendLine("  Strip Metadata: enabled");
            if (UseStreaming) sb.AppendLine("  Streaming: enabled");
            if (!string.IsNullOrEmpty(Compression)) sb.AppendLine($"  Compression: {Compression} (level {CompressionLevel})");

            return sb.ToString();
        }
    }
}