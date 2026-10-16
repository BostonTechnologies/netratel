using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using NetRatel.AgentClient;
using NetRatel.Mcp.Http;
using NetRatel.Setup;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.Setup;

public sealed class BoundedVerifierTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("structured_failure")]
    [InlineData("downstream_401")]
    [InlineData("malformed")]
    [InlineData("cancelled")]
    public async Task Temporary_verifier_suggests_one_permission_and_cleans_up_every_check_outcome(string outcome)
    {
        using var fixture = new Fixture(outcome);
        await using var session = fixture.Session();
        await session.DiscoverAsync("https://instance.example", default);
        await session.BeginPairingAsync(default, temporaryVerifier: true);
        await session.PollAsync(default);
        Assert.True(fixture.SuggestedVerifier);
        Assert.Equal(new[] { "mcp.discovery.read" }, fixture.SuggestedPermissions);
        await Assert.ThrowsAsync<AgentClientValidationException>(() => session.SaveAsync(false, default));
        if (outcome == "cancelled")
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.VerifyTransientHttpMcpAsync(cancelled.Token));
        }
        else
        {
            var checks = await session.VerifyTransientHttpMcpAsync(default);
            var operation = checks.Single(check => check.Name == "permitted read-only tool" || check.Name == "transport");
            Assert.Equal(outcome == "success", operation.Status == "completed");
            if (outcome is "structured_failure" or "downstream_401")
            {
                Assert.True(operation.TransportSucceeded);
                Assert.False(operation.OperationSucceeded);
                Assert.Equal(outcome == "downstream_401" ? "API" : "operation", operation.Layer);
                Assert.Equal(outcome == "downstream_401" ? 401 : null, operation.DownstreamStatus);
                Assert.Equal("0123456789abcdef0123456789abcdef", operation.Reference);
                Assert.NotNull(operation.NextAction);
                Assert.DoesNotContain("do-not-output", JsonSerializer.Serialize(checks));
            }
        }
        Assert.Equal(1, fixture.CancelCalls);
        Assert.False(session.CleanupPending);
        Assert.Null(session.Delivery);
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.json", SearchOption.AllDirectories));
        await session.CancelPairingAsync();
        Assert.Equal(1, fixture.CancelCalls);
    }

    [Fact]
    public async Task Disposal_revokes_and_unavailable_api_keeps_a_private_retry_record_for_next_setup()
    {
        using var fixture = new Fixture("success") { CancelUnavailable = true };
        var session = fixture.Session();
        await session.DiscoverAsync("https://instance.example", default);
        await session.BeginPairingAsync(default, temporaryVerifier: true);
        await session.PollAsync(default);
        var checks = await session.VerifyTransientHttpMcpAsync(default);
        Assert.Contains(checks, check => check.Code == "cleanup_pending" && check.Status == "pending");
        Assert.True(session.CleanupPending);
        var cleanupFile = Assert.Single(Directory.EnumerateFiles(fixture.DirectoryPath, "*.json", SearchOption.AllDirectories));
        var record = File.ReadAllText(cleanupFile);
        Assert.DoesNotContain("nrt_ic_temporary", record);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cleanupFile));
        await session.DisposeAsync();
        await using (var blocked = fixture.Session())
        {
            await blocked.DiscoverAsync("https://instance.example", default);
            Assert.True(blocked.CleanupPending);
            await Assert.ThrowsAsync<AgentClientValidationException>(() => blocked.BeginPairingAsync(default, temporaryVerifier: true));
            Assert.Null(blocked.Pairing);
        }
        fixture.CancelUnavailable = false;
        await using var resumed = fixture.Session();
        await resumed.DiscoverAsync("https://instance.example", default);
        Assert.False(resumed.CleanupPending);
        Assert.False(File.Exists(cleanupFile));
        await resumed.BeginPairingAsync(default, temporaryVerifier: true);
        await resumed.PollAsync(default);
        var priorCalls = fixture.CancelCalls;
        await resumed.DisposeAsync();
        Assert.Equal(priorCalls + 1, fixture.CancelCalls);
    }

    [Fact]
    public async Task Cancelled_delivery_poll_cleans_up_the_owned_attempt_before_disposal()
    {
        using var fixture = new Fixture("success");
        await using var session = fixture.Session();
        await session.DiscoverAsync("https://instance.example", default);
        await session.BeginPairingAsync(default, temporaryVerifier: true);
        await session.PollAsync(default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PollAsync(cancelled.Token));
        Assert.Equal(1, fixture.CancelCalls);
        Assert.Null(session.Pairing);
        Assert.Null(session.Delivery);
    }

    [Fact]
    public async Task Legacy_capabilities_cannot_silently_issue_an_unbounded_setup_verifier()
    {
        using var fixture = new Fixture("success") { AdvertiseVerifier = false };
        await using var session = fixture.Session();
        await session.DiscoverAsync("https://instance.example", default);
        await Assert.ThrowsAsync<AgentClientValidationException>(() => session.BeginPairingAsync(default, temporaryVerifier: true));
        Assert.Null(session.Pairing);
        Assert.False(fixture.SuggestedVerifier);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Audit_preserves_legacy_protocol_outcome_and_records_structured_operation_outcome(bool protocol, bool operation, bool overall)
    {
        var content = JsonSerializer.SerializeToElement(new { success = operation, status = operation ? "completed" : "failed" });
        var outcome = McpToolInvocationAudit.DescribeOutcome(new CallToolResult { IsError = !protocol, StructuredContent = content });
        Assert.True(outcome.TransportSucceeded);
        Assert.True(outcome.ProtocolSucceeded);
        Assert.Equal(protocol && operation, outcome.OperationSucceeded);
        Assert.Equal(overall, outcome.OverallSucceeded);
        Assert.Equal(operation ? "completed" : "failed", outcome.ResultStatus);
    }

    [Fact]
    public void Audit_leaves_unstructured_operation_outcome_unknown()
    {
        var outcome = McpToolInvocationAudit.DescribeOutcome(new CallToolResult { IsError = false });
        Assert.True(outcome.ProtocolSucceeded);
        Assert.Null(outcome.OperationSucceeded);
        Assert.Null(outcome.OverallSucceeded);
    }

    private sealed class Fixture(string outcome) : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "netratel-bounded-verifier-" + Guid.NewGuid().ToString("N"));
        public int CancelCalls { get; private set; }
        public bool CancelUnavailable { get; set; }
        public bool AdvertiseVerifier { get; set; } = true;
        public bool SuggestedVerifier { get; private set; }
        public string[] SuggestedPermissions { get; private set; } = [];
        public SetupSession Session() => new(ConfigurationSurface.HttpMcpClient, Path.Combine(DirectoryPath, "verifier.json"), "host-verification", new Handler(Respond), _ => null);
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities")) return Json(new IntegrationPairingCapabilities(1, "https://instance.example", true,
                "https://instance.example/account/integration-credentials", "", "", "", "", "https://instance.example/mcp")
                { ConfigurationCapabilities = AdvertiseVerifier ? [IntegrationPairingProtocol.TemporaryVerifierCapability] : [] });
            if (route.EndsWith("start"))
            {
                using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                SuggestedVerifier = input.RootElement.GetProperty("temporaryVerifier").GetBoolean();
                SuggestedPermissions = input.RootElement.GetProperty("requestedPermissions").EnumerateArray().Select(x => x.GetString()!).ToArray();
                return Json(new IntegrationPairingStartResponse("request", "ABCD-EFGH", "private-cancel-capability",
                    "https://instance.example/account/integration-credentials", "https://instance.example", DateTimeOffset.UtcNow.AddMinutes(1), 5));
            }
            if (route.EndsWith("poll")) return Json(new IntegrationPairingPollResponse("issued", 5,
                new("credential", "nrt_ic_temporary", "verifier", PairingCredentialPurpose.HttpMcp, "https://instance.example/mcp",
                    DateTimeOffset.UtcNow.AddMinutes(10), [], ["mcp.discovery.read"])));
            if (route.EndsWith("cancel"))
            {
                CancelCalls++;
                return CancelUnavailable ? new(HttpStatusCode.ServiceUnavailable) : Json(new { status = "cancelled" });
            }
            ct.ThrowIfCancellationRequested();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return body.RootElement.GetProperty("method").GetString() switch
            {
                "initialize" => Json(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = "2025-03-26" } }),
                "notifications/initialized" => new(HttpStatusCode.Accepted),
                "tools/list" => Json(new { jsonrpc = "2.0", id = 2, result = new { tools = new[] { new { name = "netratel_auth" } } } }),
                "tools/call" when outcome == "malformed" => new(HttpStatusCode.OK) { Content = new StringContent("invalid JSON") },
                "tools/call" => Json(new { jsonrpc = "2.0", id = 3, result = new { isError = false, structuredContent = new
                { success = outcome == "success", status = outcome == "success" ? "completed" : "failed", correlationId = "0123456789abcdef0123456789abcdef",
                    summary = "do-not-output raw private payload", error = new { code = outcome == "downstream_401" ? "upstream_error" : "unknown_operation_error", upstreamStatus = outcome == "downstream_401" ? (int?)401 : null },
                    data = new { authenticated = true, authMode = "IntegrationCredential", groups = Array.Empty<string>(), roles = Array.Empty<string>() } } } }),
                _ => throw new InvalidOperationException("Unexpected method")
            };
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
