using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FormatConverter.Logger
{
    /// <summary>
    /// Interface for logging operations
    /// </summary>
    public interface ILogger
    {
        void WriteError(string message);
        void WriteWarning(string message);
        void WriteInfo(string message);
        void WriteSuccess(string message);
        void WriteColored(ConsoleColor color, string prefix, string message, bool stderr = false);
    }
}
