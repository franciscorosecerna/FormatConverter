using Newtonsoft.Json.Linq;
using System.Text;

namespace FormatConverter.Bxml.BxmlWriter
{
    public sealed class BxmlDocumentWriter : IDisposable
    {
        private readonly BinaryWriter _writer;
        private readonly BxmlWriteOptions _options;
        private bool _disposed = false;
        private long _nodeCount = 0;

        public BxmlDocumentWriter(Stream stream, BxmlWriteOptions? options = null)
        {
            _writer = new BinaryWriter(stream, options?.Encoding ?? Encoding.UTF8,
                options?.LeaveOpen ?? false);
            _options = options ?? BxmlWriteOptions.Default;
        }

        public void WriteDocument(JToken root, string rootName = "root")
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (rootName == null) throw new ArgumentNullException(nameof(rootName));

            WriteHeader();

            var stringTable = BuildStringTable(root, rootName);

            stringTable.WriteTo(_writer);

            WriteNode(stringTable, root, rootName, 0);

            WriteFooter();
        }

        private void WriteHeader()
        {
            _writer.Write(Encoding.UTF8.GetBytes("BXML"));

            _writer.Write((byte)1);

            byte flags = 0;
            if (_options.CompressArrays) flags |= 0x01;
            if (_options.Endianness == Endianness.BigEndian) flags |= 0x02;
            _writer.Write(flags);

            _writer.Write((ushort)0);
        }

        private void WriteFooter()
        {
            _writer.Write(Encoding.UTF8.GetBytes("EOFB"));
        }

        private BxmlStringTable BuildStringTable(JToken root, string rootName)
        {
            var stringTable = new BxmlStringTable();
            CollectStrings(stringTable, root, rootName, 0);
            return stringTable;
        }

