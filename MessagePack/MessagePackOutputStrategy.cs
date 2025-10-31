using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using MessagePack;
using MessagePack.Resolvers;
using System.Text;
using System.Buffers;

namespace FormatConverter.MessagePack
{
    public class MessagePackOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = PreprocessToken(data);
            var result = SerializeToken(processed);

            if (Config.StrictMode)
            {
                ValidateMessagePack(result);
            }

            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            var options = GetMessagePackOptions();
            var chunkSize = GetChunkSize();
            var isBinaryOutput = IsBinaryOutput();

            if (isBinaryOutput)
            {
                SerializeStreamBinary(data, output, options, chunkSize, cancellationToken);
            }
            else
            {
                SerializeStreamText(data, output, options, chunkSize, cancellationToken);
            }
        }

        private void SerializeStreamBinary(IEnumerable<JToken> data, Stream output, MessagePackSerializerOptions options, int chunkSize, CancellationToken cancellationToken)
        {
            var buffer = new List<JToken>();

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    WriteChunkToStreamBinary(buffer, options, output, cancellationToken);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStreamBinary(buffer, options, output, cancellationToken);
            }

            output.Flush();
        }

        private void SerializeStreamText(IEnumerable<JToken> data, Stream output, MessagePackSerializerOptions options, int chunkSize, CancellationToken cancellationToken)
        {
            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);

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
                    WriteChunkToStreamText(buffer, options, writer, cancellationToken, ref isFirst);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStreamText(buffer, options, writer, cancellationToken, ref isFirst);
            }

            if (!Config.Minify)
            {
                writer.WriteLine();
                writer.WriteLine("]");
            }

            writer.Flush();
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
        }

        private void WriteChunkToStreamBinary(List<JToken> items, MessagePackSerializerOptions options, Stream output, CancellationToken ct)
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
                        var bytes = SerializeTokenToBytes(items[i], options);

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
                        Logger.WriteWarning($"MessagePack serialization error in item {i}: {ex.Message}");
                        var errorBytes = CreateErrorOutputBytes(ex.Message, ex.GetType().Name, items[i], options);

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

        private void WriteChunkToStreamText(List<JToken> items, MessagePackSerializerOptions options, StreamWriter writer, CancellationToken ct, ref bool isFirst)
        {
            if (items.Count == 0) return;

            const int charBufferSize = 8192;
            char[]? charBuffer = null;

            try
            {
                charBuffer = ArrayPool<char>.Shared.Rent(charBufferSize);

                for (int i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var bytes = SerializeTokenToBytes(items[i], options);
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
                        Logger.WriteWarning($"MessagePack serialization error in item {i}: {ex.Message}");
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
            finally
            {
                if (charBuffer != null)
                {
                    ArrayPool<char>.Shared.Return(charBuffer);
                }
            }
        }

        private string SerializeToken(JToken token)
        {
            try
            {
                var obj = ConvertJTokenToObject(token);

                if (obj == null && token.Type != JTokenType.Null)
                {
                    if (Config.IgnoreErrors)
                    {
                        Logger.WriteWarning("Failed to convert token to object");
                        return CreateErrorOutput("Failed to convert token to object", "ConversionError", token);
                    }
                    throw new FormatException("Failed to convert JSON to object for MessagePack serialization");
                }

                var options = GetMessagePackOptions();
                var bytes = MessagePackSerializer.Serialize(obj, options);

                return FormatOutput(bytes);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"MessagePack serialization error ignored: {ex.Message}");
                return CreateErrorOutput(ex.Message, ex.GetType().Name, token);
            }
        }

        private void ValidateMessagePack(string result)
        {
            try
            {
                var bytes = result.StartsWith("0x") || result.All(c => char.IsDigit(c)
                || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || char.IsWhiteSpace(c))
                    ? ConvertFromHex(result)
                    : Convert.FromBase64String(result);

                var options = GetMessagePackOptions();
                MessagePackSerializer.Deserialize<object>(bytes, options);
            }
            catch when (!Config.StrictMode) { }
        }

        private static byte[] ConvertFromHex(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("\n", "").Replace("\r", "");
            return Convert.FromHexString(hex);
        }

        private bool IsBinaryOutput()
        {
            var format = Config.NumberFormat?.ToLower();
            return format == "raw" || string.IsNullOrEmpty(format);
        }

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;

        private byte[] SerializeTokenToBytes(JToken token, MessagePackSerializerOptions options)
        {
            var obj = ConvertJTokenToObject(token);
            return MessagePackSerializer.Serialize(obj, options);
        }

        private MessagePackSerializerOptions GetMessagePackOptions()
        {
            var options = MessagePackSerializerOptions.Standard
                .WithResolver(ContractlessStandardResolver.Instance);

            if (!string.IsNullOrEmpty(Config.Compression))
            {
                options = Config.Compression.ToLower() switch
                {
                    "lz4" => options.WithCompression(MessagePackCompression.Lz4Block),
                    "lz4array" => options.WithCompression(MessagePackCompression.Lz4BlockArray),
                    _ => options
                };
            }

            if (Config.StrictMode)
            {
                options = options.WithSecurity(MessagePackSecurity.UntrustedData);
            }

            if (Config.MaxDepth.HasValue)
            {
                var security = MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(Config.MaxDepth.Value);
                options = options.WithSecurity(security);
            }

            return options;
        }

        private object? ConvertJTokenToObject(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    return ConvertJObjectToDictionary((JObject)token);

                case JTokenType.Array:
                    return ConvertJArrayToList((JArray)token);

                case JTokenType.String:
                    var stringValue = token.Value<string>();
                    return ProcessStringValue(stringValue);

                case JTokenType.Integer:
                    return token.Value<long>();

                case JTokenType.Float:
                    return token.Value<double>();

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Date:
                    return token.Value<DateTime>();

                case JTokenType.Null:
                    return null;

                case JTokenType.Bytes:
                    return token.Value<byte[]>();

                default:
                    return token.ToString();
            }
        }

        private Dictionary<string, object?> ConvertJObjectToDictionary(JObject jObject)
        {
            var dict = new Dictionary<string, object?>();

            foreach (var property in jObject.Properties())
            {
                dict[property.Name] = ConvertJTokenToObject(property.Value);
            }

            return dict;
        }

        private List<object?> ConvertJArrayToList(JArray jArray)
        {
            var list = new List<object?>();

            foreach (var item in jArray)
            {
                list.Add(ConvertJTokenToObject(item));
            }

            return list;
        }

        private static object ProcessStringValue(string? value)
        {
            if (value == null) return null!;

            if (ShouldTreatAsBinary(value))
            {
                try
                {
                    return Convert.FromBase64String(value);
                }
                catch { }
            }
            return value;
        }

        private static bool ShouldTreatAsBinary(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 8 || value.Length % 4 != 0)
                return false;

            return value.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');
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

        private byte[] CreateErrorOutputBytes(string errorMessage, string errorType, JToken originalToken, MessagePackSerializerOptions options)
        {
            try
            {
                var errorDict = new Dictionary<string, object>
                {
                    ["error"] = errorMessage,
                    ["error_type"] = errorType,
                    ["original_type"] = originalToken.Type.ToString(),
                    ["timestamp"] = DateTime.UtcNow.ToString("o")
                };

                return MessagePackSerializer.Serialize(errorDict, options);
            }
            catch
            {
                var errorObj = new JObject
                {
                    ["error"] = errorMessage,
                    ["error_type"] = errorType,
                    ["original_type"] = originalToken.Type.ToString(),
                    ["timestamp"] = DateTime.UtcNow.ToString("o")
                };

                return Config.Encoding.GetBytes(errorObj.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

        private string CreateErrorOutput(string errorMessage, string errorType, JToken originalToken)
        {
            var errorObj = new JObject
            {
                ["error"] = errorMessage,
                ["error_type"] = errorType,
                ["original_type"] = originalToken.Type.ToString(),
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            try
            {
                var options = GetMessagePackOptions();
                var errorDict = new Dictionary<string, object>
                {
                    ["error"] = errorMessage,
                    ["error_type"] = errorType,
                    ["original_type"] = originalToken.Type.ToString(),
                    ["timestamp"] = DateTime.UtcNow.ToString("o")
                };

                var errorBytes = MessagePackSerializer.Serialize(errorDict, options);
                return FormatOutput(errorBytes);
            }
            catch
            {
                var errorBytes = Config.Encoding.GetBytes(errorObj.ToString(Newtonsoft.Json.Formatting.None));
                return Convert.ToBase64String(errorBytes);
            }
        }
    }
}