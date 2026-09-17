using NetRatel.Client.Service.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NetRatel.Client.Service.Terminal;

public enum TerminalBackendPreference
{
    Auto,
    ConPty,
    UnixPty,
    Redirected
}

public sealed class TerminalHostOptions
{
    public TerminalBackendPreference BackendPreference { get; set; } = TerminalBackendPreference.Auto;
    public bool EnableNativeUnixPty { get; set; } = true;
    public int GracefulExitTimeoutMs { get; set; } = 1500;
    public int KillTimeoutMs { get; set; } = 2000;
}

internal sealed class TerminalOutputChunk
{
    public required string Direction { get; init; }
    public required string Data { get; init; }
}

internal interface ITerminalHostSession : IDisposable
{
    string Backend { get; }
    string ShellType { get; }
    bool HasExited { get; }
    event Action<string?>? Exited;
    Task StartAsync(Func<TerminalOutputChunk, Task> onOutput, CancellationToken ct);
    Task WriteInputAsync(string data, CancellationToken ct);
    Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct);
    Task RequestCloseAsync(string? reason, CancellationToken ct);
}

internal sealed record TerminalResizeResult(
    string Result,
    int RequestedCols,
    int RequestedRows,
    int? AppliedCols = null,
    int? AppliedRows = null,
    string? Error = null)
{
    public static TerminalResizeResult Applied(int cols, int rows) =>
        new("applied", cols, rows, cols, rows);

    public static TerminalResizeResult Failed(int cols, int rows, string error) =>
        new("failed", cols, rows, null, null, error);

    public static TerminalResizeResult Unsupported(int cols, int rows, string reason) =>
        new("unsupported", cols, rows, null, null, reason);
}

internal sealed class TerminalHostFactory
{
    private readonly TerminalHostOptions _options;

    public TerminalHostFactory(TerminalHostOptions options)
    {
        _options = options;
    }

    public ITerminalHostSession Create(Terminal.TerminalOpenPayload? payload)
    {
        foreach (var host in CreateCandidates(payload))
        {
            return host;
        }

        throw new InvalidOperationException("No terminal backend is available.");
    }

    public IReadOnlyList<ITerminalHostSession> CreateCandidates(Terminal.TerminalOpenPayload? payload)
    {
        var shellType = NormalizeShellType(payload?.ShellType);
        var executable = ResolveExecutable(shellType);
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException($"Executable not found for shell '{shellType}'.");
        }

        var context = new TerminalHostContext(
            shellType,
            executable,
            BuildShellArguments(shellType),
            payload?.WorkingDirectory,
            Math.Clamp(payload?.Cols ?? 120, 40, 300),
            Math.Clamp(payload?.Rows ?? 32, 10, 120),
            _options);

        var preference = _options.BackendPreference;
        if (preference == TerminalBackendPreference.Redirected)
        {
            return new[] { new RedirectedProcessTerminalHost(context) };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (preference == TerminalBackendPreference.UnixPty)
            {
                throw new InvalidOperationException("UnixPty backend is not available on Windows.");
            }

            if (preference is TerminalBackendPreference.Auto or TerminalBackendPreference.ConPty)
            {
                var host = ConPtyTerminalHost.TryCreate(context);
                if (host is not null)
                {
                    return new[] { host };
                }

                if (preference == TerminalBackendPreference.ConPty)
                {
                    throw new InvalidOperationException("ConPTY backend is unavailable on this Windows host.");
                }
            }

            return new[] { new RedirectedProcessTerminalHost(context) };
        }

        if (preference == TerminalBackendPreference.ConPty)
        {
            throw new InvalidOperationException("ConPty backend is only available on Windows.");
        }

        if (preference is TerminalBackendPreference.Auto or TerminalBackendPreference.UnixPty)
        {
            var candidates = new List<ITerminalHostSession>();
            if (_options.EnableNativeUnixPty)
            {
                AddIfNotNull(candidates, NativeUnixPtyTerminalHost.TryCreate(context));
                AddIfNotNull(candidates, UnixPtyHelperTerminalHost.TryCreate(context));
            }
            else
            {
                LogManager.WriteLog("[TerminalHost] Native Unix PTY disabled by config/default; using script/socat fallback order.");
            }

            AddIfNotNull(candidates, UnixPtyTerminalHost.TryCreateScript(context));
            AddIfNotNull(candidates, UnixPtyTerminalHost.TryCreateSocat(context));
            if (candidates.Count > 0)
            {
                return candidates;
            }

            if (preference == TerminalBackendPreference.UnixPty)
            {
                throw new InvalidOperationException("Unix PTY backend is unavailable. Neither 'script' nor 'socat' was found.");
            }
        }

