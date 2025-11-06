using FormatConverter.Bxml.BxmlWriter;
using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using System.Buffers;
using System.Text;

namespace FormatConverter.Bxml
{
    public class BxmlOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            Logger.WriteTrace(() => "Serialize: Starting BXML serialization");

            ArgumentNullException.ThrowIfNull(data);

            Logger.WriteDebug(() => $"Serialize: Input token type: {data.Type}");
            var processed = PreprocessToken(data);
            var result = SerializeRegular(processed);

            if (Config.StrictMode)
            {
                Logger.WriteDebug(() => "Serialize: Validating BXML in strict mode");
                ValidateBxml(result);
            }

            Logger.WriteSuccess($"Serialize: BXML serialization completed ({result.Length} characters)");
            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo(() => "SerializeStream: Starting stream serialization");

            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(output);

            var chunkSize = GetChunkSize();
            var isBinaryOutput = IsBinaryOutput();

            Logger.WriteDebug(() => $"SerializeStream: Chunk size: {chunkSize}, Binary output: {isBinaryOutput}");

            if (isBinaryOutput)
            {
                Logger.WriteDebug(() => "SerializeStream: Using binary serialization");
                SerializeStreamBinary(data, output, chunkSize, cancellationToken);
            }
            else
            {
                Logger.WriteDebug(() => "SerializeStream: Using text serialization");
                SerializeStreamText(data, output, chunkSize, cancellationToken);
            }

            Logger.WriteSuccess("SerializeStream: Stream serialization completed");
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo(() => $"SerializeStream: Writing to file '{outputPath}'");

