using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetRatel.Shared.Service.Logging
{
    public static class LogManager
    {
        private static readonly object Sync = new();
        private static string LogFolderPath = Path.Combine(AppContext.BaseDirectory, "logs");
        private static string LogFilePath = Path.Combine(LogFolderPath, "client.log");

        // Set maximum allowed log file size to 1 MB.
        private const long MaxLogFileSize = 1 * 1024 * 1024; // 1 MB in bytes

        // Static constructor ensures the log folder exists.
        static LogManager()
        {
            TryEnsureLogFolder(LogFolderPath);
        }

        public static void Initialize(string? logFilePath)
        {
            if (string.IsNullOrWhiteSpace(logFilePath))
            {
                return;
            }

            lock (Sync)
            {
                var folder = Path.GetDirectoryName(logFilePath);
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return;
                }

                if (!TryEnsureLogFolder(folder))
                {
                    return;
                }

                LogFolderPath = folder;
                LogFilePath = logFilePath;
            }
        }

        /// <summary>
        /// Writes a log message to the client.log file and outputs it to the console.
        /// If the log file exceeds 1MB, it is reset before writing.
        /// </summary>
        /// <param name="message">The message to log.</param>
        public static void WriteLog(string message)
        {
            lock (Sync)
            {
                try
                {
                    TryEnsureLogFolder(LogFolderPath);
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

        private static bool TryEnsureLogFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Logging error: " + ex.Message);
                return false;
            }
        }
    }
}
