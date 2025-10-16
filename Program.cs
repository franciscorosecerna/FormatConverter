using CommandLine;
using System.IO.Compression;

namespace FormatConverter
{
    class Program
    {
        public const string VERSION = "2.0";
        public static readonly string[] BinaryFormats = ["messagepack", "cbor", "protobuf", "bxml"];

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
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool success = false;

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

                ValidateOptions(opts);
                var formatConfig = FormatConfig.FromOptions(opts);
                formatConfig.ValidateConfiguration();

                if (opts.Verbose)
                {
                    WriteInfo("Configuration:");
                    Console.WriteLine(formatConfig);
                }

                if (opts.UseStreaming)
                {
                    WriteInfo("Streaming mode enabled.");
                    using var cts = new CancellationTokenSource();

                    Console.CancelKeyPress += (s, e) =>
                    {
                        WriteWarning("Cancellation requested (Ctrl+C)...");
                        e.Cancel = true;
                        cts.Cancel();
                    };

                    return RunStreamingConversion(opts, formatConfig, cts.Token);
                }

                var result = ProcessRegularConversion(opts, formatConfig);
                success = true;
                return result;
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
            finally
            {
                stopwatch.Stop();
                if (success && !opts.Version && !opts.ListFormats)
                {
                    ConversionMetrics.RecordConversion(opts.InputFormat, opts.OutputFormat, stopwatch.Elapsed);
                }
            }
        }

        #region Regular conversion (no streaming)
        internal static int ProcessRegularConversion(Options opts, FormatConfig config)
        {
            var supported = FormatStrategyFactory.GetSupportedFormats();

            if (!supported.Contains(opts.InputFormat.ToLower()) ||
                !supported.Contains(opts.OutputFormat.ToLower()))
            {
                WriteError($"Unsupported format. Supported: {string.Join(", ", supported)}");
                return 3;
            }

            if (opts.InputFormat.Equals(opts.OutputFormat, StringComparison.OrdinalIgnoreCase) &&
                !HasFormatSpecificOptions(opts))
            {
                WriteWarning("Input and output formats are the same and no format-specific options provided.");
                return 0;
            }

            try
            {
                var inputText = ReadInput(opts.InputFile, opts.InputFormat, config);

                var inputStrategy = FormatStrategyFactory.CreateInputStrategy(opts.InputFormat, config);
                var outputStrategy = FormatStrategyFactory.CreateOutputStrategy(opts.OutputFormat, config);

                if (opts.Verbose) WriteInfo($"Parsing {opts.InputFormat}...");
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
            catch (Exception ex)
            {
                WriteError($"Conversion error: {ex.Message}");
                return 4;
            }
        }
        #endregion

        #region Streaming conversion
        internal static int RunStreamingConversion(Options opts, FormatConfig config, CancellationToken token)
        {
            var supportedFormats = FormatStrategyFactory.GetSupportedFormats();

            if (!supportedFormats.Contains(opts.InputFormat.ToLower()) ||
                !supportedFormats.Contains(opts.OutputFormat.ToLower()))
            {
                WriteError($"Unsupported format. Supported formats: {string.Join(", ", supportedFormats)}");
                return 3;
            }

            var inputStrategy = FormatStrategyFactory.CreateInputStrategy(opts.InputFormat, config);
            var outputStrategy = FormatStrategyFactory.CreateOutputStrategy(opts.OutputFormat, config);

            string outputFile = opts.OutputFile ?? GenerateOutputFileName(opts.InputFile, opts.OutputFormat);

            if (File.Exists(outputFile) && !opts.Force)
            {
                WriteError($"Output file '{outputFile}' already exists. Use --force to overwrite.");
                return 2;
            }

            if (opts.Verbose)
                WriteInfo($"Streaming from {opts.InputFile} → {outputFile}");

            using var inputStream = opts.InputFile == "-"
                ? Console.OpenStandardInput()
                : File.OpenRead(opts.InputFile);

            using var reader = new StreamReader(inputStream, config.Encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: false);

            using var outputStream = opts.OutputFile == "-"
                ? Console.OpenStandardOutput()
                : File.Create(outputFile);

            int count = 0;
            try
            {
                foreach (var tokenData in inputStrategy.ParseStream(opts.InputFile, token))
                {
                    token.ThrowIfCancellationRequested();

                    outputStrategy.SerializeStream([tokenData], outputStream, token);

                    count++;
                    if (opts.Verbose && count % 100 == 0)
                        WriteInfo($"Processed {count} records...");
                }

                WriteSuccess($"Streaming conversion completed ({count} records): {opts.InputFormat.ToUpper()} → {opts.OutputFormat.ToUpper()}");
                Console.WriteLine($"Output: {outputFile}");
                return 0;
            }
            catch (OperationCanceledException)
            {
                WriteWarning("Conversion canceled by user.");
                return 1;
            }
            catch (Exception ex)
            {
                WriteError($"Streaming conversion failed: {ex.Message}");
#if DEBUG
                Console.Error.WriteLine(ex);
#endif
                return 4;
            }
        }

        #endregion

        #region Helpers
        internal static bool HasFormatSpecificOptions(Options opts) =>
            opts.IndentSize.HasValue ||
            opts.Minify ||
            !opts.PrettyPrint ||
            opts.NoMetadata ||
            opts.SortKeys ||
            opts.JsonEscapeUnicode ||
            opts.JsonAllowTrailingCommas ||
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
                _ => throw new NotSupportedException($"Unsupported compression: {config.Compression}")
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

            var fileInfo = new FileInfo(path);

            // Advertencia para archivos grandes
            if (fileInfo.Length > 50 * 1024 * 1024) // 50MB
            {
                WriteWarning($"Large input file detected: {fileInfo.Length / 1024 / 1024}MB");
                WriteWarning("Consider using --streaming mode for better performance");
            }

            // Validar que archivos binarios no estén vacíos
            if (BinaryFormats.Contains(format.ToLower()) && fileInfo.Length == 0)
                throw new InvalidDataException("Binary input file is empty");

            return BinaryFormats.Contains(format.ToLower())
                ? Convert.ToBase64String(File.ReadAllBytes(path))
                : File.ReadAllText(path, config.Encoding);
        }

