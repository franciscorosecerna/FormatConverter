namespace FormatConverter.Logger
{
    /// <summary>
    /// Interface for logging operations
    /// </summary>
    public interface ILogger
    {
        VerbosityLevel Verbosity { get; set; }

        void Write(VerbosityLevel level, Func<string> message);

        void WriteError(Func<string> message);
        void WriteWarning(Func<string> message);
        void WriteInfo(Func<string> message);
        void WriteSuccess(string message);
        void WriteDebug(Func<string> message);
        void WriteTrace(Func<string> message);
        void WriteColored(ConsoleColor color, string prefix, string message, bool stderr = false);
    }

    public enum VerbosityLevel
    {
        None = 0,
        Error = 1,
        Warning = 2,
        Info = 3,
        Debug = 4,
        Trace = 5
    }
}
