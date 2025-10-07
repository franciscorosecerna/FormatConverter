using FormatConverter.Interfaces;
using Newtonsoft.Json.Linq;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;

namespace FormatConverter.Protobuf
{
    public class ProtobufInputStrategy : BaseInputStrategy
    {
        public override JToken Parse(string input)
        {
            if (Config.UseStreaming)
            {
                var firstToken = ParseStream(input).FirstOrDefault();
                return firstToken ?? new JObject();
            }

            try
            {
                var token = ParseProtobufDocument(input);

                if (Config.SortKeys)
                    token = SortKeysRecursively(token);

                return token;
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                return HandleParsingError(ex, input);
            }
        }

        public override IEnumerable<JToken> ParseStream(string input)
        {
            if (!Config.UseStreaming)
            {
                yield return Parse(input);
                yield break;
            }

            IEnumerable<JToken> tokens;

            try
            {
                if (HasMultipleMessages(input))
                {
                    tokens = StreamMultipleMessages(input);
                }
                else if (HasLargeRepeatedFields(input))
                {
                    tokens = StreamLargeRepeatedFields(input);
                }
                else if (HasManyFields(input))
                {
                    tokens = StreamByFields(input);
                }
                else
                {
                    tokens = [ParseProtobufDocument(input)];
                }
            }
            catch (InvalidProtocolBufferException ex)
            {
                tokens = HandleStreamingError(ex, input);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                tokens = HandleStreamingError(ex, input);
            }

            foreach (var token in tokens)
            {
                yield return token;
            }
        }

        private JToken ParseProtobufDocument(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                throw new FormatException("Protobuf input is empty or null");
            }

            byte[] bytes = DecodeInput(input);

            JToken result = TryParseAsStruct(bytes) ??
                           TryParseAsAny(bytes) ??
                           TryParseAsValue(bytes) ??
                           ParseAsGenericMessage(bytes);

            if (Config.NoMetadata)
            {
                result = RemoveMetadataProperties(result);
            }

            return result;
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

