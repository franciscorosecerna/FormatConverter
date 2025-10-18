namespace FormatConverter.Bxml
{
    public class BxmlElement
    {
        public uint NameIndex { get; set; }
        public Dictionary<uint, uint> Attributes { get; set; } = [];

        /// <summary>
        /// The value of this element. Can be:
        /// - long (integers of all sizes)
        /// - double (floats of all sizes)
        /// - bool
        /// - DateTime
        /// - byte[]
        /// - null (for objects, arrays, or null values)
        /// </summary>
        public object? Value { get; set; }

        public List<BxmlElement> Children { get; set; } = [];

        /// <summary>
        /// Index in the string table if this element's value is a string.
        /// Mutually exclusive with Value.
        /// </summary>
        public uint? TextIndex { get; set; }
    }
}