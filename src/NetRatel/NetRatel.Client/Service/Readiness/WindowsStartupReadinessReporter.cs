using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;

namespace NetRatel.Client.Service.Readiness;

/// <summary>
/// Writes protected, current-start progress for the Windows SYSTEM service.
/// The installer independently verifies every process and session binding.
/// </summary>
public sealed class WindowsStartupReadinessReporter
{
    public const string SystemSid = "S-1-5-18";
    public const string RequestSchema = "netratel.install-readiness.request.v1";
    public const string ReadySchema = "netratel.install-readiness.ready.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _requestPath;
    private readonly string _readyPath;
    private readonly StartupReadinessChallenge? _request;
    private readonly ReadinessProcessIdentity? _processIdentity;
    private readonly Action<string> _diagnostic;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _writeGate = new();

    private WindowsStartupReadinessReporter(
        string requestPath,
        string readyPath,
        StartupReadinessChallenge? request,
        ReadinessProcessIdentity? processIdentity,
        Action<string> diagnostic,
        Func<DateTimeOffset> clock)
    {
        _requestPath = requestPath;
        _readyPath = readyPath;
        _request = request;
        _processIdentity = processIdentity;
        _diagnostic = diagnostic;
        _clock = clock;
    }

    public static WindowsStartupReadinessReporter Create(
        ServiceReadinessOptions options,
        bool serviceMode,
        Action<string> diagnostic)
    {
        var requestPath = options.RequestPath;
        var readyPath = options.ReadyPath;
        if (!OperatingSystem.IsWindows() || !serviceMode || string.IsNullOrWhiteSpace(requestPath) ||
            string.IsNullOrWhiteSpace(readyPath))
        {
            return new WindowsStartupReadinessReporter(requestPath, readyPath, null, null, diagnostic, () => DateTimeOffset.UtcNow);
        }

        try
        {
            var normalizedRequest = Path.GetFullPath(requestPath);
            var normalizedReady = Path.GetFullPath(readyPath);
            if (!string.Equals(Path.GetDirectoryName(normalizedRequest), Path.GetDirectoryName(normalizedReady), StringComparison.OrdinalIgnoreCase))
            {
                diagnostic("[Readiness] Ignoring startup challenge: request and ready files must share one protected directory.");
                return new WindowsStartupReadinessReporter(normalizedRequest, normalizedReady, null, null, diagnostic, () => DateTimeOffset.UtcNow);
            }

            var identity = GetCurrentProcessIdentity();
            var request = ReadRequest(normalizedRequest);
            if (!IsValidRequest(request, DateTimeOffset.UtcNow) || !IsEligibleProcess(identity, request!.RequestedAtUtc))
            {
                diagnostic("[Readiness] Startup evidence requires a fresh challenge from the current LocalSystem service process in session 0.");
                return new WindowsStartupReadinessReporter(normalizedRequest, normalizedReady, null, null, diagnostic, () => DateTimeOffset.UtcNow);
            }

            return new WindowsStartupReadinessReporter(normalizedRequest, normalizedReady, request, identity, diagnostic, () => DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            diagnostic($"[Readiness] Startup challenge could not be loaded ({exception.GetType().Name}).");
            return new WindowsStartupReadinessReporter(requestPath, readyPath, null, null, diagnostic, () => DateTimeOffset.UtcNow);
        }
    }

    internal static WindowsStartupReadinessReporter CreateForTesting(
        string requestPath,
        string readyPath,
        ReadinessProcessIdentity processIdentity,
        Action<string>? diagnostic = null,
        Func<DateTimeOffset>? clock = null)
    {
        var normalizedRequest = Path.GetFullPath(requestPath);
        var normalizedReady = Path.GetFullPath(readyPath);
        var currentTime = (clock ?? (() => DateTimeOffset.UtcNow))();
        var request = ReadRequest(normalizedRequest);
        var eligible = IsValidRequest(request, currentTime) &&
            IsEligibleProcess(processIdentity, request!.RequestedAtUtc);
        return new WindowsStartupReadinessReporter(
            normalizedRequest,
            normalizedReady,
            eligible ? request : null,
            eligible ? processIdentity : null,
            diagnostic ?? (_ => { }),
            clock ?? (() => DateTimeOffset.UtcNow));
    }

    public void Report(
        string stage,
        Guid? agentId = null,
        int? tenantId = null,
        ulong? connectionEpoch = null,
        Guid? connectionId = null)
    {
        try
        {
            if (_request is null || _processIdentity is null || !RequestIsStillCurrent())
                return;

            var observedAt = _clock();
            var record = new ReadinessRecord(
                ReadySchema,
                _request.AttemptId,
                _request.Nonce,
                stage,
                _processIdentity.ProcessId,
                _processIdentity.ProcessStartedAtUtc,
                _processIdentity.SessionId,
                _processIdentity.UserSid,
                agentId?.ToString("D"),
                tenantId,
                connectionEpoch,
                connectionId?.ToString("D"),
                _request.RequestedAtUtc,
                observedAt);
            var json = JsonSerializer.Serialize(record, JsonOptions);
            lock (_writeGate)
            {
                if (!RequestIsStillCurrent()) return;
                var directory = Path.GetDirectoryName(_readyPath)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = $"{_readyPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(temporaryPath, json);
                    File.Move(temporaryPath, _readyPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            _diagnostic($"[Readiness] Startup stage '{stage}' could not be recorded ({exception.GetType().Name}).");
        }
    }

    private bool RequestIsStillCurrent()
    {
        var current = ReadRequest(_requestPath);
        return current is not null && _request is not null &&
            string.Equals(current.Schema, RequestSchema, StringComparison.Ordinal) &&
            string.Equals(current.AttemptId, _request.AttemptId, StringComparison.Ordinal) &&
            string.Equals(current.Nonce, _request.Nonce, StringComparison.Ordinal) &&
            current.RequestedAtUtc == _request.RequestedAtUtc &&
            current.ExpiresAtUtc > _clock();
    }

    internal static bool IsValidRequest(StartupReadinessChallenge? request, DateTimeOffset now) =>
        request is not null &&
        string.Equals(request.Schema, RequestSchema, StringComparison.Ordinal) &&
        Guid.TryParse(request.AttemptId, out var attemptId) && attemptId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(request.Nonce) && request.Nonce.Length >= 40 &&
        request.RequestedAtUtc <= now.AddSeconds(5) &&
        request.ExpiresAtUtc > now &&
        request.ExpiresAtUtc <= request.RequestedAtUtc.AddMinutes(10);

    internal static bool IsEligibleProcess(ReadinessProcessIdentity identity, DateTimeOffset requestedAtUtc) =>
        identity.ProcessId > 0 &&
        identity.ProcessStartedAtUtc > requestedAtUtc &&
        identity.SessionId == 0 &&
        string.Equals(identity.UserSid, SystemSid, StringComparison.Ordinal);

    private static StartupReadinessChallenge? ReadRequest(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return JsonSerializer.Deserialize<StartupReadinessChallenge>(File.ReadAllText(path), JsonOptions);
    }

    [SupportedOSPlatform("windows")]
    private static ReadinessProcessIdentity GetCurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        return new ReadinessProcessIdentity(
            Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime()),
            process.SessionId,
            identity.User?.Value ?? string.Empty);
    }

    internal sealed record StartupReadinessChallenge(
        string Schema,
        string AttemptId,
        string Nonce,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed record ReadinessRecord(
        string Schema,
        string AttemptId,
        string Nonce,
        string Stage,
        int ProcessId,
        DateTimeOffset ProcessStartedAtUtc,
        int SessionId,
        string UserSid,
        string? AgentId,
        int? TenantId,
        ulong? ConnectionEpoch,
        string? ConnectionId,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset ObservedAtUtc);

    internal sealed record ReadinessProcessIdentity(
        int ProcessId,
        DateTimeOffset ProcessStartedAtUtc,
        int SessionId,
        string UserSid);
}
