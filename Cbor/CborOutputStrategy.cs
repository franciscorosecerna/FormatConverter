using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System.Text;
using System.Buffers;

namespace FormatConverter.Cbor
{
    public class CborOutputStrategy : BaseOutputStrategy
    {
        public override string Serialize(JToken data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var processed = PreprocessToken(data);
            var result = SerializeToken(processed);

            if (Config.StrictMode)
            {
                ValidateCbor(result);
            }

            return result;
        }

        public override void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            var options = GetCborEncodeOptions();
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

        private void SerializeStreamBinary(IEnumerable<JToken> data, Stream output, CBOREncodeOptions options, int chunkSize, CancellationToken cancellationToken)
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

        private void SerializeStreamText(IEnumerable<JToken> data, Stream output, CBOREncodeOptions options, int chunkSize, CancellationToken cancellationToken)
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

        private void WriteChunkToStreamBinary(List<JToken> items, CBOREncodeOptions options, Stream output, CancellationToken ct)
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
                        Logger.WriteWarning($"Error serializing token to binary: {ex.Message}");
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

        private void WriteChunkToStreamText(List<JToken> items, CBOREncodeOptions options, StreamWriter writer, CancellationToken ct, ref bool isFirst)
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
                        Logger.WriteWarning($"Error serializing token to text: {ex.Message}");
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
                var cborObj = ConvertJTokenToCbor(token);

                if (cborObj == null)
                {
                    if (Config.IgnoreErrors)
                    {
                        Logger.WriteWarning("Failed to convert token to CBOR");
                        return CreateErrorOutput("Failed to convert token to CBOR", "ConversionError", token);
                    }
                    throw new FormatException("Failed to convert JSON to CBOR object");
                }

                var options = GetCborEncodeOptions();
                var bytes = cborObj.EncodeToBytes(options);

