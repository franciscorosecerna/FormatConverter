using System.Text;

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

            config.ApplyPrecedenceRules();

            return config;
        }

        private void ApplyPrecedenceRules()
        {
            //1. Minify takes precedence over PrettyPrint
            if (Minify && PrettyPrint)
            {
                Console.Error.WriteLine("Warning: Both --minify and --pretty specified. Minify takes precedence.");
                PrettyPrint = false;
            }

            //2. Minify makes IndentSize irrelevant
            if (Minify && IndentSize.HasValue)
            {
                Console.Error.WriteLine("Warning: Indent size ignored when minifying.");
                IndentSize = null;
            }

            //3. StrictMode takes precedence over IgnoreErrors
            if (StrictMode && IgnoreErrors)
            {
                Console.Error.WriteLine("Warning: Both --strict and --ignore-errors specified. Strict mode takes precedence.");
                IgnoreErrors = false;
            }

            //4. StrictMode disables non-standard JSON input options
            if (StrictMode)
            {
                if (JsonAllowTrailingCommas)
                {
                    Console.Error.WriteLine("Warning: Trailing commas disabled in strict mode (invalid JSON).");
                    JsonAllowTrailingCommas = false;
                }

                if (!JsonStrictPropertyNames)
                {
                    Console.Error.WriteLine("Warning: Unquoted property names disabled in strict mode (invalid JSON).");
                    JsonStrictPropertyNames = true;
                }

                if (JsonAllowSingleQuotes)
                {
                    Console.Error.WriteLine("Warning: Single quotes disabled in strict mode (invalid JSON).");
                    JsonAllowSingleQuotes = false;
                }
            }

            //5. YAML Canonical takes precedence over flow style
            if (YamlCanonical && YamlFlowStyle)
            {
                Console.Error.WriteLine("Warning: Flow style disabled in canonical YAML mode.");
                YamlFlowStyle = false;
            }

            //6. YAML Canonical requires pretty print
            if (YamlCanonical && !PrettyPrint)
            {
                Console.Error.WriteLine("Warning: Pretty print enabled for canonical YAML.");
                PrettyPrint = true;
            }

            //7. CBOR Canonical disables indefinite length
            if (CborCanonical && CborAllowIndefiniteLength)
            {
                Console.Error.WriteLine("Warning: Indefinite length disabled in canonical CBOR mode.");
                CborAllowIndefiniteLength = false;
            }

            //8. ArrayWrap and FlattenArrays are mutually exclusive
            if (ArrayWrap && FlattenArrays)
            {
                Console.Error.WriteLine("Warning: Both --array-wrap and --flatten-arrays specified. Array wrap takes precedence.");
                FlattenArrays = false;
            }

            //9. MessagePack old spec doesn't support LZ4
            if (MessagePackOldSpec && MessagePackLz4Compression)
            {
                Console.Error.WriteLine("Warning: LZ4 compression not supported in old MessagePack spec. Compression disabled.");
                MessagePackLz4Compression = false;
            }

            //10. XML namespace prefix requires namespace
            if (!string.IsNullOrEmpty(XmlNamespacePrefix) && string.IsNullOrEmpty(XmlNamespace))
            {
                Console.Error.WriteLine("Warning: Namespace prefix specified without namespace. Prefix ignored.");
                XmlNamespacePrefix = null;
            }

            //11. Streaming requires valid chunk size
            if (UseStreaming && ChunkSize <= 0)
            {
                Console.Error.WriteLine("Warning: Invalid chunk size for streaming. Using default (100).");
                ChunkSize = 100;
            }

            //12. Minify disables TOML multiline strings
            if (Minify && TomlMultilineStrings)
            {
                Console.Error.WriteLine("Warning: Multiline strings disabled when minifying TOML.");
                TomlMultilineStrings = false;
            }

            //13. Invalid MaxDepth
            if (MaxDepth.HasValue && MaxDepth <= 0)
            {
                Console.Error.WriteLine("Warning: Invalid max depth. Unlimited depth will be used.");
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
                Console.WriteLine($"Warning: Unknown timezone '{Timezone}', using UTC");
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

            if (ArrayChunkSize <= 0)
            {
                throw new ArgumentException("Array chunk size must be positive");
            }

            if (MapChunkSize <= 0)
            {
                throw new ArgumentException("Map chunk size must be positive");
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
            if (MessagePackLz4Compression || ArrayChunkSize != 100 || MapChunkSize != 50)
            {
                sb.AppendLine("  MessagePack Options:");
                if (MessagePackLz4Compression) sb.AppendLine("    - LZ4 compression");
                if (ArrayChunkSize != 100) sb.AppendLine($"    - Array chunk size: {ArrayChunkSize}");
                if (MapChunkSize != 50) sb.AppendLine($"    - Map chunk size: {MapChunkSize}");
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