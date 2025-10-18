using FormatConverter.Cbor.CborReader;
using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System.Buffers;

namespace FormatConverter.Cbor
{
    public class CborInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("CBOR input cannot be null or empty", nameof(input));
            }

            try
            {
                var bytes = DecodeInput(input);
                var cborObj = CBORObject.DecodeFromBytes(bytes)
                    ?? throw new FormatException("CBOR deserialization returned null");

                var token = ConvertCborToJToken(cborObj);

                return token;
            }
            catch (Exception ex) when (ex is not FormatException && ex is not ArgumentException)
            {
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Input file not found.", path);

            return ParseStreamInternal(path, cancellationToken);
        }

        private IEnumerable<JToken> ParseStreamInternal(string path, CancellationToken cancellationToken)
        {
            using var fileStream = File.OpenRead(path);

            var fileSize = fileStream.Length;
            var showProgress = fileSize > 10_485_760;

            const int BufferSize = 8192;
            var arrayPool = ArrayPool<byte>.Shared;
            byte[] buffer = arrayPool.Rent(BufferSize);
            using var memoryStream = new MemoryStream();

            int bytesRead;
            int tokensProcessed = 0;

            try
            {
                while ((bytesRead = fileStream.Read(buffer, 0, BufferSize)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    memoryStream.Write(buffer, 0, bytesRead);

                    while (true)
                    {
                        var (success, token, consumed, error) = TryReadNextCborToken(
                            memoryStream.GetBuffer(),
                            (int)memoryStream.Length,
                            path);

                        if (success && token != null)
                        {
                            tokensProcessed++;

                            if (showProgress && tokensProcessed % 100 == 0)
                            {
                                var progress = (double)fileStream.Position / fileStream.Length * 100;
                                Logger.WriteInfo($"Processing: {progress:F1}% ({tokensProcessed} elements)");
                            }

                            yield return token;
                        }
                        else if (error != null)
                        {
                            if (Config.IgnoreErrors)
                            {
                                Logger.WriteWarning(error.Message);
                                yield return CreateErrorToken(error, $"File: {path}");
                            }
                            else
                            {
                                throw error;
                            }
                        }

                        if (consumed > 0)
                        {
                            var remaining = (int)(memoryStream.Length - consumed);
                            if (remaining > 0)
                            {
                                var temp = memoryStream.GetBuffer();
                                Buffer.BlockCopy(temp, consumed, temp, 0, remaining);
                                memoryStream.SetLength(remaining);
                            }
                            else
                            {
                                memoryStream.SetLength(0);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                if (memoryStream.Length > 0)
                {
                    if (Config.IgnoreErrors)
                    {
                        Logger.WriteWarning($"{memoryStream.Length} bytes of incomplete CBOR data at end of file");
                        yield return CreateErrorToken(
                            new FormatException($"Incomplete CBOR data: {memoryStream.Length} bytes remaining"),
                            $"File: {path}");
                    }
                    else
                    {
                        throw new FormatException($"Incomplete CBOR object at end of file ({memoryStream.Length} bytes remaining)");
                    }
                }

                if (showProgress)
                {
                    Logger.WriteSuccess($"Completed: {tokensProcessed} objects processed");
                }
            }
            finally
            {
                arrayPool.Return(buffer);
            }
        }

        private (bool success, JToken? token, int consumed, Exception? error)
            TryReadNextCborToken(byte[] buffer, int length, string context)
        {
            if (length == 0)
                return (false, null, 0, null);

            try
            {
                var cborStream = new CborStreamReader(Config.CborAllowIndefiniteLength,
                    Config.CborAllowMultipleContent,
                    Config.MaxDepth!.Value);
                var bytesNeeded = cborStream.CalculateObjectSize(buffer, length);

                if (bytesNeeded < 0)
                {
                    return (false, null, 0, null);
                }

                if (bytesNeeded > length)
                {
                    return (false, null, 0, null);
                }

                var objectBytes = new byte[bytesNeeded];
                Array.Copy(buffer, 0, objectBytes, 0, bytesNeeded);

                var cborObj = CBORObject.DecodeFromBytes(objectBytes);
                var token = ConvertCborToJToken(cborObj);

                return (true, token, bytesNeeded, null);
            }
            catch (CBORException)
            {
                return (false, null, 0, null);
            }
            catch (Exception ex)
            {
                return (false, null, 0,
                    new FormatException($"Unexpected CBOR error in {context}: {ex.Message}", ex));
            }
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"CBOR parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

            throw new FormatException($"Invalid CBOR: {ex.Message}", ex);
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

        private JToken ConvertCborToJToken(CBORObject cbor)
        {
            if (cbor == null) return JValue.CreateNull();

            return cbor.Type switch
            {
                CBORType.Map => ConvertCborMapToJObject(cbor),
                CBORType.Array => ConvertCborArrayToJArray(cbor),
                CBORType.TextString => new JValue(cbor.AsString()),
                CBORType.Integer => new JValue(cbor.CanValueFitInInt64()
                    ? cbor.AsInt64Value()
                    : cbor.ToObject<System.Numerics.BigInteger>().ToString()),
                CBORType.FloatingPoint => new JValue(cbor.AsDoubleValue()),
                CBORType.Boolean => new JValue(cbor.AsBoolean()),
                CBORType.SimpleValue => cbor.IsNull ? JValue.CreateNull()
                    : cbor.IsUndefined ? JValue.CreateUndefined()
                    : new JValue(cbor.SimpleValue),
                CBORType.ByteString => new JValue(Convert.ToBase64String(cbor.GetByteString())),
                _ => new JValue(cbor.ToString())
            };
        }

        private JObject ConvertCborMapToJObject(CBORObject cborMap)
        {
            var result = new JObject();

            foreach (var key in cborMap.Keys)
            {
                var keyString = ConvertCborKeyToString(key);
                var value = ConvertCborToJToken(cborMap[key]);
                result[keyString] = value;
            }

            return result;
        }

        private JArray ConvertCborArrayToJArray(CBORObject cborArray)
        {
            var result = new JArray();

            for (int i = 0; i < cborArray.Count; i++)
            {
                result.Add(ConvertCborToJToken(cborArray[i]));
            }

            return result;
        }

        private static string ConvertCborKeyToString(CBORObject key)
        {
            return key.Type switch
            {
                CBORType.TextString => key.AsString(),
                CBORType.Integer => key.AsInt64Value().ToString(),
                CBORType.ByteString => Convert.ToBase64String(key.GetByteString()),
                _ => key.ToString()
            };
        }
    }
}