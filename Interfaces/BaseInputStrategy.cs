using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public abstract class BaseInputStrategy : IInputFormatStrategy
    {
        protected FormatConfig Config { get; private set; } = new FormatConfig();

        public virtual void Configure(FormatConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public abstract JToken Parse(string input);

        public abstract IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken);
    }
}