                return FormatOutput(bytes);
            }
            catch (Exception ex) when (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"Error serializing token: {ex.Message}");
                return CreateErrorOutput(ex.Message, ex.GetType().Name, token);
            }
        }

        private void ValidateCbor(string result)
        {
            try
            {
                var bytes = result.StartsWith("0x") || result.All(c => char.IsDigit(c)
                || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || char.IsWhiteSpace(c))
                    ? ConvertFromHex(result)
                    : Convert.FromBase64String(result);

                CBORObject.DecodeFromBytes(bytes);
            }
            catch (Exception ex) when (!Config.StrictMode)
            {
                Logger.WriteWarning($"CBOR validation warning: {ex.Message}");
            }
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

        private byte[] SerializeTokenToBytes(JToken token, CBOREncodeOptions options)
        {
            var cborObj = ConvertJTokenToCbor(token);
            return cborObj.EncodeToBytes(options);
        }

        private CBORObject ConvertJTokenToCbor(JToken token)
        {
            if (token == null) return CBORObject.Null;

            if (Config.CborPreserveTags && token is JObject jobj)
            {
                if (jobj.TryGetValue("__cbor_tag__", out var tagToken) &&
                    jobj.TryGetValue("__cbor_value__", out var valueToken))
                {
                    var tag = tagToken.Value<int>();
                    var innerValue = ConvertJTokenToCbor(valueToken);
                    return CBORObject.FromObjectAndTag(innerValue, tag);
                }
            }

            return token.Type switch
            {
                JTokenType.Object => ConvertJObjectToCborMap((JObject)token),
                JTokenType.Array => ConvertJArrayToCborArray((JArray)token),
                JTokenType.String => ConvertStringToCbor(token.Value<string>()),
                JTokenType.Integer => ProcessIntegerValue(token.Value<long>()),
                JTokenType.Float => CBORObject.FromObject(token.Value<double>()),
                JTokenType.Boolean => CBORObject.FromObject(token.Value<bool>()),
                JTokenType.Date => ConvertDateToCbor(token.Value<DateTime>()),
                JTokenType.Null => CBORObject.Null,
                JTokenType.Undefined => CBORObject.Undefined,
                JTokenType.Bytes => ConvertBytesToCbor(token.Value<byte[]>()),
                _ => CBORObject.FromObject(token.ToString())
            };
        }

        private static CBORObject ConvertBytesToCbor(byte[]? bytes)
        {
            if (bytes == null) return CBORObject.Null;

            return CBORObject.FromObject(bytes);
        }

        private CBORObject ConvertJObjectToCborMap(JObject jObject)
        {
            var cborMap = CBORObject.NewMap();

            foreach (var property in jObject.Properties())
            {
                var key = CBORObject.FromObject(property.Name);
                var value = ConvertJTokenToCbor(property.Value);
                cborMap[key] = value;
            }

            return cborMap;
        }

        private CBORObject ConvertJArrayToCborArray(JArray jArray)
        {
            var cborArray = CBORObject.NewArray();

            foreach (var item in jArray)
            {
                cborArray.Add(ConvertJTokenToCbor(item));
            }

            return cborArray;
        }

        private static CBORObject ConvertStringToCbor(string? value)
        {
            if (value == null) return CBORObject.Null;

            if (ShouldTreatAsBinary(value))
            {
                try
                {
                    return CBORObject.FromObject(Convert.FromBase64String(value));
                }
                catch { }
            }

            return CBORObject.FromObject(value);
        }

        private CBORObject ConvertDateToCbor(DateTime dateTime)
        {
            if (Config.CborUseDateTimeTags)
            {
                if (!string.IsNullOrEmpty(Config.DateFormat)
                    && Config.DateFormat.Equals("unix", StringComparison.CurrentCultureIgnoreCase))
                {
                    var unixTime = ((DateTimeOffset)dateTime).ToUnixTimeSeconds();
                    return CBORObject.FromObjectAndTag(unixTime, 1);
                }
                else
                {
                    var isoString = dateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                    return CBORObject.FromObjectAndTag(isoString, 0);
                }
            }

            return CBORObject.FromObject(dateTime);
        }

        private static bool ShouldTreatAsBinary(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 8 || value.Length % 4 != 0)
                return false;

            return value.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');
        }

        private CBORObject ProcessIntegerValue(long value)
        {
            if (Config.CborUseBigNumTags)
            {
                if (value > int.MaxValue || value < int.MinValue)
                {
                    if (value >= 0)
                    {
                        var bytes = BitConverter.GetBytes(value);
                        if (BitConverter.IsLittleEndian)
                            Array.Reverse(bytes);
                        return CBORObject.FromObjectAndTag(bytes, 2);
                    }
                    else
                    {
                        var absValue = Math.Abs(value) - 1;
                        var bytes = BitConverter.GetBytes(absValue);
                        if (BitConverter.IsLittleEndian)
                            Array.Reverse(bytes);
                        return CBORObject.FromObjectAndTag(bytes, 3);
                    }
                }
            }

            if (value >= int.MinValue && value <= int.MaxValue)
                return CBORObject.FromObject((int)value);

            return CBORObject.FromObject(value);
        }

        private CBOREncodeOptions GetCborEncodeOptions()
        {
            var opts = new List<string>();

            if (Config.CborCanonical)
            {
                opts.Add("ctap2canonical=true");
            }
            else if (Config.Minify)
            {
                opts.Add("ctap2canonical=true");
            }

            if (Config.NoMetadata)
            {
                opts.Add("allowduplicatekeys=false");
            }

            return opts.Count > 0
                ? new CBOREncodeOptions(string.Join(",", opts))
                : CBOREncodeOptions.Default;
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

        private byte[] CreateErrorOutputBytes(string errorMessage, string errorType, JToken originalToken, CBOREncodeOptions options)
        {
            try
            {
                var errorMap = CBORObject.NewMap();
                errorMap[CBORObject.FromObject("error")] = CBORObject.FromObject(errorMessage);
                errorMap[CBORObject.FromObject("error_type")] = CBORObject.FromObject(errorType);
                errorMap[CBORObject.FromObject("original_type")] = CBORObject.FromObject(originalToken.Type.ToString());
                errorMap[CBORObject.FromObject("timestamp")] = CBORObject.FromObject(DateTime.UtcNow.ToString("o"));

                return errorMap.EncodeToBytes(options);
            }
            catch (Exception ex)
            {
                Logger.WriteWarning($"Failed to create error output bytes: {ex.Message}");
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
                var options = GetCborEncodeOptions();
                var errorMap = CBORObject.NewMap();
                errorMap[CBORObject.FromObject("error")] = CBORObject.FromObject(errorMessage);
                errorMap[CBORObject.FromObject("error_type")] = CBORObject.FromObject(errorType);
                errorMap[CBORObject.FromObject("original_type")] = CBORObject.FromObject(originalToken.Type.ToString());
                errorMap[CBORObject.FromObject("timestamp")] = CBORObject.FromObject(DateTime.UtcNow.ToString("o"));

                var errorBytes = errorMap.EncodeToBytes(options);
                return FormatOutput(errorBytes);
            }
            catch (Exception ex)
            {
                Logger.WriteWarning($"Failed to create error output: {ex.Message}");
                var errorBytes = Config.Encoding.GetBytes(errorObj.ToString(Newtonsoft.Json.Formatting.None));
                return Convert.ToBase64String(errorBytes);
            }
        }
    }
}