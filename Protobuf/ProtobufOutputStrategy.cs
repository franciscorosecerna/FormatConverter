using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using System.Text;
using System.Buffers;

namespace FormatConverter.Protobuf
{
    public class ProtobufOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = PreprocessToken(data);
            var result = SerializeToken(processed);

            if (Config.StrictMode)
            {
                ValidateProtobuf(result);
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
            var shouldWriteRaw = ShouldWriteRawBinary();

            if (shouldWriteRaw)
            {
                SerializeStreamBinary(data, output, chunkSize, cancellationToken);
            }
            else
            {
                SerializeStreamText(data, output, chunkSize, cancellationToken);
            }
        }

        private void SerializeStreamBinary(IEnumerable<JToken> data, Stream output, int chunkSize, CancellationToken cancellationToken)
        {
            var buffer = new List<JToken>();

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    WriteChunkToStreamBinary(buffer, output, cancellationToken);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStreamBinary(buffer, output, cancellationToken);
            }
            output.Flush();
        }

        private void SerializeStreamText(IEnumerable<JToken> data, Stream output, int chunkSize, CancellationToken cancellationToken)
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
                    WriteChunkToStreamText(buffer, writer, cancellationToken, ref isFirst);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChunkToStreamText(buffer, writer, cancellationToken, ref isFirst);
            }

            if (!Config.Minify)
            {
                writer.WriteLine();
                writer.WriteLine("]");
            }
            output.Flush();
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);
        }

        private void WriteChunkToStreamBinary(List<JToken> items, Stream output, CancellationToken ct)
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
                        var bytes = SerializeTokenToBytes(items[i]);

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
                        Logger.WriteWarning($"Protobuf serialization error in item {i}: {ex.Message}");
                        var errorBytes = CreateErrorOutputBytes(ex.Message, ex.GetType().Name, items[i]);

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

        private void WriteChunkToStreamText(List<JToken> items, StreamWriter writer, CancellationToken ct, ref bool isFirst)
        {
            if (items.Count == 0) return;

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var bytes = SerializeTokenToBytes(items[i]);
                    var formatted = FormatOutput(bytes);

                    WriteFormattedToken(writer, formatted, ref isFirst, i < items.Count - 1);
                }
                catch (Exception ex) when (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"Protobuf serialization error in item {i}: {ex.Message}");
                    var errorOutput = CreateErrorOutput(ex.Message, ex.GetType().Name, items[i]);
                    WriteFormattedToken(writer, errorOutput, ref isFirst, i < items.Count - 1);
                }
            }
        }

        private void WriteFormattedToken(StreamWriter writer, string formatted, ref bool isFirst, bool hasMore)
        {
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
                if (hasMore)
                    writer.Write(",");
            }
            else
            {
                writer.Write("  ");
                writer.Write(formatted);
            }
        }

        private string SerializeToken(JToken token)
        {
            try
            {
                var bytes = SerializeTokenToBytes(token);
                return FormatOutput(bytes);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"Protobuf serialization error ignored: {ex.Message}");
                return CreateErrorOutput(ex.Message, ex.GetType().Name, token);
            }
        }

        private byte[] SerializeTokenToBytes(JToken token)
        {
            IMessage protobufMessage;

            if (ShouldSerializeAsAny(token))
            {
                protobufMessage = ConvertJTokenToAny(token);
            }
            else if (ShouldSerializeAsValue(token))
            {
                protobufMessage = ConvertJTokenToValue(token);
            }
            else
            {
                protobufMessage = ConvertJTokenToStruct(token);
            }

            return protobufMessage.ToByteArray();
        }

        private void ValidateProtobuf(string result)
        {
            try
            {
                var bytes = result.StartsWith("0x") || result.All(c => char.IsDigit(c)
                    || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || char.IsWhiteSpace(c))
                    ? ConvertFromHex(result)
                    : Convert.FromBase64String(result);

                var _ = Struct.Parser.ParseFrom(bytes);
            }
            catch when (!Config.StrictMode) { }
        }

        private static byte[] ConvertFromHex(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("\n", "").Replace("\r", "");
            return Convert.FromHexString(hex);
        }

        private bool ShouldWriteRawBinary()
        {
            var format = Config.NumberFormat?.ToLower();
            return format == "raw" || string.IsNullOrEmpty(format);
        }

        private int GetChunkSize() => Config.ChunkSize > 0 ? Config.ChunkSize : 100;

        private static bool ShouldSerializeAsAny(JToken data)
        {
            return data is JObject obj &&
                   obj.ContainsKey("@type") &&
                   obj.ContainsKey("value");
        }

        private static bool ShouldSerializeAsValue(JToken data)
        {
            return data.Type switch
            {
                JTokenType.String => true,
                JTokenType.Integer => true,
                JTokenType.Float => true,
                JTokenType.Boolean => true,
                JTokenType.Null => true,
                _ => false
            };
        }

        private static Any ConvertJTokenToAny(JToken data)
        {
            if (data is not JObject obj)
                throw new FormatException("Any message must be a JSON object with @type and value fields");

            var typeUrl = obj["@type"]?.Value<string>() ??
                throw new FormatException("Any message missing @type field");

            var valueString = obj["value"]?.Value<string>() ??
                throw new FormatException("Any message missing value field");

            var valueBytes = Convert.FromBase64String(valueString);

            return new Any
            {
                TypeUrl = typeUrl,
                Value = ByteString.CopyFrom(valueBytes)
            };
        }

        private Value ConvertJTokenToValue(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => Value.ForString(token.Value<string>() ?? string.Empty),
                JTokenType.Integer => Value.ForNumber(token.Value<long>()),
                JTokenType.Float => Value.ForNumber(token.Value<double>()),
                JTokenType.Boolean => Value.ForBool(token.Value<bool>()),
                JTokenType.Null => Value.ForNull(),
                JTokenType.Array => ConvertJArrayToValue((JArray)token),
                JTokenType.Object => ConvertJObjectToValue((JObject)token),
                JTokenType.Date => Value.ForString(token.Value<DateTime>().ToString("O")),
                _ => Value.ForString(token.ToString())
            };
        }

        private Value ConvertJArrayToValue(JArray array)
        {
            var listValue = new ListValue();

            foreach (var item in array)
            {
                listValue.Values.Add(ConvertJTokenToValue(item));
            }

            return new Value { ListValue = listValue };
        }

        private Value ConvertJObjectToValue(JObject obj)
        {
            var structValue = ConvertJObjectToStruct(obj);
            return Value.ForStruct(structValue);
        }

        private Struct ConvertJTokenToStruct(JToken token)
        {
            if (token is not JObject obj)
                throw new FormatException("Cannot convert non-object JToken to Protobuf Struct");

            return ConvertJObjectToStruct(obj);
        }

        private Struct ConvertJObjectToStruct(JObject json)
        {
            var structValue = new Struct();

            foreach (var property in json.Properties())
            {
                structValue.Fields[property.Name] = ConvertJTokenToValue(property.Value);
            }

            return structValue;
        }

        private string FormatOutput(byte[] bytes)
        {
            return Config.NumberFormat?.ToLower() switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
                "binary" => FormatAsBinary(bytes),
                "raw" => FormatAsRaw(bytes),
                _ => Convert.ToBase64String(bytes)
            };
        }

        private string FormatAsRaw(byte[] bytes)
        {
            if (Config.Encoding is UTF8Encoding)
            {
                return Encoding.UTF8.GetString(bytes);
            }

            return Config.Encoding.GetString(bytes);
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

        private byte[] CreateErrorOutputBytes(string errorMessage, string errorType, JToken originalToken)
        {
            try
            {
                var errorStruct = new Struct();
                errorStruct.Fields["error"] = Value.ForString(errorMessage);
                errorStruct.Fields["error_type"] = Value.ForString(errorType);
                errorStruct.Fields["original_type"] = Value.ForString(originalToken.Type.ToString());
                errorStruct.Fields["timestamp"] = Value.ForString(DateTime.UtcNow.ToString("O"));

                return errorStruct.ToByteArray();
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

                return Config.Encoding.GetBytes(errorObj.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

        private string CreateErrorOutput(string errorMessage, string errorType, JToken originalToken)
        {
            try
            {
                var errorBytes = CreateErrorOutputBytes(errorMessage, errorType, originalToken);
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
    }
}