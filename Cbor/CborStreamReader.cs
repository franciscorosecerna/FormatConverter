using System;

namespace FormatConverter.Cbor
{
    /// <summary>
    /// Low-level CBOR parser that implements RFC 8949 (CBOR specification).
    /// </summary>
    public class CborStreamReader
    {
        private const long INDEFINITE_LENGTH = -2;
        private const long INSUFFICIENT_DATA = -1;
        private const byte BREAK_STOP_CODE = 0xFF;
        private const int MAJOR_TYPE_MASK = 0x07;
        private const int ADDITIONAL_INFO_MASK = 0x1F;
        private const int DEFAULT_MAX_DEPTH = 100;

        private readonly bool _allowIndefiniteLength;
        private readonly bool _allowMultipleContent;
        private readonly int _maxDepth;

        /// <summary>
        /// Initializes a new instance of the CborStreamReader class.
        /// </summary>
        /// <param name="allowIndefiniteLength">Whether to allow indefinite-length items.</param>
        /// <param name="allowMultipleContent">Whether to allow multiple root-level CBOR objects.</param>
        /// <param name="maxDepth">Maximum nesting depth allowed. Default is 100.</param>
        public CborStreamReader(bool allowIndefiniteLength = true, bool allowMultipleContent = false, int maxDepth = DEFAULT_MAX_DEPTH)
        {
            if (maxDepth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDepth), "Maximum depth must be greater than zero.");
            }

