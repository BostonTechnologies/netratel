using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Logging;
using NetRatel.Shared.Client;
using NuGet.Versioning;

namespace NetRatel.Client.Service.Updates;

public interface IAgentGatewayUpdateHandler
{
    void PopulateHello(ConnectHello hello);
    void OnAcknowledgement(ClientUpdateOffer? offer, ClientUpdatePolicy? policy, UpdateActivationConfirmation? confirmation);
    void OnPresenceConnected(ulong connectionEpoch);
    void OnActivationHeartbeatSent(ulong connectionEpoch);
    void OnActivationHeartbeatAccepted(ulong connectionEpoch);
    void RecordAcknowledgementFailure(Exception exception);
}

public sealed record PendingUpdateActivation(Guid AttemptId, Guid ReleaseId, string TargetVersion);

public sealed class AkkaClientAutoUpdateCoordinator : IAgentGatewayUpdateHandler, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ClientOptions _options;
    private readonly string _currentVersion;
    private readonly Uri _apiOrigin;
    private readonly string _runtimeId;
    private readonly string _stateDirectory;
    private readonly string _requestPath;
    private readonly string _readyPath;
    private readonly string _resultPath;
    private readonly string _suspensionPath;
    private readonly string _policyPath;
    private readonly string _presencePath;
    private readonly string _activationPath;
    private readonly IAgentTokenService _tokenService;
    private readonly HttpClient _apiClient;
    private readonly Channel<bool> _offers = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _processor;
    private readonly TimeProvider _timeProvider;
    private readonly Action _startUpdater;
    private readonly bool? _enabled;
    private readonly Action<string> _writeLog;
    private readonly object _offerGate = new();
    private UpdateAttempt? _selectedAttempt;
    private readonly Dictionary<Guid, UpdateAttempt> _attempts = new();
    private CancellationTokenSource? _attemptCancellation;
    private bool _handoverStarted;
    private bool _disposed;
    private long _policyRevision;

    public AkkaClientAutoUpdateCoordinator(
        ClientOptions options,
        string currentVersion,
        IAgentTokenService tokenService,
        HttpClient apiClient)
        : this(options, currentVersion, tokenService, apiClient, TimeProvider.System, null, null)
    {
    }

    internal AkkaClientAutoUpdateCoordinator(ClientOptions options, string currentVersion,
        IAgentTokenService tokenService, HttpClient apiClient, TimeProvider timeProvider,
        Action? startUpdater, bool? enabled, Action<string>? writeLog = null)
    {
        _writeLog = writeLog ?? LogManager.WriteLog;
        _timeProvider = timeProvider;
        _startUpdater = startUpdater ?? StartUpdater;
        _enabled = enabled;
        _options = options;
        _currentVersion = NormalizeVersion(currentVersion);
        _tokenService = tokenService;
        _apiClient = apiClient;
        _apiOrigin = new Uri(ClientEndpointAddress.NormalizeApiBase(options.ApiBaseUrl), UriKind.Absolute);
        _runtimeId = ClientUpdateVersioning.ResolveRuntimeId(options.AutoUpdate.RuntimeId);
        _stateDirectory = string.IsNullOrWhiteSpace(options.AutoUpdate.StateDirectory)
            ? OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel", "update")
                : "/var/lib/netratel/update"
            : options.AutoUpdate.StateDirectory;
        _requestPath = ResolvePath(options.AutoUpdate.RequestPath, "request.json");
        _readyPath = ResolvePath(options.AutoUpdate.ReadyPath, "ready.json");
        _resultPath = Path.Combine(_stateDirectory, "result.json");
        _suspensionPath = Path.Combine(_stateDirectory, "suspension.json");
        _policyPath = Path.Combine(_stateDirectory, "policy.json");
        _presencePath = Path.Combine(_stateDirectory, "presence.json");
        _activationPath = Path.Combine(_stateDirectory, "activation.json");
        _policyRevision = ReadPolicyRevision();
        _processor = Task.Run(() => ProcessAsync(_stopping.Token));
    }

    public bool IsEnabled => _enabled ?? (
        string.Equals(_options.AutoUpdate.Mode, "Service", StringComparison.OrdinalIgnoreCase) &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
        !string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists("/.dockerenv"));

    /// <summary>Reads the existing protected handover; it neither creates readiness nor advances update state.</summary>
    public PendingUpdateActivation? ReadPendingActivation(DateTimeOffset now)
    {
        if (!IsEnabled || !NativeServiceRecoveryPolicy.IsOwnedService) return null;
        return ReadPendingActivationCore(_requestPath, Path.Combine(_stateDirectory, "state.json"), _resultPath,
            _readyPath, _currentVersion, _runtimeId, now);
    }

    internal static PendingUpdateActivation? ReadPendingActivationCore(string requestPath, string statePath,
        string resultPath, string readyPath, string currentVersion, string runtimeId, DateTimeOffset now)
    {
        try
        {
            foreach (var path in new[] { requestPath, statePath, resultPath, readyPath })
                if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) return null;
            if (!ProtectedUpdateState.IsProtectedFile(requestPath) || !ProtectedUpdateState.IsProtectedFile(statePath)) return null;
            var request = ReadJson<UpdateRequest>(requestPath);
            var state = ReadJson<PendingState>(statePath);
            if (request is null || state is null || request.Schema != "netratel.update.request.v2" ||
                request.AttemptId == Guid.Empty || request.ReleaseId == Guid.Empty || string.IsNullOrWhiteSpace(request.AdmissionNonce) ||
                request.ToVersion != currentVersion || request.RuntimeId != runtimeId || state.Version != currentVersion ||
                state.State is not ("applying" or "verifying") ||
                state.AttemptId is { } stateAttempt && stateAttempt != request.AttemptId ||
                request.RequestedAtUtc > now.AddMinutes(1) || request.RequestedAtUtc < now.AddDays(-1) ||
                state.UpdatedAtUtc > now.AddMinutes(1) || state.UpdatedAtUtc < now.AddSeconds(-180)) return null;
            if (File.Exists(resultPath))
            {
                if (!ProtectedUpdateState.IsProtectedFile(resultPath)) return null;
                var result = ReadJson<UpdateResult>(resultPath);
                if (result is null || result.Schema != "netratel.update.result.v2" || result.AttemptId == request.AttemptId) return null;
            }
            if (File.Exists(readyPath))
            {
                if (!ProtectedUpdateState.IsProtectedFile(readyPath)) return null;
                var ready = ReadJson<PendingReady>(readyPath);
                if (ready is null || ready.Schema != "netratel.update.ready.v2") return null;
                if (ready.AttemptId == request.AttemptId &&
                    ready.ReleaseId == request.ReleaseId && ready.Version == currentVersion && ready.ConfirmationId != Guid.Empty) return null;
            }
            return new(request.AttemptId, request.ReleaseId, request.ToVersion);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public void PopulateHello(ConnectHello hello)
    {
        if (!IsEnabled) return;
        hello.Capabilities.Add("client-auto-update-v2");
        hello.RuntimeId = _runtimeId;
        hello.UpdateChannel = string.Equals(_options.AutoUpdate.Channel, "Prerelease", StringComparison.OrdinalIgnoreCase)
            ? "prerelease" : "stable";
        hello.UpdatePolicyRevision = _policyRevision;

        var request = ReadJson<UpdateRequest>(_requestPath);
        if (request is null)
        {
            return;
        }

        // A rolled-back binary can still see the failed candidate's request. Only
        // the candidate named by that request may advance its activation record.
        if (!string.Equals(request.ToVersion, _currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RecordActivation(request, "request_loaded", requestLoadedAtUtc: DateTimeOffset.UtcNow);
        if (request.AttemptId != Guid.Empty && request.ReleaseId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(request.AdmissionNonce))
        {
            // Activation admission is an exact release check. The regular agent
            // version includes build metadata, so send the normalized SemVer only
            // for this authenticated activation handshake.
            hello.AgentVersion = _currentVersion;
            hello.UpdateActivation = new UpdateActivationContext
            {
                AttemptId = request.AttemptId.ToString("D"),
                ReleaseId = request.ReleaseId.ToString("D"),
                AdmissionNonce = request.AdmissionNonce
            };
            RecordActivation(request, "activation_context_attached", activationContextAttachedAtUtc: DateTimeOffset.UtcNow);
        }
        else
        {
            RecordActivation(request, "activation_context_rejected", errorCode: "activation_context_invalid");
        }
    }

    public void OnAcknowledgement(ClientUpdateOffer? offer, ClientUpdatePolicy? policy, UpdateActivationConfirmation? confirmation)
    {
        if (!IsEnabled) return;
        lock (_offerGate) { if (_disposed) return; }
        try
        {
            AtomicWriteJson(_presencePath, new { version = _currentVersion, acknowledgedAtUtc = DateTimeOffset.UtcNow });
            ApplyPolicy(policy);
            ApplyConfirmation(confirmation);
        }
        catch (Exception exception)
        {
            RecordAcknowledgementFailure(exception);
            throw;
        }
        lock (_offerGate)
        {
            if (_disposed || _handoverStarted) return;
            if (offer is null || !Guid.TryParse(offer.ReleaseId, out var releaseId) || releaseId == Guid.Empty ||
                !string.Equals(offer.RuntimeId, _runtimeId, StringComparison.OrdinalIgnoreCase) ||
                !ClientUpdateVersioning.IsNewerVersion(offer.Version, _currentVersion) ||
                File.Exists(_suspensionPath) || policy is { AutoUpdateEnabled: false } or { Suspended: true } ||
                policy?.SuppressedReleaseId == offer.ReleaseId)
            {
                if (_selectedAttempt is { } withdrawn) withdrawn.Queued = false;
                _selectedAttempt = null;
                _attemptCancellation?.Cancel();
                return;
            }

            if (_selectedAttempt?.Offer.ReleaseId != offer.ReleaseId)
            {
                if (_selectedAttempt is { } replaced) replaced.Queued = false;
                _attemptCancellation?.Cancel();
                // A changed offer must not discard a claim's nonce or completed stages.
                if (!_attempts.TryGetValue(releaseId, out var retained))
                    _attempts.Add(releaseId, retained = new UpdateAttempt(offer.Clone()));
                _selectedAttempt = retained;
            }
            var attempt = _selectedAttempt;
            if (attempt.Queued || attempt.Active || attempt.Phase == UpdatePhase.Terminal ||
                attempt.RetryDelay > TimeSpan.Zero &&
                _timeProvider.GetElapsedTime(attempt.FailedAt) < attempt.RetryDelay) return;
            attempt.Queued = true;
            _offers.Writer.TryWrite(true);
        }
    }

    public void OnPresenceConnected(ulong connectionEpoch) =>
        RecordActivationForRequest("presence_connected", connectionEpoch: checked((long)connectionEpoch), connectAcceptedAtUtc: DateTimeOffset.UtcNow);

    public void OnActivationHeartbeatSent(ulong connectionEpoch) =>
        RecordActivationForRequest("first_heartbeat_sent", connectionEpoch: checked((long)connectionEpoch), firstHeartbeatSentAtUtc: DateTimeOffset.UtcNow);

    public void OnActivationHeartbeatAccepted(ulong connectionEpoch) =>
        RecordActivationForRequest("heartbeat_accepted", connectionEpoch: checked((long)connectionEpoch), heartbeatAcceptedAtUtc: DateTimeOffset.UtcNow);

    public void RecordAcknowledgementFailure(Exception exception) =>
        RecordActivationForRequest("acknowledgement_failed", errorCode: "update_acknowledgement_failed", exception: exception);

    public async ValueTask DisposeAsync()
    {
        lock (_offerGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping.Cancel();
            _attemptCancellation?.Cancel();
            _offers.Writer.TryComplete();
        }
        try { await _processor.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            LogManager.WriteLog("[UpdateManagement] Akka update coordinator stopped.");
        }
        _stopping.Dispose();
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(ProcessOffersAsync(cancellationToken), MonitorResultsAsync(cancellationToken)).ConfigureAwait(false);
    }

    private async Task ProcessOffersAsync(CancellationToken cancellationToken)
    {
        // Offers arrive on every accepted presence heartbeat. A single bounded wakeup
        // plus a monotonic cooldown avoids retry timers and only retries fresh eligibility.
        await foreach (var _ in _offers.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            UpdateAttempt attempt;
            CancellationTokenSource operation;
            lock (_offerGate)
            {
                if (_selectedAttempt is not { Queued: true } selected) continue;
                attempt = selected;
                attempt.Queued = false;
                attempt.Active = true;
                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _attemptCancellation = operation;
            }
            Exception? failure = null;
            var decision = "completed";
            try
            {
                await StageAsync(attempt, operation.Token).ConfigureAwait(false);
                if (attempt.Phase == UpdatePhase.Terminal)
                {
                    decision = "terminal";
                    failure = attempt.TerminalFailure;
                }
                else if (!_handoverStarted) decision = "cancelled";
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                decision = "cancelled";
            }
            catch (Exception exception)
            {
                failure = exception;
                lock (_offerGate)
                {
                    if (!_handoverStarted && (attempt.StagingFailure is not null || IsRecoverable(exception, attempt.Operation)))
                    {
                        attempt.Failures = Math.Min(attempt.Failures + 1, 5);
                        attempt.FailedAt = _timeProvider.GetTimestamp();
                        attempt.RetryDelay = TimeSpan.FromSeconds(Math.Min(30 * (1 << (attempt.Failures - 1)), 300));
                        decision = "retry-on-fresh-offer";
                    }
                    else
                    {
                        attempt.Phase = UpdatePhase.Terminal;
                        attempt.TerminalFailure = exception;
                        decision = _handoverStarted ? "handover-uncertain-no-relaunch" : "terminal";
                    }
                }
            }
            finally
            {
                lock (_offerGate)
                {
                    attempt.Active = false;
                    _attemptCancellation = null;
                    operation.Dispose();
                }
            }
            WriteUpdateDiagnostic(attempt, failure, decision);
        }
    }

    private async Task MonitorResultsAsync(CancellationToken cancellationToken)
    {
        await ConsumeResultAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ConsumeResultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StageAsync(UpdateAttempt attempt, CancellationToken cancellationToken)
    {
        attempt.Operation = "agent-authentication";
        var token = await _tokenService.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var offer = attempt.Offer;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (attempt.Phase)
            {
                case UpdatePhase.Claim:
                    attempt.Operation = "claim";
                    var channel = string.Equals(_options.AutoUpdate.Channel, "Prerelease", StringComparison.OrdinalIgnoreCase)
                        ? "prerelease" : "stable";
                    using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/agent-updates/releases/{offer.ReleaseId}/claim")
                    {
                        Content = JsonContent.Create(new { runtimeId = _runtimeId, currentVersion = _currentVersion, channel,
                            admissionNonce = attempt.AdmissionNonce })
                    })
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                        using var response = await _apiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        var claim = await response.Content.ReadFromJsonAsync<ClaimResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Update claim response was empty.");
                        if (claim.AttemptId == Guid.Empty || claim.ReleaseId.ToString("D") != offer.ReleaseId ||
                            claim.AdmissionNonce != attempt.AdmissionNonce ||
                            !NuGetVersion.TryParse(claim.TargetVersion, out var claimedVersion) ||
                            !NuGetVersion.TryParse(offer.Version, out var offeredVersion) ||
                            claimedVersion.ToNormalizedString() != offeredVersion.ToNormalizedString())
                            throw new InvalidOperationException("Update claim does not match the offered release and admission context.");
                        attempt.Claim = claim;
                        attempt.AttemptId = claim.AttemptId;
                    }
                    attempt.Phase = UpdatePhase.DownloadingReport;
                    break;
                case UpdatePhase.DownloadingReport:
                    attempt.Operation = "state/result-reporting(Downloading)";
                    await ReportStateAsync(attempt.Claim!.AttemptId, "Downloading", null, null, token.AccessToken, cancellationToken).ConfigureAwait(false);
                    attempt.Phase = UpdatePhase.Download;
                    break;
                case UpdatePhase.Download:
                    attempt.Operation = "download/verification";
                    try
                    {
                        await DownloadAndVerifyOfferedArtifactAsync(_apiClient, _apiOrigin, offer.DownloadPath, token.AccessToken,
                            PackagePath(attempt), offer.Version, _runtimeId, offer.SizeBytes, _options.AutoUpdate.MaximumArtifactBytes,
                            offer.Sha256, cancellationToken, _timeProvider).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        attempt.StagingFailure = exception;
                        attempt.Phase = UpdatePhase.FailureReport;
                        try
                        {
                            await ReportStagingFailureAsync(attempt, token.AccessToken, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception reportException) when (reportException is not OperationCanceledException)
                        {
                            WriteUpdateDiagnostic(attempt, reportException, "failure-report-deferred", "state/result-reporting(FailedPreActivation)");
                        }
                        // Keep the original download/validation failure identifiable even if its report failed.
                        attempt.Operation = "download/verification";
                        throw;
                    }
                    attempt.Phase = UpdatePhase.StagedReport;
                    break;
                case UpdatePhase.StagedReport:
                    attempt.Operation = "state/result-reporting(Staged)";
                    await ReportStateAsync(attempt.Claim!.AttemptId, "Staged", null, null, token.AccessToken, cancellationToken).ConfigureAwait(false);
                    attempt.Phase = UpdatePhase.Request;
                    break;
                case UpdatePhase.Request:
                    attempt.Operation = "activation-request";
                    Directory.CreateDirectory(_stateDirectory);
                    if (File.Exists(_readyPath)) File.Delete(_readyPath);
                    var claimContext = attempt.Claim!;
                    AtomicWriteJson(_requestPath, new UpdateRequest("netratel.update.request.v2", claimContext.AttemptId,
                        claimContext.ReleaseId, claimContext.AdmissionNonce, _runtimeId, _currentVersion, offer.Version,
                        PackagePath(attempt), offer.Sha256, _readyPath, _presencePath, _resultPath, LogManager.LogFilePath,
                        _timeProvider.GetUtcNow()));
                    attempt.Phase = UpdatePhase.ActivatingReport;
                    break;
                case UpdatePhase.ActivatingReport:
                    attempt.Operation = "state/result-reporting(Activating)";
                    await ReportStateAsync(attempt.Claim!.AttemptId, "Activating", null, null, token.AccessToken, cancellationToken).ConfigureAwait(false);
                    attempt.Phase = UpdatePhase.Handover;
                    break;
                case UpdatePhase.Handover:
                    attempt.Operation = "activation-handover";
                    lock (_offerGate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_selectedAttempt != attempt || _disposed || _handoverStarted || File.Exists(_suspensionPath)) return;
                        // Fence every offer before invoking the native updater. An uncertain
                        // launch outcome must never cause a second activation.
                        _handoverStarted = true;
                        _startUpdater();
                    }
                    return;
                case UpdatePhase.FailureReport:
                    attempt.Operation = "state/result-reporting(FailedPreActivation)";
                    await ReportStagingFailureAsync(attempt, token.AccessToken, cancellationToken).ConfigureAwait(false);
                    break;
                case UpdatePhase.Terminal:
                    return;
            }
        }
    }

    private async Task ReportStagingFailureAsync(UpdateAttempt attempt, string accessToken, CancellationToken cancellationToken)
    {
        var failure = attempt.StagingFailure!;
        await ReportStateAsync(attempt.Claim!.AttemptId, "FailedPreActivation", FailureCode(failure),
            "Artifact staging failed; see client update diagnostics.", accessToken, cancellationToken).ConfigureAwait(false);
        attempt.StagingFailure = null;
        attempt.Phase = IsRecoverable(failure, "download/verification") ? UpdatePhase.Claim : UpdatePhase.Terminal;
        if (attempt.Phase == UpdatePhase.Claim) attempt.Claim = null;
        else attempt.TerminalFailure = failure;
    }

    private string PackagePath(UpdateAttempt attempt) =>
        Path.Combine(_stateDirectory, "staging", $"{_runtimeId}-{attempt.Offer.Version}.zip");

    private static bool IsRecoverable(Exception exception, string operation) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => IsTransientStatus(status) ||
            operation == "claim" && status == HttpStatusCode.Conflict,
        HttpRequestException or OperationCanceledException => true,
        IOException when exception is not InvalidDataException => true,
        _ => false
    };

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.Unauthorized || (int)status >= 500;

    private static string FailureCode(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => $"http_{(int)status}",
        HttpRequestException => "transport_error",
        OperationCanceledException => "transport_timeout",
        InvalidOperationException { Message: "Downloaded update SHA-256 does not match the offer." } => "artifact_checksum_mismatch",
        InvalidOperationException { Message: "Downloaded update size does not match the offer." } => "artifact_size_mismatch",
        InvalidOperationException { Message: "Update offer size is outside the configured limit." } => "offer_size_invalid",
        InvalidOperationException { Message: "Update manifest is missing." or "Update manifest does not match the offered artifact." } => "artifact_manifest_invalid",
        InvalidOperationException { Message: "Update offer download path must be a same-origin API path." or
            "Update offer download path resolved outside the configured API origin." } => "artifact_origin_invalid",
        InvalidOperationException { Message: "Artifact download redirect refused." } => "artifact_redirect_refused",
        InvalidOperationException { Message: "Update claim does not match the offered release and admission context." } => "claim_context_invalid",
        InvalidOperationException { Message: "Update claim response was empty." } => "claim_response_empty",
        InvalidDataException => "artifact_archive_invalid",
        JsonException => "invalid_json",
        KeyNotFoundException => "artifact_manifest_invalid",
        IOException when exception is not InvalidDataException => "io_error",
        _ => "local_operation_failed"
    };

    private void WriteUpdateDiagnostic(UpdateAttempt attempt, Exception? failure, string decision, string? operation = null)
    {
        var stage = operation ?? attempt.Operation;
        var endpoint = stage == "claim" ? "/api/v2/agent-updates/releases/{releaseId}/claim"
            : stage == "agent-authentication" ? "agent-token"
            : stage.StartsWith("state/result-reporting", StringComparison.Ordinal) ? "/api/v2/agent-updates/attempts/{attemptId}/result"
            : stage == "download/verification" ? "/api/v1/client-artifacts/{runtimeId}/{version}/raw-download" : "native-updater/local-state";
        _writeLog($"[UpdateManagement] current={_currentVersion} target={attempt.Offer.Version} runtime={_runtimeId} " +
            $"release={attempt.Offer.ReleaseId} attempt={attempt.AttemptId?.ToString("D") ?? "unknown"} stage={stage} " +
            $"endpoint={endpoint} code={(failure is null ? "ok" : FailureCode(failure))} retry={decision} " +
            $"nextDelaySeconds={(decision == "retry-on-fresh-offer" ? attempt.RetryDelay.TotalSeconds : 0)}");
    }

    internal static async Task DownloadAndVerifyOfferedArtifactAsync(
        HttpClient apiClient,
        Uri apiOrigin,
        string downloadPath,
        string accessToken,
        string packagePath,
        string version,
        string runtimeId,
        long exactSizeBytes,
        long maximumArtifactBytes,
        string expectedSha256,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        if (exactSizeBytes <= 0 || exactSizeBytes > maximumArtifactBytes)
            throw new InvalidOperationException("Update offer size is outside the configured limit.");

        var downloadUri = ResolveSameOriginDownloadUri(apiOrigin, downloadPath);
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath) ??
            throw new InvalidOperationException("The staged package path has no directory."));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var temporaryPackagePath = packagePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    if ((int)response.StatusCode is >= 300 and < 400)
                        throw new InvalidOperationException("Artifact download redirect refused.");
                    throw new HttpRequestException("Artifact download failed.", null, response.StatusCode);
                }
                if (response.Content.Headers.ContentLength is long contentLength && contentLength != exactSizeBytes)
                    throw new InvalidOperationException("Downloaded update size does not match the offer.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var destination = new FileStream(temporaryPackagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
                {
                    await CopyOfferedArtifactAsync(source, destination, exactSizeBytes, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!string.Equals(await ComputeShaAsync(temporaryPackagePath, cancellationToken).ConfigureAwait(false),
                        expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Downloaded update SHA-256 does not match the offer.");
                ValidateManifest(temporaryPackagePath, version, runtimeId);
                // Windows will not rename a file whose writer is still open with FileShare.None.
                File.Move(temporaryPackagePath, packagePath, true);
                break;
            }
            catch (Exception exception) when (attempt < 3 &&
                (exception is HttpRequestException { StatusCode: null } or IOException))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), timeProvider ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(temporaryPackagePath)) File.Delete(temporaryPackagePath);
            }
        }

    }

    internal static Uri ResolveSameOriginDownloadUri(Uri apiOrigin, string downloadPath)
    {
        if (!apiOrigin.IsAbsoluteUri ||
            (apiOrigin.Scheme != Uri.UriSchemeHttp && apiOrigin.Scheme != Uri.UriSchemeHttps) ||
            apiOrigin.UserInfo.Length != 0 ||
            string.IsNullOrWhiteSpace(downloadPath) || downloadPath[0] != '/' ||
            (downloadPath.Length > 1 && downloadPath[1] == '/') ||
            downloadPath.Contains('\\') ||
            !Uri.TryCreate(downloadPath, UriKind.Relative, out var relativePath))
        {
            throw new InvalidOperationException("Update offer download path must be a same-origin API path.");
        }

        var downloadUri = new Uri(apiOrigin, relativePath);
        if (!string.Equals(downloadUri.Scheme, apiOrigin.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(downloadUri.Host, apiOrigin.Host, StringComparison.OrdinalIgnoreCase) ||
            downloadUri.Port != apiOrigin.Port || downloadUri.UserInfo.Length != 0 || downloadUri.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Update offer download path resolved outside the configured API origin.");
        }

        return downloadUri;
    }

    private static async Task CopyOfferedArtifactAsync(
        Stream source,
        Stream destination,
        long exactSizeBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0) break;
            if (bytesRead > exactSizeBytes - totalBytes)
                throw new InvalidOperationException("Downloaded update exceeds the offered artifact size.");
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalBytes += bytesRead;
        }

        if (totalBytes != exactSizeBytes)
            throw new InvalidOperationException("Downloaded update size does not match the offer.");
    }

    private void ApplyConfirmation(UpdateActivationConfirmation? confirmation)
    {
        if (confirmation is null || !Guid.TryParse(confirmation.AttemptId, out var attemptId) ||
            !Guid.TryParse(confirmation.ReleaseId, out var releaseId) ||
            !Guid.TryParse(confirmation.ConfirmationId, out var confirmationId)) return;
        var request = ReadJson<UpdateRequest>(_requestPath);
        if (request is null || request.AttemptId != attemptId || request.ReleaseId != releaseId ||
            !string.Equals(request.ToVersion, _currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            RecordActivationForRequest("confirmation_rejected", errorCode: "confirmation_mismatch");
            return;
        }
        RecordActivation(request, "confirmation_received", confirmationReceivedAtUtc: DateTimeOffset.UtcNow);
        AtomicWriteJson(_readyPath, new
        {
            schema = "netratel.update.ready.v2",
            attemptId,
            releaseId,
            version = _currentVersion,
            confirmationId,
            readyAtUtc = DateTimeOffset.UtcNow
        });
        RecordActivation(request, "ready_marker_written", readyMarkerWrittenAtUtc: DateTimeOffset.UtcNow);
    }

    private void ApplyPolicy(ClientUpdatePolicy? policy)
    {
        if (policy is null || policy.Revision <= _policyRevision) return;
        _policyRevision = policy.Revision;
        AtomicWriteJson(_policyPath, new { revision = _policyRevision, updatedAtUtc = DateTimeOffset.UtcNow });
        if (policy.ResumeRequested && !policy.Suspended && File.Exists(_suspensionPath)) File.Delete(_suspensionPath);
    }

    private async Task ConsumeResultAsync(CancellationToken cancellationToken)
    {
        var result = ReadJson<UpdateResult>(_resultPath);
        if (result is null || result.AttemptId == Guid.Empty) return;
        if (string.Equals(result.State, "Accepted", StringComparison.OrdinalIgnoreCase))
        {
            File.Move(_resultPath, Path.Combine(_stateDirectory, $"result-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.consumed.json"), true);
            return;
        }
        if (result.State is "RolledBack" or "RollbackUnverified")
            AtomicWriteJson(_suspensionPath, new { result.AttemptId, result.ReleaseId, reason = result.FailureCode, suspendedAtUtc = DateTimeOffset.UtcNow });
        var operation = "agent-authentication";
        try
        {
            var token = await _tokenService.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            operation = "state/result-reporting";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/agent-updates/attempts/{result.AttemptId}/result")
            {
                Content = JsonContent.Create(new { state = result.State, result.FailureCode, result.Message })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var response = await _apiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            File.Move(_resultPath, Path.Combine(_stateDirectory, $"result-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.consumed.json"), true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _writeLog($"[UpdateManagement] current={_currentVersion} target={ReadJson<UpdateRequest>(_requestPath)?.ToVersion ?? "unknown"} " +
                $"runtime={_runtimeId} release={result.ReleaseId} attempt={result.AttemptId} stage={operation} " +
                $"endpoint={(operation == "agent-authentication" ? "agent-token" : "/api/v2/agent-updates/attempts/{attemptId}/result")} " +
                $"code={FailureCode(exception)} retry=deferred nextDelaySeconds=30");
        }
    }

    private async Task ReportStateAsync(Guid attemptId, string state, string? failureCode, string? message,
        string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/agent-updates/attempts/{attemptId:D}/result")
        {
            Content = JsonContent.Create(new { state, failureCode, message })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _apiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private void StartUpdater()
    {
        if (OperatingSystem.IsWindows())
        {
            var root = Environment.GetEnvironmentVariable("NetRatel_UPDATE_ROOT");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetRatel", "Client");
            }

            var startInfo = CreateWindowsUpdaterStartInfo(
                ResolveTrustedWindowsPowerShellPath(),
                root,
                _stateDirectory,
                _requestPath);
            using var updaterProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The verified Windows updater process could not be started.");
            return;
        }
        using var linuxUpdaterProcess = Process.Start(new ProcessStartInfo("systemctl", $"start {_options.AutoUpdate.LinuxServiceName}")
        { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("The Linux updater process could not be started.");
    }

    internal static ProcessStartInfo CreateWindowsUpdaterStartInfo(
        string powerShellPath,
        string rootDirectory,
        string stateDirectory,
        string requestPath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var state = Path.GetFullPath(stateDirectory);
        var request = Path.GetFullPath(requestPath);
        var script = Path.GetFullPath(Path.Combine(root, "updater", "netratel-update.ps1"));
        var startInfo = new ProcessStartInfo(powerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script) ?? root
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.Environment["NetRatel_UPDATE_ROOT"] = root;
        startInfo.Environment["NetRatel_UPDATE_STATE"] = state;
        startInfo.Environment["NetRatel_UPDATE_REQUEST"] = request;
        return startInfo;
    }

    private static string ResolveTrustedWindowsPowerShellPath()
    {
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsRoot))
        {
            windowsRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(windowsRoot))
        {
            throw new InvalidOperationException("The trusted Windows directory is unavailable for the updater launch.");
        }

        var relativeRoot = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
            ? "Sysnative"
            : "System32";
        var path = Path.GetFullPath(Path.Combine(windowsRoot, relativeRoot, "WindowsPowerShell", "v1.0", "powershell.exe"));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The trusted Windows PowerShell executable was not found in the Windows directory.");
        }

        return path;
    }

    private static void ValidateManifest(string packagePath, string version, string runtimeId)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.Entries.SingleOrDefault(x => string.Equals(Path.GetFileName(x.FullName),
            "netratel-client-manifest.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Update manifest is missing.");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var executable = root.GetProperty("executable").GetString();
        if (root.GetProperty("schema").GetString() != "netratel.client.manifest.v1" ||
            root.GetProperty("product").GetString() != "NetRatel.Client" ||
            !string.Equals(root.GetProperty("version").GetString(), version, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(root.GetProperty("runtimeId").GetString(), runtimeId, StringComparison.OrdinalIgnoreCase) ||
            !IsFullCommitSha(root.GetProperty("commitSha").GetString()) || string.IsNullOrWhiteSpace(executable) ||
            archive.Entries.All(x => !string.Equals(Path.GetFileName(x.FullName), executable, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Update manifest does not match the offered artifact.");
    }

    private static bool IsFullCommitSha(string? value)
    {
        if (value is null || value.Length != 40) return false;
        try { return Convert.FromHexString(value).Length == 20; }
        catch (FormatException) { return false; }
    }

    private static async Task<string> ComputeShaAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private string ResolvePath(string configured, string fileName) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(_stateDirectory, fileName) : configured;

    private static string NormalizeVersion(string version) => ClientUpdateVersioning.NormalizePublishedVersion(version);

    private long ReadPolicyRevision() => ReadJson<PolicyState>(_policyPath)?.Revision ?? 0;

    private static T? ReadJson<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default; }
        catch { return default; }
    }

    private static void AtomicWriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void RecordActivationForRequest(
        string stage,
        long? connectionEpoch = null,
        string? errorCode = null,
        Exception? exception = null,
        DateTimeOffset? connectAcceptedAtUtc = null,
        DateTimeOffset? firstHeartbeatSentAtUtc = null,
        DateTimeOffset? heartbeatAcceptedAtUtc = null)
    {
        var request = ReadJson<UpdateRequest>(_requestPath);
        if (request is not null && string.Equals(request.ToVersion, _currentVersion, StringComparison.OrdinalIgnoreCase))
            RecordActivation(request, stage, connectionEpoch, errorCode, exception, connectAcceptedAtUtc,
                firstHeartbeatSentAtUtc, heartbeatAcceptedAtUtc);
    }

    private void RecordActivation(
        UpdateRequest request,
        string stage,
        long? connectionEpoch = null,
        string? errorCode = null,
        Exception? exception = null,
        DateTimeOffset? requestLoadedAtUtc = null,
        DateTimeOffset? activationContextAttachedAtUtc = null,
        DateTimeOffset? connectAcceptedAtUtc = null,
        DateTimeOffset? firstHeartbeatSentAtUtc = null,
        DateTimeOffset? heartbeatAcceptedAtUtc = null,
        DateTimeOffset? confirmationReceivedAtUtc = null,
        DateTimeOffset? readyMarkerWrittenAtUtc = null)
    {
        try
        {
            var existing = ReadJson<ActivationDiagnostic>(_activationPath);
            if (existing?.AttemptId != request.AttemptId)
                existing = null;
            var now = DateTimeOffset.UtcNow;
            var diagnostic = new ActivationDiagnostic(
                "netratel.update.activation.v1", request.AttemptId, request.ReleaseId, request.ToVersion, _currentVersion,
                request.RuntimeId, Environment.ProcessId, Environment.ProcessPath, stage,
                existing?.StartedAtUtc ?? now,
                existing?.RequestLoadedAtUtc ?? requestLoadedAtUtc,
                existing?.ActivationContextAttachedAtUtc ?? activationContextAttachedAtUtc,
                existing?.ConnectAcceptedAtUtc ?? connectAcceptedAtUtc,
                connectionEpoch ?? existing?.ConnectionEpoch,
                existing?.FirstHeartbeatSentAtUtc ?? firstHeartbeatSentAtUtc,
                existing?.HeartbeatAcceptedAtUtc ?? heartbeatAcceptedAtUtc,
                existing?.ConfirmationReceivedAtUtc ?? confirmationReceivedAtUtc,
                existing?.ReadyMarkerWrittenAtUtc ?? readyMarkerWrittenAtUtc,
                errorCode ?? existing?.LastErrorCode,
                exception?.GetType().Name ?? existing?.LastErrorType,
                now);
            AtomicWriteJson(_activationPath, diagnostic);
        }
        catch (Exception writeException)
        {
            LogManager.WriteLog($"[UpdateManagement] Activation diagnostic write failed: {writeException.GetType().Name}: {writeException.Message}");
        }
    }

    private enum UpdatePhase { Claim, DownloadingReport, Download, StagedReport, Request, ActivatingReport, Handover, FailureReport, Terminal }

    private sealed class UpdateAttempt(ClientUpdateOffer offer)
    {
        internal ClientUpdateOffer Offer { get; } = offer;
        internal string AdmissionNonce { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        internal ClaimResponse? Claim { get; set; }
        internal Guid? AttemptId { get; set; }
        internal UpdatePhase Phase { get; set; }
        internal string Operation { get; set; } = "claim";
        internal Exception? StagingFailure { get; set; }
        internal Exception? TerminalFailure { get; set; }
        internal bool Queued { get; set; }
        internal bool Active { get; set; }
        internal int Failures { get; set; }
        internal long FailedAt { get; set; }
        internal TimeSpan RetryDelay { get; set; }
    }

    private sealed record ClaimResponse(Guid AttemptId, Guid ReleaseId, string AdmissionNonce, string TargetVersion);
    private sealed record PolicyState(long Revision);
    private sealed record UpdateRequest(string Schema, Guid AttemptId, Guid ReleaseId, string AdmissionNonce,
        string RuntimeId, string FromVersion, string ToVersion, string PackagePath, string Sha256,
        string ReadyPath, string PresencePath, string ResultPath, string LogPath, DateTimeOffset RequestedAtUtc);
    private sealed record UpdateResult(Guid AttemptId, Guid ReleaseId, string State, string? FailureCode, string? Message, string? Schema);
    private sealed record PendingState(string State, string Version, DateTimeOffset UpdatedAtUtc, Guid? AttemptId);
    private sealed record PendingReady(string Schema, Guid AttemptId, Guid ReleaseId, string Version, Guid ConfirmationId);
    private sealed record ActivationDiagnostic(string Schema, Guid AttemptId, Guid ReleaseId, string TargetVersion,
        string CurrentVersion, string RuntimeId, int ProcessId, string? ExecutablePath, string Stage,
        DateTimeOffset StartedAtUtc, DateTimeOffset? RequestLoadedAtUtc, DateTimeOffset? ActivationContextAttachedAtUtc,
        DateTimeOffset? ConnectAcceptedAtUtc, long? ConnectionEpoch, DateTimeOffset? FirstHeartbeatSentAtUtc,
        DateTimeOffset? HeartbeatAcceptedAtUtc, DateTimeOffset? ConfirmationReceivedAtUtc,
        DateTimeOffset? ReadyMarkerWrittenAtUtc, string? LastErrorCode, string? LastErrorType,
        DateTimeOffset LastUpdatedAtUtc);
}
