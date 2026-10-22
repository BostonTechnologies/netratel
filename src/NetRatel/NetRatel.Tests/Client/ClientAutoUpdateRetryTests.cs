using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
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
    public async Task TokenAcquisitionFailureIdentifiesAuthenticationBeforeAnyUpdateRequest()
    {
        await using var rig = new Rig();
        rig.TokenFailure = HttpStatusCode.ServiceUnavailable;
        rig.Offer();
        var diagnostic = await rig.Outcome("retry-on-fresh-offer");
        Assert.Contains("stage=agent-authentication endpoint=agent-token code=http_503", diagnostic);
        Assert.Equal(0, rig.Claims);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Offer();
        await rig.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, rig.Claims);
        Assert.Equal(1, rig.Launches);
        rig.AssertSafeDiagnostics();
    }

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
        Assert.Equal(invalidArtifact ? 1 : 2, rig.Nonces.Distinct().Count());
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

    [Fact]
    public async Task RejectedNativeLaunchCanResumeTheSameStagedAttemptWithoutASecondClaimOrDownload()
    {
        await using var rig = new Rig();
        rig.LaunchFailure = new NativeUpdaterLaunchException("linux_updater_start_rejected", retrySafe: true);
        rig.Offer();
        var diagnostic = await rig.Outcome("retry-on-fresh-offer");
        Assert.Contains("stage=activation-handover", diagnostic);
        Assert.Contains("code=linux_updater_start_rejected", diagnostic);
        Assert.Equal(1, rig.Claims);
        Assert.Equal(1, rig.Downloads);
        Assert.Equal(0, rig.Launches);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Offer();
        await rig.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Coordinator.DisposeAsync();
        Assert.Equal(1, rig.Claims);
        Assert.Equal(1, rig.Downloads);
        Assert.Equal(1, rig.Launches);
        Assert.Single(rig.Nonces.Distinct());
    }

    [Fact]
    public async Task UncertainNativeLaunchRemainsFencedAcrossRepeatedOffers()
    {
        await using var rig = new Rig();
        rig.LaunchFailure = new NativeUpdaterLaunchException("linux_updater_acknowledgement_timeout", retrySafe: false);
        rig.Offer();
        await rig.Outcome("handover-uncertain-no-relaunch");
        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 20; i++) rig.Offer();
        await rig.Coordinator.DisposeAsync();
        Assert.Equal(1, rig.Claims);
        Assert.Equal(1, rig.Downloads);
        Assert.Equal(0, rig.Launches);
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
        internal HttpStatusCode? ClaimFailure, DownloadFailure, TokenFailure;
        internal string? ReportFailure;
        internal NativeUpdaterLaunchException? LaunchFailure;
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
                Clock, () =>
                {
                    if (LaunchFailure is { } failure) { LaunchFailure = null; throw failure; }
                    Interlocked.Increment(ref Launches);
                    Launched.TrySetResult();
                }, true, log =>
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
                    var downloadHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    try { await downloadHeld.Task.WaitAsync(ct); }
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
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            if (TokenFailure is { } status)
            {
                TokenFailure = null;
                throw new HttpRequestException("secret-test-token", null, status);
            }
            return Task.FromResult(("secret-test-token", Clock.GetUtcNow().AddHours(1)));
        }
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

public sealed class ClientUpdateResultDeliveryTests
{
    [Fact]
    public async Task OversizedNativeResultIsRetainedAndReportedOnceWithoutAFalseDeliveryReceipt()
    {
        await using var rig = new DeliveryRig();
        File.WriteAllText(rig.ResultPath, new string('x', 64 * 1024 + 1));
        var logs = new List<string>();
        rig.Worker = rig.CreateWorker(logs.Add);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.True(rig.Worker.HasUnresolvedResult);
        Assert.Equal(0, rig.Requests);
        Assert.Single(logs);
        Assert.Contains("delivery=local_state_unreadable reconciliation=required", logs[0]);
        Assert.True(File.Exists(rig.ResultPath));
        Assert.False(File.Exists(Path.Combine(rig.Root, "result-delivery.json")));
    }

