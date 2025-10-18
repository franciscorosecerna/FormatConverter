using CommandLine;
using FormatConverter.Logger;
using System.IO.Compression;

namespace FormatConverter
{
    class Program
    {
        public const string VERSION = "2.0";
        public static readonly string[] BinaryFormats = ["messagepack", "cbor", "protobuf", "bxml"];
        internal static readonly string[] sourceArray = ["gzip", "deflate", "brotli"];

        private static readonly ConsoleLogger _logger = new();

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

                ValidateOptions(opts);
                var formatConfig = FormatConfig.FromOptions(opts);
                formatConfig.ValidateConfiguration();

                if (opts.Verbose)
                {
                    _logger.WriteInfo("Configuration:");
                    Console.WriteLine(formatConfig);
                }

                if (opts.UseStreaming)
                {
                    _logger.WriteInfo("Streaming mode enabled.");
                    using var cts = new CancellationTokenSource();

                    Console.CancelKeyPress += (s, e) =>
                    {
                        _logger.WriteWarning("Cancellation requested (Ctrl+C)...");
                        e.Cancel = true;
                        cts.Cancel();
                    };
                    return RunStreamingConversion(opts, formatConfig, _logger, cts.Token);
                }

                var result = ProcessRegularConversion(opts, formatConfig, _logger);
                return result;
            }
            catch (ArgumentException ex)
            {
                _logger.WriteError($"Configuration error: {ex.Message}");
                return 3;
            }
            catch (Exception ex)
            {
                _logger.WriteError($"Unexpected error: {ex.Message}");
#if DEBUG
                Console.Error.WriteLine(ex);
#endif
                return 4;
            }
        }

        #region Regular conversion (no streaming)
        internal static int ProcessRegularConversion(Options opts, FormatConfig config, ILogger logger)
        {
            var supported = FormatStrategyFactory.GetSupportedFormats();

            if (!supported.Contains(opts.InputFormat.ToLower()) ||
                !supported.Contains(opts.OutputFormat.ToLower()))
            {
                logger.WriteError($"Unsupported format. Supported: {string.Join(", ", supported)}");
                return 3;
            }

            if (opts.InputFormat.Equals(opts.OutputFormat, StringComparison.OrdinalIgnoreCase) &&
                !HasFormatSpecificOptions(opts))
            {
                logger.WriteWarning("Input and output formats are the same and no format-specific options provided.");
                return 0;
            }

            try
            {
                var inputText = ReadInput(opts.InputFile, opts.InputFormat, config, logger);

                var inputStrategy = FormatStrategyFactory.CreateInputStrategy(opts.InputFormat, config);
                var outputStrategy = FormatStrategyFactory.CreateOutputStrategy(opts.OutputFormat, config);

                if (opts.Verbose) logger.WriteInfo($"Parsing {opts.InputFormat}...");
                var parsedData = inputStrategy.Parse(inputText);

                if (opts.Verbose) logger.WriteInfo($"Serializing to {opts.OutputFormat}...");
                string result = outputStrategy.Serialize(parsedData);

                if (!string.IsNullOrEmpty(config.Compression))
                {
                    if (opts.Verbose) logger.WriteInfo($"Applying {config.Compression} compression...");
                    result = CompressString(result, config);
                }

                return WriteResult(opts, config, result, logger);
            }
            catch (Exception ex)
            {
                logger.WriteError($"Conversion error: {ex.Message}");
                return 4;
            }
        }
        #endregion

        #region Streaming conversion
        internal static int RunStreamingConversion(Options opts, FormatConfig config, ILogger logger, CancellationToken token)
        {
            var supportedFormats = FormatStrategyFactory.GetSupportedFormats();

            if (!supportedFormats.Contains(opts.InputFormat.ToLower()) ||
                !supportedFormats.Contains(opts.OutputFormat.ToLower()))
            {
                logger.WriteError($"Unsupported format. Supported formats: {string.Join(", ", supportedFormats)}");
                return 3;
            }

            var inputStrategy = FormatStrategyFactory.CreateInputStrategy(opts.InputFormat, config);
            var outputStrategy = FormatStrategyFactory.CreateOutputStrategy(opts.OutputFormat, config);

            string outputFile = opts.OutputFile ?? GenerateOutputFileName(opts.InputFile, opts.OutputFormat);

            if (File.Exists(outputFile) && !opts.Force)
            {
                logger.WriteError($"Output file '{outputFile}' already exists. Use --force to overwrite.");
                return 2;
            }

            if (opts.Verbose)
                logger.WriteInfo($"Streaming from {opts.InputFile} → {outputFile}");

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
                        logger.WriteInfo($"Processed {count} records...");
                }

                logger.WriteSuccess($"Streaming conversion completed ({count} records): {opts.InputFormat.ToUpper()} → {opts.OutputFormat.ToUpper()}");
                Console.WriteLine($"Output: {outputFile}");
                return 0;
            }
            catch (OperationCanceledException)
            {
                logger.WriteWarning("Conversion canceled by user.");
                return 1;
            }
            catch (Exception ex)
            {
                logger.WriteError($"Streaming conversion failed: {ex.Message}");
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

        internal static string ReadInput(string path, string format, FormatConfig config, ILogger logger)
        {
            if (path == "-")
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), config.Encoding);
                return reader.ReadToEnd();
            }

            if (!File.Exists(path))
                throw new FileNotFoundException($"Input file '{path}' not found.");

            var fileInfo = new FileInfo(path);

            if (fileInfo.Length > 50 * 1024 * 1024)
            {
                logger.WriteWarning($"Large input file detected: {fileInfo.Length / 1024 / 1024}MB");
                logger.WriteWarning("Consider using --streaming mode for better performance");
            }

            if (BinaryFormats.Contains(format.ToLower()) && fileInfo.Length == 0)
                throw new InvalidDataException("Binary input file is empty");

            return BinaryFormats.Contains(format.ToLower())
                ? Convert.ToBase64String(File.ReadAllBytes(path))
                : File.ReadAllText(path, config.Encoding);
        }

        internal static int WriteResult(Options opts, FormatConfig config, string result, ILogger logger)
        {
            if (opts.InputFile == "-")
            {
                WriteToStream(Console.OpenStandardOutput(), result, config);
                return 0;
            }

            string outputFile = opts.OutputFile ?? GenerateOutputFileName(opts.InputFile, opts.OutputFormat);

            if (File.Exists(outputFile) && !opts.Force)
            {
                logger.WriteError($"Output file '{outputFile}' already exists. Use --force to overwrite.");
                return 2;
            }

            FileLogger? fileLogger = null;
            try
            {
                if (!BinaryFormats.Contains(opts.OutputFormat.ToLower()))
                {
                    fileLogger = new FileLogger(outputFile, config.Encoding, includeTimestamps: false);
                    fileLogger.WriteSuccess(result);
                }
                else
                {
                    WriteOutput(outputFile, result, opts.OutputFormat, config);
                }

                logger.WriteSuccess($"Success: {opts.InputFormat.ToUpper()} → {opts.OutputFormat.ToUpper()}");
                Console.WriteLine($"Output: {outputFile}");
                return 0;
            }
            finally
            {
                fileLogger?.Dispose();
            }
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
            _logger.WriteInfo($"FormatConverter CLI v{VERSION}");
            _logger.WriteInfo("Usage examples:");
            _logger.WriteInfo("  FormatConverter -i data.json --input-format json --output-format xml --pretty");
            _logger.WriteInfo("  FormatConverter -i data.xml --input-format xml --output-format yaml --minify");
            _logger.WriteInfo("  FormatConverter -i data.json --input-format json --output-format bxml --streaming");
        }

        internal static void ShowSupportedFormats()
        {
            _logger.WriteInfo("Supported formats:");
            foreach (var f in FormatStrategyFactory.GetSupportedFormats())
                Console.WriteLine($"  - {f}");
        }

        internal static void ValidateOptions(Options opts)
        {
            if (string.IsNullOrEmpty(opts.InputFile))
                throw new ArgumentException("Input file is required");

            if (string.IsNullOrEmpty(opts.InputFormat) || string.IsNullOrEmpty(opts.OutputFormat))
                throw new ArgumentException("Input and output formats are required");

            if (!string.IsNullOrEmpty(opts.Compression) &&
                !sourceArray.Contains(opts.Compression.ToLower()))
            {
                throw new ArgumentException($"Unsupported compression type: {opts.Compression}");
            }
        }
        #endregion
    }
}