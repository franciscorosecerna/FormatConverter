using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public interface IInputFormatStrategy
    {
        JToken Parse(string input);
        void Configure(FormatConfig config);
    }
}
