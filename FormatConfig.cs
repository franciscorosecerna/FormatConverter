using FormatConverter.Bxml.BxmlWriter;
using FormatConverter.Logger;
using System.Text;

namespace FormatConverter
{
    public class FormatConfig
    {
        private static readonly ConsoleLogger _logger = new();

        //general
        public int? IndentSize { get; set; }
        public bool Minify { get; set; }
        public bool PrettyPrint { get; set; } = true;
        public bool NoMetadata { get; set; }
        public bool SortKeys { get; set; }
        public Encoding Encoding { get; set; } = Encoding.UTF8;

        //json specific
        public bool JsonEscapeUnicode { get; set; }
        public bool JsonAllowTrailingCommas { get; set; }
        public bool JsonStrictPropertyNames { get; set; } = true;
        public bool JsonAllowSingleQuotes { get; set; }

        //xml specific
        public string? XmlRootElement { get; set; }
        public string? XmlNamespace { get; set; }
        public string? XmlNamespacePrefix { get; set; }
        public bool XmlIncludeDeclaration { get; set; } = true;
        public bool XmlStandalone { get; set; }
        public bool XmlUseCData { get; set; }
        public bool XmlUseAttributes { get; set; }

        //yaml specific
        public bool YamlPreserveLeadingZeros { get; set; } = true;
        public bool YamlFlowStyle { get; set; }
        public bool YamlAllowDuplicateKeys { get; set; }
        public bool YamlExplicitStart { get; set; }
        public bool YamlExplicitEnd { get; set; }
        public bool YamlQuoteStrings { get; set; }
        public bool YamlCanonical { get; set; }

        //toml specific
        public bool TomlArrayOfTables { get; set; }
        public bool TomlMultilineStrings { get; set; }
        public bool TomlStrictTypes { get; set; }
        public string? TomlArrayWrapperKey { get; set; }

        //messagePack specific
        public bool MessagePackUseContractless { get; set; } = true;
        public bool MessagePackOldSpec { get; set; }

        //cbor specific
        public bool CborAllowIndefiniteLength { get; set; } = true;
        public bool CborAllowMultipleContent { get; set; }
        public bool CborCanonical { get; set; }
        public bool CborPreserveTags { get; set; }
        public bool CborUseDateTimeTags { get; set; }
        public bool CborUseBigNumTags { get; set; }

        //bxml specific
        public Endianness Endianness { get; set; }
        public bool CompressArrays { get; set; }

        //other
        public string? Compression { get; set; }
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
                JsonAllowTrailingCommas = options.JsonAllowTrailingCommas,
                JsonStrictPropertyNames = options.JsonStrictPropertyNames,
                JsonAllowSingleQuotes = options.JsonAllowSingleQuotes,

                //XML
                XmlRootElement = options.XmlRootElement,
                XmlNamespace = options.XmlNamespace,
                XmlNamespacePrefix = options.XmlNamespacePrefix,
                XmlIncludeDeclaration = options.XmlIncludeDeclaration,
                XmlStandalone = options.XmlStandalone,
                XmlUseCData = options.XmlUseCData,
                XmlUseAttributes = options.XmlUseAttributes,

                //YAML
                YamlPreserveLeadingZeros = options.YamlPreserveLeadingZeros,
                YamlFlowStyle = options.YamlFlowStyle,
                YamlExplicitStart = options.YamlExplicitStart,
                YamlExplicitEnd = options.YamlExplicitEnd,
                YamlQuoteStrings = options.YamlQuoteStrings,
                YamlCanonical = options.YamlCanonical,
                YamlAllowDuplicateKeys = options.YamlAllowDuplicateKeys,

                //TOML
                TomlArrayOfTables = options.TomlArrayOfTables,
                TomlMultilineStrings = options.TomlMultilineStrings,
                TomlStrictTypes = options.TomlStrictTypes,
                TomlArrayWrapperKey = options.TomlArrayWrapperKey,

                //MessagePack
                MessagePackUseContractless = options.MessagePackUseContractless,
                MessagePackOldSpec = options.MessagePackOldSpec,

