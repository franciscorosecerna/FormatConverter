using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            ShowUsage();
            return;
        }

        string? inputFile = args[0];
        string? inputFormat = args[1]?.ToLower();
        string? outputFormat = args[2]?.ToLower();
        bool forceOverwrite = args.Contains("--force");

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

        if (!FormatConverter.FormatConverter.SupportedFormats.Contains(inputFormat) || !FormatConverter.FormatConverter.SupportedFormats.Contains(outputFormat))
        {
            Console.WriteLine($"Error: Unsupported format. Supported formats: {string.Join(", ", FormatConverter.FormatConverter.SupportedFormats)}");
            return;
        }

        if (inputFormat == outputFormat)
        {
            Console.WriteLine("Warning: Input and output formats are the same. No conversion needed.");
            return;
        }

        try
        {
            string inputText = ReadInputFile(inputFile, inputFormat);
            string result = FormatConverter.FormatConverter.ConvertFormat(inputText, inputFormat, outputFormat);

            string outputFile = GenerateOutputFileName(inputFile, outputFormat);

            if (File.Exists(outputFile) && !forceOverwrite)
            {
                Console.WriteLine($"Error: Output file '{outputFile}' already exists. Use --force to overwrite.");
                return;
            }

            WriteOutputFile(outputFile, result, outputFormat);
            Console.WriteLine($"Success: Converted {inputFormat.ToUpper()} to {outputFormat.ToUpper()}. Output saved to '{outputFile}'");
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

        StringBuilder sb = new();
        foreach(var format in FormatConverter.FormatConverter.SupportedFormats)
        {
            sb.Append(format + ", ");
        }
        sb.Remove(sb.Length - 2, 1);
        Console.WriteLine("Supported formats: " + sb.ToString());
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --force    Overwrite output file if it already exists");
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
            if (format == "messagepack" || format == "cbor")
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
            if (format == "messagepack" || format == "cbor")
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
            _ => ".out"
        };
    }
}