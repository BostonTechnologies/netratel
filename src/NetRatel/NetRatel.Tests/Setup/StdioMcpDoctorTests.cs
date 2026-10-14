using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NetRatel.AgentClient;
using NetRatel.Setup;
using Xunit;

namespace NetRatel.Tests.Setup;

public sealed class StdioMcpDoctorTests
{
    private const string SyntheticSecret = "nrt_ic_synthetic_stdio_probe_secret";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_uses_an_immutable_private_snapshot_and_only_the_offered_read_only_status_operation(bool authorized)
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-stdio-doctor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var savedPath = Path.Combine(directory, "saved.json");
        File.WriteAllText(savedPath, "original profile remains unchanged");
        using var child = new FakeProcess(Transcript(authorized));
        string? snapshotPath = null;
        var doctor = new StdioMcpDoctor(() => new("selected-mcp", []), info =>
        {
            Assert.False(info.UseShellExecute);
            Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
            Assert.Empty(info.ArgumentList);
            Assert.False(info.Environment.ContainsKey("NETRATEL_CLI_INTEGRATION_TOKEN"));
            Assert.False(info.Environment.ContainsKey("NetRatel__AgentClient__OidcAppPassword"));
            Assert.False(info.Environment.ContainsKey("NETRATEL_MCP_OUTBOUND_API_BASE_URL"));
            snapshotPath = info.Environment["NETRATEL_MCP_CONFIG"];
            var snapshot = NetRatelConfigurationService.ReadConfiguration(snapshotPath!);
            Assert.Equal("https://selected.example.test", snapshot.ApiBaseUrl);
            Assert.Equal(SyntheticSecret, snapshot.IntegrationCredential);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(snapshotPath!));
            return child;
        }, new Dictionary<string, string?> {
            ["NETRATEL_CLI_INTEGRATION_TOKEN"] = "ambient-cli-secret", ["NetRatel__AgentClient__OidcAppPassword"] = "ambient-agent-secret",
            ["NETRATEL_MCP_OUTBOUND_API_BASE_URL"] = "https://stale.example.test", ["NETRATEL_MCP_CONFIG"] = "/unapproved/config.json" });
        try
        {
            var checks = await doctor.CheckAsync(Effective(savedPath));
            Assert.Contains(checks, check => check.Name == "stdio MCP initialize" && check.Status == "completed");
            Assert.Contains(checks, check => check.Name == "permitted read-only tool" && check.Status == (authorized ? "completed" : "unauthorized"));
            Assert.DoesNotContain(SyntheticSecret, JsonSerializer.Serialize(checks));
            var sent = Encoding.UTF8.GetString(child.Written.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/call" }, sent.Select(document => document.RootElement.GetProperty("method").GetString()));
                var arguments = sent[3].RootElement.GetProperty("params");
                Assert.Equal("netratel_auth", arguments.GetProperty("name").GetString());
                Assert.Equal("status", arguments.GetProperty("arguments").GetProperty("operation").GetString());
                Assert.DoesNotContain(SyntheticSecret, Encoding.UTF8.GetString(child.Written.ToArray()));
            }
            finally { foreach (var document in sent) document.Dispose(); }
            Assert.True(child.Aborted);
            Assert.False(File.Exists(snapshotPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(snapshotPath)));
            Assert.Equal("original profile remains unchanged", File.ReadAllText(savedPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("\u001b[31mBanner contains nrt_ic_synthetic_stdio_probe_secret\n")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{}}\n")]
    public async Task Unexpected_stdout_or_reply_identity_cannot_be_treated_as_protocol_success(string invalidOutput)
    {
        using var child = new FakeProcess(invalidOutput + Transcript(true));
        var doctor = new StdioMcpDoctor(() => new("selected-mcp", []), _ => child);
        var checks = await doctor.CheckAsync(Effective());
        Assert.DoesNotContain(checks, check => check.Status == "completed");
        Assert.DoesNotContain(SyntheticSecret, JsonSerializer.Serialize(checks));
        Assert.True(child.Aborted);
    }

    [Fact]
    public async Task Caller_cancellation_aborts_the_child_and_removes_its_private_export()
    {
        using var child = new FakeProcess(new BlockingStream());
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? snapshot = null;
        var doctor = new StdioMcpDoctor(() => new("selected-mcp", []), info =>
        { snapshot = info.Environment["NETRATEL_MCP_CONFIG"]; started.SetResult(); return child; });
        var running = doctor.CheckAsync(Effective(), cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(child.Aborted);
        Assert.False(File.Exists(snapshot));
        Assert.False(Directory.Exists(Path.GetDirectoryName(snapshot)));
    }

    [Fact]
    public async Task Missing_local_archive_reports_unavailable_and_native_sibling_resolution_is_supported()
    {
        var checks = await new StdioMcpDoctor(() => null, _ => throw new InvalidOperationException("The child must not start.")).CheckAsync(Effective());
        Assert.Single(checks);
        Assert.Equal("unavailable", checks[0].Status);
        var directory = Path.Combine(Path.GetTempPath(), "netratel-stdio-resolver-" + Guid.NewGuid().ToString("N"));
        var cli = Path.Combine(directory, "cli");
        var sibling = Path.Combine(directory, "netratel-mcp-" + RuntimeInformation.RuntimeIdentifier);
        Directory.CreateDirectory(cli); Directory.CreateDirectory(sibling);
        var executable = Path.Combine(sibling, OperatingSystem.IsWindows() ? "NetRatel.Mcp.exe" : "NetRatel.Mcp");
        File.WriteAllText(executable, "resolver fixture");
        try { Assert.Equal(executable, StdioMcpDoctor.ResolveLaunch(cli)!.Command); }
        finally { Directory.Delete(directory, true); }
    }

    private static EffectiveAgentClientConfiguration Effective(string? savedPath = null) => new(
        AgentClientConfiguration.Empty with { ApiBaseUrl = "https://selected.example.test", IntegrationCredential = SyntheticSecret,
            AuthenticationMode = "IntegrationCredential", CredentialPurpose = "Api", Instance = "prod", ProfileName = "stdio-test" },
        ConfigurationSurface.StdioMcp, savedPath, "stdio-test", new Dictionary<string, ConfigurationSettingSource>());
    private static string Transcript(bool authorized) => string.Join('\n', new[] {
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = "2025-03-26", capabilities = new { tools = new { } } } }),
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, result = new { tools = new[] { new { name = "netratel_auth" } } } }),
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 3, result = new { structuredContent = new { success = authorized,
            summary = SyntheticSecret, data = new { authenticated = authorized, authMode = "integration_credential", groups = Array.Empty<string>(), roles = Array.Empty<string>() },
            error = authorized ? null : new { upstreamStatus = 403 } } } }) }) + "\n";
    private sealed class FakeProcess : IStdioMcpProbeProcess, IDisposable
    {
        public MemoryStream Written { get; } = new();
        public Stream Input => Written;
        public Stream Output { get; }
        public Stream Error { get; } = new MemoryStream(Encoding.UTF8.GetBytes(SyntheticSecret));
        public bool Aborted { get; private set; }
        public FakeProcess(string transcript) : this(new MemoryStream(Encoding.UTF8.GetBytes(transcript))) { }
        public FakeProcess(Stream output) => Output = output;
        public void Abort() => Aborted = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose() { Written.Dispose(); Output.Dispose(); Error.Dispose(); }
    }
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
