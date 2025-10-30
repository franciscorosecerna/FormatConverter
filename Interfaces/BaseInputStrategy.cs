using FormatConverter.Logger;
using Newtonsoft.Json.Linq;

namespace FormatConverter.Interfaces
{
    public abstract class BaseInputStrategy : IInputFormatStrategy
    {
        protected FormatConfig Config { get; private set; } = new FormatConfig();
        protected ILogger Logger { get; private set; } = new ConsoleLogger();

        /// <summary>
        /// Configures the strategy with the specified parsing configuration
        /// </summary>
        /// <param name="config">The configuration to apply for parsing operations</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null</exception>
        public virtual void Configure(FormatConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Parses the input string into a JSON token
        /// </summary>
        /// <param name="input">The input string to parse</param>
        /// <returns>A JToken representing the parsed data structure</returns>
        public abstract JToken Parse(string input);

        /// <summary>
        /// Parses data from a stream source, returning an enumerable sequence of JSON tokens
        /// </summary>
        /// <param name="path">The file path or resource location to read from</param>
        /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
        /// <returns>An enumerable sequence of JToken objects representing the parsed data</returns>
        public abstract IEnumerable<JToken> ParseStream(string path, CancellationToken cancellationToken);
    }
}