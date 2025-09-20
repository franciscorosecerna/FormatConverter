using CommandLine;
using System.IO.Compression;

namespace FormatConverter
{
    class Program
    {
        public const string VERSION = "1.3.1";
        public static readonly string[] BinaryFormats = { "messagepack", "cbor", "protobuf", "bxml" };

        static int Main(string[] args)
        {
            return Parser.Default.ParseArguments<Options>(args)
                .MapResult(Run, HandleParseErrors);
        }

        internal static int HandleParseErrors(IEnumerable<Error> errs)
        {
            if (errs.Any(x => x is HelpRequestedError || x is VersionRequestedError))
                return -1;

            return -2;
        }

        internal static int Run(Options opts)
        {
            try
            {
                if (opts.Version)
                {
                    ShowVersionInfo();
                    return 0;
                }

                if (opts.ListFormats)
                {
                    ShowSupportedFormats();
                    return 0;
                }

                var formatConfig = FormatConfig.FromOptions(opts);
                formatConfig.ValidateConfiguration();

                if (opts.Verbose)
                {
                    WriteInfo("Configuration:");
                    Console.WriteLine(formatConfig);
                }

                return ProcessConversion(opts, formatConfig);
            }
            catch (ArgumentException ex)
            {
                WriteError($"Configuration error: {ex.Message}");
                return 3;
            }
            catch (Exception ex)
            {
                WriteError($"Unexpected error: {ex.Message}");
#if DEBUG
                Console.Error.WriteLine(ex);
#endif
                return 4;
            }
        }

        internal static int ProcessConversion(Options opts, FormatConfig config)
        {
            var supportedFormats = FormatStrategyFactory.GetSupportedFormats();

            if (!supportedFormats.Contains(opts.InputFormat.ToLower()) ||
                !supportedFormats.Contains(opts.OutputFormat.ToLower()))
            {
                WriteError($"Unsupported format. Supported formats: {string.Join(", ", supportedFormats)}");
                return 3;
            }

            if (opts.InputFormat.Equals(opts.OutputFormat, StringComparison.OrdinalIgnoreCase))
            {
                if (!HasFormatSpecificOptions(opts))
                {
                    WriteWarning("Input and output formats are the same with no format-specific options. No conversion needed.");
                    return 0;
                }
                WriteInfo("Same format conversion with format-specific options applied.");
            }

            try
            {
                if (opts.Verbose) WriteInfo($"Reading input from {(opts.InputFile == "-" ? "stdin" : opts.InputFile)}...");

                var inputText = ReadInput(opts.InputFile, opts.InputFormat, config);

                if (opts.Verbose) WriteInfo($"Parsing {opts.InputFormat}...");
                var inputStrategy = FormatStrategyFactory.CreateInputStrategy(opts.InputFormat, config);
                var outputStrategy = FormatStrategyFactory.CreateOutputStrategy(opts.OutputFormat, config);

                var parsedData = inputStrategy.Parse(inputText);

                if (opts.Verbose) WriteInfo($"Serializing to {opts.OutputFormat}...");
                string result = outputStrategy.Serialize(parsedData);

                if (!string.IsNullOrEmpty(config.Compression))
                {
                    if (opts.Verbose) WriteInfo($"Applying {config.Compression} compression...");
                    result = CompressString(result, config);
                }

                return WriteResult(opts, config, result);
            }
            catch (IOException ex)
            {
                WriteError($"File I/O error: {ex.Message}");
                return 2;
            }
            catch (FormatException ex)
            {
                WriteError($"Format error: {ex.Message}");
                return 3;
            }
            catch (Exception ex)
            {
                if (config.IgnoreErrors)
                {
                    WriteWarning($"Non-critical error ignored: {ex.Message}");
                    return 0;
                }
                WriteError($"Processing error: {ex.Message}");
                return 4;
            }
        }

        internal static int WriteResult(Options opts, FormatConfig config, string result)
        {
            if (opts.InputFile == "-")
            {
                if (opts.Verbose) WriteInfo("Writing result to stdout...");
                WriteToStream(Console.OpenStandardOutput(), result, config);
                return 0;
            }

            string outputFile = opts.OutputFile ?? GenerateOutputFileName(opts.InputFile, opts.OutputFormat);

            if (File.Exists(outputFile) && !opts.Force)
            {
                WriteError($"Output file '{outputFile}' already exists. Use --force to overwrite.");
                return 2;
            }

            if (opts.Verbose) WriteInfo($"Writing output to {outputFile}");
            WriteOutput(outputFile, result, opts.OutputFormat, config);

            WriteSuccess($"Success: {opts.InputFormat.ToUpper()} → {opts.OutputFormat.ToUpper()}");
            Console.WriteLine($"Output: {outputFile}");

            if (opts.Verbose)
            {
                var inputSize = GetFileSize(opts.InputFile);
                var outputSize = GetFileSize(outputFile);
                var ratio = inputSize > 0 ? (double)outputSize / inputSize : 1.0;
                WriteInfo($"Size: {FormatBytes(inputSize)} → {FormatBytes(outputSize)} ({ratio:P1})");
            }

            return 0;
        }

