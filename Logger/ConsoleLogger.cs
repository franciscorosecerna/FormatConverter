namespace FormatConverter.Logger
{
    /// <summary>
    /// Console implementation of ILogger with verbosity support
    /// </summary>
    public class ConsoleLogger : ILogger
    {
        private readonly Lock _lockObject = new();

        private static readonly Dictionary<VerbosityLevel, LogLevelConfig> LogConfigs = new()
        {
            [VerbosityLevel.Error] = new(ConsoleColor.Red, "ERROR", true),
            [VerbosityLevel.Warning] = new(ConsoleColor.Yellow, "WARNING", false),
            [VerbosityLevel.Info] = new(ConsoleColor.Cyan, "INFO", false),
            [VerbosityLevel.Debug] = new(ConsoleColor.Gray, "DEBUG", false),
            [VerbosityLevel.Trace] = new(ConsoleColor.DarkGray, "TRACE", false)
        };

        public VerbosityLevel Verbosity { get; set; } = VerbosityLevel.Info;

        public void Write(VerbosityLevel level, string message)
        {
            if (!ShouldLog(level))
                return;

            if (LogConfigs.TryGetValue(level, out var config))
            {
                WriteColored(config.Color, $"{config.Prefix}: ", message, config.UseStdErr);
            }
        }

        public void WriteError(string message) => Write(VerbosityLevel.Error, message);
        public void WriteWarning(string message) => Write(VerbosityLevel.Warning, message);
        public void WriteInfo(string message) => Write(VerbosityLevel.Info, message);
        public void WriteDebug(string message) => Write(VerbosityLevel.Debug, message);
        public void WriteTrace(string message) => Write(VerbosityLevel.Trace, message);

        public void WriteSuccess(string message) => WriteColored(ConsoleColor.Green, "SUCCESS: ", message);

        public void WriteColored(ConsoleColor color, string prefix, string message, bool stderr = false)
        {
            lock (_lockObject)
            {
                var original = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = color;
                    var output = $"{prefix}{message}";

                    if (stderr)
                        Console.Error.WriteLine(output);
                    else
                        Console.WriteLine(output);
                }
                finally
                {
                    Console.ForegroundColor = original;
                }
            }
        }

        private bool ShouldLog(VerbosityLevel level)
            => Verbosity != VerbosityLevel.None && level <= Verbosity;

        private record LogLevelConfig(ConsoleColor Color, string Prefix, bool UseStdErr);
    }
}