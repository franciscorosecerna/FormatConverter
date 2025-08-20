namespace FormatConverter.Bxml
{
    public class BxmlElement
    {
        public uint NameIndex { get; set; }
        public Dictionary<uint, uint> Attributes { get; set; } = [];
        public uint? TextIndex { get; set; }
        public List<BxmlElement> Children { get; set; } = [];
    }
}
