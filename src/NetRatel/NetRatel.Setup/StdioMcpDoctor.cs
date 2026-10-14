using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NetRatel.AgentClient;

namespace NetRatel.Setup;

public sealed record StdioMcpLaunch(string Command, IReadOnlyList<string> Arguments);

/// <summary>One redirected, bounded stdio child. Its diagnostics are never copied to the terminal.</summary>
public interface IStdioMcpProbeProcess : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    void Abort();
}

/// <summary>Validates the selected local stdio transport and its offered read-only authentication operation.</summary>
public sealed class StdioMcpDoctor(Func<StdioMcpLaunch?>? resolveLaunch = null,
    Func<ProcessStartInfo, IStdioMcpProbeProcess>? startProcess = null, IReadOnlyDictionary<string, string?>? childEnvironment = null)
{
    public const int MaximumMessageCharacters = 1_048_576;
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public static StdioMcpLaunch? ResolveLaunch(string? baseDirectory = null)
    {
        baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var roots = new[] { baseDirectory, Path.Combine(baseDirectory, "tooling", "stdio"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "stdio")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "netratel-mcp-" + RuntimeInformation.RuntimeIdentifier)) };
        var executable = OperatingSystem.IsWindows() ? "NetRatel.Mcp.exe" : "NetRatel.Mcp";
        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            var native = Path.Combine(root, executable);
            if (File.Exists(native)) return new(native, []);
            var library = Path.Combine(root, "NetRatel.Mcp.dll");
            if (File.Exists(library)) return new("dotnet", [library]);
        }
        return null;
    }

    public async Task<IReadOnlyList<SetupCheck>> CheckAsync(EffectiveAgentClientConfiguration effective,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<SetupCheck>();
        if (effective.Surface != ConfigurationSurface.StdioMcp || !effective.Validate().IsValid)
            return [new("stdio MCP initialize", "failed", "The selected stdio configuration is invalid; run config validate.")];
        var launch = (resolveLaunch ?? (() => ResolveLaunch()))();
        if (launch is null)
            return [new("stdio MCP initialize", "unavailable", "Install the stdio archive beside this installation, then run doctor to validate its protocol transport.")];
        var directory = Path.Combine(Path.GetTempPath(), "netratel-stdio-probe-" + Guid.NewGuid().ToString("N"));
        IStdioMcpProbeProcess? child = null;
        Task? stderr = null;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ProbeTimeout);
        try
        {
            // Freeze the validated effective snapshot. The child cannot select ambient credentials or another target.
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var snapshotPath = Path.Combine(directory, "stdio.json");
            var snapshot = JsonSerializer.Serialize(effective.Configuration, AgentClientConfiguration.JsonOptions);
            if (Encoding.UTF8.GetByteCount(snapshot) > 65_536)
                throw new AgentClientValidationException("The selected stdio configuration exceeds the validation export bound.");
            ConfigurationFileStorage.WriteAtomic(snapshotPath, snapshot);
            var info = new ProcessStartInfo(launch.Command)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in launch.Arguments) info.ArgumentList.Add(argument);
            if (childEnvironment is not null)
            {
                info.Environment.Clear();
                foreach (var pair in childEnvironment) info.Environment[pair.Key] = pair.Value;
            }
            foreach (var key in info.Environment.Keys.ToArray())
                if (IsCredentialEnvironment(key)) info.Environment.Remove(key);
            info.Environment["NETRATEL_MCP_CONFIG"] = snapshotPath;
            child = (startProcess ?? (settings => new LocalProcess(settings)))(info);
            stderr = DiscardAsync(child.Error, bounded.Token);
            using var writer = new StreamWriter(child.Input, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(child.Output, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await WriteAsync(writer, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new {
                protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "NetRatel setup", version = typeof(StdioMcpDoctor).Assembly.GetName().Version!.ToString() } } }, bounded.Token);
            var initialized = await ReadReplyAsync(reader, 1, bounded.Token);
            if (!initialized.TryGetProperty("result", out var initialization) ||
                !initialization.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.String ||
                version.GetString() is not ("2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25"))
            {
                checks.Add(new("stdio MCP initialize", "failed", "The local process did not complete a compatible MCP handshake."));
                return checks;
            }
            checks.Add(new("stdio MCP initialize", "completed", "The local process completed its JSON-RPC protocol handshake."));
            await WriteAsync(writer, new { jsonrpc = "2.0", method = "notifications/initialized" }, bounded.Token);
            await WriteAsync(writer, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, bounded.Token);
            var catalog = await ReadReplyAsync(reader, 2, bounded.Token);
            if (!catalog.TryGetProperty("result", out var catalogResult) || !catalogResult.TryGetProperty("tools", out var tools) ||
                tools.ValueKind != JsonValueKind.Array || !tools.EnumerateArray().Any(tool => tool.ValueKind == JsonValueKind.Object &&
                    tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "netratel_auth"))
            {
                checks.Add(new("permitted read-only tool", "unavailable", "The local MCP process did not offer the authentication status tool."));
                return checks;
            }
            await WriteAsync(writer, new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new {
                name = "netratel_auth", arguments = new { operation = "status" } } }, bounded.Token);
            var reply = await ReadReplyAsync(reader, 3, bounded.Token);
            var accepted = reply.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                !(result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True) &&
                result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object &&
                structured.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True &&
                structured.TryGetProperty("data", out var status) && IsAuthenticationStatus(status);
            checks.Add(new("permitted read-only tool", accepted ? "completed" : IsUnauthorized(reply) ? "unauthorized" : "failed",
                accepted ? "The stdio authentication status tool completed with the selected identity without mutation." :
                    "The stdio status tool did not confirm the selected identity. Check its purpose, current grants and lifecycle."));
            return checks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or
            JsonException or DecoderFallbackException or AgentClientValidationException or OperationCanceledException or InvalidOperationException)
        {
            checks.Add(new("stdio MCP transport", "unavailable", "The bounded stdio check could not complete. Check the local executable, configuration, TLS and authentication; diagnostics were not displayed."));
            return checks;
        }
        finally
        {
            bounded.Cancel();
            if (child is not null) { child.Abort(); await child.DisposeAsync(); }
            if (stderr is not null) { try { await stderr; } catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { } }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static bool IsCredentialEnvironment(string key) =>
        key.StartsWith("NETRATEL_CLI_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NETRATEL_AGENT_CLIENT_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("NETRATEL_MCP_OUTBOUND_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NETRATEL_MCP_CLIENT_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("NETRATEL_INTEGRATION_TOKEN", StringComparison.OrdinalIgnoreCase) || key.StartsWith("BT_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("NetRatel__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NetRatel:", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "NETRATEL_MCP_CONFIG", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "NetRatel_MCP_CONFIG", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "NETRATEL_MCP_INSTANCE", StringComparison.OrdinalIgnoreCase);
    private static async Task WriteAsync(StreamWriter writer, object request, CancellationToken ct)
        => await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
    private static async Task<JsonElement> ReadReplyAsync(StreamReader reader, int requestId, CancellationToken ct)
    {
        var single = new char[1];
        for (var skipped = 0; skipped < 32; skipped++)
        {
            var frame = new StringBuilder();
            var terminated = false;
            while (await reader.ReadAsync(single.AsMemory(), ct) != 0)
            {
                if (single[0] == '\n') { terminated = true; break; }
                if (frame.Length >= MaximumMessageCharacters) throw new AgentClientValidationException("MCP frame exceeds the validation bound.");
                frame.Append(single[0]);
            }
            if (!terminated || frame.Length == 0) throw new AgentClientValidationException("The local MCP process closed its protocol output.");
            using var document = JsonDocument.Parse(frame.ToString());
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("jsonrpc", out var rpc) ||
                rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0") throw new JsonException();
            if (message.TryGetProperty("id", out var id))
            {
                if (message.TryGetProperty("method", out _) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var returned) ||
                    returned != requestId || message.TryGetProperty("result", out _) == message.TryGetProperty("error", out _)) throw new JsonException();
                return message.Clone();
            }
            if (!message.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) throw new JsonException();
        }
        throw new AgentClientValidationException("The local MCP process exceeded the notification bound.");
    }
    private static bool IsAuthenticationStatus(JsonElement status) => status.ValueKind == JsonValueKind.Object &&
        status.TryGetProperty("authenticated", out var authenticated) && authenticated.ValueKind == JsonValueKind.True &&
        status.TryGetProperty("authMode", out var mode) && mode.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mode.GetString()) &&
        status.TryGetProperty("groups", out var groups) && IsStringArray(groups) && status.TryGetProperty("roles", out var roles) && IsStringArray(roles);
    private static bool IsStringArray(JsonElement array) => array.ValueKind == JsonValueKind.Array &&
        array.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()));
    private static bool IsUnauthorized(JsonElement reply) => reply.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object &&
        structured.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
        error.TryGetProperty("upstreamStatus", out var status) && status.TryGetInt32(out var code) && code is 401 or 403;
    private static async Task DiscardAsync(Stream error, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (await error.ReadAsync(buffer.AsMemory(), ct) > 0) { }
    }
    private sealed class LocalProcess : IStdioMcpProbeProcess
    {
        private readonly Process _process;
        public LocalProcess(ProcessStartInfo info) => _process = Process.Start(info) ?? throw new IOException("The local MCP process could not start.");
        public Stream Input => _process.StandardInput.BaseStream;
        public Stream Output => _process.StandardOutput.BaseStream;
        public Stream Error => _process.StandardError.BaseStream;
        public void Abort()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
        }
        public ValueTask DisposeAsync() { Abort(); _process.Dispose(); return ValueTask.CompletedTask; }
    }
}
