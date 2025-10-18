using CommandLine;

namespace FormatConverter
{
    public class Options
    {
        [Option('i', "input", MetaValue = "FILE", HelpText = "Path to input file (or '-' for stdin)", Required = true)]
        public string InputFile { get; set; } = "";

        [Option("input-format", MetaValue = "FORMAT", HelpText = "Format of the input data (json, xml, yaml, messagepack, cbor, protobuf, bxml)", Required = true)]
        public string InputFormat { get; set; } = "";

        [Option("output-format", MetaValue = "FORMAT", HelpText = "Desired format for the output data (json, xml, yaml, messagepack, cbor, protobuf, bxml)", Required = true)]
        public string OutputFormat { get; set; } = "";

        [Option('o', "output", MetaValue = "FILE", HelpText = "Output file path (if not specified, will be auto-generated based on input file name)")]
        public string? OutputFile { get; set; }

        [Option('f', "force", HelpText = "Overwrite output file if it already exists")]
        public bool Force { get; set; }

        [Option('v', "verbose", HelpText = "Enable detailed output")]
        public bool Verbose { get; set; }

        [Option("version", HelpText = "Show version info and exit")]
        public bool Version { get; set; }

        [Option('l', "list-formats", HelpText = "List all supported formats and exit")]
        public bool ListFormats { get; set; }

        [Option("encoding", Default = "utf-8", HelpText = "Input/output file encoding (utf-8, utf-16, ascii, etc.)")]
        public string Encoding { get; set; } = "utf-8";

        [Option("indent", MetaValue = "SIZE", HelpText = "Indentation size for formatted output (JSON, XML, YAML). Use 0 for tabs")]
        public int? IndentSize { get; set; }

        [Option("minify", HelpText = "Minify output by removing unnecessary whitespace")]
        public bool Minify { get; set; }

        [Option("pretty", HelpText = "Pretty print output with proper formatting and indentation")]
        public bool PrettyPrint { get; set; } = true;

        [Option("no-metadata", HelpText = "Strip metadata, comments, and processing instructions from output")]
        public bool NoMetadata { get; set; }

        [Option("sort-keys", HelpText = "Sort object keys alphabetically in output")]
        public bool SortKeys { get; set; }

        #region JSON Options

        [Option("json-escape-unicode", HelpText = "Escape non-ASCII characters in JSON output")]
        public bool JsonEscapeUnicode { get; set; }

        [Option("json-allow-trailing-commas", HelpText = "Allow trailing commas in JSON input (non-standard but commonly used)")]
        public bool JsonAllowTrailingCommas { get; set; }

        [Option("json-strict-property-names", Default = true, HelpText = "Require quoted property names in JSON input (default: true, set to false to allow unquoted names)")]
        public bool JsonStrictPropertyNames { get; set; } = true;

        [Option("json-allow-single-quotes", HelpText = "Allow single quotes in JSON input (non-standard, only works with direct string parsing)")]
        public bool JsonAllowSingleQuotes { get; set; }

        #endregion

        #region XML Options

        [Option("xml-root", MetaValue = "NAME", HelpText = "Custom root element name for XML output")]
        public string? XmlRootElement { get; set; }

        [Option("xml-namespace", MetaValue = "URI", HelpText = "XML namespace URI for output")]
        public string? XmlNamespace { get; set; }

        [Option("xml-namespace-prefix", MetaValue = "PREFIX", HelpText = "XML namespace prefix")]
        public string? XmlNamespacePrefix { get; set; }

        [Option("xml-declaration", HelpText = "Include XML declaration (<?xml version='1.0'?>)")]
        public bool XmlIncludeDeclaration { get; set; } = true;

        [Option("xml-standalone", HelpText = "Set standalone='yes' in XML declaration")]
        public bool XmlStandalone { get; set; }

        [Option("xml-cdata", HelpText = "Wrap text content in CDATA sections when necessary")]
        public bool XmlUseCData { get; set; }

        [Option("xml-attributes", HelpText = "Convert simple properties to XML attributes instead of elements")]
        public bool XmlUseAttributes { get; set; }

        #endregion

        #region YAML Options

        [Option("yaml-flow-style", HelpText = "Use flow style (inline) for YAML output")]
        public bool YamlFlowStyle { get; set; }

        [Option("yaml-explicit-start", HelpText = "Include explicit document start marker (---) in YAML")]
        public bool YamlExplicitStart { get; set; }

        [Option("yaml-explicit-end", HelpText = "Include explicit document end marker (...) in YAML")]
        public bool YamlExplicitEnd { get; set; }

        [Option("yaml-quote-strings", HelpText = "Force quoting of all string values in YAML")]
        public bool YamlQuoteStrings { get; set; }

        [Option("yaml-canonical", HelpText = "Use canonical YAML format")]
        public bool YamlCanonical { get; set; }

        [Option("yaml-allow-duplicate-keys", HelpText = "Allow duplicate keys in YAML")]
        public bool YamlAllowDuplicateKeys { get; set; }

