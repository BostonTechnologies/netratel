using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.Logging;

namespace NetRatel.Client.Service.Logging.Linux;

/// <summary>
/// Agent-owned Linux log providers. Source identifiers are discovered here and
/// map only to fixed journal predicates or a fixed file allowlist; callers can
/// never supply a path, shell fragment, or journalctl argument string.
/// </summary>
internal interface ILinuxLogCommandRunner
{
    Task<LinuxCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    IAsyncEnumerable<string> FollowAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed record LinuxCommandResult(int ExitCode, IReadOnlyList<string> StandardOutput, string StandardError);

internal sealed class LinuxLogSourceAdapter(ILinuxLogCommandRunner? commandRunner = null) : IClientLogSourceAdapter
{
    private const int MaximumPageSize = 100;
    private const int MaximumLineBytes = 8 * 1024;
    private const int MaximumServiceSources = 48;
    private static readonly IReadOnlyDictionary<string, string> FileSources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["linux-file-syslog"] = "/var/log/syslog",
        ["linux-file-messages"] = "/var/log/messages",
        ["linux-file-authentication"] = "/var/log/auth.log",
        ["linux-file-secure"] = "/var/log/secure",
        ["linux-file-kernel"] = "/var/log/kern.log"
    };
    private static readonly string[] FilterCapabilities = ["severity", "prefix", "category", "text", "time"];
    private readonly ILinuxLogCommandRunner _commands = commandRunner ?? new ProcessLinuxLogCommandRunner();
    private readonly Dictionary<string, LinuxSource> _sources = new(StringComparer.Ordinal);
    private readonly object _sourceGate = new();

    public async Task<IReadOnlyList<ClientLogSourceDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return [];