            if (string.IsNullOrEmpty(outputPath))
            {
                Logger.WriteError(() => "SerializeStream: Output path is null or empty");
                throw new ArgumentNullException(nameof(outputPath));
            }

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);

            Logger.WriteSuccess($"SerializeStream: File written successfully to '{outputPath}'");
        }

        private string SerializeRegular(JToken data)
        {
            Logger.WriteTrace(() => $"SerializeRegular: Serializing token type {data.Type}");

            try
            {
                var bytes = SerializeToBxml(data);
                Logger.WriteDebug(() => $"SerializeRegular: Generated {bytes.Length} bytes");
                return FormatOutput(bytes);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning(() => $"BXML serialization error ignored: {ex.Message}");
                return CreateErrorOutput(ex.Message, ex.GetType().Name, data);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                Logger.WriteError(() => $"SerializeRegular: Fatal error - {ex.Message}");
                throw new FormatException($"BXML serialization failed: {ex.Message}", ex);
            }
        }

        private byte[] SerializeToBxml(JToken data)
        {
            Logger.WriteTrace(() => $"SerializeToBxml: Converting token type {data.Type} to BXML");

            using var stream = new MemoryStream();
            var options = CreateBxmlOptions();

            using var writer = new BxmlDocumentWriter(stream, options);
            writer.WriteDocument(data, "Root");

            var bytes = stream.ToArray();
            Logger.WriteTrace(() => $"SerializeToBxml: Generated {bytes.Length} bytes");
            return bytes;
        }

        private BxmlWriteOptions CreateBxmlOptions()
        {
            Logger.WriteTrace(() => "CreateBxmlOptions: Creating BXML write options");

            Logger.WriteDebug(() => $"CreateBxmlOptions: Encoding={Config.Encoding?.EncodingName ?? "default"}, " +
                            $"Endianness={Config.Endianness}, MaxDepth={Config.MaxDepth ?? 1000}, " +
                            $"CompressArrays={Config.CompressArrays}");

            return new BxmlWriteOptions
            {
                Encoding = Config.Encoding!,
                Endianness = Config.Endianness,
                MaxDepth = Config.MaxDepth ?? 1000,
                CompressArrays = Config.CompressArrays,
                LeaveOpen = false
            };
        }

        private void SerializeStreamBinary(IEnumerable<JToken> data, Stream output, int chunkSize, CancellationToken cancellationToken)
        {
            Logger.WriteTrace(() => "SerializeStreamBinary: Starting binary stream serialization");

            var buffer = new List<JToken>();
            var options = CreateBxmlOptions();
            var totalProcessed = 0;

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    Logger.WriteTrace(() => $"SerializeStreamBinary: Writing chunk of {buffer.Count} items");
                    WriteChunkToStreamBinary(buffer, output, options, cancellationToken);
                    totalProcessed += buffer.Count;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.WriteTrace(() => $"SerializeStreamBinary: Writing final chunk of {buffer.Count} items");
                WriteChunkToStreamBinary(buffer, output, options, cancellationToken);
                totalProcessed += buffer.Count;
            }

            output.Flush();
            Logger.WriteSuccess($"SerializeStreamBinary: Completed. Total items: {totalProcessed}");
        }

        private void SerializeStreamText(IEnumerable<JToken> data, Stream output, int chunkSize, CancellationToken cancellationToken)
        {
            Logger.WriteTrace(() => "SerializeStreamText: Starting text stream serialization");

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);
            var options = CreateBxmlOptions();

            var buffer = new List<JToken>();
            bool isFirst = true;
            var totalProcessed = 0;

            if (!Config.Minify)
            {
                writer.WriteLine("[");
            }

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    Logger.WriteTrace(() => $"SerializeStreamText: Writing chunk of {buffer.Count} items");
                    WriteChunkToStreamText(buffer, writer, options, cancellationToken, ref isFirst);
                    totalProcessed += buffer.Count;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.WriteTrace(() => $"SerializeStreamText: Writing final chunk of {buffer.Count} items");
                WriteChunkToStreamText(buffer, writer, options, cancellationToken, ref isFirst);
                totalProcessed += buffer.Count;
            }

            if (!Config.Minify)
            {
                writer.WriteLine();
                writer.WriteLine("]");
            }

            writer.Flush();
            Logger.WriteSuccess($"SerializeStreamText: Completed. Total items: {totalProcessed}");
        }

        private void WriteChunkToStreamBinary(List<JToken> items, Stream output, BxmlWriteOptions options, CancellationToken ct)
        {
            if (items.Count == 0)
            {
                Logger.WriteTrace(() => "WriteChunkToStreamBinary: Empty chunk, skipping");
                return;
            }

            Logger.WriteTrace(() => $"WriteChunkToStreamBinary: Processing {items.Count} items");

            const int initialBufferSize = 8192;
            byte[]? rentedBuffer = null;

            try
            {
                rentedBuffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
                Logger.WriteTrace(() => $"WriteChunkToStreamBinary: Rented buffer of {initialBufferSize} bytes");

                for (int i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var bytes = SerializeToBxml(items[i]);
                        Logger.WriteTrace(() => $"WriteChunkToStreamBinary: Item {i} serialized to {bytes.Length} bytes");

                        if (bytes.Length <= rentedBuffer.Length)
                        {
                            Buffer.BlockCopy(bytes, 0, rentedBuffer, 0, bytes.Length);
                            output.Write(rentedBuffer, 0, bytes.Length);
                        }
                        else
                        {
                            Logger.WriteDebug(() => $"WriteChunkToStreamBinary: Item {i} exceeds buffer, writing directly");
                            output.Write(bytes, 0, bytes.Length);
                        }
                    }
                    catch (Exception ex) when (Config.IgnoreErrors)
                    {
                        Logger.WriteWarning(() => $"BXML serialization error in item {i}: {ex.Message}");
                        var errorBytes = CreateErrorBxmlBytes(ex.Message, ex.GetType().Name, items[i]);

                        if (errorBytes.Length <= rentedBuffer.Length)
                        {
                            Buffer.BlockCopy(errorBytes, 0, rentedBuffer, 0, errorBytes.Length);
                            output.Write(rentedBuffer, 0, errorBytes.Length);
                        }
                        else
                        {
                            output.Write(errorBytes, 0, errorBytes.Length);
                        }
                    }
                }
            }
            finally
            {
                if (rentedBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                    Logger.WriteTrace(() => "WriteChunkToStreamBinary: Buffer returned to pool");
                }
            }
        }

        private void WriteChunkToStreamText(List<JToken> items, StreamWriter writer, BxmlWriteOptions options, CancellationToken ct, ref bool isFirst)
        {
            if (items.Count == 0)
            {
                Logger.WriteTrace(() => "WriteChunkToStreamText: Empty chunk, skipping");
                return;
            }

            Logger.WriteTrace(() => $"WriteChunkToStreamText: Processing {items.Count} items");

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var bytes = SerializeToBxml(items[i]);
                    var formatted = FormatOutput(bytes);
                    Logger.WriteTrace(() => $"WriteChunkToStreamText: Item {i} formatted ({formatted.Length} characters)");

                    if (!isFirst && !Config.Minify)
                    {
                        writer.Write(",");
                        writer.WriteLine();
                    }
                    else if (isFirst)
                    {
                        isFirst = false;
                    }

                    if (Config.Minify)
                    {
                        writer.Write(formatted);
                        if (i < items.Count - 1)
                            writer.Write(",");
                    }
                    else
                    {
                        writer.Write("  ");
                        writer.Write(formatted);
                    }
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => $"BXML serialization error in item {i}: {ex.Message}");
                    var errorOutput = CreateErrorOutput(ex.Message, ex.GetType().Name, items[i]);

                    if (!isFirst && !Config.Minify)
                    {
                        writer.Write(",");
                        writer.WriteLine();
                    }
                    else if (isFirst)
                    {
                        isFirst = false;
                    }

                    if (!Config.Minify)
                        writer.Write("  ");

                    writer.Write(errorOutput);
                }
            }
        }

        private string FormatOutput(byte[] bytes)
        {
            var format = Config.NumberFormat?.ToLower();
            Logger.WriteTrace(() => $"FormatOutput: Formatting {bytes.Length} bytes as '{format ?? "base64"}'");

            return format switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
                "binary" => FormatAsBinary(bytes),
                "raw" => Encoding.UTF8.GetString(bytes),
                _ => Convert.ToBase64String(bytes)
            };
        }

        private string FormatAsHex(byte[] bytes)
        {
            Logger.WriteTrace(() => $"FormatAsHex: Formatting {bytes.Length} bytes as hexadecimal");

            const int stackAllocThreshold = 256;

            if (bytes.Length <= stackAllocThreshold)
            {
                Logger.WriteTrace(() => "FormatAsHex: Using stack allocation");
                Span<char> hexChars = stackalloc char[bytes.Length * 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i].TryFormat(hexChars.Slice(i * 2, 2), out _, "x2");
                }

                if (Config.PrettyPrint && !Config.Minify)
                {
                    return FormatHexWithSpaces(hexChars);
                }

                return new string(hexChars);
            }
            else
            {
                Logger.WriteTrace(() => "FormatAsHex: Using heap allocation");
                var hex = Convert.ToHexString(bytes);

                if (Config.PrettyPrint && !Config.Minify)
                {
                    return string.Join(" ",
                        Enumerable.Range(0, hex.Length / 2)
                        .Select(i => hex.Substring(i * 2, 2)));
                }

                return hex.ToLowerInvariant();
            }
        }

        private static string FormatHexWithSpaces(ReadOnlySpan<char> hexChars)
        {
            char[]? buffer = null;
            try
            {
                int resultLength = hexChars.Length + (hexChars.Length / 2) - 1;
                buffer = ArrayPool<char>.Shared.Rent(resultLength);
                Span<char> result = buffer.AsSpan(0, resultLength);

                int writePos = 0;
                for (int i = 0; i < hexChars.Length; i += 2)
                {
                    if (writePos > 0)
                    {
                        result[writePos++] = ' ';
                    }
                    result[writePos++] = hexChars[i];
                    result[writePos++] = hexChars[i + 1];
                }

                return new string(result);
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }
            }
        }

        private string FormatAsBinary(byte[] bytes)
        {
            Logger.WriteTrace(() => $"FormatAsBinary: Formatting {bytes.Length} bytes as binary");

            const int bitsPerByte = 8;
            int totalChars = bytes.Length * bitsPerByte;

            char[]? buffer = null;
            try
            {
                if (Config.PrettyPrint && !Config.Minify)
                {
                    Logger.WriteTrace(() => "FormatAsBinary: Using pretty print format");
                    int sizeNeeded = totalChars + bytes.Length - 1;
                    buffer = ArrayPool<char>.Shared.Rent(sizeNeeded);
                    Span<char> span = buffer.AsSpan(0, sizeNeeded);

                    int pos = 0;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        if (i > 0)
                        {
                            span[pos++] = ' ';
                        }

                        byte b = bytes[i];
                        for (int bit = 7; bit >= 0; bit--)
                        {
                            span[pos++] = ((b >> bit) & 1) == 1 ? '1' : '0';
                        }
                    }

                    return new string(span);
                }
                else
                {
                    Logger.WriteTrace(() => "FormatAsBinary: Using compact format");
                    buffer = ArrayPool<char>.Shared.Rent(totalChars);
                    Span<char> span = buffer.AsSpan(0, totalChars);

                    int pos = 0;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        byte b = bytes[i];
                        for (int bit = 7; bit >= 0; bit--)
                        {
                            span[pos++] = ((b >> bit) & 1) == 1 ? '1' : '0';
                        }
                    }

                    return new string(span);
                }
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<char>.Shared.Return(buffer);
                    Logger.WriteTrace(() => "FormatAsBinary: Buffer returned to pool");
                }
            }
        }

        private byte[] CreateErrorBxmlBytes(string errorMessage, string errorType, JToken originalToken)
        {
            Logger.WriteTrace(() => "CreateErrorBxmlBytes: Creating error BXML output");

            try
            {
                var errorObj = new JObject
                {
                    ["error"] = errorMessage,
                    ["error_type"] = errorType,
                    ["original_type"] = originalToken.Type.ToString(),
                    ["timestamp"] = DateTime.UtcNow.ToString("O")
                };

                return SerializeToBxml(errorObj);
            }
            catch (Exception ex)
            {
                Logger.WriteWarning(() => $"CreateErrorBxmlBytes: Failed to serialize error as BXML, using JSON fallback - {ex.Message}");

                var fallbackError = new JObject
                {
                    ["error"] = "Error serialization failed",
                    ["original_error"] = errorMessage,
                    ["timestamp"] = DateTime.UtcNow.ToString("O")
                };

                return Config.Encoding.GetBytes(fallbackError.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

        private string CreateErrorOutput(string errorMessage, string errorType, JToken originalToken)
        {
            Logger.WriteTrace(() => "CreateErrorOutput: Creating error output");

            try
            {
                var errorBytes = CreateErrorBxmlBytes(errorMessage, errorType, originalToken);
                return FormatOutput(errorBytes);
            }
            catch (Exception ex)
            {
                Logger.WriteWarning(() => $"CreateErrorOutput: Failed to format error output, using base64 fallback - {ex.Message}");

                var errorObj = new JObject
                {
                    ["error"] = errorMessage,
                    ["error_type"] = errorType,
                    ["original_type"] = originalToken.Type.ToString(),
                    ["timestamp"] = DateTime.UtcNow.ToString("O")
                };

                var errorBytes = Config.Encoding.GetBytes(errorObj.ToString(Newtonsoft.Json.Formatting.None));
                return Convert.ToBase64String(errorBytes);
            }
        }

        private void ValidateBxml(string result)
        {
            Logger.WriteTrace(() => $"ValidateBxml: Starting validation ({result.Length} characters)");

            if (!Config.StrictMode) return;

            try
            {
                byte[] bytes;

                if (result.StartsWith("0x") || result.All(c => char.IsDigit(c)
                    || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || char.IsWhiteSpace(c)))
                {
                    Logger.WriteTrace(() => "ValidateBxml: Detected hex format");
                    bytes = ConvertFromHex(result);
                }
                else if (result.All(c => c == '0' || c == '1' || char.IsWhiteSpace(c)))
                {
                    Logger.WriteTrace(() => "ValidateBxml: Detected binary format");
                    bytes = ConvertFromBinary(result);
                }
                else
                {
                    Logger.WriteTrace(() => "ValidateBxml: Detected base64 format");
                    bytes = Convert.FromBase64String(result);
                }

                Logger.WriteDebug(() => $"ValidateBxml: Validating {bytes.Length} bytes");

                if (bytes.Length < 4)
                {
                    Logger.WriteError(() => "ValidateBxml: BXML too short (< 4 bytes)");
                    throw new FormatException("Invalid BXML: too short");
                }

                var magic = Encoding.ASCII.GetString(bytes, 0, 4);
                if (magic != "BXML")
                {
                    Logger.WriteError(() => $"ValidateBxml: Invalid magic bytes - expected 'BXML', got '{magic}'");
                    throw new FormatException($"Invalid BXML magic: expected 'BXML', got '{magic}'");
                }

                Logger.WriteDebug(() => "ValidateBxml: Validation successful");
            }
            catch (Exception ex) when (Config.StrictMode)
            {
                Logger.WriteError(() => $"ValidateBxml: Validation failed - {ex.Message}");
                throw new FormatException($"BXML validation failed: {ex.Message}", ex);
            }
        }

        private static byte[] ConvertFromHex(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("\n", "").Replace("\r", "");
            return Convert.FromHexString(hex);
        }

        private static byte[] ConvertFromBinary(string binary)
        {
            binary = binary.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            var byteCount = binary.Length / 8;
            var bytes = new byte[byteCount];

            for (int i = 0; i < byteCount; i++)
            {
                bytes[i] = Convert.ToByte(binary.Substring(i * 8, 8), 2);
            }

            return bytes;
        }

        private bool IsBinaryOutput()
        {
            var format = Config.NumberFormat?.ToLower();
            return format == "raw" || string.IsNullOrEmpty(format);
        }

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;
    }
}