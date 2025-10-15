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

        /// <summary>
        /// Calculates the size in bytes of a complete CBOR object in the buffer.
        /// </summary>
        /// <returns>
        /// Number of bytes the object occupies, or -1 if there is not enough data.
        /// </returns>
        public static int CalculateObjectSize(byte[] buffer, int length)
        {
            if (length == 0) return -1;

            int position = 0;
            return CalculateItemSize(buffer, length, ref position);
        }

        private static int CalculateItemSize(byte[] buffer, int length, ref int position)
        {
            if (position >= length) return -1;

            int startPosition = position;
            byte initialByte = buffer[position];

            if (initialByte == 0xFF)
            {
                throw new FormatException("Unexpected CBOR break (0xFF) outside indefinite-length context.");
            }

            position++;

            int majorType = (initialByte >> 5) & 0x07;
            int additionalInfo = initialByte & 0x1F;

            long argument = ReadArgument(buffer, length, ref position, additionalInfo);

            if (argument == INSUFFICIENT_DATA)
            {
                return -1;
            }

            return majorType switch
            {
                0 or 1 => position - startPosition,
                2 or 3 => CalculateStringSize(buffer, length, ref position, argument, startPosition),
                4 => CalculateArraySize(buffer, length, ref position, argument, startPosition),
                5 => CalculateMapSize(buffer, length, ref position, argument, startPosition),
                6 => CalculateTaggedSize(buffer, length, ref position, startPosition),
                7 => CalculateSimpleOrFloatSize(buffer, length, ref position, additionalInfo, startPosition),
                _ => throw new FormatException($"Unknown CBOR major type: {majorType}"),
            };
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

        private static int CalculateStringSize(byte[] buffer, int length, ref int position,
            long argument, int startPosition)
        {
            if (argument == INDEFINITE_LENGTH)
            {
                return CalculateIndefiniteLengthStringSize(buffer, length, ref position, startPosition);
            }
            else
            {
                if (position + argument > length) return -1;
                position += (int)argument;
                return position - startPosition;
            }
        }

        private static int CalculateArraySize(byte[] buffer, int length, ref int position,
            long argument, int startPosition)
        {
            if (argument == INDEFINITE_LENGTH)
            {
                while (position < length)
                {
                    if (buffer[position] == 0xFF)
                    {
                        position++;
                        return position - startPosition;
                    }

                    int itemSize = CalculateItemSize(buffer, length, ref position);
                    if (itemSize < 0) return -1;
                }

                return -1;
            }
            else
            {
                for (long i = 0; i < argument; i++)
                {
                    int itemSize = CalculateItemSize(buffer, length, ref position);
                    if (itemSize < 0) return -1;
                }
                return position - startPosition;
            }
        }

        private static int CalculateMapSize(byte[] buffer, int length, ref int position,
            long argument, int startPosition)
        {
            if (argument == INDEFINITE_LENGTH)
            {
                while (position < length)
                {
                    if (buffer[position] == 0xFF)
                    {
                        position++;
                        return position - startPosition;
                    }

                    int keySize = CalculateItemSize(buffer, length, ref position);
                    if (keySize < 0) return -1;

                    int valueSize = CalculateItemSize(buffer, length, ref position);
                    if (valueSize < 0) return -1;
                }

                return -1;
            }
            else
            {
                for (long i = 0; i < argument; i++)
                {
                    int keySize = CalculateItemSize(buffer, length, ref position);
                    if (keySize < 0) return -1;

                    int valueSize = CalculateItemSize(buffer, length, ref position);
                    if (valueSize < 0) return -1;
                }
                return position - startPosition;
            }
        }

        private static int CalculateTaggedSize(byte[] buffer, int length, ref int position, int startPosition)
        {
            int contentSize = CalculateItemSize(buffer, length, ref position);
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

        private static int CalculateIndefiniteLengthStringSize(byte[] buffer, int length,
            ref int position, int startPosition)
        {
            while (position < length)
            {
                if (buffer[position] == 0xFF)
                {
                    position++;
                    return position - startPosition;
                }

                int chunkSize = CalculateItemSize(buffer, length, ref position);
                if (chunkSize < 0) return -1;
            }

            return -1;
        }
    }
}