        internal static int WriteResult(Options opts, FormatConfig config, string result)
        {
            if (opts.InputFile == "-")
            {
                WriteToStream(Console.OpenStandardOutput(), result, config);
                return 0;
            }

            string outputFile = opts.OutputFile ?? GenerateOutputFileName(opts.InputFile, opts.OutputFormat);

            if (File.Exists(outputFile) && !opts.Force)
            {
                WriteError($"Output file '{outputFile}' already exists. Use --force to overwrite.");
                return 2;
            }

            WriteOutput(outputFile, result, opts.OutputFormat, config);

            WriteSuccess($"Success: {opts.InputFormat.ToUpper()} → {opts.OutputFormat.ToUpper()}");
            Console.WriteLine($"Output: {outputFile}");
            return 0;
        }

        internal static void WriteOutput(string path, string content, string format, FormatConfig config)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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
            string dir = Path.GetDirectoryName(inputFile) ?? "";
            string name = Path.GetFileNameWithoutExtension(inputFile);
            string ext = GetFileExtension(outputFormat);
            return Path.Combine(dir, name + ext);
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

        internal static void ShowVersionInfo()
        {
            WriteInfo($"FormatConverter CLI v{VERSION}");
            WriteInfo("Usage examples:");
            WriteInfo("  FormatConverter -i data.json --input-format json --output-format xml --pretty");
            WriteInfo("  FormatConverter -i data.xml --input-format xml --output-format yaml --minify");
            WriteInfo("  FormatConverter -i data.json --input-format json --output-format bxml --streaming");
        }

        internal static void ShowSupportedFormats()
        {
            WriteInfo("Supported formats:");
            foreach (var f in FormatStrategyFactory.GetSupportedFormats())
                Console.WriteLine($"  - {f}");
        }

        internal static void ValidateOptions(Options opts)
        {
            if (string.IsNullOrEmpty(opts.InputFile))
                throw new ArgumentException("Input file is required");

            if (string.IsNullOrEmpty(opts.InputFormat) || string.IsNullOrEmpty(opts.OutputFormat))
                throw new ArgumentException("Input and output formats are required");

            // Validar que el formato de compresión sea soportado
            if (!string.IsNullOrEmpty(opts.Compression) &&
                !new[] { "gzip", "deflate", "brotli" }.Contains(opts.Compression.ToLower()))
            {
                throw new ArgumentException($"Unsupported compression type: {opts.Compression}");
            }
        }

        internal static void WriteError(string msg) => WriteColored(ConsoleColor.Red, "ERROR: ", msg, true);
        internal static void WriteWarning(string msg) => WriteColored(ConsoleColor.Yellow, "WARNING: ", msg);
        internal static void WriteInfo(string msg) => WriteColored(ConsoleColor.Cyan, "INFO: ", msg);
        internal static void WriteSuccess(string msg) => WriteColored(ConsoleColor.Green, "SUCCESS: ", msg);

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
        #endregion
    }

    #region Metrics Class
    public static class ConversionMetrics
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _conversions = new();
        private static readonly List<TimeSpan> _durations = new();

        public static void RecordConversion(string from, string to, TimeSpan duration)
        {
            var key = $"{from.ToLower()}_{to.ToLower()}";
            _conversions.AddOrUpdate(key, 1, (_, count) => count + 1);
            _durations.Add(duration);
        }

        public static void ReportMetrics()
        {
            if (_conversions.IsEmpty) return;

            Program.WriteInfo("=== Conversion Statistics ===");
            foreach (var (conversion, count) in _conversions.OrderByDescending(x => x.Value))
            {
                var formats = conversion.Split('_');
                Console.WriteLine($"  {formats[0]} → {formats[1]}: {count} conversions");
            }

            if (_durations.Count != 0)
            {
                Console.WriteLine($"  Average duration: {_durations.Average(t => t.TotalMilliseconds):F0}ms");
                Console.WriteLine($"  Total conversions: {_durations.Count}");
            }
        }
    }
    #endregion
}