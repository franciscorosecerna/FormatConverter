﻿using FormatConverter.Interfaces;
using MessagePack;
using MessagePack.Resolvers;
using Newtonsoft.Json.Linq;
using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;

namespace FormatConverter.MessagePack
{
    public class MessagePackInputStrategy : BaseInputStrategy
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

        public override JToken Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Config.IgnoreErrors
                    ? new JObject()
                    : throw new ArgumentException("MessagePack input cannot be null or empty", nameof(input));
            }

            try
            {
                var bytes = DecodeInput(input);
                var options = GetMessagePackOptions();
                var obj = MessagePackSerializer.Deserialize<object>(bytes, options)
                    ?? throw new FormatException("MessagePack deserialization returned null");

                var token = ConvertObjectToJToken(obj);

                if (Config.MaxDepth.HasValue)
                {
                    ValidateDepth(token, Config.MaxDepth.Value);
                }

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
            var options = GetMessagePackOptions();

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

                    var sequence = new ReadOnlySequence<byte>(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
                    var reader = new MessagePackReader(sequence);

                    while (!reader.End)
                    {
                        var (success, token, consumed, error) = TryReadNextMessagePackToken(ref reader, options, path);

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
                                Buffer.BlockCopy(temp, (int)consumed, temp, 0, remaining);
                                memoryStream.SetLength(remaining);
                            }
                            else
                            {
                                memoryStream.SetLength(0);
                            }

                            sequence = new ReadOnlySequence<byte>(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
                            reader = new MessagePackReader(sequence);
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
                        Logger.WriteWarning($"{memoryStream.Length} bytes of incomplete MessagePack data at end of file");
                        yield return CreateErrorToken(
                            new FormatException($"Incomplete MessagePack data: {memoryStream.Length} bytes remaining"),
                            $"File: {path}");
                    }
                    else
                    {
                        throw new FormatException($"Incomplete MessagePack object at end of file ({memoryStream.Length} bytes remaining)");
                    }
                }

                if (showProgress)
                {
                    Logger.WriteInfo($"Completed: {tokensProcessed} objects processed");
                }
            }
            finally
            {
                arrayPool.Return(buffer);
            }
        }

        private (bool success, JToken? token, long consumed, Exception? error)
    TryReadNextMessagePackToken(ref MessagePackReader reader, MessagePackSerializerOptions options, string path)
        {
            var consumedBefore = reader.Consumed;

            try
            {
                var obj = MessagePackSerializer.Deserialize<object>(ref reader, options);
                if (obj != null)
                {
                    var token = ConvertObjectToJToken(obj);

                    if (Config.MaxDepth.HasValue)
                    {
                        ValidateDepth(token, Config.MaxDepth.Value);
                    }

                    return (true, token, reader.Consumed - consumedBefore, null);
                }

                return (false, null, reader.Consumed - consumedBefore, null);
            }
            catch (MessagePackSerializationException ex)
            {
                if (reader.Consumed == consumedBefore)
                {
                    return (false, null, 0, null);
                }

                return (false, null, reader.Consumed - consumedBefore,
                    new FormatException($"Invalid MessagePack data at offset {reader.Consumed}: {ex.Message}", ex));
            }
            catch (Exception ex)
            {
                return (false, null, reader.Consumed - consumedBefore,
                    new FormatException($"Unexpected MessagePack error in {path}: {ex.Message}", ex));
            }
        }

        private void ValidateDepth(JToken token, int maxDepth)
        {
            var actualDepth = CalculateDepth(token);

            if (actualDepth > maxDepth)
            {
                var message = $"MessagePack structure depth ({actualDepth}) exceeds maximum allowed depth ({maxDepth})";

                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(message);
                }
                else
                {
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

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Logger.WriteWarning($"MessagePack parsing error: {ex.Message}");
                return CreateErrorToken(ex, input);
            }

            throw new FormatException($"Invalid MessagePack: {ex.Message}", ex);
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

        private MessagePackSerializerOptions GetMessagePackOptions()
        {
            MessagePackSerializerOptions options;

            if (Config.MessagePackUseContractless)
            {
                options = MessagePackSerializerOptions.Standard
                    .WithResolver(ContractlessStandardResolver.Instance);
            }
            else
            {
                options = MessagePackSerializerOptions.Standard
                    .WithResolver(StandardResolver.Instance);
            }

            if (Config.MessagePackOldSpec)
            {
                options = options.WithOldSpec();
            }

            if (Config.StrictMode)
            {
                options = options.WithSecurity(MessagePackSecurity.UntrustedData);
            }

            return options;
        }

        private JToken ConvertObjectToJToken(object? obj, int currentDepth = 100)
        {
            if (Config.MaxDepth.HasValue && currentDepth > Config.MaxDepth.Value)
            {
                var message = $"MessagePack structure depth ({currentDepth}) exceeds maximum allowed depth ({Config.MaxDepth.Value}) during conversion";

                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning(message);
                    return new JValue($"[Depth limit exceeded at level {currentDepth}]");
                }

                throw new FormatException(message);
            }

            if (obj == null) return JValue.CreateNull();

            return obj switch
            {
                Dictionary<object, object> dict => ConvertDictionaryToJObject(dict, currentDepth),
                Dictionary<string, object> stringDict => ConvertStringDictionaryToJObject(stringDict, currentDepth),
                List<object> list => ConvertListToJArray(list, currentDepth),
                byte[] bytes => new JValue(Convert.ToBase64String(bytes)),
                Array array => ConvertArrayToJArray(array, currentDepth),
                string str => new JValue(str),
                bool b => new JValue(b),
                byte b => new JValue(b),
                sbyte sb => new JValue(sb),
                short s => new JValue(s),
                ushort us => new JValue(us),
                int i => new JValue(i),
                uint ui => new JValue(ui),
                long l => new JValue(l),
                ulong ul => ul > long.MaxValue
                    ? new JValue(ul.ToString())
                    : new JValue((long)ul),
                float f => new JValue(f),
                double d => new JValue(d),
                decimal m => new JValue(m),
                DateTime dt => new JValue(dt),
                DateTimeOffset dto => new JValue(dto.DateTime),
                Guid guid => new JValue(guid.ToString()),
                Enum e => new JValue(e.ToString()),
                _ when obj.GetType().IsArray => ConvertArrayToJArray((Array)obj, currentDepth),
                _ => ConvertComplexObjectToJToken(obj, currentDepth)
            };
        }

        private JObject ConvertDictionaryToJObject(Dictionary<object, object> dict, int currentDepth)
        {
            var result = new JObject();
            foreach (var kvp in dict)
            {
                var key = ConvertKeyToString(kvp.Key);
                result[key] = ConvertObjectToJToken(kvp.Value, currentDepth + 1);
            }
            return result;
        }

        private JObject ConvertStringDictionaryToJObject(Dictionary<string, object> dict, int currentDepth)
        {
            var result = new JObject();
            foreach (var kvp in dict)
                result[kvp.Key] = ConvertObjectToJToken(kvp.Value, currentDepth + 1);
            return result;
        }

        private JArray ConvertListToJArray(List<object> list, int currentDepth)
        {
            var result = new JArray();
            foreach (var item in list)
                result.Add(ConvertObjectToJToken(item, currentDepth + 1));
            return result;
        }

        private JArray ConvertArrayToJArray(Array array, int currentDepth)
        {
            var result = new JArray();
            foreach (var item in array)
                result.Add(ConvertObjectToJToken(item, currentDepth + 1));
            return result;
        }

        private JToken ConvertComplexObjectToJToken(object obj, int currentDepth)
        {
            var type = obj.GetType();

            if (!type.IsClass || type == typeof(string))
            {
                return new JValue(obj.ToString());
            }

            try
            {
                var result = new JObject();

                var properties = PropertyCache.GetOrAdd(type, t => t.GetProperties());

                foreach (var prop in properties)
                {
                    if (prop.CanRead)
                    {
                        try
                        {
                            var value = prop.GetValue(obj);
                            result[prop.Name] = ConvertObjectToJToken(value, currentDepth + 1);
                        }
                        catch (Exception ex) when (Config.IgnoreErrors)
                        {
                            Logger.WriteWarning($"Error reading property '{prop.Name}': {ex.Message}");
                            result[prop.Name] = new JValue($"[Error reading property: {ex.Message}]");
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                if (Config.IgnoreErrors)
                {
                    Logger.WriteWarning($"Failed to convert complex object: {ex.Message}");
                    return new JValue($"[Complex object: {obj.ToString()}]");
                }
                throw new FormatException($"Failed to convert complex object: {ex.Message}", ex);
            }
        }

        private static string ConvertKeyToString(object key)
        {
            return key switch
            {
                string str => str,
                null => "null",
                byte[] bytes => Convert.ToBase64String(bytes),
                Guid guid => guid.ToString(),
                Enum e => e.ToString(),
                _ => key.ToString() ?? "unknown"
            };
        }
    }
}