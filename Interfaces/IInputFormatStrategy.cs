using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public interface IInputFormatStrategy
    {
        JToken Parse(string input);
        IEnumerable<JToken> ParseStream(string input);
        void Configure(FormatConfig config);
    }
}
