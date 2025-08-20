using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public interface IInputFormatStrategy
    {
        JObject Parse(string input);
    }
}