                //CBOR
                CborAllowIndefiniteLength = options.CborAllowIndefiniteLength,
                CborAllowMultipleContent = options.CborAllowMultipleContent,
                CborCanonical = options.CborCanonical,
                CborPreserveTags = options.CborPreserveTags,
                CborUseDateTimeTags = options.CborUseDateTimeTags,
                CborUseBigNumTags = options.CborUseBigNumTags,

                //BXML
                CompressArrays = options.CompressArrays,
                Endianness = options.Endianness.ToLowerInvariant() switch
                {
                    "bigendian" => Endianness.BigEndian,
                    _ => Endianness.LittleEndian
                },

                //Other
                Compression = options.Compression,
                StrictMode = options.StrictMode,
                IgnoreErrors = options.IgnoreErrors,
                UseStreaming = options.UseStreaming,
                ChunkSize = options.ChunkSize,
                NumberFormat = options.NumberFormat,
                DateFormat = options.DateFormat,
                Timezone = options.Timezone,
                ArrayWrap = options.ArrayWrap,
                FlattenArrays = options.FlattenArrays,
                MaxDepth = options.MaxDepth ?? 100,
            };

            try
            {
                config.Encoding = Encoding.GetEncoding(options.Encoding);
            }
            catch (ArgumentException)
            {
                _logger.WriteWarning($"Unknown encoding '{options.Encoding}', using UTF-8");
                config.Encoding = Encoding.UTF8;
            }

            config.ApplyPrecedenceRules();

