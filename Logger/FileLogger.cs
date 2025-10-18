using System;
using System.Collections.Generic;
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

        public FileLogger(string logFilePath, Encoding encoding,bool includeTimestamps = true)
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _encoding = encoding;
            _writer = new StreamWriter(logFilePath, append: true, encoding: _encoding)
            {
                AutoFlush = true
            };
            _includeTimestamps = includeTimestamps;
        }

        public void WriteError(string message)
        {
            WriteLine("ERROR", message);
        }

        public void WriteWarning(string message)
        {
            WriteLine("WARNING", message);
        }

        public void WriteInfo(string message)
        {
            WriteLine("INFO", message);
        }

        public void WriteSuccess(string message)
        {
            lock (_lockObject)
            {
                if (_includeTimestamps)
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
