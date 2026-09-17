using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Logging;
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

public sealed class AkkaClientAutoUpdateCoordinator : IAgentGatewayUpdateHandler, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ClientOptions _options;
    private readonly string _currentVersion;
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
    private readonly Channel<ClientUpdateOffer> _offers = Channel.CreateBounded<ClientUpdateOffer>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _processor;
    private Guid? _lastOfferId;
    private long _policyRevision;

    public AkkaClientAutoUpdateCoordinator(
        ClientOptions options,
        string currentVersion,
        IAgentTokenService tokenService,
        HttpClient apiClient)
    {
        _options = options;
        _currentVersion = NormalizeVersion(currentVersion);
        _tokenService = tokenService;
        _apiClient = apiClient;
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

    public bool IsEnabled =>
        string.Equals(_options.AutoUpdate.Mode, "Service", StringComparison.OrdinalIgnoreCase) &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
        !string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists("/.dockerenv");

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
        if (offer is null || !Guid.TryParse(offer.ReleaseId, out var releaseId) || _lastOfferId == releaseId) return;
        _lastOfferId = releaseId;
        _offers.Writer.TryWrite(offer.Clone());
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
        _stopping.Cancel();
        _offers.Writer.TryComplete();
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
        await foreach (var offer in _offers.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (File.Exists(_suspensionPath) || !NuGetVersion.TryParse(offer.Version, out var candidate) ||
                    !NuGetVersion.TryParse(_currentVersion, out var current) || candidate <= current)
                    continue;
                await StageAsync(offer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                LogManager.WriteLog($"[UpdateManagement] Akka update staging failed: {exception.Message}");
            }
        }
    }

    private async Task MonitorResultsAsync(CancellationToken cancellationToken)
    {
        await ConsumeResultAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ConsumeResultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StageAsync(ClientUpdateOffer offer, CancellationToken cancellationToken)
    {
        var token = await _tokenService.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var admissionNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var channel = string.Equals(_options.AutoUpdate.Channel, "Prerelease", StringComparison.OrdinalIgnoreCase)
            ? "prerelease" : "stable";
        using var claimRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/agent-updates/releases/{offer.ReleaseId}/claim")
        {
            Content = JsonContent.Create(new { runtimeId = _runtimeId, currentVersion = _currentVersion, channel, admissionNonce })
        };
        claimRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var claimResponse = await _apiClient.SendAsync(claimRequest, cancellationToken).ConfigureAwait(false);
        claimResponse.EnsureSuccessStatusCode();
        var claim = await claimResponse.Content.ReadFromJsonAsync<ClaimResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Update claim response was empty.");
        if (!NuGetVersion.TryParse(offer.Version, out var offeredVersion) ||
            !NuGetVersion.TryParse(claim.TargetVersion, out var claimedVersion) ||
            !string.Equals(offeredVersion.ToNormalizedString(), claimedVersion.ToNormalizedString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Update claim target version does not match the offered artifact.");
        }
        await ReportStateAsync(claim.AttemptId, "Downloading", null, null, token.AccessToken, cancellationToken).ConfigureAwait(false);

        try
        {
            await DownloadStageAndActivateAsync(offer, claim, token.AccessToken, downloadPath: offer.DownloadPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await ReportStateAsync(claim.AttemptId, "FailedPreActivation", "staging_failed", exception.Message,
                    token.AccessToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reportException) when (reportException is not OperationCanceledException)
            {
                LogManager.WriteLog($"[UpdateManagement] Pre-activation failure report deferred: {reportException.Message}");
            }
            throw;
        }
    }

    private async Task DownloadStageAndActivateAsync(
        ClientUpdateOffer offer,
        ClaimResponse claim,
        string accessToken,
        string downloadPath,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(downloadPath, UriKind.Relative, out var relativeDownloadPath) || !downloadPath.StartsWith('/'))
            throw new InvalidOperationException("Update offer download path must be same-origin and relative.");
        if (offer.SizeBytes <= 0 || offer.SizeBytes > _options.AutoUpdate.MaximumArtifactBytes)
            throw new InvalidOperationException("Update offer size is outside the configured limit.");

        Directory.CreateDirectory(Path.Combine(_stateDirectory, "staging"));
        var packagePath = Path.Combine(_stateDirectory, "staging", $"{_runtimeId}-{offer.Version}.zip");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeDownloadPath);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var destination = new FileStream(packagePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // Windows will not rename a file whose writer is still open with FileShare.None.
                File.Move(packagePath + ".tmp", packagePath, true);
                break;
            }
            catch when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        var fileInfo = new FileInfo(packagePath);
        if (fileInfo.Length != offer.SizeBytes || !string.Equals(await ComputeShaAsync(packagePath, cancellationToken), offer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Downloaded update size or SHA-256 does not match the offer.");
        ValidateManifest(packagePath, offer.Version, _runtimeId);
        await ReportStateAsync(claim.AttemptId, "Staged", null, null, accessToken, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_stateDirectory);
        if (File.Exists(_readyPath)) File.Delete(_readyPath);
        var updateRequest = new UpdateRequest("netratel.update.request.v2", claim.AttemptId, claim.ReleaseId,
            claim.AdmissionNonce, _runtimeId, _currentVersion, offer.Version, packagePath, offer.Sha256,
            _readyPath, _presencePath, _resultPath, LogManager.LogFilePath, DateTimeOffset.UtcNow);
        AtomicWriteJson(_requestPath, updateRequest);
        await ReportStateAsync(claim.AttemptId, "Activating", null, null, accessToken, cancellationToken).ConfigureAwait(false);
        StartUpdater();
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
        try
        {
            var token = await _tokenService.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/agent-updates/attempts/{result.AttemptId}/result")
            {
                Content = JsonContent.Create(new { state = result.State, result.FailureCode, result.Message })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var response = await _apiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            File.Move(_resultPath, Path.Combine(_stateDirectory, $"result-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.consumed.json"), true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogManager.WriteLog($"[UpdateManagement] Update result report deferred: {exception.Message}");
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
            var script = Path.Combine(Environment.GetEnvironmentVariable("NetRatel_UPDATE_ROOT") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetRatel", "Client"),
                "updater", "netratel-update.ps1");
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            return;
        }
        Process.Start(new ProcessStartInfo("systemctl", $"start {_options.AutoUpdate.LinuxServiceName}")
        { UseShellExecute = false, CreateNoWindow = true });
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

    private sealed record ClaimResponse(Guid AttemptId, Guid ReleaseId, string AdmissionNonce, string TargetVersion);
    private sealed record PolicyState(long Revision);
    private sealed record UpdateRequest(string Schema, Guid AttemptId, Guid ReleaseId, string AdmissionNonce,
        string RuntimeId, string FromVersion, string ToVersion, string PackagePath, string Sha256,
        string ReadyPath, string PresencePath, string ResultPath, string LogPath, DateTimeOffset RequestedAtUtc);
    private sealed record UpdateResult(Guid AttemptId, Guid ReleaseId, string State, string? FailureCode, string? Message);
    private sealed record ActivationDiagnostic(string Schema, Guid AttemptId, Guid ReleaseId, string TargetVersion,
        string CurrentVersion, string RuntimeId, int ProcessId, string? ExecutablePath, string Stage,
        DateTimeOffset StartedAtUtc, DateTimeOffset? RequestLoadedAtUtc, DateTimeOffset? ActivationContextAttachedAtUtc,
        DateTimeOffset? ConnectAcceptedAtUtc, long? ConnectionEpoch, DateTimeOffset? FirstHeartbeatSentAtUtc,
        DateTimeOffset? HeartbeatAcceptedAtUtc, DateTimeOffset? ConfirmationReceivedAtUtc,
        DateTimeOffset? ReadyMarkerWrittenAtUtc, string? LastErrorCode, string? LastErrorType,
        DateTimeOffset LastUpdatedAtUtc);
}
