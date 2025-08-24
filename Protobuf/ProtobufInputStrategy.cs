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
            try
            {
                if (string.IsNullOrEmpty(input))
                {
                    throw new FormatException("Protobuf input is empty or null");
                }

                byte[] bytes;

                try
                {
                    bytes = Convert.FromBase64String(input);
                }
                catch (FormatException)
                {
                    bytes = ParseHexString(input);
                }

                JToken result = TryParseAsStruct(bytes) ??
                               TryParseAsAny(bytes) ??
                               TryParseAsValue(bytes) ??
                               ParseAsGenericMessage(bytes);

                if (Config.SortKeys && result is JObject)
                {
                    result = SortKeysRecursively(result);
                }

                return result;
            }
            catch (InvalidProtocolBufferException ex)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: Protobuf parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"Invalid Protobuf: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                if (Config.IgnoreErrors)
                {
                    Console.WriteLine($"Warning: Protobuf parsing error ignored: {ex.Message}");
                    return new JObject
                    {
                        ["error"] = ex.Message,
                        ["raw"] = input
                    };
                }
                throw new FormatException($"Protobuf parsing failed: {ex.Message}", ex);
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

        private JObject ParseAsGenericMessage(byte[] bytes)
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
                    fields[$"field_{fieldNum}"] = fieldValue;
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

        private JValue ReadFieldValue(BinaryReader reader, int wireType)
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

        private JValue ReadLengthDelimitedField(BinaryReader reader)
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