        return new[] { new RedirectedProcessTerminalHost(context) };
    }

    private static void AddIfNotNull(List<ITerminalHostSession> hosts, ITerminalHostSession? host)
    {
        if (host is not null)
        {
            hosts.Add(host);
        }
    }

    internal static string NormalizeShellType(string? shellType) =>
        (shellType ?? "pwsh").Trim().ToLowerInvariant() switch
        {
            "powershell.exe" => "powershell",
            "pwsh.exe" => "pwsh",
            "cmd.exe" => "cmd",
            var value when !string.IsNullOrWhiteSpace(value) => value,
            _ => "pwsh"
        };

    private static string BuildShellArguments(string shellType) =>
        shellType switch
        {
            "powershell" => "-NoLogo -NoProfile",
            "pwsh" when !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) => "-NoLogo -NoProfile -NoExit -Command \"try { Import-Module PSReadLine -ErrorAction SilentlyContinue; Set-PSReadLineOption -PredictionSource None -HistorySaveStyle SaveNothing -ErrorAction SilentlyContinue } catch {}\"",
            "pwsh" => "-NoLogo -NoProfile",
            "cmd" => "/Q /K",
            "bash" or "sh" or "zsh" => "-i",
            _ => string.Empty
        };

    internal static string? ResolveExecutable(string exeName)
    {
        var normalized = exeName switch
        {
            "powershell" => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell.exe" : "powershell",
            "cmd" => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "cmd",
            _ => exeName
        };

        if (Path.IsPathRooted(normalized) && File.Exists(normalized))
        {
            return normalized;
        }

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? Array.Empty<string>();

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "", ".exe", ".cmd", ".bat" }
            : new[] { "" };

        foreach (var dir in pathDirs)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir, normalized + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }
}

