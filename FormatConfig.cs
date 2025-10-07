using System.Text;
using System.Globalization;

namespace FormatConverter
{
    public class FormatConfig
    {
        //general
        public int? IndentSize { get; set; }
        public bool Minify { get; set; }
        public bool PrettyPrint { get; set; } = true;
        public bool NoMetadata { get; set; }
        public bool SortKeys { get; set; }
        public Encoding Encoding { get; set; } = Encoding.UTF8;

        //json specific
        public bool JsonEscapeUnicode { get; set; }
        public bool JsonTrailingCommas { get; set; }
        public bool JsonQuoteNames { get; set; } = true;
        public bool JsonSingleQuotes { get; set; }

        //xml specific
        public string? XmlRootElement { get; set; }
        public string? XmlNamespace { get; set; }
        public string? XmlNamespacePrefix { get; set; }
        public bool XmlIncludeDeclaration { get; set; } = true;
        public bool XmlStandalone { get; set; }
        public bool XmlUseCData { get; set; }
        public bool XmlUseAttributes { get; set; }

        //yaml specific
        public bool YamlFlowStyle { get; set; }
        public bool YamlExplicitStart { get; set; }
        public bool YamlExplicitEnd { get; set; }
        public bool YamlQuoteStrings { get; set; }
        public bool YamlCanonical { get; set; }

        //toml specific
        public bool TomlArrayOfTables { get; set; }
        public bool TomlMultilineStrings { get; set; }
        public bool TomlStrictTypes { get; set; }

        //messagePack specific
        public bool MessagePackUseContractless { get; set; } = true;
        public bool MessagePackLz4Compression { get; set; }
        public bool MessagePackOldSpec { get; set; }
        public int ArrayChunkSize { get; set; } = 100;
        public int MapChunkSize { get; set; } = 50;

        //cbor specific
        public bool CborAllowIndefiniteLength { get; set; } = true;
        public bool CborAllowMultipleContent { get; set; }
        public bool CborCanonical { get; set; }
        public bool CborPreserveTags { get; set; }
        public bool CborUseDateTimeTags { get; set; }
        public bool CborUseBigNumTags { get; set; }

        //other
        public string? Compression { get; set; }
        public int CompressionLevel { get; set; } = 6;
        public string? SchemaFile { get; set; }
        public bool StrictMode { get; set; }
        public bool IgnoreErrors { get; set; }
        public bool UseStreaming { get; set; }
        public int ChunkSize { get; set; } = 100;
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

                //JSON
                JsonEscapeUnicode = options.JsonEscapeUnicode,
                JsonTrailingCommas = options.JsonTrailingCommas,
                JsonQuoteNames = options.JsonQuoteNames,
                JsonSingleQuotes = options.JsonSingleQuotes,

                //XML
                XmlRootElement = options.XmlRootElement,
                XmlNamespace = options.XmlNamespace,
                XmlNamespacePrefix = options.XmlNamespacePrefix,
                XmlIncludeDeclaration = options.XmlIncludeDeclaration,
                XmlStandalone = options.XmlStandalone,
                XmlUseCData = options.XmlUseCData,
                XmlUseAttributes = options.XmlUseAttributes,

                //YAML
                YamlFlowStyle = options.YamlFlowStyle,
                YamlExplicitStart = options.YamlExplicitStart,
                YamlExplicitEnd = options.YamlExplicitEnd,
                YamlQuoteStrings = options.YamlQuoteStrings,
                YamlCanonical = options.YamlCanonical,

                //TOML
                TomlArrayOfTables = options.TomlArrayOfTables,
                TomlMultilineStrings = options.TomlMultilineStrings,
                TomlStrictTypes = options.TomlStrictTypes,

                //MessagePack
                MessagePackUseContractless = options.MessagePackUseContractless,
                MessagePackLz4Compression = options.MessagePackLz4Compression,
                MessagePackOldSpec = options.MessagePackOldSpec,
                ArrayChunkSize = options.MessagePackArrayChunkSize,
                MapChunkSize = options.MessagePackMapChunkSize,

                //CBOR
                CborAllowIndefiniteLength = options.CborAllowIndefiniteLength,
                CborAllowMultipleContent = options.CborAllowMultipleContent,
                CborCanonical = options.CborCanonical,
                CborPreserveTags = options.CborPreserveTags,
                CborUseDateTimeTags = options.CborUseDateTimeTags,
                CborUseBigNumTags = options.CborUseBigNumTags,

                //Other
                Compression = options.Compression,
                CompressionLevel = options.CompressionLevel,
                SchemaFile = options.SchemaFile,
                StrictMode = options.StrictMode,
                IgnoreErrors = options.IgnoreErrors,
                UseStreaming = options.UseStreaming,
                ChunkSize = options.ChunkSize,
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

            GetTimeZone();

            if (IndentSize < 0)
            {
                throw new ArgumentException("Indent size cannot be negative");
            }

            if (CompressionLevel < 1 || CompressionLevel > 9)
            {
                throw new ArgumentException("Compression level must be between 1 and 9");
            }

            if (MaxDepth != null && MaxDepth <= 0)
            {
                throw new ArgumentException("Max depth must be positive");
            }

            if (ArrayChunkSize <= 0)
            {
                throw new ArgumentException("Array chunk size must be positive");
            }

            if (MapChunkSize <= 0)
            {
                throw new ArgumentException("Map chunk size must be positive");
            }

            if (!string.IsNullOrEmpty(Compression))
            {
                var validCompressions = new[] { "gzip", "deflate", "brotli", "lz4" };
                if (!Array.Exists(validCompressions, c => c.Equals(Compression, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException($"Unsupported compression format: {Compression}. Valid options: {string.Join(", ", validCompressions)}");
                }
            }

            if (!string.IsNullOrEmpty(NumberFormat))
            {
                var validFormats = new[] { "decimal", "hexadecimal", "scientific" };
                if (!Array.Exists(validFormats, f => f.Equals(NumberFormat, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException($"Unsupported number format: {NumberFormat}. Valid options: {string.Join(", ", validFormats)}");
                }
            }

            if (!string.IsNullOrEmpty(DateFormat))
            {
                var validFormats = new[] { "iso8601", "unix", "rfc3339" };
                if (!Array.Exists(validFormats, f => f.Equals(DateFormat, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        DateTime.Now.ToString(DateFormat);
                    }
                    catch
                    {
                        throw new ArgumentException($"Invalid date format: {DateFormat}");
                    }
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
            if (UseStreaming) sb.AppendLine($"  Streaming: enabled");
            if (!string.IsNullOrEmpty(Compression)) sb.AppendLine($"  Compression: {Compression} (level {CompressionLevel})");

            if (MessagePackLz4Compression) sb.AppendLine("  MessagePack: LZ4 compression enabled");
            if (ArrayChunkSize != 100) sb.AppendLine($"  MessagePack: Array chunk size = {ArrayChunkSize}");
            if (MapChunkSize != 50) sb.AppendLine($"  MessagePack: Map chunk size = {MapChunkSize}");

            if (CborCanonical) sb.AppendLine("  CBOR: Canonical encoding");
            if (CborPreserveTags) sb.AppendLine("  CBOR: Preserving semantic tags");

            return sb.ToString();
        }
    }
}