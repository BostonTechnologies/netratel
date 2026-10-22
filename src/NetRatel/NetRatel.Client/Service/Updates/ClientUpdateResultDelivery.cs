using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Application.ClientAuth;

namespace NetRatel.Client.Service.Updates;

/// <summary>Delivery metadata accompanies the native result; it never changes the installation outcome.</summary>
internal sealed class ClientUpdateResultDelivery(
    string stateDirectory, string resultPath, string requestPath, string readyPath, string suspensionPath,
    IAgentTokenService tokens, HttpClient api, TimeProvider clock, object gate, Action<string> log)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _deliveryPath = Path.Combine(stateDirectory, "result-delivery.json");
    private long _failedAt;
    private TimeSpan _retryDelay;
    private string? _retryIdentity;
    private bool _localFaultReported;

    internal bool HasUnresolvedResult
    {
        get
        {
            lock (gate)
            {
                try
                {
                    var delivery = ReadDelivery();
                    if (delivery is { DeliveryState: "identity_conflict" })
                    {
                        delivery = ObserveIdentityConflict(delivery);
                        if (TryArchiveResumedIdentityConflict(delivery)) return File.Exists(resultPath);
                    }
                    if (delivery is { DeliveryState: "conflict" } && IsResumed(delivery))
                    {
                        ArchiveResult(delivery, unresolved: true);
                        return File.Exists(resultPath);
                    }
                    if (File.Exists(resultPath)) return true;
                    return File.Exists(_deliveryPath) && (delivery is null || delivery.DeliveryState is not ("acknowledged" or "archived_unresolved"));
                }
                catch (Exception exception) when (IsLocalStateFailure(exception)) { LocalDiagnostic(exception); return true; }
            }
        }
    }

    internal Guid? GetResumeAttemptId()
    {
        lock (gate)
        {
            var delivery = ReadDelivery();
            if (delivery is { DeliveryState: "identity_conflict" })
                return ObserveIdentityConflict(delivery).AttemptId;
            return delivery is { DeliveryState: "conflict" } ? delivery.AttemptId : null;
        }
    }

    internal bool CanClearSuspension(long resumeRevision, DateTimeOffset? resumedAt)
    {
        lock (gate)
        {
            var delivery = ReadDelivery();
            return delivery is not { DeliveryState: "identity_conflict" } ||
                IsFreshIdentityResume(ObserveIdentityConflict(delivery).IdentityConflict, resumeRevision, resumedAt);
        }
    }

    // One scheduled operation gets at most two HTTP requests (one rejected-token refresh).
    // Transient outages retain their durable next eligibility and continue with capped backoff.
    internal async Task DeliverPendingAsync(CancellationToken cancellationToken)
    {
        try { await DeliverCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsLocalStateFailure(exception)) { LocalDiagnostic(exception); }
    }

    private async Task DeliverCoreAsync(CancellationToken cancellationToken)
    {
        DeliveryRecord? delivery;
        try
        {
            lock (gate)
            {
                delivery = LoadPending();
                if (delivery is null || delivery.DeliveryState is not ("pending" or "retry_wait")) return;
                if (_retryIdentity == delivery.ResultHash && _retryDelay > TimeSpan.Zero)
                {
                    if (clock.GetElapsedTime(_failedAt) < _retryDelay) return;
                }
                else if (delivery.NextAttemptAtUtc is { } next && next > clock.GetUtcNow())
                {
                    // Bound a persisted wait after clock changes or a restart.
                    _retryIdentity = delivery.ResultHash;
                    _failedAt = clock.GetTimestamp();
                    _retryDelay = TimeSpan.FromSeconds(Math.Min((next - clock.GetUtcNow()).TotalSeconds, 300));
                    return;
                }
            }
        }
        catch (Exception exception) when (IsLocalStateFailure(exception))
        {
            LocalDiagnostic(exception);
            return;
        }

        var operation = "agent-authentication";
        try
        {
            for (var requestBudget = 0; requestBudget < 2; requestBudget++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var token = await tokens.GetAccessTokenAsync(requestCancellation.Token).ConfigureAwait(false);
                operation = "result-delivery";
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/v2/agent-updates/attempts/{delivery.AttemptId:D}/result")
                {
                    Content = JsonContent.Create(new { state = delivery.InstallationState,
                        delivery.FailureCode, delivery.Message, delivery.AdmissionNonce })
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && requestBudget == 0)
                {
                    tokens.InvalidateAccessToken(token.AccessToken);
                    operation = "agent-authentication-refresh";
                    continue;
                }
                var body = await ReadReplyAsync(response, requestCancellation.Token).ConfigureAwait(false);
                var reply = body.Reply;
                lock (gate)
                {
                    // An acknowledgement cannot consume an outcome that changed during the request.
                    var current = ReadDelivery();
                    if (current?.ResultHash != delivery.ResultHash ||
                        current.DeliveryState is not ("pending" or "retry_wait")) return;
                    var code = reply?.Code is "report_applied" or "report_duplicate" or "invalid_report_state" or
                        "attempt_not_found" or "state_conflict" or "accepted_authority_conflict" or "admission_mismatch" or
                        "newer_attempt_exists" or "invalid_report_details" ? reply.Code : $"http_{(int)response.StatusCode}";
                    if (response.IsSuccessStatusCode && (reply?.Outcome is "applied" or "duplicate" ||
                        response.StatusCode == HttpStatusCode.Accepted && body.IsEmpty))
                    {
                        delivery = delivery with { DeliveryState = "acknowledged", ReconciliationState = "complete",
                            ServerState = reply?.State, ConfirmationId = reply?.ConfirmationId,
                            PolicyRevision = reply?.PolicyRevision, LastCode = code, LastHttpStatus = (int)response.StatusCode,
                            NextAttemptAtUtc = null, UpdatedAtUtc = clock.GetUtcNow() };
                        Write(delivery);
                        if (reply is { SuspensionActive: false }) RemoveMatchingSuspension(delivery.AttemptId);
                        ArchiveResult(delivery);
                        Diagnostic(delivery, operation);
                        return;
                    }
                    if (response.StatusCode == HttpStatusCode.Conflict && reply?.Outcome == "conflict")
                    {
                        delivery = delivery with { DeliveryState = "conflict", ReconciliationState = "required",
                            ServerState = reply.State, ConfirmationId = reply.ConfirmationId, PolicyRevision = reply.PolicyRevision, LastCode = code,
                            LastHttpStatus = (int)response.StatusCode, NextAttemptAtUtc = null, UpdatedAtUtc = clock.GetUtcNow() };
                        Write(delivery);
                        if (reply.SuspensionActive is false) RemoveMatchingSuspension(delivery.AttemptId);
                        Diagnostic(delivery, operation);
                        return;
                    }
                    if (response.StatusCode == HttpStatusCode.NotFound && reply is { Outcome: "not_found", Code: "attempt_not_found" } ||
                        response.StatusCode == HttpStatusCode.BadRequest && reply is { Outcome: "invalid_state", Code: "invalid_report_state" or "invalid_report_details" })
                    {
                        delivery = delivery with { DeliveryState = "permanent_rejected", ReconciliationState = "required",
                            ServerState = reply.State, LastCode = code, LastHttpStatus = (int)response.StatusCode,
                            NextAttemptAtUtc = null, UpdatedAtUtc = clock.GetUtcNow() };
                        Write(delivery);
                        Diagnostic(delivery, operation);
                        return;
                    }
                    if (IsTransient(response.StatusCode))
                        Defer(delivery, code, (int)response.StatusCode, RetryAfter(response), operation);
                    else
                    {
                        delivery = delivery with { DeliveryState = "unclassified_response", ReconciliationState = "required",
                            LastCode = code, LastHttpStatus = (int)response.StatusCode, NextAttemptAtUtc = null,
                            UpdatedAtUtc = clock.GetUtcNow() };
                        Write(delivery);
                        Diagnostic(delivery, operation);
                    }
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                var current = ReadDelivery();
                if (current?.ResultHash != delivery.ResultHash) return;
                if (current.DeliveryState is not ("pending" or "retry_wait")) { LocalDiagnostic(exception); return; }
                var transient = exception is AgentClientAuthException { IsRecoverable: true } or
                    HttpRequestException { StatusCode: null } or OperationCanceledException or IOException ||
                    exception is HttpRequestException { StatusCode: { } httpStatus } && IsTransient(httpStatus);
                if (transient)
                    Defer(delivery, exception is AgentClientAuthException auth ? $"auth_{auth.FailureKind.ToString().ToLowerInvariant()}" :
                        exception is OperationCanceledException ? "transport_timeout" :
                        exception is HttpRequestException { StatusCode: { } status } ? $"http_{(int)status}" : "transport_error",
                        (exception as AgentClientAuthException)?.StatusCode,
                        (exception as AgentClientAuthException)?.RetryAfter, operation);
                else
                {
                    delivery = delivery with { DeliveryState = exception is AgentClientAuthException || operation.StartsWith("agent-authentication", StringComparison.Ordinal)
                            ? "authentication_attention" : "local_failure",
                        ReconciliationState = "required",
                        LastCode = exception is AgentClientAuthException authFailure
                            ? $"auth_{authFailure.FailureKind.ToString().ToLowerInvariant()}" : "local_operation_failed", NextAttemptAtUtc = null, UpdatedAtUtc = clock.GetUtcNow() };
                    Write(delivery);
                    Diagnostic(delivery, operation);
                }
            }
        }
    }

    private DeliveryRecord? LoadPending()
    {
        var existing = ReadDelivery();
        if (File.Exists(_deliveryPath) && existing is null)
            throw new InvalidDataException("Protected delivery metadata is unreadable.");
        if (existing is { DeliveryState: "identity_conflict" })
        {
            existing = ObserveIdentityConflict(existing);
            return TryArchiveResumedIdentityConflict(existing) ? null : existing;
        }
        if (!File.Exists(resultPath)) return existing?.DeliveryState is "acknowledged" or "archived_unresolved" ? null : existing;
        if (!ProtectedUpdateState.IsProtectedFile(resultPath))
            throw new UnauthorizedAccessException("Native update result permissions do not satisfy the protected-state contract.");
        if (new FileInfo(resultPath).Length > 64 * 1024)
            throw new InvalidDataException("Native update result exceeds the protected-state size limit.");
        var bytes = File.ReadAllBytes(resultPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (existing?.ResultHash == hash)
        {
            if (existing.DeliveryState == "acknowledged") ArchiveResult(existing);
            else if (existing.DeliveryState == "confirmation_missing") return ResolveAccepted(existing);
            return existing;
        }
        if (existing is { DeliveryState: "conflict" } && IsResumed(existing))
            ArchiveResult(existing, unresolved: true);
        if (existing is not null && existing.DeliveryState is not ("acknowledged" or "archived_unresolved") &&
            !(existing.DeliveryState == "conflict" && IsResumed(existing)))
        {
            // Keep the original payload embedded in protected metadata if another producer
            // changed result.json. The prior unresolved attempt continues to fence new work.
            var conflict = existing with { DeliveryState = "identity_conflict", ReconciliationState = "required",
                LastCode = "local_result_replaced", NextAttemptAtUtc = null, UpdatedAtUtc = clock.GetUtcNow() };
            conflict = ObserveIdentityConflict(conflict);
            Diagnostic(conflict, "local-state");
            return conflict;
        }
        // Windows PowerShell 5.1 emits a UTF-8 BOM. Hash/archive the original
        // bytes, while JSON parsing consumes the document after that prefix.
        var jsonOffset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(jsonOffset));
        var result = document.RootElement.Deserialize<NativeResult>(JsonOptions);
        if (result is null || result.Schema != "netratel.update.result.v2" ||
            result.AttemptId == Guid.Empty || result.ReleaseId == Guid.Empty || string.IsNullOrWhiteSpace(result.State)) return null;
        var request = ProtectedUpdateState.IsProtectedFile(requestPath) ? Read<RequestContext>(requestPath) : null;
        var delivery = new DeliveryRecord("netratel.update.result-delivery.v1", result.AttemptId, result.ReleaseId,
            hash, result.State, result.FailureCode, result.Message,
            request is not null && request.AttemptId == result.AttemptId && request.ReleaseId == result.ReleaseId ? request.AdmissionNonce : null,
            document.RootElement.Clone(), "pending", "pending", 0, null, null, null, null, null, null, clock.GetUtcNow());
        Write(delivery);
        // Suspension is a one-time installation effect. Replays and retries never recreate it.
        var policy = ReadResumeFence();
        if (result.State is "RolledBack" or "RollbackUnverified" && policy?.ResumeAttemptId != result.AttemptId)
            ProtectedUpdateState.AtomicWriteJson(suspensionPath, new { result.AttemptId, result.ReleaseId,
                reason = result.FailureCode, suspendedAtUtc = clock.GetUtcNow() });
        if (result.State == "Accepted") return ResolveAccepted(delivery);
        return delivery;
    }

    private DeliveryRecord ResolveAccepted(DeliveryRecord delivery)
    {
        var result = delivery.OriginalResult.Deserialize<NativeResult>(JsonOptions)!;
        var request = ProtectedUpdateState.IsProtectedFile(requestPath) ? Read<RequestContext>(requestPath) : null;
        var ready = ProtectedUpdateState.IsProtectedFile(readyPath) ? Read<ReadyContext>(readyPath) : null;
        var confirmed = ready is { Schema: "netratel.update.ready.v2" } && ready.AttemptId == result.AttemptId &&
            ready.ReleaseId == result.ReleaseId && ready.ConfirmationId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(result.ToVersion) && ready.Version == result.ToVersion && request is not null && request.ToVersion == result.ToVersion &&
            request.AttemptId == result.AttemptId && request.ReleaseId == result.ReleaseId &&
            !string.IsNullOrWhiteSpace(request.AdmissionNonce) && request.AdmissionNonce == delivery.AdmissionNonce;
        var updated = delivery with { DeliveryState = confirmed ? "acknowledged" : "confirmation_missing",
            ReconciliationState = confirmed ? "gateway_confirmed" : "required",
            ServerState = confirmed ? "Accepted" : null, ConfirmationId = confirmed ? ready!.ConfirmationId : null,
            LastCode = confirmed ? "gateway_confirmation_recorded" : "accepted_confirmation_missing" };
        if (updated != delivery)
        {
            Write(updated);
            Diagnostic(updated, "local-state");
        }
        if (confirmed) ArchiveResult(updated);
        return updated;
    }

    private void Defer(DeliveryRecord delivery, string code, int? status, TimeSpan? retryAfter, string operation)
    {
        var failures = Math.Min(delivery.RetryCount + 1, 20);
        var exponentialSeconds = Math.Min(30 * Math.Pow(2, Math.Min(failures - 1, 4)), 300);
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        var delay = TimeSpan.FromSeconds(Math.Min(exponentialSeconds * jitter, 300));
        if (retryAfter is { } requested && requested > delay)
            delay = TimeSpan.FromSeconds(Math.Min(requested.TotalSeconds, 300));
        _retryIdentity = delivery.ResultHash;
        _failedAt = clock.GetTimestamp();
        _retryDelay = delay;
        delivery = delivery with { DeliveryState = "retry_wait", ReconciliationState = "pending",
            RetryCount = failures, NextAttemptAtUtc = clock.GetUtcNow() + delay, LastCode = code,
            LastHttpStatus = status, UpdatedAtUtc = clock.GetUtcNow() };
        Write(delivery);
        Diagnostic(delivery, operation);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow());
        return delay is { } value ? TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, 300)) : null;
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.NotFound or
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized || (int)status >= 500;

    private static async Task<(ResultReply? Reply, bool IsEmpty)> ReadReplyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > 16 * 1024) return (null, false);
            buffer.Write(bytes, 0, read);
        }
        if (buffer.Length == 0) return (null, true);
        try { return (JsonSerializer.Deserialize<ResultReply>(buffer.ToArray(), JsonOptions), false); }
        catch (JsonException) { return (null, false); }
    }

    private void RemoveMatchingSuspension(Guid attemptId)
    {
        var suspension = Read<RequestContext>(suspensionPath);
        if (suspension?.AttemptId == attemptId) File.Delete(suspensionPath);
    }

    private ResumeFence? ReadResumeFence()
    {
        var policyPath = Path.Combine(stateDirectory, "policy.json");
        return ProtectedUpdateState.IsProtectedFile(policyPath) ? Read<ResumeFence>(policyPath) : null;
    }

    private bool IsResumed(DeliveryRecord delivery)
    {
        var fence = ReadResumeFence();
        return fence is { ResumeRevision: { } resumeRevision } && fence.ResumeAttemptId == delivery.AttemptId &&
            resumeRevision >= (delivery.PolicyRevision ?? 0);
    }

    private DeliveryRecord ObserveIdentityConflict(DeliveryRecord delivery, bool observeCurrentResult = false)
    {
        // A durable observation identifies the atomic move destination before
        // recovery starts. On restart inspect the bytes actually moved there.
        var retainedPath = delivery.IdentityConflict is { } retained ? RetainedResultArchive(delivery, retained) : null;
        var fromRetained = !observeCurrentResult && retainedPath is not null && File.Exists(retainedPath);
        var bytes = ReadProtectedBytes(fromRetained ? retainedPath! : resultPath, 64 * 1024);
        var hash = Hash(bytes);
        PreserveBytes(ReplacementArchive(delivery, hash), bytes);
        PreserveRecoveryState(delivery, _deliveryPath, "delivery", 128 * 1024);
        PreserveRecoveryState(delivery, requestPath, "request");
        PreserveRecoveryState(delivery, readyPath, "ready");
        PreserveRecoveryState(delivery, suspensionPath, "suspension");
        if (!observeCurrentResult && delivery.IdentityConflict?.ResultHash == hash) return delivery;

        var observation = new IdentityConflictObservation(hash, ReadResumeFence()?.Revision ?? 0, clock.GetUtcNow(),
            fromRetained ? delivery.IdentityConflict!.PreservationId : Guid.NewGuid());
        var observed = delivery with { IdentityConflict = observation, ArchivedBy = null,
            UpdatedAtUtc = observation.DetectedAtUtc };
        Write(observed);
        return observed;
    }

    private bool TryArchiveResumedIdentityConflict(DeliveryRecord delivery)
    {
        var conflict = delivery.IdentityConflict;
        var fence = ReadResumeFence();
        if (conflict is null || fence is not { ResumeRevision: { } revision, ResumedAtUtc: { } resumedAt } ||
            fence.ResumeAttemptId != delivery.AttemptId || fence.Revision < revision ||
            !IsFreshIdentityResume(conflict, revision, resumedAt))
            return false;

        // Require the operator action's authoritative timestamp as well as a
        // newer revision; late delivery of an older Resume is not authorization.
        var replacementPath = ReplacementArchive(delivery, conflict.ResultHash);
        var replacement = ReadProtectedBytes(replacementPath, 64 * 1024);
        if (Hash(replacement) != conflict.ResultHash)
            throw new InvalidDataException("The archived conflicting update result changed.");

        // The native producer is another process. Moving first preserves even
        // bytes replaced between our read and this operation; never check/delete.
        var retainedPath = RetainedResultArchive(delivery, conflict);
        if (!File.Exists(retainedPath)) File.Move(resultPath, retainedPath);
        if (!ReadProtectedBytes(retainedPath, 64 * 1024).AsSpan().SequenceEqual(replacement))
        {
            ObserveIdentityConflict(delivery);
            return false;
        }
        if (File.Exists(resultPath))
        {
            ObserveIdentityConflict(delivery, observeCurrentResult: true);
            return false;
        }

        var original = JsonSerializer.SerializeToUtf8Bytes(delivery.OriginalResult, JsonOptions);
        PreserveBytes(RecoveryArchive(delivery, "embedded-original", original), original);
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "netratel.update.identity-recovery.v1", originalDelivery = delivery,
            replacementSha256 = conflict.ResultHash, resumeAttemptId = fence.ResumeAttemptId,
            resumeRevision = revision, resumedAtUtc = resumedAt,
            reconciliationState = "required"
        }, JsonOptions);
        PreserveBytes(RecoveryArchive(delivery, "recovery", receipt), receipt);
        PreserveRecoveryState(delivery, requestPath, "request");
        PreserveRecoveryState(delivery, readyPath, "ready");
        PreserveRecoveryState(delivery, suspensionPath, "suspension");
        PreserveRecoveryState(delivery, Path.Combine(stateDirectory, "policy.json"), "policy");

        var archived = delivery with { DeliveryState = "archived_unresolved", ReconciliationState = "required",
            LastCode = "operator_resume_archived_identity_conflict", NextAttemptAtUtc = null,
            ArchivedBy = new RecoveryAuthorization(revision, resumedAt), UpdatedAtUtc = clock.GetUtcNow() };
        Write(archived);
        _retryIdentity = null;
        _retryDelay = TimeSpan.Zero;
        Diagnostic(archived, "operator-recovery");
        return true;
    }

    private static bool IsFreshIdentityResume(IdentityConflictObservation? conflict, long resumeRevision, DateTimeOffset? resumedAt) =>
        conflict is not null && resumeRevision > conflict.ObservedPolicyRevision &&
        resumedAt is { } authorizedAt && authorizedAt > conflict.DetectedAtUtc;

    private string ReplacementArchive(DeliveryRecord delivery, string hash) =>
        Path.Combine(stateDirectory, $"result-{delivery.AttemptId:D}-{hash}.replacement.json");

    private string RetainedResultArchive(DeliveryRecord delivery, IdentityConflictObservation observation) =>
        Path.Combine(stateDirectory, $"result-{delivery.AttemptId:D}-{observation.PreservationId:D}.retained.json");

    private string RecoveryArchive(DeliveryRecord delivery, string kind, byte[] bytes) =>
        Path.Combine(stateDirectory, $"result-{delivery.AttemptId:D}-{Hash(bytes)}.{kind}.json");

    private void PreserveRecoveryState(DeliveryRecord delivery, string path, string kind, int maximumBytes = 64 * 1024)
    {
        if (!File.Exists(path)) return;
        var bytes = ReadProtectedBytes(path, maximumBytes);
        PreserveBytes(RecoveryArchive(delivery, kind, bytes), bytes);
    }

    private static byte[] ReadProtectedBytes(string path, int maximumBytes)
    {
        if (!ProtectedUpdateState.IsProtectedFile(path) || new FileInfo(path).Length > maximumBytes)
            throw new InvalidDataException("Protected update recovery state is unreadable or oversized.");
        return File.ReadAllBytes(path);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void PreserveBytes(string destination, byte[] bytes)
    {
        if (File.Exists(destination))
        {
            if (!ReadProtectedBytes(destination, 256 * 1024).AsSpan().SequenceEqual(bytes))
                throw new IOException("Archived update recovery evidence differs.");
            return;
        }
        var temporary = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Never overwrite an earlier archive, including during a racing writer.
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ArchiveResult(DeliveryRecord delivery, bool unresolved = false)
    {
        var receipt = Path.Combine(stateDirectory, $"result-{delivery.AttemptId:D}-{delivery.ResultHash[..12]}.delivery.json");
        if (!File.Exists(receipt)) ProtectedUpdateState.AtomicWriteJson(receipt, delivery);
        if (!File.Exists(resultPath)) return;
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(resultPath))).ToLowerInvariant() != delivery.ResultHash) return;
        var destination = Path.Combine(stateDirectory,
            $"result-{delivery.AttemptId:D}-{delivery.ResultHash[..12]}.{(unresolved ? "unresolved" : "consumed")}.json");
        if (File.Exists(destination))
        {
            if (File.ReadAllText(destination) != File.ReadAllText(resultPath)) throw new IOException("Archived update result identity differs.");
            File.Delete(resultPath);
        }
        else File.Move(resultPath, destination);
    }

    private DeliveryRecord? ReadDelivery()
    {
        if (!ProtectedUpdateState.IsProtectedFile(_deliveryPath) || new FileInfo(_deliveryPath).Length > 128 * 1024) return null;
        var delivery = Read<DeliveryRecord>(_deliveryPath);
        if (delivery is null || delivery.Schema != "netratel.update.result-delivery.v1" ||
            delivery.AttemptId == Guid.Empty || delivery.ReleaseId == Guid.Empty ||
            delivery.ResultHash is not { Length: 64 } ||
            !delivery.ResultHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F') ||
            string.IsNullOrWhiteSpace(delivery.InstallationState) || delivery.OriginalResult.ValueKind != JsonValueKind.Object ||
            delivery.DeliveryState is not ("pending" or "retry_wait" or "acknowledged" or "conflict" or "permanent_rejected" or
                "unclassified_response" or "local_failure" or "authentication_attention" or "identity_conflict" or "confirmation_missing" or "archived_unresolved") ||
            delivery.ReconciliationState is not ("pending" or "required" or "complete" or "gateway_confirmed") ||
            delivery.RetryCount is < 0 or > 20 || delivery.UpdatedAtUtc == default) return null;
        if (delivery.IdentityConflict is { } conflict &&
            (conflict.ResultHash is not { Length: 64 } || !conflict.ResultHash.All(char.IsAsciiHexDigit) ||
             conflict.ObservedPolicyRevision < 0 || conflict.DetectedAtUtc == default || conflict.PreservationId == Guid.Empty)) return null;
        if (delivery.DeliveryState == "archived_unresolved" &&
            (delivery.IdentityConflict is not { } archivedConflict || delivery.ArchivedBy is not { } archivedBy ||
             archivedBy.ResumeRevision <= archivedConflict.ObservedPolicyRevision ||
             archivedBy.ResumedAtUtc <= archivedConflict.DetectedAtUtc || delivery.ReconciliationState != "required")) return null;
        try
        {
            var original = delivery.OriginalResult.Deserialize<NativeResult>(JsonOptions);
            return original is { Schema: "netratel.update.result.v2" } && original.AttemptId == delivery.AttemptId &&
                original.ReleaseId == delivery.ReleaseId && original.State == delivery.InstallationState &&
                original.FailureCode == delivery.FailureCode && original.Message == delivery.Message ? delivery : null;
        }
        catch (JsonException) { return null; }
    }
    private static bool IsLocalStateFailure(Exception exception) => exception is IOException or InvalidDataException or JsonException or
        UnauthorizedAccessException or System.Security.SecurityException or EntryPointNotFoundException;
    private void LocalDiagnostic(Exception exception)
    {
        if (_localFaultReported) return;
        _localFaultReported = true;
        log($"[UpdateManagement] stage=result-delivery/local-state delivery=local_state_unreadable reconciliation=required code={exception.GetType().Name}");
    }
    private static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default; }
        catch (JsonException) { return default; }
    }
    private void Write(DeliveryRecord delivery) => ProtectedUpdateState.AtomicWriteJson(_deliveryPath, delivery);
    private void Diagnostic(DeliveryRecord delivery, string operation) => log(
        $"[UpdateManagement] release={delivery.ReleaseId} attempt={delivery.AttemptId} stage={operation} " +
        $"endpoint=/api/v2/agent-updates/attempts/{{attemptId}}/result installation={delivery.InstallationState} " +
        $"delivery={delivery.DeliveryState} reconciliation={delivery.ReconciliationState} code={delivery.LastCode ?? "pending"} " +
        $"retry={(delivery.DeliveryState == "retry_wait" ? "deferred" : "stopped")} nextDelaySeconds={_retryDelay.TotalSeconds:0}");

    private sealed record NativeResult(string Schema, Guid AttemptId, Guid ReleaseId, string State, string? FailureCode, string? Message,
        string? ToVersion = null);
    private sealed record ResumeFence(long Revision, Guid? ResumeAttemptId, long? ResumeRevision, DateTimeOffset? ResumedAtUtc = null);
    private sealed record IdentityConflictObservation(string ResultHash, long ObservedPolicyRevision, DateTimeOffset DetectedAtUtc,
        Guid PreservationId);
    private sealed record RecoveryAuthorization(long ResumeRevision, DateTimeOffset ResumedAtUtc);
    private sealed record RequestContext(Guid AttemptId, Guid ReleaseId, string? AdmissionNonce, string? ToVersion = null);
    private sealed record ReadyContext(string Schema, Guid AttemptId, Guid ReleaseId, Guid ConfirmationId, string? Version);
    private sealed record ResultReply(string? Outcome, string? Code, string? State, Guid? ConfirmationId,
        bool? SuspensionActive, long? PolicyRevision);
    private sealed record DeliveryRecord(string Schema, Guid AttemptId, Guid ReleaseId, string ResultHash,
        string InstallationState, string? FailureCode, string? Message, string? AdmissionNonce, JsonElement OriginalResult,
        string DeliveryState, string ReconciliationState, int RetryCount, DateTimeOffset? NextAttemptAtUtc,
        string? LastCode, int? LastHttpStatus, string? ServerState, Guid? ConfirmationId, long? PolicyRevision, DateTimeOffset UpdatedAtUtc,
        IdentityConflictObservation? IdentityConflict = null, RecoveryAuthorization? ArchivedBy = null);
}