internal sealed class UnixPtyHelperTerminalHost : ITerminalHostSession
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResizeTimeout = TimeSpan.FromSeconds(3);
    private readonly TerminalHostContext _context;
    private readonly string _pythonPath;
    private readonly string _helperPath;
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private readonly SemaphoreSlim _resizeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Process? _process;
    private StreamWriter? _controlWriter;
    private StreamReader? _statusReader;
    private Func<TerminalOutputChunk, Task>? _onOutput;
    private long _resizeRequestId;
    private int _exitRaised;
    private bool _disposed;

    private UnixPtyHelperTerminalHost(
        TerminalHostContext context,
        string pythonPath,
        string helperPath)
    {
        _context = context;
        _pythonPath = pythonPath;
        _helperPath = helperPath;
    }

    public string Backend => "unix-pty-python";
    public string ShellType => _context.ShellType;
    public bool HasExited
    {
        get
        {
            try { return _process is null || _process.HasExited; }
            catch { return true; }
        }
    }

    public event Action<string?>? Exited;

    public static ITerminalHostSession? TryCreate(TerminalHostContext context)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var availability = GetAvailability();
        if (!availability.IsAvailable)
        {
            LogManager.WriteLog(
                $"[TerminalHost] Unix PTY helper unavailable appBase={availability.AppBaseDirectory} process={availability.ProcessPath ?? "missing"} python3={availability.PythonPath ?? "missing"} helper={availability.HelperPath ?? "missing"}; using wrapper fallback.");
            return null;
        }

        return new UnixPtyHelperTerminalHost(context, availability.PythonPath!, availability.HelperPath!);
    }

    internal static UnixPtyHelperAvailability GetAvailability()
    {
        var pythonPath = TerminalHostFactory.ResolveExecutable("python3");
        var helperPath = ResolveHelperPath();
        return new UnixPtyHelperAvailability(
            AppContext.BaseDirectory,
            Environment.ProcessPath,
            pythonPath,
            helperPath);
    }

    public async Task StartAsync(Func<TerminalOutputChunk, Task> onOutput, CancellationToken ct)
    {
        _onOutput = onOutput;
        var startInfo = CreateStartInfo();
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start Unix PTY helper process.");
        }

        _controlWriter = _process.StandardInput;
        _controlWriter.AutoFlush = true;
        _statusReader = _process.StandardError;

        string? readyLine;
        using (var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            readyCts.CancelAfter(ReadyTimeout);
            readyLine = await _statusReader.ReadLineAsync(readyCts.Token).ConfigureAwait(false);
        }

        if (!TryParseStatus(readyLine, out var ready) ||
            !string.Equals(ready.Type, "ready", StringComparison.OrdinalIgnoreCase))
        {
            var detail = ready?.Error ?? readyLine ?? "helper exited before ready handshake";
            throw new InvalidOperationException($"Unix PTY helper failed readiness handshake: {detail}");
        }

        _ = Task.Run(() => ReadOutputLoopAsync(_lifetimeCts.Token));
        _ = Task.Run(() => WaitForExitAsync(_lifetimeCts.Token));
        LogManager.WriteLog(
            $"[TerminalHost] Started {Backend} helperPid={_process.Id} shellPid={ready.Pid} shell={ShellType} cols={ready.Cols} rows={ready.Rows}");
    }

    public Task WriteInputAsync(string data, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(data))
        {
            return Task.CompletedTask;
        }

        return SendControlAsync(new
        {
            type = "input",
            data = Convert.ToBase64String(Encoding.UTF8.GetBytes(data))
        }, ct);
    }

    public async Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        var (requestedCols, requestedRows) = NativeUnixPtyTerminalHost.ClampSize(cols, rows);
        await _resizeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_statusReader is null || HasExited)
            {
                return TerminalResizeResult.Failed(cols, rows, "Unix PTY helper is not running.");
            }

            var requestId = Interlocked.Increment(ref _resizeRequestId);
            await SendControlAsync(new
            {
                type = "resize",
                id = requestId,
                cols = requestedCols,
                rows = requestedRows
            }, ct).ConfigureAwait(false);

            using var resizeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            resizeCts.CancelAfter(ResizeTimeout);
            while (true)
            {
                var line = await _statusReader.ReadLineAsync(resizeCts.Token).ConfigureAwait(false);
                if (!TryParseStatus(line, out var status))
                {
                    return TerminalResizeResult.Failed(cols, rows, line ?? "Unix PTY helper closed its status stream.");
                }

                if (string.Equals(status.Type, "resize", StringComparison.OrdinalIgnoreCase) &&
                    status.Id == requestId)
                {
                    if (string.Equals(status.Result, "applied", StringComparison.OrdinalIgnoreCase))
                    {
                        LogManager.WriteLog(
                            $"[TerminalHost] Unix PTY helper resize applied shell={ShellType} cols={status.Cols} rows={status.Rows}");
                        return TerminalResizeResult.Applied(status.Cols ?? requestedCols, status.Rows ?? requestedRows);
                    }

                    return TerminalResizeResult.Failed(
                        cols,
                        rows,
                        status.Error ?? "Unix PTY helper rejected resize.");
                }

                if (string.Equals(status.Type, "error", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status.Type, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    return TerminalResizeResult.Failed(
                        cols,
                        rows,
                        status.Error ?? $"Unix PTY helper {status.Type}.");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TerminalResizeResult.Failed(cols, rows, "Unix PTY helper resize acknowledgement timed out.");
        }
        finally
        {
            _resizeLock.Release();
        }
    }

    public async Task RequestCloseAsync(string? reason, CancellationToken ct)
    {
        try
        {
            if (!HasExited)
            {
                await SendControlAsync(new { type = "close", reason }, ct).ConfigureAwait(false);
                if (_process is not null)
                {
                    await _process.WaitForExitAsync(ct)
                        .WaitAsync(TimeSpan.FromMilliseconds(_context.Options.GracefulExitTimeoutMs), ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (TimeoutException)
        {
            TryKill();
        }
        catch (OperationCanceledException)
        {
            TryKill();
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[TerminalHost] Unix PTY helper close failed: {ex.Message}");
            TryKill();
        }
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = _pythonPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(_helperPath);
        info.ArgumentList.Add("--cols");
        info.ArgumentList.Add(_context.Cols.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--rows");
        info.ArgumentList.Add(_context.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(_context.WorkingDirectory) && Directory.Exists(_context.WorkingDirectory))
        {
            info.ArgumentList.Add("--cwd");
            info.ArgumentList.Add(_context.WorkingDirectory);
        }
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("/bin/sh");
        info.ArgumentList.Add("-lc");
        info.ArgumentList.Add(BuildShellCommand());
        info.Environment["TERM"] = "xterm-256color";
        info.Environment["LANG"] = "C.UTF-8";
        info.Environment["LC_ALL"] = "C.UTF-8";
        info.Environment["COLUMNS"] = _context.Cols.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["LINES"] = _context.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return info;
    }

    private string BuildShellCommand()
    {
        var command = string.IsNullOrWhiteSpace(_context.Arguments)
            ? QuoteShell(_context.Executable)
            : $"{QuoteShell(_context.Executable)} {_context.Arguments}";
        return $"exec {command}";
    }

    private async Task SendControlAsync(object message, CancellationToken ct)
    {
        await _controlLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_controlWriter is null || HasExited)
            {
                throw new InvalidOperationException("Unix PTY helper control stream is unavailable.");
            }

            var json = JsonSerializer.Serialize(message);
            await _controlWriter.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _controlWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _controlLock.Release();
        }
    }

    private async Task ReadOutputLoopAsync(CancellationToken ct)
    {
        if (_process is null || _onOutput is null)
        {
            return;
        }

        var stream = _process.StandardOutput.BaseStream;
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (charCount > 0)
                {
                    await _onOutput(new TerminalOutputChunk
                    {
                        Direction = "stdout",
                        Data = new string(chars, 0, charCount)
                    }).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[TerminalHost] Unix PTY helper output failed: {ex.Message}");
        }
    }

    private async Task WaitForExitAsync(CancellationToken ct)
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            await _process.WaitForExitAsync(ct).ConfigureAwait(false);
            RaiseExited($"Shell exited with code {_process.ExitCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void RaiseExited(string? reason)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
        {
            Exited?.Invoke(reason);
        }
    }

    private void TryKill()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static bool TryParseStatus(string? line, out HelperStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            status = JsonSerializer.Deserialize<HelperStatus>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return status is not null && !string.IsNullOrWhiteSpace(status.Type);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveHelperPath()
    {
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(executableDirectory)
                ? null
                : Path.Combine(executableDirectory, "terminal_pty_helper.py"),
            Path.Combine(AppContext.BaseDirectory, "terminal_pty_helper.py"),
            Path.Combine(AppContext.BaseDirectory, "Service", "Terminal", "terminal_pty_helper.py")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static string QuoteShell(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _lifetimeCts.Cancel(); } catch { }
        TryKill();
        try { _controlWriter?.Dispose(); } catch { }
        try { _statusReader?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }
        _controlLock.Dispose();
        _resizeLock.Dispose();
        _lifetimeCts.Dispose();
    }

    private sealed class HelperStatus
    {
        public string? Type { get; set; }
        public long? Id { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
        public int? Pid { get; set; }
        public int? Cols { get; set; }
        public int? Rows { get; set; }
    }

    internal sealed record UnixPtyHelperAvailability(
        string AppBaseDirectory,
        string? ProcessPath,
        string? PythonPath,
        string? HelperPath)
    {
        public bool IsAvailable =>
            !string.IsNullOrWhiteSpace(PythonPath) &&
            !string.IsNullOrWhiteSpace(HelperPath);
    }
}

internal sealed class TerminalHostContext
{
    public TerminalHostContext(
        string shellType,
        string executable,
        string arguments,
        string? workingDirectory,
        int cols,
        int rows,
        TerminalHostOptions options)
    {
        ShellType = shellType;
        Executable = executable;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        Cols = cols;
        Rows = rows;
        Options = options;
    }

    public string ShellType { get; }
    public string Executable { get; }
    public string Arguments { get; }
    public string? WorkingDirectory { get; }
    public int Cols { get; }
    public int Rows { get; }
    public TerminalHostOptions Options { get; }
}

internal class RedirectedProcessTerminalHost : ITerminalHostSession
{
    private readonly TerminalHostContext _context;
    private Process? _process;
    private StreamWriter? _inputWriter;
    private Func<TerminalOutputChunk, Task>? _onOutput;

    public RedirectedProcessTerminalHost(TerminalHostContext context)
    {
        _context = context;
    }

    public virtual string Backend => "redirected";
    public string ShellType => _context.ShellType;
    public bool HasExited => _process?.HasExited ?? true;
    public event Action<string?>? Exited;
    protected TerminalHostContext Context => _context;

    public virtual Task StartAsync(Func<TerminalOutputChunk, Task> onOutput, CancellationToken ct)
    {
        _onOutput = onOutput;
        var psi = CreateStartInfo();
        _process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };
        _process.Exited += (_, _) => Exited?.Invoke("Shell exited.");

        if (!_process.Start())
        {
            throw new InvalidOperationException("Process.Start returned false.");
        }

        _inputWriter = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false), leaveOpen: false)
        {
            AutoFlush = true
        };

        _ = Task.Run(() => ReadStreamAsync(_process.StandardOutput, "stdout", ct));
        _ = Task.Run(() => ReadStreamAsync(_process.StandardError, "stderr", ct));
        LogManager.WriteLog($"[TerminalHost] Started {Backend} shell={ShellType} pid={_process.Id}");
        return Task.CompletedTask;
    }

    protected virtual ProcessStartInfo CreateStartInfo() =>
        BuildStartInfo(_context.Executable, _context.Arguments, _context.WorkingDirectory, _context.Cols, _context.Rows);

    public virtual async Task WriteInputAsync(string data, CancellationToken ct)
    {
        if (_inputWriter is null || string.IsNullOrEmpty(data))
        {
            return;
        }

        var input = NormalizeInput(ShellType, data);
        await _inputWriter.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
        await _inputWriter.FlushAsync().ConfigureAwait(false);
    }

    public virtual Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        LogManager.WriteLog($"[TerminalHost] Resize ignored by {Backend} shell={ShellType} cols={cols} rows={rows}");
        return Task.FromResult(TerminalResizeResult.Unsupported(cols, rows, $"{Backend} does not support terminal resize."));
    }

    public virtual async Task RequestCloseAsync(string? reason, CancellationToken ct)
    {
        try
        {
            if (_process is null || _process.HasExited)
            {
                return;
            }

            if (_inputWriter is not null)
            {
                await _inputWriter.WriteLineAsync("exit").ConfigureAwait(false);
                await _inputWriter.FlushAsync().ConfigureAwait(false);
            }

            await Task.Delay(_context.Options.GracefulExitTimeoutMs, ct).ConfigureAwait(false);
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[TerminalHost] Close failed backend={Backend}: {ex.Message}");
        }
    }

    protected static ProcessStartInfo BuildStartInfo(string executable, string arguments, string? workingDirectory, int cols, int rows)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            info.WorkingDirectory = workingDirectory;
        }

        info.Environment["TERM"] = "xterm-256color";
        info.Environment["LANG"] = "C.UTF-8";
        info.Environment["LC_ALL"] = "C.UTF-8";
        info.Environment["COLUMNS"] = cols.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["LINES"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return info;
    }

    protected async Task ReadStreamAsync(StreamReader reader, string direction, CancellationToken ct)
    {
        var buffer = new char[2048];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var charsRead = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (charsRead <= 0)
                {
                    break;
                }

                var chunk = NormalizeOutput(new string(buffer, 0, charsRead));
                if (!string.IsNullOrEmpty(chunk) && _onOutput is not null)
                {
                    await _onOutput(new TerminalOutputChunk { Direction = direction, Data = chunk }).ConfigureAwait(false);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[TerminalHost] Read error backend={Backend} direction={direction}: {ex.Message}");
        }
    }

    protected virtual string NormalizeOutput(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    protected virtual string NormalizeInput(string shellType, string data)
    {
        if (shellType is "pwsh" or "powershell" or "cmd")
        {
            return data
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return data;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                try { _inputWriter?.Close(); } catch { }
                if (!_process.WaitForExit(200))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }
        finally
        {
            _inputWriter?.Dispose();
            _process?.Dispose();
        }
    }
}

internal sealed class UnixPtyTerminalHost : RedirectedProcessTerminalHost
{
    private readonly string _shellCommand;
    private readonly string? _socatAddress;

    private UnixPtyTerminalHost(TerminalHostContext context, string shellCommand, string? socatAddress = null)
        : base(context)
    {
        _shellCommand = shellCommand;
        _socatAddress = socatAddress;
    }

    public override string Backend => _socatAddress is null ? "unix-pty-script" : "unix-pty-socat";

    public static ITerminalHostSession? TryCreate(TerminalHostContext context) =>
        TryCreateScript(context) ?? TryCreateSocat(context);

    public static ITerminalHostSession? TryCreateScript(TerminalHostContext context)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var shellCommand = BuildShellCommand(context.Executable, context.Arguments, context.Cols, context.Rows);
        var script = TerminalHostFactory.ResolveExecutable("script");
        if (!string.IsNullOrWhiteSpace(script))
        {
            var scriptContext = new TerminalHostContext(
                context.ShellType,
                script,
                string.Empty,
                context.WorkingDirectory,
                context.Cols,
                context.Rows,
                context.Options);
            return new UnixPtyTerminalHost(scriptContext, shellCommand);
        }

        return null;
    }

    public static ITerminalHostSession? TryCreateSocat(TerminalHostContext context)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var shellCommand = BuildShellCommand(context.Executable, context.Arguments, context.Cols, context.Rows);
        var socat = TerminalHostFactory.ResolveExecutable("socat");
        if (string.IsNullOrWhiteSpace(socat))
        {
            return null;
        }

        var ptyContext = new TerminalHostContext(
            context.ShellType,
            socat,
            string.Empty,
            context.WorkingDirectory,
            context.Cols,
            context.Rows,
            context.Options);
        return new UnixPtyTerminalHost(ptyContext, shellCommand, $"EXEC:{shellCommand},pty,setsid,ctty,stderr");
    }

    public override Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        const string reason = "dynamic TTY window-size application is not available for the current script/socat wrapper";
        LogManager.WriteLog($"[TerminalHost] Resize unsupported by {Backend} shell={ShellType} cols={cols} rows={rows}: {reason}.");
        return Task.FromResult(TerminalResizeResult.Unsupported(cols, rows, reason));
    }

    protected override string NormalizeInput(string shellType, string data) => data;
    protected override string NormalizeOutput(string value) => base.NormalizeOutput(value);

    protected override ProcessStartInfo CreateStartInfo()
    {
        var info = BuildStartInfo(Context.Executable, string.Empty, Context.WorkingDirectory, Context.Cols, Context.Rows);
        if (_socatAddress is not null)
        {
            info.ArgumentList.Add("-");
            info.ArgumentList.Add(_socatAddress);
            return info;
        }

        info.ArgumentList.Add("-qfec");
        info.ArgumentList.Add(_shellCommand);
        info.ArgumentList.Add("/dev/null");
        return info;
    }

    private static string BuildShellCommand(string executable, string arguments, int cols, int rows)
    {
        var command = string.IsNullOrWhiteSpace(arguments) ? executable : $"{executable} {arguments}";
        return $"stty cols {cols} rows {rows} 2>/dev/null; exec {command}";
    }
}

