using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FormatConverter.Bxml.BxmlWriter
{
    public enum Endianness
    {
        LittleEndian,
        BigEndian
    }

    public class BxmlWriteOptions
    {
        public static BxmlWriteOptions Default => new();

        public Encoding Encoding { get; set; } = Encoding.UTF8;
        public Endianness Endianness { get; set; } = Endianness.LittleEndian;
        public int MaxDepth { get; set; } = 1000;
        public bool LeaveOpen { get; set; }
        public bool CompressArrays { get; set; } = true;
    }
}
