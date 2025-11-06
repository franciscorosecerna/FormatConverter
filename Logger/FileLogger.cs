using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FormatConverter.Logger
{
    /// <summary>
    /// File logger implementation
    /// </summary>
    public class FileLogger : ILogger, IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Lock _lockObject = new();
        private readonly bool _includeTimestamps;
        private readonly Encoding _encoding;

        public VerbosityLevel Verbosity { get; set; } = VerbosityLevel.None;

        public FileLogger(string logFilePath, Encoding encoding, bool includeTimestamps = true)
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _encoding = encoding;
            _writer = new StreamWriter(logFilePath, append: false, encoding: _encoding)
            {
                AutoFlush = true
            };
            _includeTimestamps = includeTimestamps;
        }

        public void Write(VerbosityLevel level, Func<string> message)
        {
            if (level > Verbosity)
                return;

            string levelName = level switch
            {
                VerbosityLevel.Error => "ERROR",
                VerbosityLevel.Warning => "WARNING",
                VerbosityLevel.Info => "INFO",
                VerbosityLevel.Debug => "DEBUG",
                VerbosityLevel.Trace => "TRACE",
                _ => "UNKNOWN"
            };

            WriteLine(levelName, message());
        }

        public void WriteError(Func<string> message) => Write(VerbosityLevel.Error, message);
        public void WriteWarning(Func<string> message) => Write(VerbosityLevel.Warning, message);
        public void WriteInfo(Func<string> message) => Write(VerbosityLevel.Info, message);
        public void WriteDebug(Func<string> message) => Write(VerbosityLevel.Debug, message);
        public void WriteTrace(Func<string> message) => Write(VerbosityLevel.Trace, message);

        public void WriteSuccess(string message)
        {
            lock (_lockObject)
            {
                if (_includeTimestamps)
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    _writer.WriteLine($"[{timestamp}] {message}");
                }
                else
                {
                    _writer.WriteLine(message);
                }
            }
        }

        public void WriteColored(ConsoleColor color, string prefix, string message, bool stderr = false)
        {
            WriteLine(prefix.TrimEnd(':', ' '), message);
        }

        private void WriteLine(string level, string message)
        {
            lock (_lockObject)
            {
                if (_includeTimestamps)
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    _writer.WriteLine($"[{timestamp}] [{level}] {message}");
                }
                else
                {
                    _writer.WriteLine($"[{level}] {message}");
                }
            }
        }

        public void Dispose()
        {
            _writer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}