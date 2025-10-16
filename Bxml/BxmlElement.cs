namespace FormatConverter.Bxml
{
    public class BxmlElement
    {
        public uint NameIndex { get; set; }
        public Dictionary<uint, uint> Attributes { get; set; } = [];

        /// <summary>
        /// The value of this element. Can be:
        /// - string (from string table)
        /// - long (integers of all sizes)
        /// - double (floats of all sizes)
        /// - bool
        /// - null (for objects, arrays, or null values)
        /// </summary>
        public object? Value { get; set; }

        public List<BxmlElement> Children { get; set; } = [];

        /// <summary>
        /// Helper property for backward compatibility.
        /// Returns the string table index if Value is a string reference.
        /// </summary>
        [Obsolete("Use Value property instead")]
        public uint? TextIndex
        {
            get => Value is string ? null : null; // Legacy support
            set { } // Ignore sets for compatibility
        }
    }
}