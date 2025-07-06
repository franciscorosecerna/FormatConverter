using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;

class Program
{
    public const string VERSION = "1.0.2";
    static void Main(string[] args)
    {
        if (args.Any(arg => arg == "--help" || arg == "--h"))
        {
            ShowUsage();
            return;
        }

        if (args.Any(arg => arg == "--version" || arg == "--v"))
        {
            Console.WriteLine($"FormatConverter CLI v{VERSION}");
            return;
        }
        bool forceOverwrite = args.Any(arg => arg == "--force" || arg == "--f");
        bool verbose = args.Contains("--verbose");

        var mainArgs = args.Where(a => !a.StartsWith("--") && !a.StartsWith("-")).ToArray();

        if (mainArgs.Length < 3)
        {
            ShowUsage();
            return;
        }

        string inputFile = mainArgs[0];
        string inputFormat = mainArgs[1].ToLower();
        string outputFormat = mainArgs[2].ToLower();

        if (!File.Exists(inputFile))
        {
            Console.WriteLine("Error: Input file not found.");
            return;
        }

        if (string.IsNullOrEmpty(inputFormat) || string.IsNullOrEmpty(outputFormat))
        {
            Console.WriteLine("Error: Input and output formats cannot be empty.");
            return;
        }

        if (!FormatConverter.FormatConverter.SupportedFormats.Contains(inputFormat) 
            || !FormatConverter.FormatConverter.SupportedFormats.Contains(outputFormat))
        {
            Console.WriteLine($"Error: Unsupported format. Supported formats: {string.Join(", ", FormatConverter.FormatConverter.SupportedFormats)}");
            return;
        }

        if (inputFormat != "json" && outputFormat != "json")
        {
            Console.WriteLine("Unsupported conversion: Only conversions involving JSON are allowed.");
            Console.WriteLine("Allowed: JSON → other format OR other format → JSON");
            Console.WriteLine("Not allowed: e.g., XML → YAML, CBOR → Protobuf, etc.");
            return;
        }

        if (inputFormat == outputFormat)
        {
            Console.WriteLine("Warning: Input and output formats are the same. No conversion needed.");
            return;
        }

        try
        {
            if (verbose) Console.WriteLine($"Reading input file: {inputFile}");
            string inputText = ReadInputFile(inputFile, inputFormat);

            if (verbose) Console.WriteLine($"Converting from {inputFormat} to {outputFormat}...");
            string result = FormatConverter.FormatConverter.ConvertFormat(inputText, inputFormat, outputFormat);

            string outputFile = GenerateOutputFileName(inputFile, outputFormat);

            if (File.Exists(outputFile) && !forceOverwrite)
            {
                Console.WriteLine($"Error: Output file '{outputFile}' already exists. Use --force to overwrite.");
                return;
            }

            if (verbose) Console.WriteLine($"Writing output to: {outputFile}");
            WriteOutputFile(outputFile, result, outputFormat);

            Console.WriteLine($"Success: Converted {inputFormat.ToUpper()} to {outputFormat.ToUpper()}.");
            Console.WriteLine($"Output: {outputFile}");
        }
        catch (FormatException ex)
        {
            Console.WriteLine($"Format error: {ex.Message}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"File I/O error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error: {ex.Message}");
#if DEBUG
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
#endif
        }
    }

    static void ShowUsage()
    {
        Console.WriteLine("Format Converter - Convert between different data formats");
        Console.WriteLine();
        Console.WriteLine("Usage: FormatConverter <input-file> <input-format> <output-format> [--force]");
        Console.WriteLine();
        Console.WriteLine($"Supported formats: " + string.Join(", ", FormatConverter.FormatConverter.SupportedFormats));
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --force, --f       Overwrite output file if it already exists");
        Console.WriteLine("  --verbose          Enable detailed output");
        Console.WriteLine("  --help, -h         Show this help message");
        Console.WriteLine("  --version, -v      Show version info");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  FormatConverter data.json json yaml");
        Console.WriteLine("  FormatConverter config.xml xml json --force");
        Console.WriteLine("  FormatConverter data.msgpack messagepack json");
    }

    static string ReadInputFile(string filePath, string format)
    {
        try
        {
            if (format == "messagepack" || format == "cbor" || format == "protobuf")
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                return Convert.ToBase64String(bytes);
            }
            else
            {
                return File.ReadAllText(filePath);
            }
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to read input file '{filePath}': {ex.Message}", ex);
        }
    }

    static void WriteOutputFile(string filePath, string content, string format)
    {
        try
        {
            if (format == "messagepack" || format == "cbor" || format == "protobuf")
            {
                byte[] bytes = Convert.FromBase64String(content);
                File.WriteAllBytes(filePath, bytes);
            }
            else
            {
                File.WriteAllText(filePath, content);
            }
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to write output file '{filePath}': {ex.Message}", ex);
        }
    }

    static string GenerateOutputFileName(string inputFile, string outputFormat)
    {
        string directory = Path.GetDirectoryName(inputFile) ?? "";
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputFile);
        string extension = GetFileExtension(outputFormat);

        return Path.Combine(directory, fileNameWithoutExtension + extension);
    }

    static string GetFileExtension(string format)
    {
        return format switch
        {
            "json" => ".json",
            "xml" => ".xml",
            "yaml" => ".yaml",
            "messagepack" => ".msgpack",
            "cbor" => ".cbor",
            "protobuf" => ".pb",
            "bxml" => ".bxml",
            _ => ".out"
        };
    }
}