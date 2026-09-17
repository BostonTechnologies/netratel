using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetRatel.Web.Services.Logging
{
    public static class LogManager
    {
        // Define the logging folder and file paths.
        private static readonly string LogFolderPath = Path.Combine(AppContext.BaseDirectory, "logs");
        private static readonly string LogFilePath = Path.Combine(LogFolderPath, "client.log");

        // Set maximum allowed log file size to 1 MB.
        private const long MaxLogFileSize = 1 * 1024 * 1024; // 1 MB in bytes

        // Static constructor ensures the log folder exists.
        static LogManager()
        {
            if (!Directory.Exists(LogFolderPath))
            {
                Directory.CreateDirectory(LogFolderPath);
            }
        }

        /// <summary>
        /// Writes a log message to the client.log file and outputs it to the console.
        /// If the log file exceeds 1MB, it is reset before writing.
        /// </summary>
        /// <param name="message">The message to log.</param>
        public static void WriteLog(string message)
        {
            try
            {
                // Create the log file if it doesn't exist.
                if (!File.Exists(LogFilePath))
                {
                    using (File.Create(LogFilePath)) { }
                }
                else
                {
                    // If the file exists, check its size.
                    var fileInfo = new FileInfo(LogFilePath);
                    if (fileInfo.Length > MaxLogFileSize)
                    {
                        // Reset the log file by truncating its content.
                        File.WriteAllText(LogFilePath, string.Empty);
                    }
                }

                // Format the log message with a timestamp.
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}{Environment.NewLine}";

                // Append log entry to file.
                File.AppendAllText(LogFilePath, logEntry);

                // Also output the log entry to the console.
                Console.WriteLine(logEntry.TrimEnd());
            }
            catch (Exception ex)
            {
                // Optionally, handle or report errors related to logging.
                Console.WriteLine("Logging error: " + ex.Message);
            }
        }
    }
}
