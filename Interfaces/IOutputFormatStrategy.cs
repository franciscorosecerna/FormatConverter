using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public interface IOutputFormatStrategy
    {
        string Serialize(JToken data);
        void SerializeStream(IEnumerable<JToken> data, Stream output, CancellationToken cancellationToken = default);
        void Configure(FormatConfig config);
    }
}
