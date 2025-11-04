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
            Logger.WriteTrace("Serialize: Starting Protobuf serialization");

            if (data == null)
            {
                Logger.WriteError("Serialize: Data is null");
                throw new ArgumentNullException(nameof(data));
            }

            Logger.WriteDebug($"Serialize: Input token type: {data.Type}");
            var processed = PreprocessToken(data);
            var result = SerializeToken(processed);

            if (Config.StrictMode)
            {
                Logger.WriteDebug("Serialize: Validating Protobuf in strict mode");
                ValidateProtobuf(result);
            }

            Logger.WriteSuccess($"Serialize: Protobuf serialization completed ({result.Length} characters)");
            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo("SerializeStream: Starting stream serialization");

            if (data == null)
            {
                Logger.WriteError("SerializeStream: Data is null");
                throw new ArgumentNullException(nameof(data));
            }
            if (output == null)
            {
                Logger.WriteError("SerializeStream: Output stream is null");
                throw new ArgumentNullException(nameof(output));
            }

            var chunkSize = GetChunkSize();
            var shouldWriteRaw = ShouldWriteRawBinary();

            Logger.WriteDebug($"SerializeStream: Chunk size: {chunkSize}, Write raw binary: {shouldWriteRaw}");

            if (shouldWriteRaw)
            {
                Logger.WriteDebug("SerializeStream: Using binary serialization");
                SerializeStreamBinary(data, output, chunkSize, cancellationToken);
            }
            else
            {
                Logger.WriteDebug("SerializeStream: Using text serialization");
                SerializeStreamText(data, output, chunkSize, cancellationToken);
            }

            Logger.WriteSuccess("SerializeStream: Stream serialization completed");
        }

        private void SerializeStreamBinary(IEnumerable<JToken> data, Stream output, int chunkSize, CancellationToken cancellationToken)
        {
            Logger.WriteTrace("SerializeStreamBinary: Starting binary stream serialization");

            var buffer = new List<JToken>();
            var totalProcessed = 0;

            foreach (var token in data)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processed = PreprocessToken(token);
                buffer.Add(processed);

                if (buffer.Count >= chunkSize)
                {
                    Logger.WriteTrace($"SerializeStreamBinary: Writing chunk of {buffer.Count} items");
                    WriteChunkToStreamBinary(buffer, output, cancellationToken);
                    totalProcessed += buffer.Count;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.WriteTrace($"SerializeStreamBinary: Writing final chunk of {buffer.Count} items");
                WriteChunkToStreamBinary(buffer, output, cancellationToken);
                totalProcessed += buffer.Count;
            }

            output.Flush();
            Logger.WriteSuccess($"SerializeStreamBinary: Completed. Total items: {totalProcessed}");
        }

        private void SerializeStreamText(IEnumerable<JToken> data, Stream output, int chunkSize, CancellationToken cancellationToken)
        {
            Logger.WriteTrace("SerializeStreamText: Starting text stream serialization");

            using var writer = new StreamWriter(output, Config.Encoding, 8192, leaveOpen: true);

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
                    Logger.WriteTrace($"SerializeStreamText: Writing chunk of {buffer.Count} items");
                    WriteChunkToStreamText(buffer, writer, cancellationToken, ref isFirst);
                    totalProcessed += buffer.Count;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.WriteTrace($"SerializeStreamText: Writing final chunk of {buffer.Count} items");
                WriteChunkToStreamText(buffer, writer, cancellationToken, ref isFirst);
                totalProcessed += buffer.Count;
            }

            if (!Config.Minify)
            {
                writer.WriteLine();
                writer.WriteLine("]");
            }

            output.Flush();
            Logger.WriteSuccess($"SerializeStreamText: Completed. Total items: {totalProcessed}");
        }

        public void SerializeStream(IEnumerable<JToken> data, string outputPath, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo($"SerializeStream: Writing to file '{outputPath}'");

            if (string.IsNullOrEmpty(outputPath))
            {
                Logger.WriteError("SerializeStream: Output path is null or empty");
                throw new ArgumentNullException(nameof(outputPath));
            }

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            SerializeStream(data, fileStream, cancellationToken);

            Logger.WriteSuccess($"SerializeStream: File written successfully to '{outputPath}'");
        }

        private void WriteChunkToStreamBinary(List<JToken> items, Stream output, CancellationToken ct)
        {
            if (items.Count == 0)
            {
                Logger.WriteTrace("WriteChunkToStreamBinary: Empty chunk, skipping");
                return;
            }

            Logger.WriteTrace($"WriteChunkToStreamBinary: Processing {items.Count} items");

            const int initialBufferSize = 8192;
            byte[]? rentedBuffer = null;

            try
            {
                rentedBuffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
                Logger.WriteTrace($"WriteChunkToStreamBinary: Rented buffer of {initialBufferSize} bytes");

                for (int i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var bytes = SerializeTokenToBytes(items[i]);
                        Logger.WriteTrace($"WriteChunkToStreamBinary: Item {i} serialized to {bytes.Length} bytes");

                        if (bytes.Length <= rentedBuffer.Length)
                        {
                            Buffer.BlockCopy(bytes, 0, rentedBuffer, 0, bytes.Length);
                            output.Write(rentedBuffer, 0, bytes.Length);
                        }
                        else
                        {
                            Logger.WriteDebug($"WriteChunkToStreamBinary: Item {i} exceeds buffer, writing directly");
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
                    Logger.WriteTrace("WriteChunkToStreamBinary: Buffer returned to pool");
                }
            }
        }

        private void WriteChunkToStreamText(List<JToken> items, StreamWriter writer, CancellationToken ct, ref bool isFirst)
        {
            if (items.Count == 0)
            {
                Logger.WriteTrace("WriteChunkToStreamText: Empty chunk, skipping");
                return;
            }

            Logger.WriteTrace($"WriteChunkToStreamText: Processing {items.Count} items");

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var bytes = SerializeTokenToBytes(items[i]);
                    var formatted = FormatOutput(bytes);
                    Logger.WriteTrace($"WriteChunkToStreamText: Item {i} formatted ({formatted.Length} characters)");

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
            Logger.WriteTrace($"SerializeToken: Serializing token type {token.Type}");

            try
            {
                var bytes = SerializeTokenToBytes(token);
                Logger.WriteDebug($"SerializeToken: Token serialized to {bytes.Length} bytes");
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
                Logger.WriteTrace("SerializeTokenToBytes: Serializing as Any");
                protobufMessage = ConvertJTokenToAny(token);
            }
            else if (ShouldSerializeAsValue(token))
            {
                Logger.WriteTrace("SerializeTokenToBytes: Serializing as Value");
                protobufMessage = ConvertJTokenToValue(token);
            }
            else
            {
                Logger.WriteTrace("SerializeTokenToBytes: Serializing as Struct");
                protobufMessage = ConvertJTokenToStruct(token);
            }

            var bytes = protobufMessage.ToByteArray();
            Logger.WriteTrace($"SerializeTokenToBytes: Generated {bytes.Length} bytes");
            return bytes;
        }

        private void ValidateProtobuf(string result)
        {
            Logger.WriteTrace("ValidateProtobuf: Starting validation");

            try
            {
                var bytes = result.StartsWith("0x") || result.All(c => char.IsDigit(c)
                    || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || char.IsWhiteSpace(c))
                    ? ConvertFromHex(result)
                    : Convert.FromBase64String(result);

                Logger.WriteDebug($"ValidateProtobuf: Validating {bytes.Length} bytes");
                var _ = Struct.Parser.ParseFrom(bytes);
                Logger.WriteDebug("ValidateProtobuf: Validation successful");
            }
            catch (Exception ex) when (!Config.StrictMode)
            {
                Logger.WriteWarning($"ValidateProtobuf: Validation failed but ignored - {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.WriteError($"ValidateProtobuf: Validation failed - {ex.Message}");
                throw;
            }
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
            => data.Type switch
            {
                JTokenType.String => true,
                JTokenType.Integer => true,
                JTokenType.Float => true,
                JTokenType.Boolean => true,
                JTokenType.Null => true,
                _ => false
            };

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
            Logger.WriteTrace($"ConvertJTokenToValue: Converting token type {token.Type}");

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
            Logger.WriteTrace($"ConvertJArrayToValue: Converting array with {array.Count} items");

            var listValue = new ListValue();

            foreach (var item in array)
            {
                listValue.Values.Add(ConvertJTokenToValue(item));
            }

            return new Value { ListValue = listValue };
        }

        private Value ConvertJObjectToValue(JObject obj)
        {
            Logger.WriteTrace($"ConvertJObjectToValue: Converting object with {obj.Count} properties");

            var structValue = ConvertJObjectToStruct(obj);
            return Value.ForStruct(structValue);
        }

        private Struct ConvertJTokenToStruct(JToken token)
        {
            Logger.WriteTrace($"ConvertJTokenToStruct: Converting token type {token.Type}");

            if (token is not JObject obj)
            {
                Logger.WriteError($"ConvertJTokenToStruct: Cannot convert {token.Type} to Struct");
                throw new FormatException("Cannot convert non-object JToken to Protobuf Struct");
            }

            return ConvertJObjectToStruct(obj);
        }

        private Struct ConvertJObjectToStruct(JObject json)
        {
            Logger.WriteTrace($"ConvertJObjectToStruct: Converting object with {json.Count} properties");

            var structValue = new Struct();

            foreach (var property in json.Properties())
            {
                structValue.Fields[property.Name] = ConvertJTokenToValue(property.Value);
            }

            return structValue;
        }

        private string FormatOutput(byte[] bytes)
        {
            var format = Config.NumberFormat?.ToLower();
            Logger.WriteTrace($"FormatOutput: Formatting {bytes.Length} bytes as '{format ?? "base64"}'");

            return format switch
            {
                "hex" or "hexadecimal" => FormatAsHex(bytes),
                "binary" => FormatAsBinary(bytes),
                "raw" => FormatAsRaw(bytes),
                _ => Convert.ToBase64String(bytes)
            };
        }

        private string FormatAsRaw(byte[] bytes)
        {
            Logger.WriteTrace($"FormatAsRaw: Converting {bytes.Length} bytes to string");

            if (Config.Encoding is UTF8Encoding)
            {
                return Encoding.UTF8.GetString(bytes);
            }

            return Config.Encoding.GetString(bytes);
        }

        private string FormatAsHex(byte[] bytes)
        {
            Logger.WriteTrace($"FormatAsHex: Formatting {bytes.Length} bytes as hexadecimal");

            const int stackAllocThreshold = 256;

            if (bytes.Length <= stackAllocThreshold)
            {
                Logger.WriteTrace("FormatAsHex: Using stack allocation");
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
                Logger.WriteTrace("FormatAsHex: Using heap allocation");
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
            Logger.WriteTrace($"FormatAsBinary: Formatting {bytes.Length} bytes as binary");

            const int bitsPerByte = 8;
            int totalChars = bytes.Length * bitsPerByte;

            char[]? buffer = null;
            try
            {
                if (Config.PrettyPrint && !Config.Minify)
                {
                    Logger.WriteTrace("FormatAsBinary: Using pretty print format");
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
                    Logger.WriteTrace("FormatAsBinary: Using compact format");
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
                    Logger.WriteTrace("FormatAsBinary: Buffer returned to pool");
                }
            }
        }

        private byte[] CreateErrorOutputBytes(string errorMessage, string errorType, JToken originalToken)
        {
            Logger.WriteTrace("CreateErrorOutputBytes: Creating error output");

            try
            {
                var errorStruct = new Struct();
                errorStruct.Fields["error"] = Value.ForString(errorMessage);
                errorStruct.Fields["error_type"] = Value.ForString(errorType);
                errorStruct.Fields["original_type"] = Value.ForString(originalToken.Type.ToString());
                errorStruct.Fields["timestamp"] = Value.ForString(DateTime.UtcNow.ToString("O"));

                return errorStruct.ToByteArray();
            }
            catch (Exception ex)
            {
                Logger.WriteWarning($"CreateErrorOutputBytes: Failed to create Protobuf error, falling back to JSON - {ex.Message}");

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
            Logger.WriteTrace("CreateErrorOutput: Creating error output");

            try
            {
                var errorBytes = CreateErrorOutputBytes(errorMessage, errorType, originalToken);
                return FormatOutput(errorBytes);
            }
            catch (Exception ex)
            {
                Logger.WriteWarning($"CreateErrorOutput: Failed to format error output, using base64 fallback - {ex.Message}");

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