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
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = PreprocessToken(data);
            var result = SerializeRegular(processed);

            if (Config.StrictMode)
            {
                ValidateBxml(result);
            }

            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            var chunkSize = GetChunkSize();
            var isBinaryOutput = IsBinaryOutput();

            if (isBinaryOutput)
            {
                SerializeStreamBinary(data, output, chunkSize, cancellationToken);
            }
            else
            {
                SerializeStreamText(data, output, chunkSize, cancellationToken);
            }
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
        }

        private string SerializeRegular(JToken data)
        {
            try
            {
                var bytes = SerializeToBxml(data);
                return FormatOutput(bytes);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                throw new FormatException($"BXML serialization failed: {ex.Message}", ex);
            }
        }

        private byte[] SerializeToBxml(JToken data)
        {
            using var stream = new MemoryStream();
            var options = CreateBxmlOptions();

            using var writer = new BxmlDocumentWriter(stream, options);
            writer.WriteDocument(data, "Root");

            return stream.ToArray();
        }

        private BxmlWriteOptions CreateBxmlOptions()
        {
            return new BxmlWriteOptions
            {
                Encoding = Config.Encoding,
                Endianness = Config.Endianness,
                MaxDepth = Config.MaxDepth!.Value > 0 ? Config.MaxDepth.Value : 1000,
                CompressArrays = Config.CompressArrays,
                LeaveOpen = false
            };
        }

        private void SerializeStreamBinary(IEnumerable<JToken> data, Stream output, int chunkSize, CancellationToken cancellationToken)
        {
            var buffer = new List<JToken>();
            var options = CreateBxmlOptions();

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    WriteChunkToStreamBinary(buffer, output, options, cancellationToken);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStreamBinary(buffer, output, options, cancellationToken);
            }

            output.Flush();
        }

        private void SerializeStreamText(IEnumerable<JToken> data, Stream output, int chunkSize, CancellationToken cancellationToken)
        {
            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);
            var options = CreateBxmlOptions();

            var buffer = new List<JToken>();
            bool isFirst = true;

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
                    WriteChunkToStreamText(buffer, writer, options, cancellationToken, ref isFirst);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStreamText(buffer, writer, options, cancellationToken, ref isFirst);
            }

            if (!Config.Minify)
            {
                writer.WriteLine();
                writer.WriteLine("]");
            }

            writer.Flush();
        }

        private void WriteChunkToStreamBinary(List<JToken> items, Stream output, BxmlWriteOptions options, CancellationToken ct)
        {
            if (items.Count == 0) return;

            const int initialBufferSize = 8192;
            byte[]? rentedBuffer = null;

            try
            {
                rentedBuffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);

                for (int i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var bytes = SerializeToBxml(items[i]);

                        if (bytes.Length <= rentedBuffer.Length)
                        {
                            Buffer.BlockCopy(bytes, 0, rentedBuffer, 0, bytes.Length);
                            output.Write(rentedBuffer, 0, bytes.Length);
                        }
                        else
                        {
                            output.Write(bytes, 0, bytes.Length);
                        }
                    }
                    catch (Exception ex) when (Config.IgnoreErrors)
                    {
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
                }
            }
        }

        private void WriteChunkToStreamText(List<JToken> items, StreamWriter writer, BxmlWriteOptions options, CancellationToken ct, ref bool isFirst)
        {
            if (items.Count == 0) return;

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var bytes = SerializeToBxml(items[i]);
                    var formatted = FormatOutput(bytes);

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
            return Config.NumberFormat?.ToLower() switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
                "binary" => FormatAsBinary(bytes),
                "raw" => Encoding.UTF8.GetString(bytes),
                _ => Convert.ToBase64String(bytes)
            };
        }

        private string FormatAsHex(byte[] bytes)
        {
            const int stackAllocThreshold = 256;

            if (bytes.Length <= stackAllocThreshold)
            {
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
            const int bitsPerByte = 8;
            int totalChars = bytes.Length * bitsPerByte;

            char[]? buffer = null;
            try
            {
                if (Config.PrettyPrint && !Config.Minify)
                {
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
                }
            }
        }

        private byte[] CreateErrorBxmlBytes(string errorMessage, string errorType, JToken originalToken)
        {
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
            catch
            {
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
            try
            {
                var errorBytes = CreateErrorBxmlBytes(errorMessage, errorType, originalToken);
                return FormatOutput(errorBytes);
            }
            catch
            {
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
            if (!Config.StrictMode) return;

            try
            {
                byte[] bytes;

                if (result.StartsWith("0x") || result.All(c => char.IsDigit(c)
                    || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || char.IsWhiteSpace(c)))
                {
                    bytes = ConvertFromHex(result);
                }
                else if (result.All(c => c == '0' || c == '1' || char.IsWhiteSpace(c)))
                {
                    bytes = ConvertFromBinary(result);
                }
                else
                {
                    bytes = Convert.FromBase64String(result);
                }

                if (bytes.Length < 4)
                    throw new FormatException("Invalid BXML: too short");

                var magic = Encoding.ASCII.GetString(bytes, 0, 4);
                if (magic != "BXML")
                    throw new FormatException($"Invalid BXML magic: expected 'BXML', got '{magic}'");
            }
            catch (Exception ex) when (Config.StrictMode)
            {
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