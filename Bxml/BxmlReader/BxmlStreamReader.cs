using System.Text;

namespace FormatConverter.Bxml.BxmlReader
{
    /// <summary>
    /// Streaming reader for BXML files compatible with BxmlDocumentWriter.
    /// </summary>
    public sealed class BxmlStreamReader : IDisposable
    {
        private const int MAX_STRING_COUNT = 50000;
        private const int MAX_ATTRIBUTE_COUNT = 5000;
        private const int MAX_CHILD_COUNT = 5000;
        private const int MAX_STRING_LENGTH = 1000000;

        private readonly Stream _stream;
        private readonly BinaryReader _reader;
        private readonly bool _strictMode;
        private readonly int _maxDepth;

        private bool _initialized;
        private byte _version;
        private bool _compressArrays;
        private bool _bigEndian;
        private long _bytesRead;

        private string[]? _stringTable;
        private bool _stringTableLoaded;

        public byte Version => _version;
        public bool CompressArrays => _compressArrays;
        public bool BigEndian => _bigEndian;
        public long BytesRead => _bytesRead;
        public bool IsInitialized => _initialized;
        public bool IsStringTableLoaded => _stringTableLoaded;

        public BxmlStreamReader(Stream stream, bool strictMode = false, int maxDepth = 100)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
            _strictMode = strictMode;
            _maxDepth = maxDepth;
        }

        /// <summary>
        /// Initializes the reader by reading the header and string table.
        /// </summary>
        public void Initialize()
        {
            if (_initialized)
                return;

            ValidateSignature();
            _version = _reader.ReadByte();

            byte flags = _reader.ReadByte();
            _compressArrays = (flags & 0x01) != 0;
            _bigEndian = (flags & 0x02) != 0;

            ushort reserved = _reader.ReadUInt16();

            _stringTable = ReadStringTable();
            _stringTableLoaded = true;

            _initialized = true;
        }

        /// <summary>
        /// Reads the root node and returns it.
        /// </summary>
        public BxmlElement ReadDocument()
        {
            if (!_initialized)
                throw new InvalidOperationException("Reader not initialized. Call Initialize() first.");

            var root = ReadElement(0);

            var footer = _reader.ReadBytes(4);
            string footerStr = Encoding.ASCII.GetString(footer);
            if (footerStr != "EOFB")
                throw new FormatException($"Invalid footer: '{footerStr}', expected 'EOFB'");

            _bytesRead = _stream.Position;
            return root;
        }

        /// <summary>
        /// Gets the loaded string table.
        /// </summary>
        public string[] GetStringTable()
        {
            if (!_stringTableLoaded)
                throw new InvalidOperationException("String table not yet loaded. Call Initialize() first.");

            return _stringTable!;
        }

        private void ValidateSignature()
        {
            var signature = _reader.ReadBytes(4);
            string sigStr = Encoding.ASCII.GetString(signature);

            if (sigStr != "BXML")
                throw new FormatException($"Invalid BXML signature: '{sigStr}', expected 'BXML'");
        }

        private string[] ReadStringTable()
        {
            ushort count = _reader.ReadUInt16();

            var maxStrings = _strictMode ? 1000 : MAX_STRING_COUNT;
            if (count > maxStrings)
                throw new FormatException($"String count {count} exceeds maximum allowed {maxStrings}");

            var table = new string[count];

            for (int i = 0; i < count; i++)
            {
                if (_stream.Position >= _stream.Length)
                    throw new FormatException($"Unexpected end of stream while reading string {i}");

                ushort length = _reader.ReadUInt16();
                var maxLength = _strictMode ? 10000 : MAX_STRING_LENGTH;
                if (length > maxLength)
                    throw new FormatException($"String {i} length {length} exceeds maximum allowed {maxLength}");

                if (_stream.Position + length > _stream.Length)
                    throw new FormatException($"String {i} extends beyond stream boundary");

                var bytes = _reader.ReadBytes(length);
                table[i] = Encoding.UTF8.GetString(bytes);
            }

            return table;
        }