        private static bool HasMultipleMessages(string input)
        {
            try
            {
                var bytes = Convert.FromBase64String(input);
                return bytes.Length > 10000;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasLargeRepeatedFields(string input)
        {
            try
            {
                var bytes = DecodeInput(input);
                using var stream = new MemoryStream(bytes);
                using var reader = new BinaryReader(stream);

                var fieldCounts = new Dictionary<int, int>();

                while (stream.Position < stream.Length && stream.Position < 1000)
                {
                    try
                    {
                        var tag = ReadVarint(reader);
                        var wireType = (int)(tag & 0x7);
                        var fieldNum = (int)(tag >> 3);

                        fieldCounts[fieldNum] = fieldCounts.GetValueOrDefault(fieldNum) + 1;

                        SkipFieldValue(reader, wireType);
                    }
                    catch
                    {
                        break;
                    }
                }

                return fieldCounts.Values.Any(count => count > 20);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasManyFields(string input)
        {
            try
            {
                var bytes = DecodeInput(input);
                using var stream = new MemoryStream(bytes);
                using var reader = new BinaryReader(stream);

                var uniqueFields = new HashSet<int>();

                while (stream.Position < stream.Length && stream.Position < 500)
                {
                    try
                    {
                        var tag = ReadVarint(reader);
                        var wireType = (int)(tag & 0x7);
                        var fieldNum = (int)(tag >> 3);

                        uniqueFields.Add(fieldNum);

                        SkipFieldValue(reader, wireType);
                    }
                    catch
                    {
                        break;
                    }
                }

                return uniqueFields.Count > 15;
            }
            catch
            {
                return false;
            }
        }

        private IEnumerable<JToken> StreamMultipleMessages(string input)
        {
            var tokens = new List<JToken>();
            const int chunkSize = 8192;

            try
            {
                var bytes = DecodeInput(input);

                for (int offset = 0; offset < bytes.Length; offset += chunkSize)
                {
                    var chunkLength = Math.Min(chunkSize, bytes.Length - offset);
                    var chunk = new byte[chunkLength];
                    Array.Copy(bytes, offset, chunk, 0, chunkLength);

                    var chunkResult = TryParseAsStruct(chunk) ??
                                     TryParseAsAny(chunk) ??
                                     TryParseAsValue(chunk) ??
                                     ParseAsGenericMessage(chunk);

                    var chunkObject = new JObject();
                    if (chunkResult is JObject obj)
                    {
                        foreach (var prop in obj.Properties())
                        {
                            chunkObject[prop.Name] = prop.Value;
                        }
                    }
                    else
                    {
                        chunkObject["value"] = chunkResult;
                    }

                    chunkObject["_chunk_info"] = new JObject
                    {
                        ["chunk_start"] = offset,
                        ["chunk_size"] = chunkLength,
                        ["total_size"] = bytes.Length,
                        ["type"] = "protobuf_chunk"
                    };

                    tokens.Add(Config.SortKeys ? SortKeysRecursively(chunkObject) : chunkObject);
                }
            }
            catch
            {
                tokens.Add(ParseProtobufDocument(input));
            }

            return tokens;
        }

        private IEnumerable<JToken> StreamLargeRepeatedFields(string input)
        {
            var chunks = new List<JToken>();
            const int itemsPerChunk = 10;

            try
            {
                var bytes = DecodeInput(input);
                using var stream = new MemoryStream(bytes);
                using var reader = new BinaryReader(stream);

                var fieldGroups = new Dictionary<int, List<JToken>>();
                var scalarFields = new JObject();

                while (stream.Position < stream.Length)
                {
                    try
                    {
                        var tag = ReadVarint(reader);
                        var wireType = (int)(tag & 0x7);
                        var fieldNum = (int)(tag >> 3);

                        var fieldValue = ReadFieldValue(reader, wireType);

                        if (!fieldGroups.ContainsKey(fieldNum))
                        {
                            fieldGroups[fieldNum] = new List<JToken>();
                        }

                        fieldGroups[fieldNum].Add(fieldValue);
                    }
                    catch
                    {
                        break;
                    }
                }

                foreach (var kvp in fieldGroups)
                {
                    var fieldNum = kvp.Key;
                    var values = kvp.Value;

                    if (values.Count > itemsPerChunk)
                    {
                        for (int i = 0; i < values.Count; i += itemsPerChunk)
                        {
                            var chunkValues = values.Skip(i).Take(itemsPerChunk).ToList();
                            var chunkObject = new JObject();
                            chunkObject[$"field_{fieldNum}"] = new JArray(chunkValues);

                            chunkObject["_chunk_info"] = new JObject
                            {
                                ["field_number"] = fieldNum,
                                ["chunk_start"] = i,
                                ["chunk_size"] = chunkValues.Count,
                                ["total_items"] = values.Count,
                                ["type"] = "repeated_field_chunk"
                            };

                            chunks.Add(Config.SortKeys ? SortKeysRecursively(chunkObject) : chunkObject);
                        }
                    }
                    else
                    {
                        scalarFields[$"field_{fieldNum}"] = values.Count == 1 ? values[0] : new JArray(values);
                    }
                }

                if (scalarFields.Count > 0)
                {
                    chunks.Insert(0, Config.SortKeys ? SortKeysRecursively(scalarFields) : scalarFields);
                }
            }
            catch
            {
                chunks.Add(ParseProtobufDocument(input));
            }

            return chunks;
        }

        private IEnumerable<JToken> StreamByFields(string input)
        {
            var chunks = new List<JToken>();
            const int fieldsPerChunk = 5;

            try
            {
                var bytes = DecodeInput(input);
                using var stream = new MemoryStream(bytes);
                using var reader = new BinaryReader(stream);

                var currentChunk = new JObject();
                var processedFields = 0;
                var uniqueFields = new HashSet<int>();

                while (stream.Position < stream.Length)
                {
                    try
                    {
                        var tag = ReadVarint(reader);
                        var wireType = (int)(tag & 0x7);
                        var fieldNum = (int)(tag >> 3);

                        var fieldValue = ReadFieldValue(reader, wireType);

                        if (!uniqueFields.Contains(fieldNum))
                        {
                            currentChunk[$"field_{fieldNum}"] = fieldValue;
                            uniqueFields.Add(fieldNum);
                            processedFields++;

                            if (processedFields >= fieldsPerChunk)
                            {
                                chunks.Add(Config.SortKeys ? SortKeysRecursively(currentChunk) : currentChunk);
                                currentChunk = new JObject();
                                processedFields = 0;
                                uniqueFields.Clear();
                            }
                        }
                        else
                        {
                            var existingValue = currentChunk[$"field_{fieldNum}"];
                            if (existingValue is JArray array)
                            {
                                array.Add(fieldValue);
                            }
                            else
                            {
                                currentChunk[$"field_{fieldNum}"] = new JArray { existingValue, fieldValue };
                            }
                        }
                    }
                    catch
                    {
                        break;
                    }
                }

                if (processedFields > 0)
                {
                    chunks.Add(Config.SortKeys ? SortKeysRecursively(currentChunk) : currentChunk);
                }
            }
            catch
            {
                chunks.Add(ParseProtobufDocument(input));
            }

            return chunks;
        }

        private IEnumerable<JToken> HandleStreamingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: Protobuf streaming error ignored: {ex.Message}");
                return [HandleParsingError(ex, input)];
            }
            else
            {
                throw new FormatException($"Protobuf streaming failed: {ex.Message}", ex);
            }
        }

        private JObject HandleParsingError(Exception ex, string input)
        {
            if (Config.IgnoreErrors)
            {
                Console.WriteLine($"Warning: Protobuf parsing error ignored: {ex.Message}");
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["raw"] = input.Length > 1000 ? string.Concat(input.AsSpan(0, 1000), "...") : input
                };
            }
            throw new FormatException($"Invalid Protobuf: {ex.Message}", ex);
        }

        private JToken RemoveMetadataProperties(JToken token)
        {
            if (token is JObject obj)
            {
                var result = new JObject();
                foreach (var prop in obj.Properties())
                {
                    if (!prop.Name.StartsWith("_") &&
                        !prop.Name.Equals("format", StringComparison.OrdinalIgnoreCase))
                    {
                        result[prop.Name] = RemoveMetadataProperties(prop.Value);
                    }
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

        private static byte[] ParseHexString(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("-", "");

            if (hex.Length % 2 != 0)
            {
                throw new FormatException("Invalid hex string length");
            }

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        private JObject? TryParseAsStruct(byte[] bytes)
        {
            try
            {
                var protobufStruct = Struct.Parser.ParseFrom(bytes);
                return ConvertStructToJToken(protobufStruct);
            }
            catch
            {
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
            try
            {
                var value = Value.Parser.ParseFrom(bytes);
                return ConvertValueToJToken(value);
            }
            catch
            {
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
                            fields[fieldKey] = new JArray { existingValue, fieldValue };
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

        private JObject ConvertStructToJToken(Struct protobufStruct)
        {
            var result = new JObject();

            foreach (var field in protobufStruct.Fields)
            {
                result[field.Key] = ConvertValueToJToken(field.Value);
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

        private JToken ConvertValueToJToken(Value value)
        {
            return value.KindCase switch
            {
                Value.KindOneofCase.StringValue => new JValue(value.StringValue),
                Value.KindOneofCase.NumberValue => new JValue(FormatNumberValue(value.NumberValue)),
                Value.KindOneofCase.BoolValue => new JValue(value.BoolValue),
                Value.KindOneofCase.NullValue => JValue.CreateNull(),
                Value.KindOneofCase.ListValue => ConvertListValueToJArray(value.ListValue),
                Value.KindOneofCase.StructValue => ConvertStructToJToken(value.StructValue),
                _ => JValue.CreateNull()
            };
        }

        private JArray ConvertListValueToJArray(ListValue listValue)
        {
            var result = new JArray();

            foreach (var item in listValue.Values)
            {
                result.Add(ConvertValueToJToken(item));
            }

            return result;
        }

        private object FormatNumberValue(double number)
        {
            if (!string.IsNullOrEmpty(Config.NumberFormat))
            {
                return Config.NumberFormat.ToLower() switch
                {
                    "hexadecimal" => $"0x{(long)number:X}",
                    "scientific" => number.ToString("E"),
                    _ => number
                };
            }
            return number;
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
            finally { }

            return new JValue(Convert.ToBase64String(bytes));
        }

        private static bool IsValidUtf8String(string str)
        {
            return str.All(c => !char.IsControl(c) || char.IsWhiteSpace(c));
        }

        private JToken SortKeysRecursively(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Object => SortJObject((JObject)token),
                JTokenType.Array => new JArray(((JArray)token).Select(SortKeysRecursively)),
                _ => token
            };
        }

        private JObject SortJObject(JObject obj)
        {
            var sorted = new JObject();
            foreach (var property in obj.Properties().OrderBy(p => p.Name))
            {
                sorted[property.Name] = SortKeysRecursively(property.Value);
            }
            return sorted;
        }
    }
}