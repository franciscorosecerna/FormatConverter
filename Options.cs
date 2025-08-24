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

        [Option("json-escape-unicode", HelpText = "Escape non-ASCII characters in JSON output")]
        public bool JsonEscapeUnicode { get; set; }

        [Option("json-trailing-commas", HelpText = "Allow trailing commas in JSON (non-standard but useful for editing)")]
        public bool JsonTrailingCommas { get; set; }

        [Option("json-quote-names", HelpText = "Always quote property names in JSON (default: true)")]
        public bool JsonQuoteNames { get; set; } = true;

        [Option("json-single-quotes", HelpText = "Use single quotes instead of double quotes in JSON")]
        public bool JsonSingleQuotes { get; set; }

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

        [Option("compress", MetaValue = "TYPE", HelpText = "Compress output using specified algorithm (gzip, deflate, brotli)")]
        public string? Compression { get; set; }

        [Option("compression-level", Default = 6, MetaValue = "LEVEL", HelpText = "Compression level (1-9, where 9 is maximum compression)")]
        public int CompressionLevel { get; set; } = 6;

        [Option("validate", MetaValue = "SCHEMA", HelpText = "Validate input against specified schema file")]
        public string? SchemaFile { get; set; }

        [Option("strict", HelpText = "Enable strict validation mode (fail on warnings)")]
        public bool StrictMode { get; set; }

        [Option("ignore-errors", HelpText = "Continue processing even if non-critical errors occur")]
        public bool IgnoreErrors { get; set; }

        [Option("streaming", HelpText = "Use streaming parser for large files (reduces memory usage)")]
        public bool UseStreaming { get; set; }

        [Option("buffer-size", Default = 4096, MetaValue = "BYTES", HelpText = "Buffer size for streaming operations")]
        public int BufferSize { get; set; } = 4096;

        [Option("number-format", MetaValue = "FORMAT", HelpText = "Number format for output (decimal, hexadecimal, scientific)")]
        public string? NumberFormat { get; set; }

        [Option("date-format", MetaValue = "FORMAT", HelpText = "Date format string (e.g., 'yyyy-MM-dd', 'ISO8601')")]
        public string? DateFormat { get; set; }

        [Option("timezone", MetaValue = "TZ", HelpText = "Target timezone for date conversion (e.g., 'UTC', 'America/New_York')")]
        public string? Timezone { get; set; }

        [Option("array-wrap", HelpText = "Wrap single items in arrays when converting")]
        public bool ArrayWrap { get; set; }

        [Option("flatten-arrays", HelpText = "Flatten nested arrays in output")]
        public bool FlattenArrays { get; set; }

        [Option("max-depth", MetaValue = "DEPTH", HelpText = "Maximum nesting depth for objects/arrays")]
        public int? MaxDepth { get; set; }
    }
}