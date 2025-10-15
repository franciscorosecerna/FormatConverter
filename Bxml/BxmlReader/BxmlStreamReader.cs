using System.Text;

namespace FormatConverter.Bxml.BxmlReader
{
    /// <summary>
    /// Streaming reader for BXML files with minimal memory usage.
    /// </summary>
    public sealed class BxmlStreamReader : IDisposable
    {
        private const int MAX_ELEMENT_COUNT = 50000;
        private const int MAX_STRING_COUNT = 50000;
        private const int MAX_ATTRIBUTE_COUNT = 5000;
        private const int MAX_CHILD_COUNT = 5000;
        private const int MAX_STRING_LENGTH = 1000000;

        private readonly Stream _stream;
        private readonly BinaryReader _reader;
        private readonly bool _strictMode;
        private readonly int _maxDepth;

        private bool _initialized;
        private uint _expectedElementCount;
        private uint _expectedStringCount;
        private long _bytesRead;

        private string[]? _stringTable;
        private bool _stringTableLoaded;

        public int ElementCount => (int)_expectedElementCount;
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
        /// Initializes the reader by reading the header of the BXML file.
        /// </summary>
        public void Initialize()
        {
            if (_initialized)
                return;

            ValidateSignature();
            _expectedElementCount = _reader.ReadUInt32();

            var maxElements = _strictMode ? 1000 : MAX_ELEMENT_COUNT;
            if (_expectedElementCount > maxElements)
                throw new FormatException($"Element count {_expectedElementCount} exceeds maximum allowed {maxElements}");

            _initialized = true;
        }

        /// <summary>
        /// Lists all items in the file sequentially.
        /// The string table is automatically loaded after all elements are read.
        /// </summary>
        public IEnumerable<BxmlElement> EnumerateElements(CancellationToken cancellationToken = default)
        {
            if (!_initialized)
                throw new InvalidOperationException("Reader not initialized. Call Initialize() first.");

            for (int i = 0; i < _expectedElementCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var element = ReadElement(0);
                _bytesRead = _stream.Position;
                yield return element;
            }

            if (!_stringTableLoaded)
            {
                _expectedStringCount = _reader.ReadUInt32();

                var maxStrings = _strictMode ? 1000 : MAX_STRING_COUNT;
                if (_expectedStringCount > maxStrings)
                    throw new FormatException($"String count {_expectedStringCount} exceeds maximum allowed {maxStrings}");

                _stringTable = ReadStringTable(_expectedStringCount);
                _stringTableLoaded = true;
            }
        }

        /// <summary>
        /// Lists items in batches for efficient processing.
        /// </summary>
        public IEnumerable<IEnumerable<BxmlElement>> EnumerateBatches(int batchSize, CancellationToken cancellationToken = default)
        {
            if (batchSize <= 0)
                throw new ArgumentException("Batch size must be positive", nameof(batchSize));

            var batch = new List<BxmlElement>(batchSize);

            foreach (var element in EnumerateElements(cancellationToken))
            {
                batch.Add(element);

                if (batch.Count >= batchSize)
                {
                    yield return batch.ToArray();
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                yield return batch.ToArray();
        }

        /// <summary>
        /// Gets the loaded string table.
        /// Only available after listing all items.
        /// </summary>
        public string[] GetStringTable()
        {
            if (!_stringTableLoaded)
                throw new InvalidOperationException("String table not yet loaded. Call EnumerateElements() first.");

            return _stringTable!;
        }

        private void ValidateSignature()
        {
            var signature = _reader.ReadBytes(4);
            string sigStr = Encoding.ASCII.GetString(signature);

            if (sigStr != "BXML")
                throw new FormatException($"Invalid BXML signature: '{sigStr}', expected 'BXML'");
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

            uint nameIndex = _reader.ReadUInt32();
            uint attrCount = _reader.ReadUInt32();

            var maxAttrs = _strictMode ? 100 : MAX_ATTRIBUTE_COUNT;
            if (attrCount > maxAttrs)
                throw new FormatException($"Attribute count {attrCount} exceeds maximum allowed {maxAttrs}");

            var element = new BxmlElement { NameIndex = nameIndex };

            for (int i = 0; i < attrCount; i++)
            {
                uint key = _reader.ReadUInt32();
                uint value = _reader.ReadUInt32();
                element.Attributes[key] = value;
            }

            byte hasText = _reader.ReadByte();
            if (hasText == 1)
            {
                element.TextIndex = _reader.ReadUInt32();
            }
            else if (hasText != 0)
            {
                throw new FormatException($"Invalid hasText flag: {hasText}, expected 0 or 1");
            }

            uint childCount = _reader.ReadUInt32();
            var maxChildren = _strictMode ? 100 : MAX_CHILD_COUNT;
            if (childCount > maxChildren)
                throw new FormatException($"Child count {childCount} exceeds maximum allowed {maxChildren}");

            for (int i = 0; i < childCount; i++)
            {
                element.Children.Add(ReadElement(depth + 1));
            }

            return element;
        }

        private string[] ReadStringTable(uint count)
        {
            var table = new string[count];

            for (int i = 0; i < count; i++)
            {
                if (_stream.Position >= _stream.Length)
                    throw new FormatException($"Unexpected end of stream while reading string {i}");

                uint length = _reader.ReadUInt32();
                var maxLength = _strictMode ? 10000 : MAX_STRING_LENGTH;
                if (length > maxLength)
                    throw new FormatException($"String {i} length {length} exceeds maximum allowed {maxLength}");

                if (_stream.Position + length > _stream.Length)
                    throw new FormatException($"String {i} extends beyond stream boundary");

                var bytes = _reader.ReadBytes((int)length);
                table[i] = Encoding.UTF8.GetString(bytes);
            }

            return table;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}