internal sealed class NativeUnixPtyTerminalHost : ITerminalHostSession
{
    private const int WNoHang = 1;
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const string LinuxSpawnLibrary = "netratel_terminal_pty";
    private readonly TerminalHostContext _context;
    private readonly string _shellCommand;
    private readonly object _lifecycleLock = new();
    private readonly object _writeLock = new();
    private int _masterFd = -1;
    private int _childPid;
    private FileStream? _masterReadStream;
    private FileStream? _masterWriteStream;
    private Func<TerminalOutputChunk, Task>? _onOutput;
    private volatile bool _childExited;
    private bool _disposed;

    static NativeUnixPtyTerminalHost()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeUnixPtyTerminalHost).Assembly, ResolveNativeLibrary);
        }
    }

    private NativeUnixPtyTerminalHost(TerminalHostContext context)
    {
        _context = context;
        _shellCommand = BuildShellCommand(context);
    }

    public string Backend => "unix-pty-native";
    public string ShellType => _context.ShellType;
    public bool HasExited
    {
        get
        {
            if (_childPid <= 0)
            {
                return true;
            }

            if (_childExited)
            {
                return true;
            }

            var result = WaitPid(_childPid, out _, WNoHang);
            if (result == _childPid)
            {
                _childExited = true;
                return true;
            }

            return false;
        }
    }

    public event Action<string?>? Exited;

    public static ITerminalHostSession? TryCreate(TerminalHostContext context)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !IsForkPtyAvailable())
        {
            return null;
        }

        return new NativeUnixPtyTerminalHost(context);
    }

    public Task StartAsync(Func<TerminalOutputChunk, Task> onOutput, CancellationToken ct)
    {
        _onOutput = onOutput;
        var (initialCols, initialRows) = ClampSize(_context.Cols, _context.Rows);
        var size = new WinSize
        {
            Row = checked((ushort)initialRows),
            Col = checked((ushort)initialCols),
            XPixel = 0,
            YPixel = 0
        };

        try
        {
            using var argv = NativeArgv.ForShellCommand(_shellCommand);
            var pid = ForkPtyAndExec(out _masterFd, argv.Path, argv.Argv, ref size);
            if (pid < 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"forkpty failed with errno {error}.");
            }

            if (pid == 0)
            {
                // macOS still uses its platform forkpty entry point. Linux uses
                // the native helper above and never returns in the child.
                Execv(argv.Path, argv.Argv);
                ExitProcess(127);
            }

            _childPid = pid;
            var writeFd = Dup(_masterFd);
            if (writeFd < 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"dup failed with errno {error}.");
            }

            var readHandle = new SafeFileHandle(new IntPtr(_masterFd), ownsHandle: true);
            var writeHandle = new SafeFileHandle(new IntPtr(writeFd), ownsHandle: true);
            _masterReadStream = new FileStream(readHandle, FileAccess.Read, 4096, isAsync: false);
            _masterWriteStream = new FileStream(writeHandle, FileAccess.Write, 4096, isAsync: false);
            _ = Task.Run(() => ReadOutputLoop(ct));
            _ = Task.Run(() => WaitForExitAsync(ct));
            LogManager.WriteLog($"[TerminalHost] Started {Backend} shell={ShellType} pid={_childPid} cols={size.Col} rows={size.Row}");
            return Task.CompletedTask;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Task WriteInputAsync(string data, CancellationToken ct)
    {
        if (_masterWriteStream is null || string.IsNullOrEmpty(data))
        {
            return Task.CompletedTask;
        }

        var bytes = Encoding.UTF8.GetBytes(data);
        lock (_writeLock)
        {
            ct.ThrowIfCancellationRequested();
            _masterWriteStream.Write(bytes, 0, bytes.Length);
            _masterWriteStream.Flush();
        }

        return Task.CompletedTask;
    }

    public Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lifecycleLock)
        {
            if (_disposed || _masterFd < 0)
            {
                return Task.FromResult(TerminalResizeResult.Failed(cols, rows, "PTY master fd is not open."));
            }

            var (appliedCols, appliedRows) = ClampSize(cols, rows);
            var size = new WinSize
            {
                Row = checked((ushort)appliedRows),
                Col = checked((ushort)appliedCols),
                XPixel = 0,
                YPixel = 0
            };

            var result = Ioctl(_masterFd, GetTiocswinsz(), ref size);
            if (result != 0)
            {
                var error = Marshal.GetLastWin32Error();
                var message = $"ioctl(TIOCSWINSZ) failed errno={error}";
                LogManager.WriteLog($"[TerminalHost] Native Unix PTY resize failed shell={ShellType} cols={cols} rows={rows}: {message}");
                return Task.FromResult(TerminalResizeResult.Failed(cols, rows, message));
            }

            LogManager.WriteLog($"[TerminalHost] Native Unix PTY resize applied shell={ShellType} cols={appliedCols} rows={appliedRows}");
            return Task.FromResult(TerminalResizeResult.Applied(appliedCols, appliedRows));
        }
    }

    public async Task RequestCloseAsync(string? reason, CancellationToken ct)
    {
        try
        {
            if (_childPid <= 0 || HasExited)
            {
                return;
            }

            await WriteInputAsync("\u0003", ct).ConfigureAwait(false);
            await Task.Delay(_context.Options.GracefulExitTimeoutMs, ct).ConfigureAwait(false);
            if (!HasExited)
            {
                Kill(_childPid, SigTerm);
            }

            await Task.Delay(_context.Options.KillTimeoutMs, ct).ConfigureAwait(false);
            if (!HasExited)
            {
                Kill(_childPid, SigKill);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[TerminalHost] Native Unix PTY close failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            if (_childPid > 0 && !HasExited)
            {
                Kill(_childPid, SigTerm);
                if (!WaitForExit(TimeSpan.FromMilliseconds(500)))
                {
                    Kill(_childPid, SigKill);
                }
            }
        }
        catch
        {
        }
        finally
        {
            lock (_lifecycleLock)
            {
                try { _masterWriteStream?.Dispose(); } catch { }
                try { _masterReadStream?.Dispose(); } catch { }
                if (_masterReadStream is null && _masterFd >= 0)
                {
                    try { CloseFd(_masterFd); } catch { }
                }

                _masterFd = -1;
            }
        }
    }

    private void ReadOutputLoop(CancellationToken ct)
    {
        if (_masterReadStream is null)
        {
            return;
        }

        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = _masterReadStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                if (_onOutput is not null)
                {
                    _onOutput(new TerminalOutputChunk
                    {
                        Direction = "stdout",
                        Data = Encoding.UTF8.GetString(buffer, 0, read)
                    }).GetAwaiter().GetResult();
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex) when (ex.Message.Contains("Input/output error", StringComparison.OrdinalIgnoreCase))
        {
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[TerminalHost] Native Unix PTY read failed: {ex.Message}");
            Exited?.Invoke($"Native Unix PTY read failed: {ex.Message}");
        }
    }

    private async Task WaitForExitAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !HasExited)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            Exited?.Invoke("Shell exited.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool WaitForExit(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (HasExited)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return HasExited;
    }

    private static bool IsForkPtyAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var helper = ResolveLinuxSpawnHelperPath();
            if (helper is null || !NativeLibrary.TryLoad(helper, out var nativeSpawnHelper))
            {
                LogManager.WriteLog("[TerminalHost] Native Unix PTY spawn helper is unavailable; Python PTY fallback remains available.");
                return false;
            }

            NativeLibrary.Free(nativeSpawnHelper);
            return true;
        }

        if (NativeLibrary.TryLoad("libutil", out var libutil))
        {
            NativeLibrary.Free(libutil);
            return true;
        }

        if (NativeLibrary.TryLoad("libutil.so.1", out var libutilSo))
        {
            NativeLibrary.Free(libutilSo);
            return true;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }

    private static string? ResolveLinuxSpawnHelperPath()
    {
        const string libraryName = "libnetratel_terminal_pty.so";
        var appBasePath = Path.Combine(AppContext.BaseDirectory, libraryName);
        if (File.Exists(appBasePath))
        {
            return appBasePath;
        }

        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        var executablePath = executableDirectory is null
            ? null
            : Path.Combine(executableDirectory, libraryName);
        return executablePath is not null && File.Exists(executablePath) ? executablePath : null;
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.Equals(libraryName, LinuxSpawnLibrary, StringComparison.Ordinal) &&
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var helper = ResolveLinuxSpawnHelperPath();
            if (helper is not null)
            {
                return NativeLibrary.Load(helper, assembly, searchPath);
            }
        }

        return IntPtr.Zero;
    }

    private static uint GetTiocswinsz() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x80087467u : 0x5414u;

    internal static (int Cols, int Rows) ClampSize(int cols, int rows) =>
        (Math.Clamp(cols, 2, 300), Math.Clamp(rows, 1, 120));

    internal static IReadOnlyList<string> BuildShellArgv(string command) =>
        ["/bin/sh", "-lc", command];

    private static string BuildShellCommand(TerminalHostContext context)
    {
        var command = string.IsNullOrWhiteSpace(context.Arguments)
            ? QuoteShell(context.Executable)
            : $"{QuoteShell(context.Executable)} {context.Arguments}";
        var workingDirectory = !string.IsNullOrWhiteSpace(context.WorkingDirectory) && Directory.Exists(context.WorkingDirectory)
            ? $"cd -- {QuoteShell(context.WorkingDirectory)} 2>/dev/null || true; "
            : string.Empty;
        return $"{workingDirectory}export TERM=xterm-256color LANG=C.UTF-8 LC_ALL=C.UTF-8 COLUMNS={context.Cols} LINES={context.Rows}; stty cols {context.Cols} rows {context.Rows} 2>/dev/null; exec {command}";
    }

    private static string QuoteShell(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static int ForkPtyAndExec(out int masterFd, IntPtr executable, IntPtr argv, ref WinSize size) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? ForkPtyMac(out masterFd, IntPtr.Zero, IntPtr.Zero, ref size)
            : ForkPtyAndExecLinux(out masterFd, executable, argv, ref size);

    [DllImport(LinuxSpawnLibrary, EntryPoint = "netratel_forkpty_exec", SetLastError = true)]
    private static extern int ForkPtyAndExecLinux(out int amaster, IntPtr executable, IntPtr argv, ref WinSize winp);

    [DllImport("libutil", EntryPoint = "forkpty", SetLastError = true)]
    private static extern int ForkPtyMac(out int amaster, IntPtr name, IntPtr termp, ref WinSize winp);

    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int Execv(IntPtr path, IntPtr argv);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void ExitProcess(int status);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, uint request, ref WinSize size);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int pid, out int status, int options);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int oldfd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFd(int fd);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Row;
        public ushort Col;
        public ushort XPixel;
        public ushort YPixel;
    }

    private sealed class NativeArgv : IDisposable
    {
        private readonly List<IntPtr> _allocated = new();

        private NativeArgv(IntPtr path, IntPtr argv)
        {
            Path = path;
            Argv = argv;
        }

        public IntPtr Path { get; }
        public IntPtr Argv { get; }

        public static NativeArgv ForShellCommand(string command)
        {
            var values = BuildShellArgv(command);
            var args = values
                .Select(Marshal.StringToHGlobalAnsi)
                .Append(IntPtr.Zero)
                .ToArray();
            var shellPath = args[0];
            var argv = Marshal.AllocHGlobal(IntPtr.Size * args.Length);
            for (var i = 0; i < args.Length; i++)
            {
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, args[i]);
            }

            var native = new NativeArgv(shellPath, argv);
            native._allocated.AddRange(args.Where(x => x != IntPtr.Zero));
            native._allocated.Add(argv);
            return native;
        }

        public void Dispose()
        {
            foreach (var ptr in _allocated)
            {
                try { Marshal.FreeHGlobal(ptr); } catch { }
            }
        }
    }
}

