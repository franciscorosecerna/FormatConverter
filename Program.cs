using CommandLine;
using FormatConverter.Logger;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using Newtonsoft.Json.Linq;
using System.IO.Compression;

namespace FormatConverter
{
    class Program
    {
        public const string VERSION = "2.1.0";
        public static readonly string[] BinaryFormats = ["messagepack", "cbor", "protobuf", "bxml"];
        internal static readonly string[] sourceArray = ["gzip", "deflate", "brotli", "lz4"];

        static int Main(string[] args)
        {
            return Parser.Default.ParseArguments<Options>(args)
                .MapResult(opts =>
                {
                    var logger = new ConsoleLogger
                    {
                        Verbosity = opts.Verbosity
                    };

                    return Run(opts, logger);
                },
                HandleParseErrors);
        }

        internal static int HandleParseErrors(IEnumerable<Error> errs)
        {
            if (errs.Any(x => x is HelpRequestedError || x is VersionRequestedError))
                return -1;
            return -2;
        }

        internal static int Run(Options opts, ILogger logger)
        {
            try
            {
                if (opts.Version)
                {
                    ShowVersionInfo(logger);
                    return 0;
                }

                if (opts.ListFormats)
                {
                    ShowSupportedFormats(logger);
                    return 0;
                }

                logger.WriteDebug($"Verbosity level: {logger.Verbosity}");

                ValidateOptions(opts, logger);

                var formatConfig = FormatConfig.FromOptions(opts);
                formatConfig.ValidateConfiguration();

                logger.WriteInfo("Configuration:");
                logger.WriteInfo(formatConfig.ToString());

                if (opts.UseStreaming)
                {
                    logger.WriteInfo("Streaming mode enabled.");
                    using var cts = new CancellationTokenSource();

                    Console.CancelKeyPress += (s, e) =>
                    {
                        logger.WriteWarning("Cancellation requested (Ctrl+C)...");
                        e.Cancel = true;
                        cts.Cancel();
                    };

                    return RunStreamingConversion(opts, formatConfig, logger, cts.Token);
                }

                return ProcessRegularConversion(opts, formatConfig, logger);
            }
            catch (ArgumentException ex)
            {
                logger.WriteError($"Configuration error: {ex.Message}");
                logger.WriteDebug($"Stack trace: {ex.StackTrace}");
                return 3;
            }
            catch (Exception ex)
            {
                logger.WriteError($"Unexpected error: {ex.Message}");
                logger.WriteDebug($"Exception type: {ex.GetType().Name}");
                logger.WriteDebug($"Stack trace: {ex.StackTrace}");
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

            logger.WriteDebug($"Supported formats: {string.Join(", ", supported)}");

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
                logger.WriteDebug($"Reading input from: {opts.InputFile}");

                var inputText = ReadInput(opts.InputFile, opts.InputFormat, config, logger);

                logger.WriteDebug($"Input text length: {inputText.Length} characters");

                var inputStrategy = FormatStrategyFactory.CreateInputStrategy(opts.InputFormat, config);
                var outputStrategy = FormatStrategyFactory.CreateOutputStrategy(opts.OutputFormat, config);

                logger.WriteInfo($"Parsing {opts.InputFormat}...");
                var parsedData = inputStrategy.Parse(inputText);

                logger.WriteDebug($"Parsed data type: {parsedData?.Type}");

                logger.WriteInfo($"Serializing to {opts.OutputFormat}...");
                string result = outputStrategy.Serialize(parsedData!);

                logger.WriteDebug($"Serialized output length: {result.Length} characters");

                if (!string.IsNullOrEmpty(config.Compression))
                {
                    logger.WriteInfo($"Applying {config.Compression} compression...");
                    var originalLength = result.Length;
                    result = CompressString(result, config, opts);

                    var compressionRatio = (1 - (double)result.Length / originalLength) * 100;
                    logger.WriteDebug($"Compression ratio: {compressionRatio:F2}%");
                }

                return WriteResult(opts, config, result, logger);
            }
            catch (Exception ex)
            {
                logger.WriteError($"Conversion error: {ex.Message}");
                logger.WriteDebug($"Exception details: {ex}");
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

            logger.WriteInfo($"Streaming from {opts.InputFile} → {outputFile}");
            logger.WriteDebug($"Input format: {opts.InputFormat}, Output format: {opts.OutputFormat}");
            logger.WriteDebug($"Encoding: {config.Encoding.EncodingName}");

            using var inputStream = opts.InputFile == "-"
                ? Console.OpenStandardInput()
                : File.OpenRead(opts.InputFile);

            using var reader = new StreamReader(inputStream, config.Encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: false);

            using var outputStream = opts.OutputFile == "-"
                ? Console.OpenStandardOutput()
                : File.Create(outputFile);

            int count = 0;
            var startTime = DateTime.UtcNow;

            try
            {
                foreach (var tokenData in inputStrategy.ParseStream(opts.InputFile, token))
                {
                    token.ThrowIfCancellationRequested();

                    outputStrategy.SerializeStream([tokenData], outputStream, token);

                    count++;
                    if (count % 100 == 0)
                    {
                        logger.WriteInfo($"Processed {count} records...");

                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        var rate = count / elapsed;
                        logger.WriteDebug($"Processing rate: {rate:F2} records/sec");
                    }
                }

                var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;
                logger.WriteSuccess($"Streaming conversion completed ({count} records in {totalTime:F2}s): {opts.InputFormat.ToUpper()} → {opts.OutputFormat.ToUpper()}");

                var avgRate = count / totalTime;
                logger.WriteDebug($"Average rate: {avgRate:F2} records/sec");

                Console.WriteLine($"Output: {outputFile}");
                return 0;
            }
            catch (OperationCanceledException)
            {
                logger.WriteWarning($"Conversion canceled by user after processing {count} records.");
                return 1;
            }
            catch (Exception ex)
            {
                logger.WriteError($"Streaming conversion failed at record {count}: {ex.Message}");
                logger.WriteDebug($"Exception details: {ex}");
#if DEBUG
                Console.Error.WriteLine(ex);
#endif
                return 4;
            }
        }

        #endregion

        #region Helpers
        internal static bool HasFormatSpecificOptions(Options opts) =>
            //general formatting
            opts.IndentSize.HasValue ||
            opts.Minify ||
            !opts.PrettyPrint ||
            opts.NoMetadata ||
            opts.SortKeys ||

            //JSON
            opts.JsonEscapeUnicode ||
            opts.JsonAllowTrailingCommas ||
            !opts.JsonStrictPropertyNames ||
            opts.JsonAllowSingleQuotes ||

            //XML
            !string.IsNullOrEmpty(opts.XmlRootElement) ||
            !string.IsNullOrEmpty(opts.XmlNamespace) ||
            !string.IsNullOrEmpty(opts.XmlNamespacePrefix) ||
            !opts.XmlIncludeDeclaration || //default true
            opts.XmlStandalone ||
            opts.XmlUseCData ||
            opts.XmlUseAttributes ||

            //YAML
            !opts.YamlPreserveLeadingZeros ||
            opts.YamlFlowStyle ||
            opts.YamlExplicitStart ||
            opts.YamlExplicitEnd ||
            opts.YamlQuoteStrings ||
            opts.YamlCanonical ||
            opts.YamlAllowDuplicateKeys ||

            //TOML
            !opts.TomlArrayOfTables ||
            opts.TomlMultilineStrings ||
            opts.TomlStrictTypes ||
            !string.IsNullOrEmpty(opts.TomlArrayWrapperKey) ||

            //MessagePack
            !opts.MessagePackUseContractless ||
            opts.MessagePackOldSpec ||

            //CBOR
            !opts.CborAllowIndefiniteLength ||
            opts.CborAllowMultipleContent ||
            opts.CborCanonical ||
            opts.CborPreserveTags ||
            opts.CborUseDateTimeTags ||
            opts.CborUseBigNumTags ||

            //BXML
            !string.Equals(opts.Endianness, "littleendian", StringComparison.OrdinalIgnoreCase) ||
            !opts.CompressArrays ||

            //compression
            !string.IsNullOrEmpty(opts.Compression) ||

            //transformation/data shaping
            opts.ArrayWrap ||
            opts.FlattenArrays ||
            opts.MaxDepth.HasValue ||

            //numeric/date/timezone formatting
            !string.IsNullOrEmpty(opts.NumberFormat) ||
            !string.IsNullOrEmpty(opts.DateFormat) ||
            !string.IsNullOrEmpty(opts.Timezone);

        internal static string CompressString(string input, FormatConfig config, Options opt)
        {
            var bytes = config.Encoding.GetBytes(input);
            if ((opt.InputFormat == "messagepack" || opt.OutputFormat == "messagepack") &&
                (string.Equals(config.Compression, "lz4", StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrEmpty(config.Compression)))
            {
                return input;
            }
            using var output = new MemoryStream();
            using (Stream compressionStream = config.Compression?.ToLower() switch
            {
                "gzip" => new GZipStream(output, CompressionLevel.Optimal),
                "deflate" => new DeflateStream(output, CompressionLevel.Optimal),
                "brotli" => new BrotliStream(output, CompressionLevel.Optimal),
                "lz4" => LZ4Stream.Encode(output, LZ4Level.L09_HC),
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
                logger.WriteDebug("Reading from standard input");
                using var reader = new StreamReader(Console.OpenStandardInput(), config.Encoding);
                return reader.ReadToEnd();
            }

            if (!File.Exists(path))
                throw new FileNotFoundException($"Input file '{path}' not found.");

            var fileInfo = new FileInfo(path);

            logger.WriteDebug($"Input file size: {fileInfo.Length} bytes ({fileInfo.Length / 1024.0:F2} KB)");

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
                logger.WriteDebug("Writing to standard output");
                WriteToStream(Console.OpenStandardOutput(), result, config);
                return 0;
            }

            string outputFile = opts.OutputFile ?? GenerateOutputFileName(opts.InputFile, opts.OutputFormat);

            if (File.Exists(outputFile) && !opts.Force)
            {
                logger.WriteError($"Output file '{outputFile}' already exists. Use --force to overwrite.");
                return 2;
            }

            try
            {
                logger.WriteDebug($"Writing output to: {outputFile}");

                WriteOutput(outputFile, result, opts.OutputFormat, config);

                var fileInfo = new FileInfo(outputFile);
                logger.WriteDebug($"Output file size: {fileInfo.Length} bytes ({fileInfo.Length / 1024.0:F2} KB)");

                logger.WriteSuccess($"Success: {opts.InputFormat.ToUpper()} → {opts.OutputFormat.ToUpper()}");
                Console.WriteLine($"Output: {outputFile}");
                return 0;
            }
            catch (Exception ex)
            {
                logger.WriteError($"Failed to write output file: {ex.Message}");
                logger.WriteDebug($"Exception details: {ex}");
                return 4;
            }
        }

        internal static void WriteOutput(string path, string content, string format, FormatConfig config)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
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

        internal static void ShowVersionInfo(ILogger logger)
        {
            logger.WriteInfo($"FormatConverter CLI v{VERSION}");
            logger.WriteInfo("Usage examples:");
            logger.WriteInfo("  FormatConverter -i data.json --input-format json --output-format xml --pretty");
            logger.WriteInfo("  FormatConverter -i data.xml --input-format xml --output-format yaml --minify");
            logger.WriteInfo("  FormatConverter -i data.json --input-format json --output-format bxml --streaming");
        }

        internal static void ShowSupportedFormats(ILogger logger)
        {
            logger.WriteInfo("Supported formats:");
            foreach (var f in FormatStrategyFactory.GetSupportedFormats())
                logger.WriteInfo($"  - {f}");
        }

        internal static void ValidateOptions(Options opts, ILogger logger)
        {
            logger.WriteDebug("Validating command line options");

            if (string.IsNullOrEmpty(opts.InputFile))
                throw new ArgumentException("Input file is required");

            if (string.IsNullOrEmpty(opts.InputFormat) || string.IsNullOrEmpty(opts.OutputFormat))
                throw new ArgumentException("Input and output formats are required");

            if (!string.IsNullOrEmpty(opts.Compression) &&
                !sourceArray.Contains(opts.Compression.ToLower()))
            {
                throw new ArgumentException($"Unsupported compression type: {opts.Compression}");
            }

            logger.WriteDebug("Options validation passed");
        }
        #endregion
    }
}