    [Fact]
    public async Task PlainRoute404SurvivesRestartAndRecoversWithoutChangingTheInstallationOutcome()
    {
        await using var rig = new DeliveryRig();
        rig.Response = () => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("route unavailable") };
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal("retry_wait", rig.Status("deliveryState"));
        Assert.Equal("RolledBack", rig.Status("installationState"));
        Assert.True(rig.Worker.HasUnresolvedResult);
        var original = File.ReadAllText(rig.ResultPath);
        File.Delete(rig.SuspensionPath); // An acknowledged Resume removes the installation suspension.
        rig.Worker = rig.CreateWorker();
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(1, rig.Requests);
        Assert.False(File.Exists(rig.SuspensionPath));
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Response = () => rig.Reply(HttpStatusCode.Accepted, "duplicate", "report_duplicate", "RolledBack", false);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(2, rig.Requests);
        Assert.Equal("acknowledged", rig.Status("deliveryState"));
        Assert.False(File.Exists(rig.ResultPath));
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(rig.Root, "*.consumed.json"))));
        Assert.False(File.Exists(rig.SuspensionPath));
        Assert.False(rig.Worker.HasUnresolvedResult);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found", "attempt_not_found", "permanent_rejected", null)]
    [InlineData(HttpStatusCode.Conflict, "conflict", "accepted_authority_conflict", "conflict", "Accepted")]
    public async Task StructuredPermanentAndConflictOutcomesRetainEvidenceWithoutAnotherRequest(
        HttpStatusCode status, string outcome, string code, string expected, string? serverState)
    {
        await using var rig = new DeliveryRig();
        rig.Response = () => rig.Reply(status, outcome, code, serverState);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(expected, rig.Status("deliveryState"));
        Assert.Equal("required", rig.Status("reconciliationState"));
        Assert.Equal(serverState, rig.Status("serverState"));
        Assert.Equal("RolledBack", rig.Status("installationState"));
        var original = File.ReadAllText(rig.ResultPath);
        rig.Worker = rig.CreateWorker();
        rig.Clock.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 10; i++) await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(original, File.ReadAllText(rig.ResultPath));
        Assert.True(rig.Worker.HasUnresolvedResult);
    }

    [Fact]
    public async Task RetryAfterIsBoundedAndRejectedTokenGetsOneRefreshWithinTheRequestBudget()
    {
        await using var rig = new DeliveryRig();
        rig.Response = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(1));
            return response;
        };
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromSeconds(299));
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(1, rig.Requests);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Response = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(3, rig.Requests);
        Assert.Equal(1, rig.Invalidations);
        Assert.Equal("retry_wait", rig.Status("deliveryState"));
        Assert.All(rig.RequestNonces, nonce => Assert.Equal("protected-test-nonce", nonce));
    }

    [Fact]
    public async Task ResumedAttemptIsNotSuspendedOnItsFirstLocalDeliveryAndChangedResultCannotReplacePendingEvidence()
    {
        await using var rig = new DeliveryRig();
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new { revision = 5, resumeAttemptId = rig.AttemptId, resumeRevision = 5 });
        rig.Response = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.False(File.Exists(rig.SuspensionPath));
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            attemptId = Guid.NewGuid(), releaseId = Guid.NewGuid(), state = "FailedPreActivation" });
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal("identity_conflict", rig.Status("deliveryState"));
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Root, "result-delivery.json")));
        Assert.Equal(rig.AttemptId, record.RootElement.GetProperty("attemptId").GetGuid());
        Assert.Equal("RolledBack", record.RootElement.GetProperty("originalResult").GetProperty("state").GetString());
        Assert.Equal(1, rig.Requests);
        Assert.True(rig.Worker.HasUnresolvedResult);
    }

    [Theory]
    [InlineData("", "acknowledged")]
    [InlineData("<html>proxy response</html>", "unclassified_response")]
    public async Task LegacyEmptyAcceptedAcknowledgesButMalformedProxyContentRetainsEvidence(string content, string expected)
    {
        await using var rig = new DeliveryRig();
        rig.Response = () => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(content) };
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(expected, rig.Status("deliveryState"));
        Assert.Equal(expected != "acknowledged", File.Exists(rig.ResultPath));
    }

    [Fact]
    public async Task WindowsPowerShellUtf8BomResultRecoversAfterMalformedDataAndArchivesItsExactBytes()
    {
        await using var rig = new DeliveryRig();
        var nativeBytes = File.ReadAllBytes(rig.ResultPath);
        File.WriteAllText(rig.ResultPath, "{");
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(0, rig.Requests);
        Assert.True(rig.Worker.HasUnresolvedResult);
        var original = new byte[nativeBytes.Length + 3];
        original[0] = 0xef;
        original[1] = 0xbb;
        original[2] = 0xbf;
        nativeBytes.CopyTo(original, 3);
        File.WriteAllBytes(rig.ResultPath, original);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(1, rig.Requests);
        Assert.Equal("acknowledged", rig.Status("deliveryState"));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(rig.Root, "*.consumed.json"))));
        Assert.False(rig.Worker.HasUnresolvedResult);
    }

    [Fact]
    public async Task MalformedAcknowledgementMetadataFencesFutureWorkWithoutLosingEvidence()
    {
        await using var rig = new DeliveryRig();
        File.Delete(rig.ResultPath);
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "result-delivery.json"), new { deliveryState = "acknowledged" });
        Assert.True(rig.Worker.HasUnresolvedResult);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(0, rig.Requests);
        Assert.Equal("acknowledged", rig.Status("deliveryState"));
        Assert.True(rig.Worker.HasUnresolvedResult);
    }

    [Fact]
    public async Task ExplicitResumeAllowsFutureWorkAfterArchivingTheConflictingOutcomeAndReceipt()
    {
        await using var rig = new DeliveryRig();
        rig.Response = () => rig.Reply(HttpStatusCode.Conflict, "conflict", "accepted_authority_conflict", "Accepted");
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.True(rig.Worker.HasUnresolvedResult);
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new { revision = 5, resumeAttemptId = rig.AttemptId, resumeRevision = 4 });
        Assert.True(rig.Worker.HasUnresolvedResult);
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new { revision = 6, resumeAttemptId = rig.AttemptId, resumeRevision = 6 });
        Assert.False(rig.Worker.HasUnresolvedResult);
        Assert.False(File.Exists(rig.ResultPath));
        using var outcome = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(rig.Root, "*.unresolved.json"))));
        Assert.Equal("RolledBack", outcome.RootElement.GetProperty("state").GetString());
        using var receipt = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(rig.Root, "*.delivery.json"))));
        Assert.Equal("conflict", receipt.RootElement.GetProperty("deliveryState").GetString());
        Assert.Equal("required", receipt.RootElement.GetProperty("reconciliationState").GetString());
        Assert.Equal("Accepted", receipt.RootElement.GetProperty("serverState").GetString());
        rig.Worker = rig.CreateWorker();
        Assert.False(rig.Worker.HasUnresolvedResult);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(1, rig.Requests);
    }

    [Fact]
    public async Task FreshOperatorResumePreservesBothIdentityConflictResultsAndAllowsFutureDeliveryAfterRestart()
    {
        await using var rig = new DeliveryRig();
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new { revision = 5 });
        rig.Response = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        var suspension = File.ReadAllBytes(rig.SuspensionPath);
        var replacedAttempt = Guid.NewGuid();
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            attemptId = replacedAttempt, releaseId = Guid.NewGuid(), state = "Accepted", toVersion = "1.2.4" });
        var resultBytes = File.ReadAllBytes(rig.ResultPath);
        byte[] replacement = [0xef, 0xbb, 0xbf, .. resultBytes];
        File.WriteAllBytes(rig.ResultPath, replacement);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal("identity_conflict", rig.Status("deliveryState"));
        Assert.True(rig.Worker.HasUnresolvedResult);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        using (var api = new HttpClient(rig, false) { BaseAddress = new Uri("https://api.example.test") })
        {
            await using var coordinator = new AkkaClientAutoUpdateCoordinator(new ClientOptions
            {
                ApiBaseUrl = api.BaseAddress.ToString(),
                AutoUpdate = new AutoUpdateOptions { StateDirectory = rig.Root, RuntimeId = "linux-x64" }
            }, "1.2.4", rig, api, rig.Clock, () => throw new InvalidOperationException("Unexpected native launch"), true);
            coordinator.OnAcknowledgement(null, new ClientUpdatePolicy
            {
                Revision = 6, ResumeRequested = true, Suspended = false, AutoUpdateEnabled = true,
                ResumedAtUtc = Timestamp.FromDateTimeOffset(rig.Clock.GetUtcNow())
            }, null);
            using var policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Root, "policy.json")));
            Assert.Equal(rig.AttemptId, policy.RootElement.GetProperty("resumeAttemptId").GetGuid());
            Assert.NotEqual(replacedAttempt, policy.RootElement.GetProperty("resumeAttemptId").GetGuid());
        }
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);

        Assert.Equal("archived_unresolved", rig.Status("deliveryState"));
        Assert.Equal("required", rig.Status("reconciliationState"));
        Assert.Equal("RolledBack", rig.Status("installationState"));
        Assert.False(File.Exists(rig.ResultPath));
        Assert.False(rig.Worker.HasUnresolvedResult);
        Assert.Equal(replacement, File.ReadAllBytes(Assert.Single(Directory.GetFiles(rig.Root, "*.replacement.json"))));
        Assert.Equal(suspension, File.ReadAllBytes(Assert.Single(Directory.GetFiles(rig.Root, "*.suspension.json"))));
        using (var receipt = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(rig.Root, "*.recovery.json")))))
        {
            var original = receipt.RootElement.GetProperty("originalDelivery");
            Assert.Equal(rig.AttemptId, original.GetProperty("attemptId").GetGuid());
            Assert.Equal("RolledBack", original.GetProperty("originalResult").GetProperty("state").GetString());
            Assert.Equal("identity_conflict", original.GetProperty("deliveryState").GetString());
            Assert.Equal(6, receipt.RootElement.GetProperty("resumeRevision").GetInt64());
            Assert.Equal("required", receipt.RootElement.GetProperty("reconciliationState").GetString());
        }
        rig.Worker = rig.CreateWorker();
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.False(rig.Worker.HasUnresolvedResult);
        Assert.Equal(1, rig.Requests);

        // Recovery releases future work; it never relabels either retained
        // installation result or reports the replacement's Accepted claim.
        rig.ExpectedAttemptId = Guid.NewGuid();
        rig.ExpectedState = "FailedPreActivation";
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            attemptId = rig.ExpectedAttemptId, releaseId = rig.ReleaseId, state = rig.ExpectedState });
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "request.json"), new
        {
            attemptId = rig.ExpectedAttemptId, releaseId = rig.ReleaseId, admissionNonce = "new-protected-test-nonce"
        });
        rig.Response = () => rig.Reply(HttpStatusCode.Accepted, "applied", "report_applied", rig.ExpectedState, suspended: false);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(2, rig.Requests);
        Assert.Equal("acknowledged", rig.Status("deliveryState"));
        Assert.Equal("new-protected-test-nonce", rig.RequestNonces.Last());
        Assert.Single(Directory.GetFiles(rig.Root, "*.recovery.json"));
    }

    [Theory]
    [InlineData("old-revision")]
    [InlineData("wrong-attempt")]
    [InlineData("old-time")]
    [InlineData("equal-time")]
    [InlineData("missing-time")]
    [InlineData("changed-result")]
    [InlineData("changed-archive")]
    public async Task IdentityConflictRequiresFreshBoundResumeAndUnchangedArchiveEvidence(string boundary)
    {
        await using var rig = new DeliveryRig();
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new
        {
            revision = 5, resumeAttemptId = rig.AttemptId, resumeRevision = 5, resumedAtUtc = rig.Clock.GetUtcNow()
        });
        rig.Response = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            attemptId = Guid.NewGuid(), releaseId = Guid.NewGuid(), state = "FailedPreActivation" });
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        var detectedAt = rig.Clock.GetUtcNow();
        Assert.Equal("identity_conflict", rig.Status("deliveryState"));
        Assert.True(rig.Worker.HasUnresolvedResult);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new
        {
            revision = 6,
            resumeAttemptId = boundary == "wrong-attempt" ? Guid.NewGuid() : rig.AttemptId,
            resumeRevision = boundary == "old-revision" ? 5 : 6,
            resumedAtUtc = boundary == "missing-time" ? (DateTimeOffset?)null :
                boundary == "old-time" ? detectedAt.AddSeconds(-1) :
                boundary == "equal-time" ? detectedAt : rig.Clock.GetUtcNow()
        });
        if (boundary == "changed-result")
            ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
                attemptId = Guid.NewGuid(), releaseId = Guid.NewGuid(), state = "FailedPreActivation" });
        var retainedResult = File.ReadAllBytes(rig.ResultPath);
        string? changedArchive = null;
        if (boundary == "changed-archive")
        {
            changedArchive = Assert.Single(Directory.GetFiles(rig.Root, "*.replacement.json"));
            File.WriteAllText(changedArchive, "existing archive must not be overwritten");
        }
        rig.Worker = rig.CreateWorker();
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);

        Assert.Equal("identity_conflict", rig.Status("deliveryState"));
        Assert.True(rig.Worker.HasUnresolvedResult);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(retainedResult, File.ReadAllBytes(rig.ResultPath));
        Assert.Empty(Directory.GetFiles(rig.Root, "*.recovery.json"));
        if (changedArchive is not null)
            Assert.Equal("existing archive must not be overwritten", File.ReadAllText(changedArchive));
    }

    [Fact]
    public async Task DelayedOperatorResumeKeepsItsAuthoritativeTimeAndCannotRecoverANewerIdentityConflict()
    {
        await using var rig = new DeliveryRig();
        var operatorActedAt = rig.Clock.GetUtcNow();
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new { revision = 5 });
        rig.Response = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            attemptId = Guid.NewGuid(), releaseId = Guid.NewGuid(), state = "FailedPreActivation" });
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        using (var api = new HttpClient(rig, false) { BaseAddress = new Uri("https://api.example.test") })
        {
            await using var coordinator = new AkkaClientAutoUpdateCoordinator(new ClientOptions
            {
                ApiBaseUrl = api.BaseAddress.ToString(),
                AutoUpdate = new AutoUpdateOptions { StateDirectory = rig.Root, RuntimeId = "linux-x64" }
            }, "1.2.4", rig, api, rig.Clock, () => throw new InvalidOperationException("Unexpected native launch"), true);
            coordinator.OnAcknowledgement(null, new ClientUpdatePolicy
            {
                Revision = 6, ResumeRequested = true, Suspended = false, AutoUpdateEnabled = true,
                ResumedAtUtc = Timestamp.FromDateTimeOffset(operatorActedAt)
            }, null);
            using var policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Root, "policy.json")));
            Assert.Equal(operatorActedAt, policy.RootElement.GetProperty("resumedAtUtc").GetDateTimeOffset());
        }

        rig.Worker = rig.CreateWorker();
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.True(rig.Worker.HasUnresolvedResult);
        Assert.Equal("identity_conflict", rig.Status("deliveryState"));
        Assert.True(File.Exists(rig.SuspensionPath));
        Assert.Equal(1, rig.Requests);
        Assert.Empty(Directory.GetFiles(rig.Root, "*.recovery.json"));
    }

    [Fact]
    public async Task RestartInspectsTheActualAtomicallyRetainedBytesBeforeConsumingResumeAuthorization()
    {
        await using var rig = new DeliveryRig();
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new { revision = 5 });
        rig.Response = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            attemptId = Guid.NewGuid(), releaseId = Guid.NewGuid(), state = "FailedPreActivation" });
        var observedReplacement = File.ReadAllBytes(rig.ResultPath);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        string retainedPath;
        using (var delivery = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Root, "result-delivery.json"))))
        {
            var preservationId = delivery.RootElement.GetProperty("identityConflict").GetProperty("preservationId").GetGuid();
            retainedPath = Path.Combine(rig.Root, $"result-{rig.AttemptId:D}-{preservationId:D}.retained.json");
        }
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new
        {
            revision = 6, resumeAttemptId = rig.AttemptId, resumeRevision = 6, resumedAtUtc = rig.Clock.GetUtcNow()
        });
        // Model replacement between observation and atomic move, followed by
        // process interruption before the worker can inspect what it moved.
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            attemptId = Guid.NewGuid(), releaseId = Guid.NewGuid(), state = "Accepted", toVersion = "1.2.5" });
        var movedReplacement = File.ReadAllBytes(rig.ResultPath);
        File.Move(rig.ResultPath, retainedPath);
        rig.Worker = rig.CreateWorker();
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.True(rig.Worker.HasUnresolvedResult);
        Assert.Equal("identity_conflict", rig.Status("deliveryState"));
        Assert.Equal(movedReplacement, File.ReadAllBytes(retainedPath));
        var replacements = Directory.GetFiles(rig.Root, "*.replacement.json").Select(File.ReadAllBytes).ToArray();
        Assert.Equal(2, replacements.Length);
        Assert.Contains(replacements, bytes => bytes.AsSpan().SequenceEqual(observedReplacement));
        Assert.Contains(replacements, bytes => bytes.AsSpan().SequenceEqual(movedReplacement));
        Assert.Empty(Directory.GetFiles(rig.Root, "*.recovery.json"));

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "policy.json"), new
        {
            revision = 7, resumeAttemptId = rig.AttemptId, resumeRevision = 7, resumedAtUtc = rig.Clock.GetUtcNow()
        });
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.False(rig.Worker.HasUnresolvedResult);
        Assert.Equal("archived_unresolved", rig.Status("deliveryState"));
        Assert.Equal("required", rig.Status("reconciliationState"));
        Assert.Equal(movedReplacement, File.ReadAllBytes(retainedPath));
        Assert.Equal(1, rig.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightResultResponseCannotReplaceAnObservedIdentityConflict(bool transportFailure)
    {
        await using var rig = new DeliveryRig();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.AsyncResponse = () => { requestStarted.SetResult(); return response.Task; };
        var inFlight = rig.Worker.DeliverPendingAsync(CancellationToken.None);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            attemptId = Guid.NewGuid(), releaseId = Guid.NewGuid(), state = "FailedPreActivation" });
        var replacement = File.ReadAllBytes(rig.ResultPath);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal("identity_conflict", rig.Status("deliveryState"));
        if (transportFailure) response.SetException(new HttpRequestException("Synthetic interrupted response"));
        else response.SetResult(rig.Reply(HttpStatusCode.Accepted, "applied", "report_applied", "RolledBack"));
        await inFlight;
        Assert.Equal("identity_conflict", rig.Status("deliveryState"));
        Assert.Equal("required", rig.Status("reconciliationState"));
        Assert.Equal("RolledBack", rig.Status("installationState"));
        Assert.Equal(replacement, File.ReadAllBytes(rig.ResultPath));
        Assert.True(rig.Worker.HasUnresolvedResult);
        Assert.Equal(1, rig.Requests);
    }

    [Fact]
    public async Task LocalFailureAfterACommittedConflictCannotTurnItBackIntoARetry()
    {
        await using var rig = new DeliveryRig();
        rig.Response = () => rig.Reply(HttpStatusCode.Conflict, "conflict", "accepted_authority_conflict", "Accepted");
        rig.Worker = rig.CreateWorker(message =>
        {
            if (message.Contains("delivery=conflict", StringComparison.Ordinal))
                throw new IOException("Synthetic diagnostic destination failure");
        });
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal("conflict", rig.Status("deliveryState"));
        Assert.Equal("required", rig.Status("reconciliationState"));
        Assert.Equal("accepted_authority_conflict", rig.Status("lastCode"));
        Assert.True(rig.Worker.HasUnresolvedResult);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(1, rig.Requests);
    }

    [Fact]
    public async Task TypedRecoverableTokenFailureRetriesAfterTheBackendReturns()
    {
        await using var rig = new DeliveryRig();
        rig.TokenFailure = new AgentClientAuthException("Backend unavailable", 502, endpointRole: AgentAuthEndpointRole.Token);
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(0, rig.Requests);
        Assert.Equal("retry_wait", rig.Status("deliveryState"));
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal(1, rig.Requests);
        Assert.Equal("acknowledged", rig.Status("deliveryState"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.2.5")]
    public async Task NativeAcceptedWithoutVersionBoundReadinessRetainsReconciliationEvidence(string? readyVersion)
    {
        await using var rig = new DeliveryRig();
        ProtectedUpdateState.AtomicWriteJson(rig.ResultPath, new { schema = "netratel.update.result.v2",
            rig.AttemptId, rig.ReleaseId, state = "Accepted", toVersion = "1.2.4" });
        ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "request.json"), new { rig.AttemptId, rig.ReleaseId,
            admissionNonce = "protected-test-nonce", toVersion = "1.2.4" });
        if (readyVersion is not null)
            ProtectedUpdateState.AtomicWriteJson(Path.Combine(rig.Root, "ready.json"), new { schema = "netratel.update.ready.v2",
                rig.AttemptId, rig.ReleaseId, confirmationId = Guid.NewGuid(), version = readyVersion });
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        Assert.Equal("confirmation_missing", rig.Status("deliveryState"));
        Assert.Equal("required", rig.Status("reconciliationState"));
        Assert.Equal(0, rig.Requests);
        Assert.True(File.Exists(rig.ResultPath));
    }

    [Fact]
    public async Task AResultBlocksNewClaimsBeforeItsDeliveryWorkerLoadsIt()
    {
        await using var rig = new DeliveryRig();
        rig.Response = () => rig.Reply(HttpStatusCode.Conflict, "conflict", "state_conflict", "Accepted");
        await rig.Worker.DeliverPendingAsync(CancellationToken.None);
        using var api = new HttpClient(rig, false) { BaseAddress = new Uri("https://api.example.test") };
        await using var coordinator = new AkkaClientAutoUpdateCoordinator(new ClientOptions
        {
            ApiBaseUrl = api.BaseAddress.ToString(),
            AutoUpdate = new AutoUpdateOptions { StateDirectory = rig.Root, RuntimeId = "linux-x64" }
        }, "1.2.3", rig, api, rig.Clock, () => throw new InvalidOperationException("Unexpected native launch"), true);
        coordinator.OnAcknowledgement(new ClientUpdateOffer { ReleaseId = Guid.NewGuid().ToString("D"),
            Version = "1.2.5", RuntimeId = "linux-x64" }, new ClientUpdatePolicy { AutoUpdateEnabled = true }, null);
        await coordinator.DisposeAsync();
        Assert.Equal(1, rig.Requests);
        Assert.True(File.Exists(rig.ResultPath));
    }

    private sealed class DeliveryRig : HttpMessageHandler, IAgentTokenService, IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "netratel-result-delivery-" + Guid.NewGuid().ToString("N"));
        internal readonly Guid AttemptId = Guid.NewGuid(), ReleaseId = Guid.NewGuid();
        internal readonly GatewayPresenceTestClock Clock = new();
        internal readonly ConcurrentQueue<string?> RequestNonces = new();
        internal string ResultPath => Path.Combine(Root, "result.json");
        internal string SuspensionPath => Path.Combine(Root, "suspension.json");
        internal ClientUpdateResultDelivery Worker;
        internal Func<HttpResponseMessage> Response;
        internal Func<Task<HttpResponseMessage>>? AsyncResponse;
        internal Guid ExpectedAttemptId;
        internal string ExpectedState = "RolledBack";
        internal int Requests, Invalidations;
        internal AgentClientAuthException? TokenFailure;
        private readonly HttpClient _api;

        internal DeliveryRig()
        {
            ExpectedAttemptId = AttemptId;
            Directory.CreateDirectory(Root);
            _api = new HttpClient(this, false) { BaseAddress = new Uri("https://api.example.test") };
            ProtectedUpdateState.AtomicWriteJson(ResultPath, new { schema = "netratel.update.result.v2", AttemptId, ReleaseId,
                state = "RolledBack", failureCode = "activation_timeout", message = "Rollback reconnected.",
                completedAtUtc = Clock.GetUtcNow(), activeTarget = "/client/previous" });
            ProtectedUpdateState.AtomicWriteJson(Path.Combine(Root, "request.json"), new { AttemptId, ReleaseId, admissionNonce = "protected-test-nonce" });
            Response = () => Reply(HttpStatusCode.Accepted, "applied", "report_applied", "RolledBack");
            Worker = CreateWorker();
        }
        internal ClientUpdateResultDelivery CreateWorker(Action<string>? log = null) => new(Root, ResultPath, Path.Combine(Root, "request.json"),
            Path.Combine(Root, "ready.json"), SuspensionPath, this, _api, Clock, new object(), log ?? (_ => { }));
        internal string? Status(string key)
        {
            using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "result-delivery.json")));
            return record.RootElement.GetProperty(key).GetString();
        }
        internal HttpResponseMessage Reply(HttpStatusCode status, string outcome, string code, string? state, bool suspended = true) =>
            new(status) { Content = JsonContent.Create(new { outcome, code, state, confirmationId = state == "Accepted" ? Guid.NewGuid() : (Guid?)null,
                suspensionActive = suspended, policyRevision = 5 }) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Assert.EndsWith($"/attempts/{ExpectedAttemptId:D}/result", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(ExpectedState, document.RootElement.GetProperty("state").GetString());
            RequestNonces.Enqueue(document.RootElement.GetProperty("admissionNonce").GetString());
            return AsyncResponse is { } asyncResponse ? await asyncResponse() : Response();
        }
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (TokenFailure is { } failure) { TokenFailure = null; throw failure; }
            return Task.FromResult(("secret-delivery-token", Clock.GetUtcNow().AddMinutes(10)));
        }
        public bool InvalidateAccessToken(string rejectedAccessToken) { Interlocked.Increment(ref Invalidations); return true; }
        public ValueTask DisposeAsync()
        {
            _api.Dispose();
            Dispose();
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
