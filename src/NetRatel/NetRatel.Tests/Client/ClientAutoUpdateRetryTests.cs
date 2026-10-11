using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Services;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Client.Service.Updates;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientAutoUpdateRetryTests
{
    [Fact]
    public async Task SameReleaseRecoversAfterRoute404WithoutDuplicateHandover()
    {
        await using var rig = new Rig();
        rig.ClaimFailure = HttpStatusCode.NotFound;
        rig.Offer();
        var diagnostic = await rig.Outcome("retry-on-fresh-offer");
        Assert.Contains("current=1.2.3 target=1.2.4 runtime=linux-x64", diagnostic);
        Assert.Contains($"release={rig.Release.ReleaseId}", diagnostic);
        Assert.Contains("stage=claim", diagnostic);
        Assert.Contains("code=http_404", diagnostic);
        Assert.Contains("nextDelaySeconds=30", diagnostic);
        rig.ClaimFailure = null;
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Offer();
        await rig.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 20; i++) rig.Offer();
        // Handover fences all releases, including a newer offer.
        rig.ReplaceRelease();
        rig.Offer();
        await rig.Coordinator.DisposeAsync();
        Assert.Equal(2, rig.Claims);
        Assert.Equal(1, rig.Downloads);
        Assert.Equal(1, rig.Launches);
        rig.AssertSafeDiagnostics();
    }

    [Fact]
    public async Task RepeatedOffersRespectCappedMonotonicCooldownWithoutAnExhaustedBudget()
    {
        await using var rig = new Rig();
        rig.ClaimFailure = HttpStatusCode.NotFound;
        rig.Offer();
        foreach (var delay in new[] { 30, 60, 120, 240, 300, 300 })
        {
            var diagnostic = await rig.Outcome("retry-on-fresh-offer");
            Assert.Contains($"nextDelaySeconds={delay}", diagnostic);
            var claims = rig.Claims;
            rig.Clock.AdjustUtc(TimeSpan.FromDays(1));
            for (var i = 0; i < 100; i++) rig.Offer();
            rig.Clock.Advance(TimeSpan.FromSeconds(delay - 1));
            for (var i = 0; i < 100; i++) rig.Offer();
            Assert.Equal(claims, rig.Claims);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            rig.Offer();
        }
        await rig.Outcome("retry-on-fresh-offer");
        rig.ClaimFailure = null;
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Offer();
        await rig.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(8, rig.Claims);
        Assert.Equal(1, rig.Launches);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("Downloading")]
    [InlineData("Staged")]
    [InlineData("Activating")]
    public async Task LostClaimOrProgressResponseResumesTheProtocolWithoutRestaging(string stage)
    {
        await using var rig = new Rig();
        rig.DropClaimResponse = stage == "claim";
        rig.ReportFailure = stage == "claim" ? null : stage;
        rig.FailAfterCommit = true;
        rig.Offer();
        var diagnostic = await rig.Outcome("retry-on-fresh-offer");
        Assert.Contains(stage == "claim" ? "stage=claim" : $"stage=state/result-reporting({stage})", diagnostic);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Offer();
        await rig.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Coordinator.DisposeAsync();
        Assert.Equal(stage == "claim" ? 2 : 1, rig.Claims);
        Assert.Single(rig.Nonces.Distinct());
        Assert.Equal(1, rig.Downloads);
        Assert.Equal(1, rig.Launches);
        Assert.DoesNotContain("FailedPreActivation", rig.Reports);
        Assert.Equal(ClientUpdateAttemptState.Activating, rig.ServerState);
        rig.AssertSafeDiagnostics();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFailureReportPreservesTheOriginalErrorAndDoesNotReplayInvalidStaging(bool invalidArtifact)
    {
        await using var rig = new Rig();
        rig.InvalidArtifact = invalidArtifact;
        rig.DownloadFailure = invalidArtifact ? null : HttpStatusCode.ServiceUnavailable;
        rig.ReportFailure = "FailedPreActivation";
        rig.FailAfterCommit = true;
        rig.Offer();
        await rig.Outcome("failure-report-deferred");
        var diagnostic = await rig.Outcome("retry-on-fresh-offer");
        Assert.Contains("stage=download/verification", diagnostic);
        Assert.Contains($"attempt={rig.AttemptId}", diagnostic);
        Assert.Contains(invalidArtifact ? "code=artifact_checksum_mismatch" : "code=http_503", diagnostic);
        rig.DownloadFailure = null;
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Offer();
        await rig.Outcome(invalidArtifact ? "terminal" : "completed");
        for (var i = 0; i < 20; i++) rig.Offer();
        await rig.Coordinator.DisposeAsync();
        Assert.Equal(invalidArtifact ? 1 : 2, rig.Claims);
        Assert.Equal(invalidArtifact ? 1 : 2, rig.Downloads);
        Assert.Equal(invalidArtifact ? 0 : 1, rig.Launches);
        rig.AssertSafeDiagnostics();
    }

    [Theory]
    [InlineData("withdrawn")]
    [InlineData("disabled")]
    [InlineData("suspended")]
    [InlineData("suppressed")]
    [InlineData("newer")]
    [InlineData("dispose")]
    public async Task ActiveDuplicatesAreCoalescedAndEligibilityChangesCancelStaleWork(string change)
    {
        await using var rig = new Rig();
        rig.HoldDownload = true;
        rig.Offer();
        await rig.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100; i++) rig.Offer();
        Assert.Equal(1, rig.Claims);
        Assert.Equal(1, rig.Downloads);
        rig.HoldDownload = false;
        switch (change)
        {
            case "withdrawn": rig.Coordinator.OnAcknowledgement(null, null, null); break;
            case "disabled": rig.Coordinator.OnAcknowledgement(rig.Release, new ClientUpdatePolicy { AutoUpdateEnabled = false }, null); break;
            case "suspended": rig.Coordinator.OnAcknowledgement(rig.Release, new ClientUpdatePolicy { AutoUpdateEnabled = true, Suspended = true }, null); break;
            case "suppressed": rig.Coordinator.OnAcknowledgement(rig.Release, new ClientUpdatePolicy { AutoUpdateEnabled = true, SuppressedReleaseId = rig.Release.ReleaseId }, null); break;
            case "newer": rig.ReplaceRelease(); rig.Offer(); break;
            case "dispose": await rig.Coordinator.DisposeAsync(); break;
        }
        await rig.DownloadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change == "newer") await rig.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Coordinator.DisposeAsync();
        rig.Clock.Advance(TimeSpan.FromDays(1));
        rig.Offer();
        Assert.Equal(change == "newer" ? 2 : 1, rig.Claims);
        Assert.Equal(change == "newer" ? 1 : 0, rig.Launches);
        Assert.Equal(0, rig.Clock.ActiveTimerCount);
    }

    [Fact]
    public async Task AQueuedReplacementCanBeWithdrawnAndReofferedWithoutStrandingWork()
    {
        await using var rig = new Rig();
        rig.HoldDownload = true;
        rig.DelayCancellation = true;
        rig.Offer();
        await rig.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.HoldDownload = false;
        rig.ReplaceRelease();
        rig.Offer();
        rig.Coordinator.OnAcknowledgement(null, null, null);
        rig.Offer();
        rig.AllowCancellation.TrySetResult();
        await rig.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, rig.Claims);
        Assert.Equal(1, rig.Launches);
    }

    [Fact]
    public async Task EligibilityWithdrawalRetainsAStagedClaimAndItsNonce()
    {
        await using var rig = new Rig();
        rig.ReportFailure = "Staged";
        rig.FailAfterCommit = true;
        rig.Offer();
        await rig.Outcome("retry-on-fresh-offer");
        rig.Coordinator.OnAcknowledgement(null, null, null);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Offer();
        await rig.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, rig.Claims);
        Assert.Equal(1, rig.Downloads);
        Assert.Equal(1, rig.Launches);
    }

    [Fact]
    public async Task PolicyRejectionIsTerminalAndShutdownDoesNotLeaveCooldownWork()
    {
        await using var rig = new Rig();
        rig.ClaimFailure = HttpStatusCode.Forbidden;
        rig.Offer();
        await rig.Outcome("terminal");
        rig.Clock.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 20; i++) rig.Offer();
        await rig.Coordinator.DisposeAsync();
        Assert.Equal(1, rig.Claims);
        Assert.Equal(0, rig.Launches);
        Assert.Equal(0, rig.Clock.ActiveTimerCount);
    }

    private sealed class Rig : HttpMessageHandler, IAgentTokenService, IAsyncDisposable
    {
        internal readonly GatewayPresenceTestClock Clock = new();
        internal readonly Channel<string> Logs = Channel.CreateUnbounded<string>();
        internal readonly ConcurrentQueue<string> Diagnostics = new();
        internal readonly ConcurrentQueue<string> Nonces = new();
        internal readonly ConcurrentQueue<string> Reports = new();
        internal readonly TaskCompletionSource Launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DownloadCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AllowCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Guid AttemptId = Guid.NewGuid();
        internal ClientUpdateOffer Release;
        internal readonly AkkaClientAutoUpdateCoordinator Coordinator;
        internal HttpStatusCode? ClaimFailure, DownloadFailure;
        internal string? ReportFailure;
        internal bool DropClaimResponse, FailAfterCommit, InvalidArtifact, HoldDownload, DelayCancellation;
        internal int Claims, Downloads, Launches;
        internal ClientUpdateAttemptState ServerState = ClientUpdateAttemptState.Claimed;
        private string? _serverNonce;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "netratel-update-retry-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client;
        private byte[] _package;

        internal Rig()
        {
            (_package, Release) = CreateRelease("1.2.4");
            _client = new HttpClient(this) { BaseAddress = new Uri("https://web.example.test") };
            Coordinator = new AkkaClientAutoUpdateCoordinator(new ClientOptions { ApiBaseUrl = _client.BaseAddress.ToString(),
                AutoUpdate = new AutoUpdateOptions { StateDirectory = _root, RuntimeId = "linux-x64" } }, "1.2.3", this, _client,
                Clock, () => { Interlocked.Increment(ref Launches); Launched.TrySetResult(); }, true, log =>
                { Diagnostics.Enqueue(log); Logs.Writer.TryWrite(log); });
        }

        internal void ReplaceRelease()
        {
            (_package, Release) = CreateRelease("1.2.5");
            AttemptId = Guid.NewGuid();
            _serverNonce = null;
            ServerState = ClientUpdateAttemptState.Claimed;
        }

        internal void Offer() => Coordinator.OnAcknowledgement(Release, new ClientUpdatePolicy { AutoUpdateEnabled = true }, null);
        internal async Task<string> Outcome(string decision)
        {
            while (true)
            {
                var log = await Logs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                if (log.Contains($"retry={decision}", StringComparison.Ordinal)) return log;
            }
        }

        internal void AssertSafeDiagnostics()
        {
            foreach (var log in Diagnostics)
            {
                Assert.DoesNotContain("secret-test-token", log);
                Assert.DoesNotContain("signed-query-secret", log);
                foreach (var nonce in Nonces) Assert.DoesNotContain(nonce, log);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-test-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Claims);
                using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(ct), cancellationToken: ct);
                var nonce = body.RootElement.GetProperty("admissionNonce").GetString()!;
                Nonces.Enqueue(nonce);
                if (ClaimFailure is { } status) return Error(status);
                if (ServerState == ClientUpdateAttemptState.FailedPreActivation)
                {
                    _serverNonce = nonce;
                    ServerState = ClientUpdateAttemptState.Claimed;
                }
                else if (_serverNonce is null) _serverNonce = nonce;
                else Assert.Equal(_serverNonce, nonce);
                if (DropClaimResponse)
                {
                    DropClaimResponse = false;
                    throw new HttpRequestException("Dropped response secret-test-token ?signature=signed-query-secret");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = JsonContent.Create(new { AttemptId, releaseId = Release.ReleaseId, admissionNonce = nonce, targetVersion = Release.Version }) };
            }
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref Downloads);
                var hold = HoldDownload;
                DownloadStarted.TrySetResult();
                if (hold)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    catch (OperationCanceledException)
                    {
                        DownloadCancelled.TrySetResult();
                        if (DelayCancellation) await AllowCancellation.Task;
                        throw;
                    }
                }
                if (DownloadFailure is { } status) return Error(status);
                var bytes = InvalidArtifact ? new byte[_package.Length] : _package;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            using var result = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(ct), cancellationToken: ct);
            var state = result.RootElement.GetProperty("state").GetString()!;
            Reports.Enqueue(state);
            var next = Enum.Parse<ClientUpdateAttemptState>(state);
            Assert.True(ClientUpdateAuthorityService.IsAllowedTransition(ServerState, next), $"Invalid protocol transition {ServerState} -> {next}");
            if (ReportFailure == state)
            {
                ReportFailure = null;
                if (FailAfterCommit) ServerState = next;
                return Error(HttpStatusCode.ServiceUnavailable);
            }
            ServerState = next;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        private static HttpResponseMessage Error(HttpStatusCode status) => new(status)
            { Content = new StringContent("secret-test-token ?signature=signed-query-secret") };
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult(("secret-test-token", Clock.GetUtcNow().AddHours(1)));
        public async ValueTask DisposeAsync()
        {
            AllowCancellation.TrySetResult();
            await Coordinator.DisposeAsync();
            _client.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static (byte[] Package, ClientUpdateOffer Offer) CreateRelease(string version)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("netratel-client-manifest.json").Open()))
                    writer.Write(JsonSerializer.Serialize(new { schema = "netratel.client.manifest.v1", product = "NetRatel.Client",
                        version, runtimeId = "linux-x64", commitSha = new string('a', 40), executable = "NetRatel.Client" }));
                using var executable = zip.CreateEntry("NetRatel.Client").Open();
                executable.WriteByte(1);
            }
            var package = stream.ToArray();
            return (package, new ClientUpdateOffer { ReleaseId = Guid.NewGuid().ToString("D"), RuntimeId = "linux-x64", Version = version,
                DownloadPath = $"/api/v1/client-artifacts/linux-x64/{version}/raw-download?signature=signed-query-secret", SizeBytes = package.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant() });
        }
    }
}
