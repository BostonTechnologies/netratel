using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Services;

public sealed class ClientOffboardingException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>Persists intent before dispatch and only decommissions on a verified helper receipt.</summary>
public sealed class ClientOffboardingService(
    OrchestratorDbContext db,
    IAgentManagementService agents,
    IClientPresenceRouter presence,
    IAgentControlSessionRegistry control,
    IDataProtectionProvider protection,
    TimeProvider clock,
    ILogger<ClientOffboardingService> logger)
{
    public const string Capability = "uninstall-v1";
    private readonly IDataProtector _protector = protection.CreateProtector("NetRatel.ClientOffboarding.Receipt.v1");
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(10);

    public async Task<ClientOffboardingDetailsDto> ReadAsync(ClientKey client, CancellationToken ct)
    {
        var agent = await db.Agents.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == client.TenantId && x.Id == client.AgentId && x.SupersededAtUtc == null, ct)
            .ConfigureAwait(false) ?? throw new ClientOffboardingException(404, "client_not_found");
        var operation = await db.ClientOffboardingOperations.AsNoTracking()
            .Where(x => x.TenantId == client.TenantId && x.AgentId == client.AgentId)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (agent.DeletedAtUtc is not null)
            return new(client.TenantId, client.AgentId, true, false, false, "client_removed", ToDto(operation));
        var availability = await ReadAvailabilityAsync(client, agent, ct).ConfigureAwait(false);
        return new(client.TenantId, client.AgentId, false, availability.Online, availability.Reason is null,
            availability.Reason, ToDto(operation));
    }

    public async Task<ClientOffboardingDetailsDto> RemoveAsync(ClientKey client, string? reason, string actor, CancellationToken ct)
    {
        await agents.DeleteAsync(client.TenantId, client.AgentId, NormalizeReason(reason), actor, ct).ConfigureAwait(false);
        return await ReadAsync(client, ct).ConfigureAwait(false);
    }

    public async Task<ClientOffboardingDetailsDto> RequestUninstallAsync(
        ClientKey client, ClientUninstallRequest request, string actor, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty) throw new ClientOffboardingException(400, "operation_id_required");
        var reason = NormalizeReason(request.Reason);
        var agent = await db.Agents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == client.TenantId && x.Id == client.AgentId && x.SupersededAtUtc == null, ct)
            .ConfigureAwait(false) ?? throw new ClientOffboardingException(404, "client_not_found");
        if (agent.DeletedAtUtc is not null) return await ReadAsync(client, ct).ConfigureAwait(false);
        var availability = await ReadAvailabilityAsync(client, agent, ct).ConfigureAwait(false);
        if (availability.Reason is { } unavailable) throw new ClientOffboardingException(409, unavailable);
        var owner = availability.Owner!.Value;

        UninstallRequest command;
        await using (var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null)
        {
            agent = await AgentLifecycleLock.LockAsync(db, client.TenantId, client.AgentId, ct).ConfigureAwait(false)
                ?? throw new ClientOffboardingException(404, "client_not_found");
            if (agent.DeletedAtUtc is not null || !agent.IsEnabled || agent.RevokedAtUtc is not null)
                throw new ClientOffboardingException(409, "client_not_active");
            if (db.Database.IsNpgsql() && !await ClientConnectionEpochStore.LockEffectiveOwnerAsync(db, owner, clock, ct).ConfigureAwait(false))
                throw new ClientOffboardingException(409, "client_session_changed");
            var requested = await db.ClientOffboardingOperations.SingleOrDefaultAsync(x => x.Id == request.OperationId, ct).ConfigureAwait(false);
            if (requested is not null && (requested.TenantId != client.TenantId || requested.AgentId != client.AgentId))
                throw new ClientOffboardingException(409, "operation_identity_mismatch");
            if (requested?.FinishedAtUtc is not null)
            {
                if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
                return await ReadAsync(client, ct).ConfigureAwait(false);
            }
            var operation = requested ?? await db.ClientOffboardingOperations
                .SingleOrDefaultAsync(x => x.TenantId == client.TenantId && x.AgentId == client.AgentId && x.FinishedAtUtc == null, ct)
                .ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (operation is null)
            {
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                operation = new ClientOffboardingOperation
                {
                    Id = request.OperationId, TenantId = client.TenantId, AgentId = client.AgentId,
                    RequestedBy = actor, Reason = reason, State = ClientOffboardingState.Requested,
                    ExpectedKeyFingerprint = agent.PublicKeyFingerprint!, CompletionTokenHash = Hash(secret),
                    ProtectedCompletionToken = _protector.Protect(secret), CreatedAtUtc = now,
                    ReceiptExpiresAtUtc = now.AddHours(24)
                };
                db.ClientOffboardingOperations.Add(operation);
                AddAudit(operation, "agent.offboarding.requested", "Client uninstall requested.");
            }
            if (operation.ReceiptExpiresAtUtc <= now)
                throw new ClientOffboardingException(409, "uninstall_confirmation_expired");
            // Concurrent retries of one command must carry the same payload so the live session can coalesce them.
            if (operation.ConnectionId != owner.ConnectionId || operation.ConnectionEpoch != owner.Epoch ||
                operation.StartDeadlineUtc <= now)
                operation.StartDeadlineUtc = now.AddMinutes(5);
            operation.ConnectionId = owner.ConnectionId;
            operation.ConnectionEpoch = owner.Epoch;
            operation.UpdatedAtUtc = now;
            command = new UninstallRequest
            {
                OperationId = operation.Id.ToString("D"),
                CompletionToken = _protector.Unprotect(operation.ProtectedCompletionToken!),
                StartDeadlineUtc = Timestamp.FromDateTimeOffset(operation.StartDeadlineUtc),
                ExpectedKeyFingerprint = operation.ExpectedKeyFingerprint
            };
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        // A disconnected browser or timed-out acknowledgement cannot undo durable intent or prove failure.
        try
        {
            var acknowledgement = await control.RequestUninstallAsync(client, owner.ConnectionId, checked((ulong)owner.Epoch),
                command, AcknowledgementTimeout, ct).ConfigureAwait(false);
            await RecordAcknowledgementAsync(client, Guid.Parse(command.OperationId), acknowledgement, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or AgentControlSessionUnavailableException ||
                                          exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Uninstall acknowledgement pending. tenantId={TenantId}, agentId={AgentId}, operationId={OperationId}",
                client.TenantId, client.AgentId, command.OperationId);
            return await ReadAsync(client, ct).ConfigureAwait(false);
        }
        return await ReadAsync(client, ct).ConfigureAwait(false);
    }

    private async Task RecordAcknowledgementAsync(ClientKey client, Guid operationId, UninstallAcknowledgement acknowledgement, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        _ = await AgentLifecycleLock.LockAsync(db, client.TenantId, client.AgentId, ct).ConfigureAwait(false);
        var operation = await db.ClientOffboardingOperations.SingleAsync(x => x.Id == operationId && x.TenantId == client.TenantId && x.AgentId == client.AgentId, ct)
            .ConfigureAwait(false);
        if (operation.FinishedAtUtc is null)
        {
            // A negative duplicate cannot roll back an earlier acceptance. The helper receipt owns final outcome.
            if (acknowledgement.Accepted)
            {
                operation.State = ClientOffboardingState.Accepted;
                operation.FailureCode = null;
            }
            else if (operation.State == ClientOffboardingState.Requested)
            {
                operation.FailureCode = NormalizeFailure(acknowledgement.FailureCode);
                // Launch can have succeeded even if the client could not observe it. Retain the receipt token
                // and operation identity for a safe retry or a delayed completion from the independent helper.
                AddAudit(operation, "agent.offboarding.request_rejected", "Client could not confirm uninstall launch.");
            }
            operation.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(UninstallReceipt receipt, CancellationToken ct)
    {
        if (receipt.TenantId <= 0 || !Guid.TryParse(receipt.ClientId, out var agentId) || agentId == Guid.Empty ||
            !Guid.TryParse(receipt.OperationId, out var operationId) || operationId == Guid.Empty ||
            receipt.CompletionToken.Length != 64 || receipt.FailureCode.Length > 128 ||
            receipt.Success && (!receipt.ServiceRemoved || !receipt.FilesRemoved || receipt.FailureCode.Length != 0))
            return false;
        var client = new ClientKey(receipt.TenantId, agentId);
        var candidate = await db.ClientOffboardingOperations.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == operationId && x.TenantId == client.TenantId && x.AgentId == client.AgentId, ct).ConfigureAwait(false);
        if (candidate is null || !HasToken(candidate, receipt.CompletionToken)) return false;
        var failure = receipt.Success ? null : NormalizeFailure(receipt.FailureCode);
        var receiptHash = Hash($"{receipt.Success}:{receipt.ServiceRemoved}:{receipt.FilesRemoved}:{failure}");
        // Identical terminal receipts remain idempotent, including when the first response was lost.
        if (candidate.ReceiptHash is not null) return candidate.ReceiptHash == receiptHash;
        if (candidate.FinishedAtUtc is not null || candidate.ReceiptExpiresAtUtc <= clock.GetUtcNow()) return false;

        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        if (await AgentLifecycleLock.LockAsync(db, client.TenantId, client.AgentId, ct).ConfigureAwait(false) is null) return false;
        var operation = await db.ClientOffboardingOperations.SingleAsync(x => x.Id == operationId, ct).ConfigureAwait(false);
        if (!HasToken(operation, receipt.CompletionToken)) return false;
        if (operation.ReceiptHash is not null) return operation.ReceiptHash == receiptHash;
        if (operation.FinishedAtUtc is not null || operation.ReceiptExpiresAtUtc <= clock.GetUtcNow()) return false;
        operation.ServiceRemoved = receipt.ServiceRemoved;
        operation.FilesRemoved = receipt.FilesRemoved;
        operation.ReceiptHash = receiptHash;
        operation.ProtectedCompletionToken = null;
        operation.FailureCode = failure;
        operation.State = receipt.Success ? ClientOffboardingState.Completed : ClientOffboardingState.Failed;
        operation.UpdatedAtUtc = clock.GetUtcNow();
        operation.FinishedAtUtc = operation.UpdatedAtUtc;
        AddAudit(operation, receipt.Success ? "agent.offboarding.completed" : "agent.offboarding.failed",
            receipt.Success ? "Client uninstall completed." : "Client uninstall failed.");
        if (receipt.Success)
            await agents.DeleteAsync(client.TenantId, client.AgentId, operation.Reason, operation.RequestedBy, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<Availability> ReadAvailabilityAsync(ClientKey client, Agent agent, CancellationToken ct)
    {
        if (!agent.IsEnabled || agent.RevokedAtUtc is not null) return new(false, null, "client_not_active");
        var snapshot = await presence.GetSnapshotAsync(client, ct).ConfigureAwait(false);
        if (!snapshot.IsAuthoritative || snapshot.Status != ClientPresenceStatus.Online || snapshot.ConnectionId is not { } connectionId ||
            snapshot.ConnectionEpoch is not > 0) return new(false, null, "client_offline");
        var owner = new OwnerKey(client, connectionId, snapshot.ConnectionEpoch.Value);
        var session = control.GetAvailability(client);
        if (session is null || session.ConnectionId != connectionId || session.ConnectionEpoch != checked((ulong)owner.Epoch))
            return new(true, owner, "control_session_unavailable");
        if (!session.Capabilities.Contains(Capability, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(agent.PublicKeyFingerprint))
            return new(true, owner, "uninstall_not_supported");
        return new(true, owner, null);
    }

    private ClientOffboardingOperationDto? ToDto(ClientOffboardingOperation? operation) => operation is null ? null : new(
        operation.Id,
        operation.FinishedAtUtc is null && operation.StartDeadlineUtc < clock.GetUtcNow() ? "confirmation_pending" : operation.State.ToString().ToLowerInvariant(),
        operation.CreatedAtUtc, operation.UpdatedAtUtc, operation.ReceiptExpiresAtUtc,
        operation.FailureCode, operation.ServiceRemoved, operation.FilesRemoved);

    private void AddAudit(ClientOffboardingOperation operation, string type, string message) => db.OutboxMessages.Add(new OutboxMessage
    {
        Id = Guid.NewGuid(), OccurredUtc = clock.GetUtcNow(), Type = type,
        PayloadJson = JsonSerializer.Serialize(new { operation.TenantId, operation.AgentId, OperationId = operation.Id, Actor = operation.RequestedBy, operation.Reason }),
        Source = "Agent", CorrelationId = $"client-offboarding-{operation.Id:N}", TenantId = operation.TenantId.ToString(),
        EntityId = operation.AgentId.ToString(), Severity = "Info", Message = message,
        Status = OutboxStatuses.Pending, NextAttemptUtc = clock.GetUtcNow()
    });

    private static string NormalizeReason(string? reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) ? "Removed by operator" : reason.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl)) throw new ClientOffboardingException(400, "invalid_removal_reason");
        return normalized;
    }

    private static string NormalizeFailure(string code) => code.Length is > 0 and <= 128 &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? code : "uninstall_failed";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool HasToken(ClientOffboardingOperation operation, string token) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(operation.CompletionTokenHash), Encoding.ASCII.GetBytes(Hash(token)));
    private sealed record Availability(bool Online, OwnerKey? Owner, string? Reason);
}
