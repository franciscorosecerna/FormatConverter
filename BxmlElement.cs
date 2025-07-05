using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace FormatConverter
{
    public class BxmlElement
    {
        public uint NameIndex { get; set; }
        public Dictionary<uint, uint> Attributes { get; set; } = [];
        public uint? TextIndex { get; set; }
        public List<BxmlElement> Children { get; set; } = [];
    }
}
