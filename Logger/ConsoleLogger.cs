using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FormatConverter.Logger
{
    /// <summary>
    /// Console implementation of ILogger
    /// </summary>
    public class ConsoleLogger : ILogger
    {
        private readonly Lock _lockObject = new();

        public void WriteError(string message)
        {
            WriteColored(ConsoleColor.Red, "ERROR: ", message, stderr: true);
        }

        public void WriteWarning(string message)
        {
            WriteColored(ConsoleColor.Yellow, "WARNING: ", message);
        }

        public void WriteInfo(string message)
        {
            WriteColored(ConsoleColor.Cyan, "INFO: ", message);
        }

        public void WriteSuccess(string message)
        {
            WriteColored(ConsoleColor.Green, "SUCCESS: ", message);
        }

        public void WriteColored(ConsoleColor color, string prefix, string message, bool stderr = false)
        {
            lock (_lockObject)
            {
                var original = Console.ForegroundColor;
                Console.ForegroundColor = color;

                if (stderr)
                    Console.Error.WriteLine($"{prefix}{message}");
                else
                    Console.WriteLine($"{prefix}{message}");

                Console.ForegroundColor = original;
            }
        }
    }
}
