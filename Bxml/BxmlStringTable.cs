using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FormatConverter.Bxml
{
    public class BxmlStringTable
    {
        private readonly Dictionary<string, int> _indexByString = new(StringComparer.Ordinal);
        private readonly List<string> _strings = [];

        public int GetOrAdd(string value)
        {
            if (value == null)
                value = string.Empty;

            if (_indexByString.TryGetValue(value, out int index))
                return index;

            index = _strings.Count;
            _strings.Add(value);
            _indexByString[value] = index;
            return index;
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write((ushort)_strings.Count);
            foreach (var str in _strings)
            {
                var bytes = Encoding.UTF8.GetBytes(str);
                writer.Write((ushort)bytes.Length);
                writer.Write(bytes);
            }
        }

        public IReadOnlyList<string> Strings => _strings;
    }
}