        private BxmlElement ReadElement(int depth)
        {
            if (depth > _maxDepth)
                throw new FormatException($"Maximum nesting depth {_maxDepth} exceeded");

            if (_stream.Position >= _stream.Length)
                throw new FormatException("Unexpected end of stream while reading element");

            byte nodeType = _reader.ReadByte();
            if (nodeType != 1)
                throw new FormatException($"Expected element type 1, got {nodeType} at position {_stream.Position - 1}");

            ushort nameIndex = _reader.ReadUInt16();
            if (nameIndex >= _stringTable!.Length)
                throw new FormatException($"Name index {nameIndex} out of bounds (table size: {_stringTable.Length})");

            var element = new BxmlElement { NameIndex = (uint)nameIndex };

            ushort attrCount = _reader.ReadUInt16();
            var maxAttrs = _strictMode ? 100 : MAX_ATTRIBUTE_COUNT;
            if (attrCount > maxAttrs)
                throw new FormatException($"Attribute count {attrCount} exceeds maximum allowed {maxAttrs}");

            for (int i = 0; i < attrCount; i++)
            {
                ushort key = _reader.ReadUInt16();
                ushort value = _reader.ReadUInt16();
                element.Attributes[(uint)key] = (uint)value;
            }

            byte valueType = _reader.ReadByte();
            element.Value = ReadValue(valueType);

            ushort childCount = _reader.ReadUInt16();
            bool isCompressedArray = (childCount & 0x8000) != 0;

            if (isCompressedArray)
            {
                childCount = (ushort)(childCount & 0x7FFF);
                ReadCompressedArray(element, childCount, depth);
            }
            else
            {
                var maxChildren = _strictMode ? 100 : MAX_CHILD_COUNT;
                if (childCount > maxChildren)
                    throw new FormatException($"Child count {childCount} exceeds maximum allowed {maxChildren}");

                for (int i = 0; i < childCount; i++)
                {
                    element.Children.Add(ReadElement(depth + 1));
                }
            }

            return element;
        }

        private object? ReadValue(byte valueType)
        {
            return valueType switch
            {
                0 => null,
                1 => _stringTable![_reader.ReadUInt16()],
                2 => _reader.ReadByte(),
                3 => ReadInt16(),
                4 => ReadInt32(),
                5 => ReadInt64(),
                6 => ReadSingle(),
                7 => ReadDouble(),
                _ => throw new FormatException($"Unknown value type: {valueType}")
            };
        }

        private void ReadCompressedArray(BxmlElement parent, ushort count, int depth)
        {
            byte typeNameIndex = _reader.ReadByte();
            string typeName = _stringTable![typeNameIndex];

            for (int i = 0; i < count; i++)
            {
                var childElement = new BxmlElement
                {
                    NameIndex = (uint)GetStringIndex("item")
                };

                switch (typeName)
                {
                    case "string":
                        childElement.TextIndex = (uint)_reader.ReadUInt16();
                        break;
                    case "integer":
                        ReadInt64();
                        break;
                    case "float":
                        ReadDouble();
                        break;
                    case "bool":
                        _reader.ReadBoolean();
                        break;
                    default:
                        _reader.ReadUInt16();
                        break;
                }

                parent.Children.Add(childElement);
            }
        }

        private ushort GetStringIndex(string value)
        {
            for (ushort i = 0; i < _stringTable!.Length; i++)
            {
                if (_stringTable[i] == value)
                    return i;
            }
            throw new FormatException($"String '{value}' not found in string table");
        }

        private short ReadInt16()
        {
            var bytes = _reader.ReadBytes(2);
            if (_bigEndian && BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToInt16(bytes, 0);
        }

        private int ReadInt32()
        {
            var bytes = _reader.ReadBytes(4);
            if (_bigEndian && BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

        private long ReadInt64()
        {
            var bytes = _reader.ReadBytes(8);
            if (_bigEndian && BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToInt64(bytes, 0);
        }

        private float ReadSingle()
        {
            var bytes = _reader.ReadBytes(4);
            if (_bigEndian && BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        private double ReadDouble()
        {
            var bytes = _reader.ReadBytes(8);
            if (_bigEndian && BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        public void Dispose()
        {
            _reader?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}