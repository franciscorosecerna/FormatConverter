namespace FormatConverter.Logger
{
    /// <summary>
    /// Interface for logging operations
    /// </summary>
    public interface ILogger
    {
        VerbosityLevel Verbosity { get; set; }

        void Write(VerbosityLevel level, string message);

        void WriteError(string message);
        void WriteWarning(string message);
        void WriteInfo(string message);
        void WriteSuccess(string message);
        void WriteDebug(string message);
        void WriteTrace(string message);
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