            return config;
        }

        private void ApplyPrecedenceRules()
        {
            //1. Minify takes precedence over PrettyPrint
            if (Minify && PrettyPrint)
            {
                _logger.WriteWarning("Both --minify and --pretty specified. Minify takes precedence.");
                PrettyPrint = false;
            }

            //2. Minify makes IndentSize irrelevant
            if (Minify && IndentSize.HasValue)
            {
                _logger.WriteWarning("Warning: Indent size ignored when minifying.");
                IndentSize = null;
            }

            //3. StrictMode takes precedence over IgnoreErrors
            if (StrictMode && IgnoreErrors)
            {
                _logger.WriteWarning("Warning: Both --strict and --ignore-errors specified. Strict mode takes precedence.");
                IgnoreErrors = false;
            }

            //4. StrictMode disables non-standard JSON input options
            if (StrictMode)
            {
                if (JsonAllowTrailingCommas)
                {
                    _logger.WriteWarning("Warning: Trailing commas disabled in strict mode (invalid JSON).");
                    JsonAllowTrailingCommas = false;
                }

                if (!JsonStrictPropertyNames)
                {
                    _logger.WriteWarning("Warning: Unquoted property names disabled in strict mode (invalid JSON).");
                    JsonStrictPropertyNames = true;
                }

                if (JsonAllowSingleQuotes)
                {
                    _logger.WriteWarning("Warning: Single quotes disabled in strict mode (invalid JSON).");
                    JsonAllowSingleQuotes = false;
                }
            }

            //5. YAML Canonical takes precedence over flow style
            if (YamlCanonical && YamlFlowStyle)
            {
                _logger.WriteWarning("Warning: Flow style disabled in canonical YAML mode.");
                YamlFlowStyle = false;
            }

            //6. YAML Canonical requires pretty print
            if (YamlCanonical && !PrettyPrint)
            {
                _logger.WriteWarning("Warning: Pretty print enabled for canonical YAML.");
                PrettyPrint = true;
            }

            //7. CBOR Canonical disables indefinite length
            if (CborCanonical && CborAllowIndefiniteLength)
            {
                _logger.WriteWarning("Warning: Indefinite length disabled in canonical CBOR mode.");
                CborAllowIndefiniteLength = false;
            }

            //8. ArrayWrap and FlattenArrays are mutually exclusive
            if (ArrayWrap && FlattenArrays)
            {
                _logger.WriteWarning("Warning: Both --array-wrap and --flatten-arrays specified. Array wrap takes precedence.");
                FlattenArrays = false;
            }

            //9. MessagePack old spec doesn't support LZ4
            if (MessagePackOldSpec && Compression?.ToLowerInvariant() == "lz4")
            {
                _logger.WriteWarning("Warning: LZ4 compression not supported in old MessagePack spec. Compression disabled.");
                Compression = null;
            }

            //10. XML namespace prefix requires namespace
            if (!string.IsNullOrEmpty(XmlNamespacePrefix) && string.IsNullOrEmpty(XmlNamespace))
            {
                _logger.WriteWarning("Warning: Namespace prefix specified without namespace. Prefix ignored.");
                XmlNamespacePrefix = null;
            }

            //11. Streaming requires valid chunk size
            if (UseStreaming && ChunkSize <= 0)
            {
                _logger.WriteWarning("Warning: Invalid chunk size for streaming. Using default (100).");
                ChunkSize = 100;
            }

            //12. Minify disables TOML multiline strings
            if (Minify && TomlMultilineStrings)
            {
                _logger.WriteWarning("Warning: Multiline strings disabled when minifying TOML.");
                TomlMultilineStrings = false;
            }

            //13. Invalid MaxDepth
            if (MaxDepth.HasValue && MaxDepth <= 0)
            {
                _logger.WriteWarning("Warning: Invalid max depth. Unlimited depth will be used.");
                MaxDepth = null;
            }
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
                _logger.WriteWarning($"Unknown timezone '{Timezone}', using UTC");
                return TimeZoneInfo.Utc;
            }
        }

        public void ValidateConfiguration()
        {
            GetTimeZone();

            if (IndentSize < 0)
            {
                throw new ArgumentException("Indent size cannot be negative");
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
                var validFormats = new[] { "decimal", "hexadecimal", "scientific", "raw" };
                if (!Array.Exists(validFormats, f => f.Equals(NumberFormat, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException($"Unsupported number format: {NumberFormat}. Valid options: {string.Join(", ", validFormats)}");
                }
            }

            if (!string.IsNullOrEmpty(DateFormat))
            {
                var validFormats = new[] { "iso8601", "unix", "rfc3339", "timestamp" };
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

            if (ChunkSize <= 0)
            {
                throw new ArgumentException("Chunk size must be positive");
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
            if (StrictMode) sb.AppendLine("  Strict Mode: enabled");
            if (IgnoreErrors) sb.AppendLine("  Ignore Errors: enabled");
            if (UseStreaming) sb.AppendLine($"  Streaming: enabled (chunk size: {ChunkSize})");
            if (!string.IsNullOrEmpty(Compression)) sb.AppendLine($"  Compression: {Compression}");

            //JSON options
            if (JsonAllowSingleQuotes || JsonAllowTrailingCommas || !JsonStrictPropertyNames || JsonEscapeUnicode)
            {
                sb.AppendLine("  JSON Options:");
                if (JsonAllowSingleQuotes) sb.AppendLine("    - Allow single quotes (input only)");
                if (JsonAllowTrailingCommas) sb.AppendLine("    - Allow trailing commas (input only)");
                if (!JsonStrictPropertyNames) sb.AppendLine("    - Allow unquoted property names (input only)");
                if (JsonEscapeUnicode) sb.AppendLine("    - Escape unicode (output)");
            }

            //MessagePack options
            if (MessagePackOldSpec || MessagePackUseContractless)
            {
                sb.AppendLine("  MessagePack Options:");
                if (MessagePackOldSpec) sb.AppendLine("    - OldSpec");
                if (MessagePackUseContractless) sb.AppendLine("    - ContractLess");
            }

            //CBOR options
            if (CborCanonical || CborPreserveTags || CborUseDateTimeTags || CborUseBigNumTags)
            {
                sb.AppendLine("  CBOR Options:");
                if (CborCanonical) sb.AppendLine("    - Canonical encoding");
                if (CborPreserveTags) sb.AppendLine("    - Preserve tags");
                if (CborUseDateTimeTags) sb.AppendLine("    - DateTime tags");
                if (CborUseBigNumTags) sb.AppendLine("    - BigNum tags");
            }

            return sb.ToString();
        }
    }
}