        private void CollectStrings(BxmlStringTable stringTable, JToken token, string name, int depth)
        {
            if (depth > _options.MaxDepth)
                throw new InvalidDataException($"Max depth {_options.MaxDepth} exceeded");

            stringTable.GetOrAdd(name);
            stringTable.GetOrAdd("type");
            stringTable.GetOrAdd(GetBxmlType(token));

            if (token.Type == JTokenType.String)
            {
                stringTable.GetOrAdd(token.Value<string>()!);
            }

            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    CollectStrings(stringTable, prop.Value, prop.Name, depth + 1);
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    CollectStrings(stringTable, item, "item", depth + 1);
                }
            }
        }

        private void WriteNode(BxmlStringTable stringTable, JToken token, string name, int depth)
        {
            if (depth > _options.MaxDepth)
                throw new InvalidDataException($"Max depth {_options.MaxDepth} exceeded");

            _nodeCount++;

            _writer.Write((byte)1);

            _writer.Write((ushort)stringTable.GetOrAdd(name));

            WriteAttributes(stringTable, token);

            WriteValue(stringTable, token);

            WriteChildren(stringTable, token, depth);
        }

        private void WriteAttributes(BxmlStringTable stringTable, JToken token)
        {
            var attrs = new Dictionary<int, int>
            {
                [stringTable.GetOrAdd("type")] = stringTable.GetOrAdd(GetBxmlType(token))
            };

            _writer.Write((ushort)attrs.Count);
            foreach (var kv in attrs)
            {
                _writer.Write((ushort)kv.Key);
                _writer.Write((ushort)kv.Value);
            }
        }

        private void WriteValue(BxmlStringTable stringTable, JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                case JTokenType.Array:
                case JTokenType.Null:
                    _writer.Write((byte)0);
                    break;

                case JTokenType.String:
                    _writer.Write((byte)1);
                    _writer.Write((ushort)stringTable.GetOrAdd(token.Value<string>()!));
                    break;

                case JTokenType.Integer:
                    WriteIntegerValue(token);
                    break;

                case JTokenType.Float:
                    WriteFloatValue(token);
                    break;

                case JTokenType.Boolean:
                    _writer.Write((byte)1);
                    _writer.Write(token.Value<bool>());
                    break;

                case JTokenType.Date:
                    _writer.Write((byte)1);
                    WriteDateTimeValue(token.Value<DateTime>());
                    break;

                case JTokenType.Bytes:
                    _writer.Write((byte)1);
                    WriteBytesValue(token.Value<byte[]>()!);
                    break;

                default:
                    _writer.Write((byte)1);
                    _writer.Write((ushort)stringTable.GetOrAdd(token.ToString()));
                    break;
            }
        }

        private void WriteIntegerValue(JToken token)
        {
            var value = token.Value<long>();

            if (value >= byte.MinValue && value <= byte.MaxValue)
            {
                _writer.Write((byte)2);
                _writer.Write((byte)value);
            }
            else if (value >= short.MinValue && value <= short.MaxValue)
            {
                _writer.Write((byte)3);
                WriteInt16((short)value);
            }
            else if (value >= int.MinValue && value <= int.MaxValue)
            {
                _writer.Write((byte)4);
                WriteInt32((int)value);
            }
            else
            {
                _writer.Write((byte)5);
                WriteInt64(value);
            }
        }

        private void WriteFloatValue(JToken token)
        {
            var value = token.Value<double>();
            var floatValue = (float)value;

            if (floatValue == value)
            {
                _writer.Write((byte)6);
                WriteSingle(floatValue);
            }
            else
            {
                _writer.Write((byte)7);
                WriteDouble(value);
            }
        }

        private void WriteChildren(BxmlStringTable stringTable, JToken token, int depth)
        {
            if (token is JObject obj)
            {
                _writer.Write((ushort)obj.Count);
                foreach (var prop in obj.Properties())
                {
                    WriteNode(stringTable, prop.Value, prop.Name, depth + 1);
                }
            }
            else if (token is JArray arr)
            {
                if (_options.CompressArrays && IsHomogeneousArray(arr) && arr.Count > 0)
                {
                    WriteCompressedArray(stringTable, arr, depth);
                }
                else
                {
                    _writer.Write((ushort)arr.Count);
                    foreach (var item in arr)
                    {
                        WriteNode(stringTable, item, "item", depth + 1);
                    }
                }
            }
            else
            {
                _writer.Write((ushort)0); // No children
            }
        }

        private void WriteCompressedArray(BxmlStringTable stringTable, JArray arr, int depth)
        {
            _writer.Write((ushort)(arr.Count | 0x8000));

            var firstItem = arr[0];
            _writer.Write((byte)stringTable.GetOrAdd(GetBxmlType(firstItem)));

            foreach (var item in arr)
            {
                WriteValueOnly(stringTable, item);
            }
        }

        private void WriteValueOnly(BxmlStringTable stringTable, JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.String:
                    _writer.Write((ushort)stringTable.GetOrAdd(token.Value<string>()!));
                    break;
                case JTokenType.Integer:
                    WriteInt64(token.Value<long>());
                    break;
                case JTokenType.Float:
                    WriteDouble(token.Value<double>());
                    break;
                case JTokenType.Boolean:
                    _writer.Write(token.Value<bool>());
                    break;
                default:
                    _writer.Write((ushort)stringTable.GetOrAdd(token.ToString()));
                    break;
            }
        }

        private static bool IsHomogeneousArray(JArray arr)
        {
            if (arr.Count == 0) return true;

            var firstType = arr[0].Type;
            for (int i = 1; i < arr.Count; i++)
            {
                if (arr[i].Type != firstType) return false;
            }
            return true;
        }

        private static string GetBxmlType(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Object => "object",
                JTokenType.Array => "array",
                JTokenType.String => "string",
                JTokenType.Integer => "integer",
                JTokenType.Float => "float",
                JTokenType.Boolean => "bool",
                JTokenType.Null => "null",
                JTokenType.Date => "date",
                JTokenType.Bytes => "bytes",
                JTokenType.Guid => "guid",
                _ => "unknown"
            };
        }

        private void WriteInt16(short value)
        {
            if (_options.Endianness == Endianness.BigEndian)
            {
                var bytes = BitConverter.GetBytes(value);
                Array.Reverse(bytes);
                _writer.Write(bytes);
            }
            else
            {
                _writer.Write(value);
            }
        }

        private void WriteInt32(int value) => WriteBytes(BitConverter.GetBytes(value));
        private void WriteInt64(long value) => WriteBytes(BitConverter.GetBytes(value));
        private void WriteSingle(float value) => WriteBytes(BitConverter.GetBytes(value));
        private void WriteDouble(double value) => WriteBytes(BitConverter.GetBytes(value));

        private void WriteBytes(byte[] bytes)
        {
            if (_options.Endianness == Endianness.BigEndian && BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            _writer.Write(bytes);
        }

        private void WriteDateTimeValue(DateTime dateTime)
        {
            WriteInt64(dateTime.ToBinary());
        }

        private void WriteBytesValue(byte[] bytes)
        {
            _writer.Write((ushort)bytes.Length);
            _writer.Write(bytes);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _writer?.Dispose();
                _disposed = true;
            }
        }
    }
}