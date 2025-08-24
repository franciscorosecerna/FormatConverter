using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public interface IOutputFormatStrategy
    {
        string Serialize(JToken data);
        void Configure(FormatConfig config);
    }
}