        #endregion

        #region TOML Options

        [Option("toml-array-of-tables", Default = true, HelpText = "Convert object arrays to TOML array of tables format ([[table]])")]
        public bool TomlArrayOfTables { get; set; } = true;

        [Option("toml-multiline-strings", Default = false, HelpText = "Use multiline string format (\"\"\"...\"\"\") for long strings")]
        public bool TomlMultilineStrings { get; set; } = false;

        [Option("toml-strict-types", Default = false, HelpText = "Enforce strict type conversion (fail if types cannot be represented in TOML)")]
        public bool TomlStrictTypes { get; set; } = false;

        [Option("toml-array-wrapper-key", HelpText = "Wrap root array; uses 'item' if not specified")]
        public string? TomlArrayWrapperKey { get; set; }

        #endregion

        #region MessagePack Options

        [Option("msgpack-contractless", Default = true, HelpText = "Use contractless resolver for MessagePack (allows dynamic types)")]
        public bool MessagePackUseContractless { get; set; } = true;

        [Option("msgpack-lz4", HelpText = "Enable LZ4 compression for MessagePack output")]
        public bool MessagePackLz4Compression { get; set; }

        [Option("msgpack-old-spec", HelpText = "Use old MessagePack specification format")]
        public bool MessagePackOldSpec { get; set; }

        #endregion

        #region CBOR Options

        [Option("cbor-indefinite-length", Default = true, HelpText = "Allow indefinite-length arrays and maps in CBOR")]
        public bool CborAllowIndefiniteLength { get; set; } = true;

        [Option("cbor-multiple-content", HelpText = "Allow multiple top-level CBOR items in sequence")]
        public bool CborAllowMultipleContent { get; set; }

        [Option("cbor-canonical", HelpText = "Use canonical CBOR encoding (sorted keys, minimal encoding)")]
        public bool CborCanonical { get; set; }

        [Option("cbor-tags", HelpText = "Preserve CBOR semantic tags in conversion")]
        public bool CborPreserveTags { get; set; }

        [Option("cbor-datetime-tag", HelpText = "Use CBOR datetime tags (tag 0 or 1) for date values")]
        public bool CborUseDateTimeTags { get; set; }

        [Option("cbor-bignum", HelpText = "Use CBOR bignum tags (tag 2/3) for very large numbers")]
        public bool CborUseBigNumTags { get; set; }

        #endregion

        #region BXML Options

        [Option("bxml-endian", Default = "littleendian", 
            HelpText = "Use 'littleendian' for Intel/x86 systems (default) or 'bigendian' for network order and some RISC architectures.")]
        public string Endianness { get; set; } = "littleendian";

        [Option("bxml-compressArrays", Default = true, HelpText = "Enables compression for homogeneous arrays to reduce output size")]
        public bool CompressArrays { get; set; } = true;
        #endregion

        #region Compression Options

        [Option("compress", MetaValue = "TYPE", HelpText = "Compress output using specified algorithm (gzip, deflate, brotli, lz4)")]
        public string? Compression { get; set; }

        #endregion

        #region Validation Options

        [Option("validate", MetaValue = "SCHEMA", HelpText = "Validate input against specified schema file")]
        public string? SchemaFile { get; set; }

        [Option("strict", HelpText = "Enable strict validation mode (fail on warnings)")]
        public bool StrictMode { get; set; }

        [Option("ignore-errors", HelpText = "Continue processing even if non-critical errors occur")]
        public bool IgnoreErrors { get; set; }

        #endregion

        #region Streaming Options

        [Option("streaming", HelpText = "Use streaming parser for large files (reduces memory usage)")]
        public bool UseStreaming { get; set; }

        [Option("chunk-size", Default = 100, HelpText = "Number of items per chunk when streaming")]
        public int ChunkSize { get; set; } = 100;

        #endregion

        #region Format Options

        [Option("number-format", MetaValue = "FORMAT", HelpText = "Number format for output (decimal, hexadecimal, scientific, binary, raw)")]
        public string? NumberFormat { get; set; }

        [Option("date-format", MetaValue = "FORMAT", HelpText = "Date format string (e.g., 'yyyy-MM-dd', 'ISO8601', 'unix')")]
        public string? DateFormat { get; set; }

        [Option("timezone", MetaValue = "TZ", HelpText = "Target timezone for date conversion (e.g., 'UTC', 'America/New_York')")]
        public string? Timezone { get; set; }

        #endregion

        #region Data Transformation Options

        [Option("array-wrap", HelpText = "Wrap single items in arrays when converting")]
        public bool ArrayWrap { get; set; }

        [Option("flatten-arrays", HelpText = "Flatten nested arrays in output")]
        public bool FlattenArrays { get; set; }

        [Option("max-depth", MetaValue = "DEPTH", HelpText = "Maximum nesting depth for objects/arrays")]
        public int? MaxDepth { get; set; }

        #endregion
    }
}