            _allowIndefiniteLength = allowIndefiniteLength;
            _allowMultipleContent = allowMultipleContent;
            _maxDepth = maxDepth;
        }

        /// <summary>
        /// Calculates the size in bytes of a complete CBOR object in the buffer.
        /// </summary>
        /// <returns>
        /// Number of bytes the object occupies, or -1 if there is not enough data.
        /// </returns>
        public int CalculateObjectSize(byte[] buffer, int length)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (length == 0) return -1;
            if (length > buffer.Length)
            {
                throw new ArgumentException("Length exceeds buffer size.", nameof(length));
            }

            int position = 0;
            int depth = 0;
            int objectSize = CalculateItemSize(buffer, length, ref position, ref depth);

            if (objectSize > 0 && !_allowMultipleContent && position < length)
            {
                throw new FormatException("Multiple root-level CBOR objects detected, but allowMultipleContent is false.");
            }

            return objectSize;
        }

        private int CalculateItemSize(byte[] buffer, int length, ref int position, ref int depth)
        {
            if (position >= length) return -1;

            depth++;
            if (depth > _maxDepth)
            {
                throw new FormatException($"Maximum nesting depth of {_maxDepth} exceeded.");
            }

            int startPosition = position;
            byte initialByte = buffer[position];

            if (initialByte == BREAK_STOP_CODE)
            {
                throw new FormatException("Unexpected CBOR break (0xFF) outside indefinite-length context.");
            }

            position++;

            int majorType = (initialByte >> 5) & MAJOR_TYPE_MASK;
            int additionalInfo = initialByte & ADDITIONAL_INFO_MASK;

            long argument = ReadArgument(buffer, length, ref position, additionalInfo);

            if (argument == INSUFFICIENT_DATA)
            {
                return -1;
            }

            if (argument == INDEFINITE_LENGTH && !_allowIndefiniteLength)
            {
                throw new FormatException("Indefinite-length items are not allowed.");
            }

            int result = majorType switch
            {
                0 or 1 => position - startPosition,
                2 or 3 => CalculateStringSize(buffer, length, ref position, argument, startPosition, ref depth),
                4 => CalculateArraySize(buffer, length, ref position, argument, startPosition, ref depth),
                5 => CalculateMapSize(buffer, length, ref position, argument, startPosition, ref depth),
                6 => CalculateTaggedSize(buffer, length, ref position, startPosition, ref depth),
                7 => CalculateSimpleOrFloatSize(buffer, length, ref position, additionalInfo, startPosition),
                _ => throw new FormatException($"Unknown CBOR major type: {majorType}"),
            };

            depth--;
            return result;
        }

        private static long ReadArgument(byte[] buffer, int length, ref int position, int additionalInfo)
        {
            if (additionalInfo < 24)
            {
                return additionalInfo;
            }

            else if (additionalInfo == 24)
            {
                if (position >= length) return INSUFFICIENT_DATA;
                return buffer[position++];
            }

            else if (additionalInfo == 25)
            {
                if (position + 2 > length) return INSUFFICIENT_DATA;
                ulong value = ReadUInt16BigEndian(buffer, ref position);
                return (long)value;
            }

            else if (additionalInfo == 26)
            {
                if (position + 4 > length) return INSUFFICIENT_DATA;
                ulong value = ReadUInt32BigEndian(buffer, ref position);
                return (long)value;
            }

            else if (additionalInfo == 27)
            {
                if (position + 8 > length) return INSUFFICIENT_DATA;
                ulong value = ReadUInt64BigEndian(buffer, ref position);

                if (value > long.MaxValue)
                {
                    throw new FormatException($"CBOR argument value {value} exceeds long.MaxValue");
                }

                return (long)value;
            }

            else if (additionalInfo >= 28 && additionalInfo <= 30)
            {
                throw new FormatException($"Invalid CBOR additional info: {additionalInfo}");
            }

            return INDEFINITE_LENGTH;
        }

        private static ulong ReadUInt16BigEndian(byte[] buffer, ref int position)
        {
            ulong value = ((ulong)buffer[position] << 8) | buffer[position + 1];
            position += 2;
            return value;
        }

        private static ulong ReadUInt32BigEndian(byte[] buffer, ref int position)
        {
            ulong value = ((ulong)buffer[position] << 24) |
                         ((ulong)buffer[position + 1] << 16) |
                         ((ulong)buffer[position + 2] << 8) |
                         buffer[position + 3];
            position += 4;
            return value;
        }

        private static ulong ReadUInt64BigEndian(byte[] buffer, ref int position)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | buffer[position++];
            }
            return value;
        }

        private int CalculateStringSize(byte[] buffer, int length, ref int position,
            long argument, int startPosition, ref int depth)
        {
            if (argument == INDEFINITE_LENGTH)
            {
                return CalculateIndefiniteLengthStringSize(buffer, length, ref position, startPosition, ref depth);
            }
            else
            {
                if (position + argument > length) return -1;
                position += (int)argument;
                return position - startPosition;
            }
        }

        private int CalculateArraySize(byte[] buffer, int length, ref int position,
            long argument, int startPosition, ref int depth)
        {
            if (argument == INDEFINITE_LENGTH)
            {
                while (position < length)
                {
                    if (buffer[position] == BREAK_STOP_CODE)
                    {
                        position++;
                        return position - startPosition;
                    }

                    int itemSize = CalculateItemSize(buffer, length, ref position, ref depth);
                    if (itemSize < 0) return -1;
                }

                return -1;
            }
            else
            {
                for (long i = 0; i < argument; i++)
                {
                    int itemSize = CalculateItemSize(buffer, length, ref position, ref depth);
                    if (itemSize < 0) return -1;
                }
                return position - startPosition;
            }
        }

        private int CalculateMapSize(byte[] buffer, int length, ref int position,
            long argument, int startPosition, ref int depth)
        {
            if (argument == INDEFINITE_LENGTH)
            {
                while (position < length)
                {
                    if (buffer[position] == BREAK_STOP_CODE)
                    {
                        position++;
                        return position - startPosition;
                    }

                    int keySize = CalculateItemSize(buffer, length, ref position, ref depth);
                    if (keySize < 0) return -1;

                    int valueSize = CalculateItemSize(buffer, length, ref position, ref depth);
                    if (valueSize < 0) return -1;
                }

                return -1;
            }
            else
            {
                for (long i = 0; i < argument; i++)
                {
                    int keySize = CalculateItemSize(buffer, length, ref position, ref depth);
                    if (keySize < 0) return -1;

                    int valueSize = CalculateItemSize(buffer, length, ref position, ref depth);
                    if (valueSize < 0) return -1;
                }
                return position - startPosition;
            }
        }

        private int CalculateTaggedSize(byte[] buffer, int length, ref int position, int startPosition, ref int depth)
        {
            int contentSize = CalculateItemSize(buffer, length, ref position, ref depth);
            if (contentSize < 0) return -1;
            return position - startPosition;
        }

        private static int CalculateSimpleOrFloatSize(byte[] buffer, int length, ref int position,
            int additionalInfo, int startPosition)
        {
            if (additionalInfo < 24)
            {
                return position - startPosition;
            }
            else if (additionalInfo == 24)
            {
                if (position >= length) return -1;
                position++;
                return position - startPosition;
            }
            else if (additionalInfo == 25)
            {
                if (position + 2 > length) return -1;
                position += 2;
                return position - startPosition;
            }
            else if (additionalInfo == 26)
            {
                if (position + 4 > length) return -1;
                position += 4;
                return position - startPosition;
            }
            else if (additionalInfo == 27)
            {
                if (position + 8 > length) return -1;
                position += 8;
                return position - startPosition;
            }
            else if (additionalInfo == 31)
            {
                throw new FormatException("Unexpected break stop code in major type 7");
            }

            return position - startPosition;
        }

        private int CalculateIndefiniteLengthStringSize(byte[] buffer, int length,
            ref int position, int startPosition, ref int depth)
        {
            while (position < length)
            {
                if (buffer[position] == BREAK_STOP_CODE)
                {
                    position++;
                    return position - startPosition;
                }

                int chunkSize = CalculateItemSize(buffer, length, ref position, ref depth);
                if (chunkSize < 0) return -1;
            }

            return -1;
        }
    }
}