using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public interface IOutputFormatStrategy
    {
        string Serialize(JToken data);
        IEnumerable<string> SerializeStream(IEnumerable<JToken> data);
        void Configure(FormatConfig config);
    }
}
