using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public interface IOutputFormatStrategy
    {
        string Serialize(JObject data);
    }
}