internal sealed class ConPtyTerminalHost : ITerminalHostSession
{
    private readonly TerminalHostContext _context;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private IntPtr _pseudoConsole;
    private IntPtr _attributeList;
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private int _processId;
    private Func<TerminalOutputChunk, Task>? _onOutput;

    private ConPtyTerminalHost(TerminalHostContext context)
    {
        _context = context;
    }

    public string Backend => "conpty";
    public string ShellType => _context.ShellType;
    public bool HasExited => _processHandle == IntPtr.Zero || WaitForSingleObject(_processHandle, 0) == WaitObject0;
    public event Action<string?>? Exited;

    public static ITerminalHostSession? TryCreate(TerminalHostContext context)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        return new ConPtyTerminalHost(context);
    }

    public Task StartAsync(Func<TerminalOutputChunk, Task> onOutput, CancellationToken ct)
    {
        _onOutput = onOutput;
        CreatePipePair(out var inputRead, out _inputWrite);
        CreatePipePair(out _outputRead, out var outputWrite);

        try
        {
            var size = new Coord
            {
                X = checked((short)_context.Cols),
                Y = checked((short)_context.Rows)
            };
            var hr = CreatePseudoConsole(size, inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), 0, out _pseudoConsole);
            if (hr < 0)
            {
                throw new Win32Exception(hr, $"CreatePseudoConsole failed with HRESULT 0x{hr:X8}.");
            }

            inputRead.Dispose();
            outputWrite.Dispose();

            StartAttachedProcess();
            // CreatePipe returns synchronous handles. Opening these FileStreams as async
            // requires overlapped handles and fails on Windows service agents.
            _inputStream = new FileStream(_inputWrite, FileAccess.Write, 4096, isAsync: false);
            _outputStream = new FileStream(_outputRead, FileAccess.Read, 4096, isAsync: false);

            _ = Task.Run(() => ReadOutputAsync(ct));
            _ = Task.Run(() => WaitForExitAsync(ct));
            LogManager.WriteLog($"[TerminalHost] Started conpty shell={ShellType} pid={_processId}");
            return Task.CompletedTask;
        }
        catch
        {
            inputRead.Dispose();
            outputWrite.Dispose();
            Dispose();
            throw;
        }
    }

    public async Task WriteInputAsync(string data, CancellationToken ct)
    {
        if (_inputStream is null || string.IsNullOrEmpty(data))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(data);
        await _inputStream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await _inputStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        if (_pseudoConsole == IntPtr.Zero)
        {
            return Task.FromResult(TerminalResizeResult.Failed(cols, rows, "Pseudo console handle is not open."));
        }

        var size = new Coord
        {
            X = checked((short)Math.Clamp(cols, 2, 300)),
            Y = checked((short)Math.Clamp(rows, 1, 120))
        };
        var hr = ResizePseudoConsole(_pseudoConsole, size);
        if (hr < 0)
        {
            var message = $"ResizePseudoConsole failed hr=0x{hr:X8}";
            LogManager.WriteLog($"[TerminalHost] {message}");
            return Task.FromResult(TerminalResizeResult.Failed(cols, rows, message));
        }
        else
        {
            LogManager.WriteLog($"[TerminalHost] ResizePseudoConsole applied shell={ShellType} cols={size.X} rows={size.Y}");
            return Task.FromResult(TerminalResizeResult.Applied(size.X, size.Y));
        }
    }

    public async Task RequestCloseAsync(string? reason, CancellationToken ct)
    {
        try
        {
            await WriteInputAsync("\u0003", ct).ConfigureAwait(false);
            await Task.Delay(_context.Options.GracefulExitTimeoutMs, ct).ConfigureAwait(false);
            if (!HasExited && _processId > 0)
            {
                Process.GetProcessById(_processId).Kill(entireProcessTree: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[TerminalHost] ConPTY close failed: {ex.Message}");
        }
    }

    private async Task ReadOutputAsync(CancellationToken ct)
    {
        if (_outputStream is null)
        {
            return;
        }

        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _outputStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (_onOutput is not null)
                {
                    await _onOutput(new TerminalOutputChunk
                    {
                        Direction = "stdout",
                        Data = Encoding.UTF8.GetString(buffer, 0, read)
                    }).ConfigureAwait(false);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[TerminalHost] ConPTY read failed: {ex.Message}");
        }
    }

    private async Task WaitForExitAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !HasExited)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            Exited?.Invoke("Shell exited.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartAttachedProcess()
    {
        var startupInfo = new StartupInfoEx();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();

        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, out var attributeListSize);
        _attributeList = Marshal.AllocHGlobal(attributeListSize);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, out attributeListSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
        }

        if (!UpdateProcThreadAttribute(
                _attributeList,
                0,
                ProcThreadAttributePseudoConsole,
                _pseudoConsole,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }

        startupInfo.lpAttributeList = _attributeList;
        var processInfo = new ProcessInformation();
        var commandLine = new StringBuilder(BuildCommandLine(_context.Executable, _context.Arguments));
        var workingDirectory = !string.IsNullOrWhiteSpace(_context.WorkingDirectory) && Directory.Exists(_context.WorkingDirectory)
            ? _context.WorkingDirectory
            : null;

        if (!CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed.");
        }

        _processHandle = processInfo.hProcess;
        _threadHandle = processInfo.hThread;
        _processId = processInfo.dwProcessId;
    }

    private static string BuildCommandLine(string executable, string arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? QuoteWindows(executable) : $"{QuoteWindows(executable)} {arguments}";

    private static string QuoteWindows(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void CreatePipePair(out SafeFileHandle read, out SafeFileHandle write)
    {
        var security = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true
        };

        if (!CreatePipe(out read, out write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
        }
    }

    public void Dispose()
    {
        try { _inputStream?.Dispose(); } catch { }
        try { _outputStream?.Dispose(); } catch { }
        try { _inputWrite?.Dispose(); } catch { }
        try { _outputRead?.Dispose(); } catch { }
        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }
        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
        if (_threadHandle != IntPtr.Zero)
        {
            CloseHandle(_threadHandle);
            _threadHandle = IntPtr.Zero;
        }
        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
    }

    private const int ExtendedStartupInfoPresent = 0x00080000;
    private const int CreateUnicodeEnvironment = 0x00000400;
    private const int WaitObject0 = 0x00000000;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SecurityAttributes lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, out IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);
}
