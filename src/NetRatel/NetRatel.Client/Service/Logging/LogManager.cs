using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Logging;

public static class LogManager
{
    private static readonly object Sync = new();
    private static string _logFolderPath = Path.Combine(AppContext.BaseDirectory, "logs");
    private static string _logFilePath = Path.Combine(_logFolderPath, $"netratel-client-{DateTime.UtcNow:yyyyMMdd}.log");

    public static string LogFolderPath => _logFolderPath;
    public static string LogFilePath => _logFilePath;

    public static void Initialize(string appBaseDir, string? logDirectory = null, string? logFilePrefix = null)
    {
        if (string.IsNullOrWhiteSpace(appBaseDir) && string.IsNullOrWhiteSpace(logDirectory))
        {
            return;
        }

        lock (Sync)
        {
            var requestedFolderPath = string.IsNullOrWhiteSpace(logDirectory)
                ? Path.Combine(appBaseDir, "logs")
                : logDirectory;
            var initialized = TrySetLogFolder(requestedFolderPath, logFilePrefix, out var error);
            if (!initialized)
            {
                initialized = TrySetLogFolder(GetFallbackLogFolder(), logFilePrefix, out error);
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - [LoggingError] {error}");
            }
        }
    }

    private static bool TrySetLogFolder(string folderPath, string? logFilePrefix, out string? error)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            var prefix = SanitizeLogFilePrefix(logFilePrefix);
            var filePath = Path.Combine(folderPath, $"{prefix}-{DateTime.UtcNow:yyyyMMdd}.log");
            File.AppendAllText(filePath, string.Empty);
            _logFolderPath = folderPath;
            _logFilePath = filePath;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Unable to initialize log folder '{folderPath}': {ex.Message}";
            return false;
        }
    }

    private static string GetFallbackLogFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "NetRatel", "Client", "logs", "fallback");
        }

        return Path.Combine(Path.GetTempPath(), "NetRatel", "Client", "logs", "fallback");
    }

    private static string SanitizeLogFilePrefix(string? logFilePrefix)
    {
        var prefix = string.IsNullOrWhiteSpace(logFilePrefix) ? "netratel-client" : logFilePrefix.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            prefix = prefix.Replace(invalid, '-');
        }

        return string.IsNullOrWhiteSpace(prefix) ? "netratel-client" : prefix;
    }

    public static void WriteLog(string message)
    {
        var timestampUtc = DateTimeOffset.UtcNow;
        var entry = $"{timestampUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} - {message}";
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(_logFolderPath);
                File.AppendAllText(_logFilePath, entry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - [LoggingError] {ex.Message}");
            }
        }

        Console.WriteLine(entry);
        ClientRuntimeLogBuffer.Capture(message, timestampUtc);
    }

    public static async Task<string> ReadEntireLogAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_logFilePath))
        {
            return string.Empty;
        }

        using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return await sr.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<string>> ReadTailLinesAsync(int maxLines, CancellationToken cancellationToken = default)
    {
        if (maxLines <= 0 || !File.Exists(_logFilePath))
        {
            return Array.Empty<string>();
        }

        var lines = new Queue<string>(maxLines);
        using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await sr.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (lines.Count == maxLines)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }

        return lines.ToList();
    }

    public static async IAsyncEnumerable<string> StreamLogTailAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lastPosition = 0L;
        if (File.Exists(_logFilePath))
        {
            lastPosition = new FileInfo(_logFilePath).Length;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!File.Exists(_logFilePath))
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < lastPosition)
            {
                lastPosition = 0;
            }

            fs.Position = lastPosition;
            using var sr = new StreamReader(fs);
            string? line;
            var yielded = false;
            while ((line = await sr.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                yielded = true;
                yield return line;
            }

            lastPosition = fs.Position;
            if (!yielded)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