        internal static bool HasFormatSpecificOptions(Options opts) =>
            opts.IndentSize.HasValue ||
            opts.Minify ||
            !opts.PrettyPrint ||
            opts.NoMetadata ||
            opts.SortKeys ||
            opts.JsonEscapeUnicode ||
            opts.JsonTrailingCommas ||
            !string.IsNullOrEmpty(opts.XmlRootElement) ||
            opts.YamlFlowStyle ||
            !string.IsNullOrEmpty(opts.Compression);

        internal static string CompressString(string input, FormatConfig config)
        {
            var bytes = config.Encoding.GetBytes(input);

            using var output = new MemoryStream();
            using (Stream compressionStream = config.Compression?.ToLower() switch
            {
                "gzip" => new GZipStream(output, CompressionLevel.Optimal),
                "deflate" => new DeflateStream(output, CompressionLevel.Optimal),
                "brotli" => new BrotliStream(output, CompressionLevel.Optimal),
                 _ => throw new NotSupportedException($"Compression format not supported: {config.Compression}")
            })
            {
                compressionStream.Write(bytes, 0, bytes.Length);
            }

            return Convert.ToBase64String(output.ToArray());
        }

        internal static string ReadInput(string path, string format, FormatConfig config)
        {
            if (path == "-")
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), config.Encoding);
                return reader.ReadToEnd();
            }

            if (!File.Exists(path))
                throw new FileNotFoundException($"Input file '{path}' not found.");

            return BinaryFormats.Contains(format.ToLower())
                ? Convert.ToBase64String(File.ReadAllBytes(path))
                : File.ReadAllText(path, config.Encoding);
        }

        internal static void WriteOutput(string path, string content, string format, FormatConfig config)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (BinaryFormats.Contains(format.ToLower()))
                File.WriteAllBytes(path, Convert.FromBase64String(content));
            else
                File.WriteAllText(path, content, config.Encoding);
        }

        internal static void WriteToStream(Stream stream, string content, FormatConfig config)
        {
            using var writer = new StreamWriter(stream, config.Encoding);
            writer.Write(content);
        }

        internal static string GenerateOutputFileName(string inputFile, string outputFormat)
        {
            string directory = Path.GetDirectoryName(inputFile) ?? "";
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputFile);
            string extension = GetFileExtension(outputFormat);
            return Path.Combine(directory, fileNameWithoutExtension + extension);
        }

        internal static string GetFileExtension(string format) => format.ToLower() switch
        {
            "json" => ".json",
            "xml" => ".xml",
            "yaml" => ".yaml",
            "messagepack" => ".msgpack",
            "cbor" => ".cbor",
            "protobuf" => ".pb",
            "bxml" => ".bxml",
            "toml" => ".toml",
            _ => ".out"
        };

        internal static long GetFileSize(string path) =>
            File.Exists(path) ? new FileInfo(path).Length : 0;

        internal static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        internal static void ShowVersionInfo()
        {
            WriteInfo($"FormatConverter CLI v{VERSION}");

            WriteInfo("Usage examples:");
            WriteInfo("  FormatConverter -i data.json --input-format json --output-format xml --pretty");
            WriteInfo("  FormatConverter -i data.xml --input-format xml --output-format yaml --minify");
            WriteInfo("  FormatConverter -i data.json --input-format json --output-format json --indent 4 --sort-keys");
        }

        internal static void ShowSupportedFormats()
        {
            WriteInfo("Supported formats:");
            foreach (var f in FormatStrategyFactory.GetSupportedFormats())
                Console.WriteLine($"  - {f}");

            WriteInfo("\nFormat-specific options:");
            WriteInfo("JSON: --json-escape-unicode, --json-trailing-commas, --json-single-quotes");
            WriteInfo("XML:  --xml-root, --xml-namespace, --xml-attributes, --xml-cdata");
            WriteInfo("YAML: --yaml-flow-style, --yaml-explicit-start, --yaml-canonical");
            WriteInfo("TOML: --toml-array-of-tables, --toml-multiline-strings, --toml-strict-types");
            WriteInfo("\nGeneral options:");
            WriteInfo("--indent <size>     Set indentation (0 for tabs)");
            WriteInfo("--minify            Remove unnecessary whitespace");
            WriteInfo("--sort-keys         Sort object keys alphabetically");
            WriteInfo("--compress <type>   Compress output (gzip, deflate)");
            WriteInfo("--encoding <enc>    Set file encoding (utf-8, utf-16, etc.)");
        }

        #region Console Helpers
        internal static void WriteColored(ConsoleColor color, string prefix, string msg, bool stderr = false)
        {
            var original = Console.ForegroundColor;
            Console.ForegroundColor = color;

            if (stderr)
                Console.Error.WriteLine($"{prefix}{msg}");
            else
                Console.WriteLine($"{prefix}{msg}");

            Console.ForegroundColor = original;
        }

        internal static void WriteError(string msg) => WriteColored(ConsoleColor.Red, "ERROR: ", msg, true);
        internal static void WriteWarning(string msg) => WriteColored(ConsoleColor.Yellow, "WARNING: ", msg);
        internal static void WriteInfo(string msg) => WriteColored(ConsoleColor.Cyan, "INFO: ", msg);
        internal static void WriteSuccess(string msg) => WriteColored(ConsoleColor.Green, "SUCCESS: ", msg);
        #endregion
    }
}