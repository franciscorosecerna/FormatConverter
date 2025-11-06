using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using System.Buffers;

namespace FormatConverter.Protobuf
{
    public class ProtobufInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            Logger.WriteTrace(() => "Parse: Starting Protobuf parsing");

            if (string.IsNullOrWhiteSpace(input))
            {
                Logger.WriteWarning(() => "Parse: Input is null or empty");
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("Protobuf input cannot be null or empty", nameof(input));
            }

            Logger.WriteDebug(() => $"Parse: Input length: {input.Length} characters");

            try
            {
                var bytes = DecodeInput(input);
                Logger.WriteDebug(() => $"Parse: Decoded to {bytes.Length} bytes");

                var token = ParseProtobufDocument(bytes);
                Logger.WriteTrace(() => $"Parse: Parsed to token type {token.Type}");

                if (Config.NoMetadata)
                {
                    Logger.WriteDebug(() => "Parse: Removing metadata properties");
                    token = RemoveMetadataProperties(token);
                }

                if (Config.MaxDepth.HasValue)
                {
                    Logger.WriteDebug(() => $"Parse: Validating depth (max: {Config.MaxDepth.Value})");
                    ValidateDepth(token, Config.MaxDepth.Value);
                }

                Logger.WriteSuccess("Parse: Protobuf parsed successfully");
                return token;
            }
            catch (Exception ex) when (ex is not FormatException && ex is not ArgumentException)
            {
                Logger.WriteError(() => $"Parse: Exception occurred - {ex.Message}");
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken = default)
        {
            Logger.WriteInfo(() => $"ParseStream: Starting stream parsing for '{path}'");

            if (string.IsNullOrWhiteSpace(path))
            {
                Logger.WriteError(() => "ParseStream: Path is null or empty");
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));
            }

            if (!File.Exists(path))
            {
                Logger.WriteError(() => $"ParseStream: File not found at '{path}'");
                throw new FileNotFoundException("Input file not found.", path);
            }

            Logger.WriteDebug(() => $"ParseStream: File found, size: {new FileInfo(path).Length} bytes");
            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken)
        {
            Logger.WriteTrace(() => "ParseStreamInternal: Opening file stream");

            using var fileStream = File.OpenRead(path);

            var fileSize = fileStream.Length;
            var showProgress = fileSize > 10_485_760;

            Logger.WriteDebug(() => $"ParseStreamInternal: File size: {fileSize:N0} bytes, progress logging: {showProgress}");

            const int BufferSize = 8192;
            var arrayPool = ArrayPool<byte>.Shared;
            byte[] buffer = arrayPool.Rent(BufferSize);
            byte[] accumulator = arrayPool.Rent(BufferSize * 2);
            int accumulatorLength = 0;

            Logger.WriteTrace(() => $"ParseStreamInternal: Using buffer size {BufferSize}, initial accumulator size {BufferSize * 2}");

            int bytesRead;
            int messagesProcessed = 0;

            try
            {
                while ((bytesRead = fileStream.Read(buffer, 0, BufferSize)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (accumulatorLength + bytesRead > accumulator.Length)
                    {
                        var newSize = Math.Max(accumulator.Length * 2, accumulatorLength + bytesRead);
                        Logger.WriteDebug(() => $"ParseStreamInternal: Expanding accumulator from {accumulator.Length} to {newSize} bytes");

                        var newAccumulator = arrayPool.Rent(newSize);
                        Buffer.BlockCopy(accumulator, 0, newAccumulator, 0, accumulatorLength);
                        arrayPool.Return(accumulator);
                        accumulator = newAccumulator;
                    }

                    Buffer.BlockCopy(buffer, 0, accumulator, accumulatorLength, bytesRead);
                    accumulatorLength += bytesRead;

                    var processed = 0;
                    while (processed < accumulatorLength)
                    {
                        var availableSpan = new ReadOnlySpan<byte>(accumulator, processed, accumulatorLength - processed);

                        var (success, token, consumed, error) = TryReadNextProtobufMessage(
                            availableSpan,
                            path);

                        if (success && token != null)
                        {
                            messagesProcessed++;

                            if (showProgress && messagesProcessed % 100 == 0)
                            {
                                var progress = (double)fileStream.Position / fileStream.Length * 100;
                                Logger.WriteInfo(() => $"Processing: {progress:F1}% ({messagesProcessed} messages)");
                            }

                            Logger.WriteTrace(() => $"ParseStreamInternal: Message {messagesProcessed} parsed, consumed {consumed} bytes");

                            if (Config.NoMetadata)
                                token = RemoveMetadataProperties(token);

                            if (Config.MaxDepth.HasValue)
                            {
                                ValidateDepth(token, Config.MaxDepth.Value);
                            }

                            yield return token;

                            processed += consumed;
                        }
                        else if (error != null)
                        {
                            if (Config.IgnoreErrors)
                            {
                                Logger.WriteWarning(() => error.Message);
                                yield return CreateErrorToken(error, $"File: {path}, Offset: {fileStream.Position - accumulatorLength + processed}");
                                processed += Math.Max(1, consumed);
                            }
                            else
                            {
                                Logger.WriteError(() => $"ParseStreamInternal: Fatal error - {error.Message}");
                                throw error;
                            }
                        }
                        else
                        {
                            int lenght = availableSpan.Length;
                            Logger.WriteTrace(() => $"ParseStreamInternal: Incomplete message, need more data (available: {lenght} bytes)");
                            break;
                        }
                    }

                    if (processed > 0)
                    {
                        var remaining = accumulatorLength - processed;
                        if (remaining > 0)
                        {
                            Buffer.BlockCopy(accumulator, processed, accumulator, 0, remaining);
                        }
                        accumulatorLength = remaining;
                        Logger.WriteTrace(() => $"ParseStreamInternal: Processed {processed} bytes, {remaining} bytes remaining in accumulator");
                    }
                }

                if (accumulatorLength > 0)
                {
                    Logger.WriteWarning(() => $"ParseStreamInternal: {accumulatorLength} bytes of incomplete data at end of file");

                    if (Config.IgnoreErrors)
                    {
                        Logger.WriteWarning(() => $"{accumulatorLength} bytes of incomplete Protobuf data at end of file");
                        yield return CreateErrorToken(
                            new FormatException($"Incomplete Protobuf data: {accumulatorLength} bytes remaining"),
                            $"File: {path}");
                    }
                    else
                    {
                        Logger.WriteError(() => $"ParseStreamInternal: Incomplete message at end ({accumulatorLength} bytes)");
                        throw new FormatException($"Incomplete Protobuf message at end of file ({accumulatorLength} bytes remaining)");
                    }
                }

                if (showProgress)
                {
                    Logger.WriteInfo(() => $"Completed: {messagesProcessed} messages processed");
                }

                Logger.WriteSuccess($"ParseStreamInternal: Stream parsing completed. Total messages: {messagesProcessed}");
            }
            finally
            {
                Logger.WriteTrace(() => "ParseStreamInternal: Returning buffers to pool");
                arrayPool.Return(buffer);
                arrayPool.Return(accumulator);
            }
        }

        private (bool success, JToken? token, int consumed, Exception? error)
            TryReadNextProtobufMessage(ReadOnlySpan<byte> data, string path)
        {
            int length = data.Length;
            Logger.WriteTrace(() => $"TryReadNextProtobufMessage: Attempting to read message from {length} bytes");


            try
            {
                if (data.Length < 1)
                {
                    Logger.WriteTrace(() => "TryReadNextProtobufMessage: Insufficient data (< 1 byte)");
                    return (false, null, 0, null);
                }

                var messageBytes = TryExtractLengthDelimitedMessage(data, out int consumed);

                if (messageBytes != null)
                {
                    Logger.WriteTrace(() => $"TryReadNextProtobufMessage: Extracted length-delimited message ({messageBytes.Length} bytes, consumed {consumed})");
                    var token = ParseProtobufDocument(messageBytes);
                    return (true, token, consumed, null);
                }

                Logger.WriteTrace(() => "TryReadNextProtobufMessage: Length-delimited extraction failed, trying single message");
                messageBytes = TryExtractSingleMessage(data, out consumed);

                if (messageBytes != null)
                {
                    Logger.WriteTrace(() => $"TryReadNextProtobufMessage: Extracted single message ({messageBytes.Length} bytes, consumed {consumed})");
                    var token = ParseProtobufDocument(messageBytes);
                    return (true, token, consumed, null);
                }

                Logger.WriteTrace(() => "TryReadNextProtobufMessage: Could not extract complete message");
                return (false, null, 0, null);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Logger.WriteWarning(() => $"TryReadNextProtobufMessage: Invalid Protobuf data - {ex.Message}");
                return (false, null, 0,
                    new FormatException($"Invalid Protobuf data: {ex.Message}", ex));
            }
            catch (Exception ex)
            {
                Logger.WriteWarning(() => $"TryReadNextProtobufMessage: Unexpected error - {ex.Message}");
                return (false, null, 0,
                    new FormatException($"Unexpected Protobuf error in {path}: {ex.Message}", ex));
            }
        }

        private void ValidateDepth(JToken token, int maxDepth)
        {
            var actualDepth = CalculateDepth(token);
            Logger.WriteDebug(() => $"ValidateDepth: Calculated depth: {actualDepth}, max allowed: {maxDepth}");

            if (actualDepth > maxDepth)
            {
                var message = $"Protobuf structure depth ({actualDepth}) exceeds maximum allowed depth ({maxDepth})";

                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => message);
                }
                else
                {
                    Logger.WriteError(() => $"ValidateDepth: {message}");
                    throw new FormatException(message);
                }
            }
        }

        private static int CalculateDepth(JToken token, int currentDepth = 1)
        {
            if (token is JObject obj)
            {
                if (!obj.HasValues) return currentDepth;

                return obj.Properties()
                    .Select(p => CalculateDepth(p.Value, currentDepth + 1))
                    .DefaultIfEmpty(currentDepth)
                    .Max();
            }
            else if (token is JArray arr)
            {
                if (!arr.HasValues) return currentDepth;

                return arr.Children()
                    .Select(child => CalculateDepth(child, currentDepth + 1))
                    .DefaultIfEmpty(currentDepth)
                    .Max();
            }

            return currentDepth;
        }

        private static byte[]? TryExtractLengthDelimitedMessage(ReadOnlySpan<byte> data, out int consumed)
        {
            consumed = 0;

            try
            {
                var (length, varintBytes) = TryReadVarintFromSpan(data);

                if (varintBytes == 0)
                    return null;

                if (data.Length < varintBytes + (int)length)
                    return null;

                consumed = varintBytes + (int)length;
                var messageData = data.Slice(varintBytes, (int)length).ToArray();

                return messageData;
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? TryExtractSingleMessage(ReadOnlySpan<byte> data, out int consumed)
        {
            consumed = 0;

            try
            {
                var tempData = data.ToArray();

                using var testStream = new MemoryStream(tempData, false);
                using var reader = new BinaryReader(testStream);

                var fields = new HashSet<int>();
                var lastValidPosition = 0L;

                while (testStream.Position < testStream.Length)
                {
                    var positionBefore = testStream.Position;

                    try
                    {
                        var tag = ReadVarint(reader);
                        var wireType = (int)(tag & 0x7);
                        var fieldNum = (int)(tag >> 3);

                        if (fieldNum == 0 || wireType > 5)
                            break;

                        fields.Add(fieldNum);
                        SkipFieldValue(reader, wireType);

                        lastValidPosition = testStream.Position;
                    }
                    catch
                    {
                        break;
                    }
                }

                if (fields.Count > 0 && lastValidPosition > 0)
                {
                    consumed = (int)lastValidPosition;
                    return data.Slice(0, consumed).ToArray();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static (ulong value, int bytesRead) TryReadVarintFromSpan(ReadOnlySpan<byte> data)
        {
            ulong value = 0;
            int shift = 0;
            int bytesRead = 0;

            for (int i = 0; i < data.Length && i < 10; i++)
            {
                var b = data[i];
                bytesRead++;
                value |= (ulong)(b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                    return (value, bytesRead);

                shift += 7;
                if (shift >= 64)
                    return (0, 0);
            }

            return (0, 0);
        }

        private JToken ParseProtobufDocument(byte[] bytes)
        {
            Logger.WriteTrace(() => $"ParseProtobufDocument: Parsing {bytes.Length} bytes");

            if (bytes == null || bytes.Length == 0)
            {
                Logger.WriteError(() => "ParseProtobufDocument: Input is empty or null");
                throw new FormatException("Protobuf input is empty or null");
            }

            JToken result = TryParseAsStruct(bytes) ??
                           TryParseAsAny(bytes) ??
                           TryParseAsValue(bytes) ??
                           ParseAsGenericMessage(bytes);

            Logger.WriteTrace(() => $"ParseProtobufDocument: Successfully parsed to {result.Type}");
            return result;
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning(() => $"Protobuf parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

            Logger.WriteError(() => $"HandleParsingError: Fatal error - {ex.Message}");
            throw new FormatException($"Invalid Protobuf: {ex.Message}", ex);
        }

        private static JObject CreateErrorToken(Exception ex, string context)
        {
            var snippet = context.Length > 1000
                ? string.Concat(context.AsSpan(0, 1000), "...")
                : context;

            return new JObject
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["raw_snippet"] = snippet,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static byte[] DecodeInput(string input)
        {
            try
            {
                return Convert.FromBase64String(input);
            }
            catch (FormatException)
            {
                return ParseHexString(input);
            }
        }

        private static byte[] ParseHexString(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("-", "");

            if (hex.Length % 2 != 0)
                throw new FormatException("Invalid hex string length");

            var bytes = new byte[hex.Length / 2];
            var hexSpan = hex.AsSpan();

            for (int i = 0; i < bytes.Length; i++)
            {
                var slice = hexSpan.Slice(i * 2, 2);
                bytes[i] = (byte)((GetHexValue(slice[0]) << 4) | GetHexValue(slice[1]));
            }

            return bytes;
        }

        private static int GetHexValue(char c)
        {
            return c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'F' => c - 'A' + 10,
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => throw new FormatException($"Invalid hex character: {c}")
            };
        }

        private JToken RemoveMetadataProperties(JToken token)
        {
            Logger.WriteTrace(() => $"RemoveMetadataProperties: Processing token type {token.Type}");

            if (token is JObject obj)
            {
                var result = new JObject();
                var removedCount = 0;

                foreach (var prop in obj.Properties())
                {
                    if (!prop.Name.StartsWith("_") &&
                        !prop.Name.Equals("format", StringComparison.OrdinalIgnoreCase))
                    {
                        result[prop.Name] = RemoveMetadataProperties(prop.Value);
                    }
                    else
                    {
                        removedCount++;
                    }
                }

                if (removedCount > 0)
                {
                    Logger.WriteDebug(() => $"RemoveMetadataProperties: Removed {removedCount} metadata properties");
                }

                return result;
            }
            else if (token is JArray array)
            {
                return new JArray(array.Select(RemoveMetadataProperties));
            }

            return token;
        }

        private static void SkipFieldValue(BinaryReader reader, int wireType)
        {
            switch (wireType)
            {
                case 0:
                    ReadVarint(reader);
                    break;
                case 1:
                    reader.ReadBytes(8);
                    break;
                case 2:
                    var length = (int)ReadVarint(reader);
                    reader.ReadBytes(length);
                    break;
                case 5:
                    reader.ReadBytes(4);
                    break;
                default:
                    throw new FormatException($"Unknown wire type: {wireType}");
            }
        }

        private JObject? TryParseAsStruct(byte[] bytes)
        {
            Logger.WriteTrace(() => $"TryParseAsStruct: Attempting to parse {bytes.Length} bytes as Struct");

            try
            {
                var protobufStruct = Struct.Parser.ParseFrom(bytes);
                Logger.WriteDebug(() => $"TryParseAsStruct: Successfully parsed as Struct with {protobufStruct.Fields.Count} fields");
                return ConvertStructToJToken(protobufStruct);
            }
            catch (Exception ex)
            {
                Logger.WriteTrace(() => $"TryParseAsStruct: Failed - {ex.Message}");
                return null;
            }
        }

        private static JObject? TryParseAsAny(byte[] bytes)
        {
            try
            {
                var anyMessage = Any.Parser.ParseFrom(bytes);
                return ConvertAnyToJToken(anyMessage);
            }
            catch
            {
                return null;
            }
        }

        private JToken? TryParseAsValue(byte[] bytes)
        {
            Logger.WriteTrace(() => $"TryParseAsValue: Attempting to parse {bytes.Length} bytes as Value");

            try
            {
                var value = Value.Parser.ParseFrom(bytes);
                Logger.WriteDebug(() => $"TryParseAsValue: Successfully parsed as Value (kind: {value.KindCase})");
                return ConvertValueToJToken(value);
            }
            catch (Exception ex)
            {
                Logger.WriteTrace(() => $"TryParseAsValue: Failed - {ex.Message}");
                return null;
            }
        }

        private static JObject ParseAsGenericMessage(byte[] bytes)
        {
            var result = new JObject();

            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);

            var fields = new JObject();

            try
            {
                while (stream.Position < stream.Length)
                {
                    var tag = ReadVarint(reader);
                    var wireType = (int)(tag & 0x7);
                    var fieldNum = (int)(tag >> 3);

                    var fieldValue = ReadFieldValue(reader, wireType);

                    var fieldKey = $"field_{fieldNum}";
                    if (fields.ContainsKey(fieldKey))
                    {
                        var existingValue = fields[fieldKey];
                        if (existingValue is JArray array)
                        {
                            array.Add(fieldValue);
                        }
                        else
                        {
                            fields[fieldKey] = new JArray { existingValue!, fieldValue };
                        }
                    }
                    else
                    {
                        fields[fieldKey] = fieldValue;
                    }
                }
            }
            catch
            {
                result["raw_data"] = Convert.ToBase64String(bytes);
                result["format"] = "unknown_protobuf";
                return result;
            }

            result["fields"] = fields;
            result["format"] = "generic_protobuf";
            return result;
        }

        private JObject ConvertStructToJToken(Struct protobufStruct, int currentDepth = 1)
        {
            Logger.WriteTrace(() => $"ConvertStructToJToken: Converting Struct with {protobufStruct.Fields.Count} fields at depth {currentDepth}");

            if (Config.MaxDepth.HasValue && currentDepth > Config.MaxDepth.Value)
            {
                var message = $"Protobuf Struct depth ({currentDepth}) exceeds maximum allowed depth ({Config.MaxDepth.Value})";

                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => message);
                    var errorObj = new JObject
                    {
                        ["_depth_error"] = $"Depth limit exceeded at level {currentDepth}"
                    };
                    return errorObj;
                }

                Logger.WriteError(() => $"ConvertStructToJToken: {message}");
                throw new FormatException(message);
            }

            var result = new JObject();

            foreach (var field in protobufStruct.Fields)
            {
                result[field.Key] = ConvertValueToJToken(field.Value, currentDepth + 1);
            }

            return result;
        }

        private static JObject ConvertAnyToJToken(Any anyMessage)
        {
            var result = new JObject
            {
                ["@type"] = anyMessage.TypeUrl,
                ["value"] = Convert.ToBase64String(anyMessage.Value.ToByteArray())
            };

            return result;
        }

        private JToken ConvertValueToJToken(Value value, int currentDepth = 1)
        {
            Logger.WriteTrace(() => $"ConvertValueToJToken: Converting Value (kind: {value.KindCase}) at depth {currentDepth}");

            if (Config.MaxDepth.HasValue && currentDepth > Config.MaxDepth.Value)
            {
                var message = $"Protobuf Value depth ({currentDepth}) exceeds maximum allowed depth ({Config.MaxDepth.Value})";

                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => message);
                    return new JValue($"[Depth limit exceeded at level {currentDepth}]");
                }

                Logger.WriteError(() => $"ConvertValueToJToken: {message}");
                throw new FormatException(message);
            }

            return value.KindCase switch
            {
                Value.KindOneofCase.StringValue => new JValue(value.StringValue),
                Value.KindOneofCase.NumberValue => new JValue(value.NumberValue),
                Value.KindOneofCase.BoolValue => new JValue(value.BoolValue),
                Value.KindOneofCase.NullValue => JValue.CreateNull(),
                Value.KindOneofCase.ListValue => ConvertListValueToJArray(value.ListValue, currentDepth + 1),
                Value.KindOneofCase.StructValue => ConvertStructToJToken(value.StructValue, currentDepth + 1),
                _ => JValue.CreateNull()
            };
        }

        private JArray ConvertListValueToJArray(ListValue listValue, int currentDepth = 1)
        {
            Logger.WriteTrace(() => $"ConvertListValueToJArray: Converting ListValue with {listValue.Values.Count} items at depth {currentDepth}");

            if (Config.MaxDepth.HasValue && currentDepth > Config.MaxDepth.Value)
            {
                var message = $"Protobuf ListValue depth ({currentDepth}) exceeds maximum allowed depth ({Config.MaxDepth.Value})";

                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(() => message);
                    var errorArray = new JArray
                    {
                        new JValue($"[Depth limit exceeded at level {currentDepth}]")
                    };
                    return errorArray;
                }

                Logger.WriteError(() => $"ConvertListValueToJArray: {message}");
                throw new FormatException(message);
            }

            var result = new JArray();

            foreach (var item in listValue.Values)
            {
                result.Add(ConvertValueToJToken(item, currentDepth + 1));
            }

            return result;
        }

        private static ulong ReadVarint(BinaryReader reader)
        {
            ulong value = 0;
            int shift = 0;

            while (true)
            {
                var b = reader.ReadByte();
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 64) throw new FormatException("Invalid varint");
            }

            return value;
        }

        private static JValue ReadFieldValue(BinaryReader reader, int wireType)
        {
            return wireType switch
            {
                0 => new JValue((long)ReadVarint(reader)),
                1 => new JValue(reader.ReadDouble()),
                2 => ReadLengthDelimitedField(reader),
                5 => new JValue(reader.ReadSingle()),
                _ => new JValue($"unknown_wire_type_{wireType}")
            };
        }

        private static JValue ReadLengthDelimitedField(BinaryReader reader)
        {
            var length = (int)ReadVarint(reader);
            var bytes = reader.ReadBytes(length);

            try
            {
                var str = System.Text.Encoding.UTF8.GetString(bytes);
                if (IsValidUtf8String(str))
                {
                    return new JValue(str);
                }
            }
            catch { }

            return new JValue(Convert.ToBase64String(bytes));
        }

        private static bool IsValidUtf8String(string str)
            => str.All(c => !char.IsControl(c) || char.IsWhiteSpace(c));
    }
}