        var discovered = new List<LinuxSource>();
        var journal = await _commands.RunAsync(
            "journalctl",
            ["--no-pager", "--quiet", "--output=json", "--lines=1"],
            cancellationToken).ConfigureAwait(false);
        if (journal.ExitCode == 0)
        {
            discovered.Add(Journal("linux-journal-system", "System Journal", []));
            discovered.Add(Journal("linux-journal-kernel", "Kernel", ["--dmesg"]));
            discovered.Add(Journal("linux-journal-authentication", "Authentication", ["SYSLOG_FACILITY=4", "+", "SYSLOG_FACILITY=10"]));

            var units = await DiscoverServiceUnitsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var unit in units)
            {
                var sourceId = UnitSourceId(unit);
                var name = unit.StartsWith("netratel", StringComparison.OrdinalIgnoreCase)
                    ? "NetRatel Client Service"
                    : $"Service: {unit}";
                discovered.Add(Journal(sourceId, name, [$"--unit={unit}"]));
            }
        }

        foreach (var (sourceId, path) in FileSources)
        {
            if (CanReadFile(path)) discovered.Add(File(sourceId, Path.GetFileName(path)));
        }

        lock (_sourceGate)
        {
            _sources.Clear();
            foreach (var source in discovered) _sources[source.Id] = source;
        }

        return discovered.Select(source => source.Descriptor).ToArray();
    }

    public async Task<ClientLogPage> ReadHistoryAsync(ClientLogQuery query, CancellationToken cancellationToken)
    {
        var source = GetSource(query.SourceId);
        if (source is null) return new([], null, null, false, ErrorCode: "source_unavailable");
        var pageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        return source.Kind switch
        {
            LinuxSourceKind.Journal => await ReadJournalHistoryAsync(source, query, pageSize, cancellationToken).ConfigureAwait(false),
            LinuxSourceKind.File => await ReadFileHistoryAsync(source, query, pageSize, cancellationToken).ConfigureAwait(false),
            _ => new([], null, null, false, ErrorCode: "source_unavailable")
        };
    }

    public async IAsyncEnumerable<ClientLogFollowResult> FollowAsync(ClientLogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var source = GetSource(query.SourceId);
        if (source is null) yield break;

        if (source.Kind == LinuxSourceKind.Journal)
        {
            var arguments = JournalArguments(source, query, 0, follow: true);
            await foreach (var line in _commands.FollowAsync("journalctl", arguments, cancellationToken).ConfigureAwait(false))
            {
                if (TryParseJournalRecord(line, source.Id, out var record) && Matches(record, query)) yield return new(record);
            }
            yield break;
        }

        if (source.Path is null) yield break;
        await foreach (var record in FollowFileAsync(source.Id, source.Path, query, cancellationToken).ConfigureAwait(false)) yield return new(record);
    }

    private async Task<IReadOnlyList<string>> DiscoverServiceUnitsAsync(CancellationToken cancellationToken)
    {
        var units = await _commands.RunAsync(
            "systemctl",
            ["list-units", "--type=service", "--state=running", "--no-legend", "--plain", "--no-pager"],
            cancellationToken).ConfigureAwait(false);
        if (units.ExitCode != 0) return [];

        return units.StandardOutput
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(IsSafeUnitName)
            .OrderByDescending(unit => unit!.StartsWith("netratel", StringComparison.OrdinalIgnoreCase))
            .ThenBy(unit => unit, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumServiceSources)
            .Cast<string>()
            .ToArray();
    }

    private async Task<ClientLogPage> ReadJournalHistoryAsync(LinuxSource source, ClientLogQuery query, int pageSize, CancellationToken cancellationToken)
    {
        var result = await _commands.RunAsync("journalctl", JournalArguments(source, query, pageSize + 1, follow: false), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return new([], null, null, false, ErrorCode: "journal_read_failed");

        var records = result.StandardOutput
            .Select(line => TryParseJournalRecord(line, source.Id, out var record) ? record : null)
            .Where(record => record is not null)
            .Cast<ClientRuntimeLogRecord>()
            .Where(record => Matches(record, query))
            .ToArray();
        var hasMore = records.Length > pageSize;
        var page = records.Take(pageSize).Reverse().ToArray();
        var previous = hasMore ? BeforeCursor(records[pageSize - 1].Cursor) : null;
        return new(page, null, previous, hasMore);
    }

    private async Task<ClientLogPage> ReadFileHistoryAsync(LinuxSource source, ClientLogQuery query, int pageSize, CancellationToken cancellationToken)
    {
        if (source.Path is null || !CanReadFile(source.Path)) return new([], null, null, false, ErrorCode: "file_unavailable");
        var lines = await TailLinesAsync(source.Path, pageSize + 1, ParseFileBeforeOffset(query.Cursor), cancellationToken).ConfigureAwait(false);
        var records = lines
            .Select(item => CreateFileRecord(source.Id, item.Offset, item.Text))
            .Where(record => Matches(record, query))
            .ToArray();
        var hasMore = records.Length > pageSize;
        var page = records.TakeLast(pageSize).ToArray();
        return new(page, null, hasMore ? BeforeCursor(page.FirstOrDefault()?.Cursor) : null, hasMore);
    }

    private async IAsyncEnumerable<ClientRuntimeLogRecord> FollowFileAsync(string sourceId, string path, ClientLogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long offset = new FileInfo(path).Length;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            if (!System.IO.File.Exists(path)) continue;
            var length = new FileInfo(path).Length;
            if (length < offset) offset = 0; // truncation or rotation
            if (length == offset) continue;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            while (true)
            {
                var lineOffset = stream.Position;
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                var record = CreateFileRecord(sourceId, lineOffset, line);
                if (Matches(record, query)) yield return record;
            }
            offset = stream.Position;
        }
    }

    private static async Task<IReadOnlyList<(long Offset, string Text)>> TailLinesAsync(string path, int maximumLines, long? endOffsetExclusive, CancellationToken cancellationToken)
    {
        var lines = new Queue<(long Offset, string Text)>(maximumLines);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = stream.Position;
            if (endOffsetExclusive is { } endOffset && offset >= endOffset) break;
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (Encoding.UTF8.GetByteCount(line) > MaximumLineBytes) line = Truncate(line);
            if (lines.Count == maximumLines) lines.Dequeue();
            lines.Enqueue((offset, line));
        }
        return lines.ToArray();
    }

    private static IReadOnlyList<string> JournalArguments(LinuxSource source, ClientLogQuery query, int lines, bool follow)
    {
        var arguments = new List<string> { "--no-pager", "--quiet", "--output=json" };
        arguments.AddRange(source.JournalPredicates);
        if (TryCursor(query.Cursor, out var before, out var cursor))
        {
            arguments.Add(before ? $"--cursor={cursor}" : $"--after-cursor={cursor}");
        }
        if (lines > 0) { arguments.Add("--reverse"); arguments.Add($"--lines={lines.ToString(CultureInfo.InvariantCulture)}"); }
        if (follow) arguments.Add("--follow");
        if (query.FromUtc is { } from) { arguments.Add("--since"); arguments.Add(from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)); }
        if (query.ToUtc is { } to) { arguments.Add("--until"); arguments.Add(to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)); }
        var priority = JournalPriority(query.Severities);
        if (priority is not null) arguments.Add($"--priority={priority}");
        return arguments;
    }

    private static bool TryParseJournalRecord(string line, string sourceId, out ClientRuntimeLogRecord record)
    {
        record = default!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var timestamp = ReadMicroseconds(root, "__REALTIME_TIMESTAMP") ?? DateTimeOffset.UtcNow;
            var cursor = String(root, "__CURSOR") ?? $"journal:{timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
            var message = Truncate(String(root, "MESSAGE") ?? string.Empty);
            var provider = String(root, "SYSLOG_IDENTIFIER") ?? String(root, "_SYSTEMD_UNIT") ?? "journal";
            var prefix = String(root, "_SYSTEMD_UNIT") ?? provider;
            record = new(cursor, ClientRuntimeLogBuffer.NextGatewaySequence(), timestamp, MapPriority(String(root, "PRIORITY")), sourceId, prefix, prefix, provider, message, message.EndsWith("… [truncated]", StringComparison.Ordinal));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static ClientRuntimeLogRecord CreateFileRecord(string sourceId, long offset, string line)
    {
        var value = Truncate(line);
        var (prefix, message) = SplitPrefix(value);
        return new($"file:{offset.ToString(CultureInfo.InvariantCulture)}", ClientRuntimeLogBuffer.NextGatewaySequence(), DateTimeOffset.UtcNow, InferSeverity(value), sourceId, prefix ?? "Other", prefix, "file", message, value.Length != line.Length);
    }

    private static bool Matches(ClientRuntimeLogRecord record, ClientLogQuery query) =>
        (query.FromUtc is null || record.TimestampUtc >= query.FromUtc) &&
        (query.ToUtc is null || record.TimestampUtc <= query.ToUtc) &&
        (query.Severities.Count == 0 || query.Severities.Contains(record.Severity, StringComparer.OrdinalIgnoreCase)) &&
        (query.Prefixes.Count == 0 || query.Prefixes.Contains(record.Prefix ?? record.Category, StringComparer.OrdinalIgnoreCase)) &&
        (query.Categories is null || query.Categories.Count == 0 || query.Categories.Contains(record.Category, StringComparer.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(query.Text) || record.Message.Contains(query.Text, StringComparison.OrdinalIgnoreCase));

    private static bool TryCursor(string? value, out bool before, out string cursor)
    {
        before = false;
        cursor = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return false;
        before = string.Equals(value[..separator], "before", StringComparison.Ordinal);
        if (!before && !string.Equals(value[..separator], "after", StringComparison.Ordinal)) return false;
        var candidate = value[(separator + 1)..];
        if (candidate.Any(char.IsControl)) return false;
        cursor = candidate;
        return true;
    }

    private static long? ParseFileBeforeOffset(string? cursor)
    {
        if (!TryCursor(cursor, out var before, out var value) || !before || !value.StartsWith("file:", StringComparison.Ordinal)) return null;
        return long.TryParse(value[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset > 0 ? offset : null;
    }

    private static string? BeforeCursor(string? cursor) => string.IsNullOrWhiteSpace(cursor) ? null : $"before:{cursor}";

    private LinuxSource? GetSource(string sourceId)
    {
        lock (_sourceGate) return _sources.TryGetValue(sourceId, out var source) ? source : null;
    }

    private static LinuxSource Journal(string id, string displayName, IReadOnlyList<string> predicates) =>
        new(id, LinuxSourceKind.Journal, null, predicates, new(id, "journal", displayName, "linux", true, null, true, true, true, FilterCapabilities));

    private static LinuxSource File(string id, string displayName) =>
        new(id, LinuxSourceKind.File, FileSources[id], [], new(id, "file", displayName, "linux", true, null, true, true, true, FilterCapabilities));

    private static bool CanReadFile(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string UnitSourceId(string unit) => $"linux-journal-unit-{Convert.ToHexString(Encoding.UTF8.GetBytes(unit)).ToLowerInvariant()}";
    private static bool IsSafeUnitName(string? value) => !string.IsNullOrWhiteSpace(value) && value!.Length <= 128 && value.EndsWith(".service", StringComparison.Ordinal) && value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '@');
    private static string? JournalPriority(IReadOnlyList<string> severities) => severities.Count == 1 ? severities[0].ToLowerInvariant() switch { "emergency" => "0", "alert" => "1", "critical" => "2", "error" => "3", "warning" => "4", "notice" => "5", "information" or "info" => "6", "debug" => "7", _ => null } : null;
    private static string MapPriority(string? priority) => priority switch { "0" => "Emergency", "1" => "Alert", "2" => "Critical", "3" => "Error", "4" => "Warning", "5" => "Notice", "6" => "Information", "7" => "Debug", _ => "Information" };
    private static DateTimeOffset? ReadMicroseconds(JsonElement root, string property) => long.TryParse(String(root, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000) : null;
    private static string? String(JsonElement root, string property) => root.TryGetProperty(property, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;
    private static string Truncate(string value) => Encoding.UTF8.GetByteCount(value) <= MaximumLineBytes ? value : string.Concat(value.AsSpan(0, Math.Min(value.Length, MaximumLineBytes - 32)), "… [truncated]");
    private static (string? Prefix, string Message) SplitPrefix(string line) { var closing = line.IndexOf(']'); return line.StartsWith("[", StringComparison.Ordinal) && closing is > 1 and <= 96 ? (line[1..closing], line[(closing + 1)..].TrimStart()) : (null, line); }
    private static string InferSeverity(string value) => value.Contains("error", StringComparison.OrdinalIgnoreCase) || value.Contains("fail", StringComparison.OrdinalIgnoreCase) ? "Error" : value.Contains("warn", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Information";

    private sealed record LinuxSource(string Id, LinuxSourceKind Kind, string? Path, IReadOnlyList<string> JournalPredicates, ClientLogSourceDescriptor Descriptor);
    private enum LinuxSourceKind { Journal, File }
}

internal sealed class ProcessLinuxLogCommandRunner : ILinuxLogCommandRunner
{
    private const int MaximumOutputLines = 4_096;

    public async Task<LinuxCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = Start(executable, arguments);
        var output = new List<string>();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        while (output.Count < MaximumOutputLines)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            output.Add(line);
        }
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new(process.ExitCode, output, await errorTask.ConfigureAwait(false));
    }

    public async IAsyncEnumerable<string> FollowAsync(string executable, IReadOnlyList<string> arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = Start(executable, arguments);
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                yield return line;
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static Process Start(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start {executable}.");
    }
}
