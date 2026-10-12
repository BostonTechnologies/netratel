using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.API.Services;

public sealed class ClientServiceControlException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>Persists authorized intent before dispatch. Reusing an operation ID only reads that intent.</summary>
public sealed class ClientServiceControlService(
    OrchestratorDbContext db,
    ClientServicesCoordinator services,
    IAgentCommandAuthorityDispatcher commands,
    IAgentCommandGatewaySessionRegistry commandSessions,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClientServiceOperationDto> ReadAsync(ClientKey client, Guid operationId, CancellationToken cancellationToken)
    {
        var operation = await db.ClientServiceOperations.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == operationId && candidate.TenantId == client.TenantId && candidate.AgentId == client.AgentId,
            cancellationToken).ConfigureAwait(false) ?? throw new ClientServiceControlException(404, "service_operation_not_found");
        return ToDto(operation);
    }

    public async Task<ClientServiceOperationDto> RequestAsync(ClientKey client, ClientServiceControlRequest request, string actor, CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty || !ClientServiceControlRules.IsValidSelection(request.ServiceName, request.Platform, request.Action))
            throw new ClientServiceControlException(400, "invalid_service_selection");
        if (ClientServiceControlRules.IsProtectedService(request.ServiceName))
            throw new ClientServiceControlException(409, "agent_service_protected");
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > 256 || reason?.Any(char.IsControl) == true || string.IsNullOrWhiteSpace(actor) || actor.Length > 256)
            throw new ClientServiceControlException(400, "invalid_service_operation_reason");

        ClientServiceOperationRecord operation;
        await using (var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null)
        {
            var agent = await AgentLifecycleLock.LockAsync(db, client.TenantId, client.AgentId, cancellationToken).ConfigureAwait(false);
            if (agent is null || agent.DeletedAtUtc is not null || agent.SupersededAtUtc is not null)
                throw new ClientServiceControlException(404, "client_not_found");
            var existing = await db.ClientServiceOperations.SingleOrDefaultAsync(candidate => candidate.Id == request.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.TenantId != client.TenantId || existing.AgentId != client.AgentId || existing.Platform != request.Platform ||
                    existing.Action != request.Action || existing.RequestedBy != actor || existing.Reason != reason ||
                    !ClientServiceContractValidator.GetServiceNameComparer(request.Platform).Equals(existing.ServiceName, request.ServiceName))
                    throw new ClientServiceControlException(409, "service_operation_identity_mismatch");
                if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ToDto(existing);
            }
            if (!agent.IsEnabled || agent.Status != AgentStatus.Active || agent.RevokedAtUtc is not null)
                throw new ClientServiceControlException(409, "client_not_active");
            var inventory = await services.ReadAsync(client, cancellationToken).ConfigureAwait(false);
            if (!inventory.Connected || !commands.IsAvailable(client)) throw new ClientServiceControlException(409, "client_offline");
            if (!commandSessions.SupportsServiceControl(client)) throw new ClientServiceControlException(409, "service_control_unsupported");
            var comparer = ClientServiceContractValidator.GetServiceNameComparer(request.Platform);
            var observed = (inventory.LastCompleteInventory?.Services ?? inventory.LastPartialInventory?.Services ?? [])
                .Concat(inventory.WatchedServices).FirstOrDefault(service => service.Platform == request.Platform && comparer.Equals(service.Name, request.ServiceName));
            if (observed is null || observed.AuthoritativeMissing) throw new ClientServiceControlException(409, "service_not_in_inventory");
            var now = clock.GetUtcNow();
            var occupiedSince = now.Subtract(ClientServiceControlRules.ExecutionTimeout);
            if (await db.ClientServiceOperations.AnyAsync(candidate => candidate.TenantId == client.TenantId && candidate.AgentId == client.AgentId &&
                candidate.FinishedAtUtc == null && candidate.ExpiresAtUtc > occupiedSince, cancellationToken).ConfigureAwait(false))
                throw new ClientServiceControlException(409, "service_operation_in_progress");
            operation = new ClientServiceOperationRecord
            {
                Id = request.OperationId, TenantId = client.TenantId, AgentId = client.AgentId,
                ServiceName = observed.Name, Platform = request.Platform, Action = request.Action, RequestedBy = actor, Reason = reason,
                Status = "Pending", CreatedAtUtc = now, UpdatedAtUtc = now, ExpiresAtUtc = now.Add(ClientServiceControlRules.DispatchLifetime), Version = 1
            };
            db.ClientServiceOperations.Add(operation);
            AddAudit(operation, "agent.service.requested", "Service operation requested.", now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var payload = new ClientServiceControlPayload(operation.Id, operation.ServiceName, operation.Platform, operation.Action, operation.ExpiresAtUtc);
            await commands.DispatchAsync(client, operation.Id.ToString("N"), Correlation(operation.Id), ClientServiceControlRules.TaskType,
                JsonSerializer.Serialize(payload, JsonOptions), 0, cancellationToken).ConfigureAwait(false);
            // Gateway acknowledgement may have won this race; never regress its persisted status.
            await RecordLifecycleAsync(client, operation.Id.ToString("N"), CommandLifecycleStatus.Dispatched, clock.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AgentCommandGatewaySessionUnavailableException or AgentServiceControlUnavailableException)
        {
            operation = await db.ClientServiceOperations.SingleAsync(candidate => candidate.Id == request.OperationId, cancellationToken).ConfigureAwait(false);
            if (operation.Status == "Pending")
            {
                operation.Status = "Failed";
                operation.FailureCode = exception is AgentServiceControlUnavailableException ? "service_control_unsupported" : "client_offline";
                operation.FinishedAtUtc = operation.UpdatedAtUtc = clock.GetUtcNow();
                operation.Version++;
                AddAudit(operation, "agent.service.failed", "Service operation was not dispatched.", operation.UpdatedAtUtc);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        // A transport cancellation may have happened after dispatch. The durable Pending
        // record remains discoverable by its ID; neither a retry nor a server restart replays it.
        return await ReadAsync(client, request.OperationId, cancellationToken).ConfigureAwait(false);
    }

    public Task RecordLifecycleAsync(ClientKey client, string commandId, CommandLifecycleStatus status, DateTimeOffset at,
        string? resultJson, CancellationToken cancellationToken) =>
        RecordLifecycleAsync(client, commandId, status, at, resultJson, cancellationToken, 0);

    private async Task RecordLifecycleAsync(ClientKey client, string commandId, CommandLifecycleStatus status, DateTimeOffset at,
        string? resultJson, CancellationToken cancellationToken, int conflicts)
    {
        if (!Guid.TryParseExact(commandId, "N", out var operationId)) return;
        var operation = await db.ClientServiceOperations.SingleOrDefaultAsync(candidate => candidate.Id == operationId &&
            candidate.TenantId == client.TenantId && candidate.AgentId == client.AgentId, cancellationToken).ConfigureAwait(false);
        if (operation is null || ClientServiceControlRules.IsTerminal(operation.Status) || Rank(status.ToString()) <= Rank(operation.Status)) return;
        ClientServiceControlResult? result = null;
        if (status is CommandLifecycleStatus.Completed or CommandLifecycleStatus.Failed && resultJson is { Length: <= 8192 })
        {
            try { result = JsonSerializer.Deserialize<ClientServiceControlResult>(resultJson, JsonOptions); }
            catch (JsonException) { result = null; }
            if (result is not null && (result.OperationId != operation.Id || result.ServiceName != operation.ServiceName || result.Platform != operation.Platform ||
                result.Action != operation.Action || result.Success != (status == CommandLifecycleStatus.Completed) || !Enum.IsDefined(result.State) ||
                result.ObservedAtUtc == default || !ClientServiceContractValidator.IsValidErrorCode(result.ErrorCode) ||
                result.Success && (result.ErrorCode is not null || result.State != (operation.Action == ClientServiceControlAction.Stop ? ClientServiceState.Stopped : ClientServiceState.Running)) ||
                !result.Success && string.IsNullOrWhiteSpace(result.ErrorCode))) result = null;
        }
        var terminal = status is CommandLifecycleStatus.Completed or CommandLifecycleStatus.Failed or CommandLifecycleStatus.Cancelled;
        operation.Status = status == CommandLifecycleStatus.Completed && result is null ? "Failed" : status.ToString();
        operation.ResultJson = result is null ? null : JsonSerializer.Serialize(result, JsonOptions);
        operation.FailureCode = result?.ErrorCode ?? (terminal && operation.Status != "Completed" ?
            status == CommandLifecycleStatus.Cancelled ? "service_operation_cancelled" : "service_result_unconfirmed" : null);
        operation.UpdatedAtUtc = at;
        if (terminal) operation.FinishedAtUtc = at;
        operation.Version++;
        if (terminal) AddAudit(operation, operation.Status == "Completed" ? "agent.service.completed" : "agent.service.failed",
            operation.Status == "Completed" ? "Service operation completed." : "Service operation did not complete successfully.", at);
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException)
        {
            // Another acknowledgement already advanced this operation; discard this stale tracked view.
            db.Entry(operation).State = EntityState.Detached;
            foreach (var audit in db.ChangeTracker.Entries<OutboxMessage>().Where(entry => entry.State == EntityState.Added && entry.Entity.CorrelationId == Correlation(operationId)).ToArray())
                audit.State = EntityState.Detached;
            var current = await db.ClientServiceOperations.AsNoTracking().SingleAsync(candidate => candidate.Id == operationId, cancellationToken).ConfigureAwait(false);
            if (!ClientServiceControlRules.IsTerminal(current.Status) && Rank(current.Status) < Rank(operation.Status))
            {
                if (conflicts >= 2) throw;
                // A terminal receipt can race the HTTP dispatch acknowledgement.
                // Reapply it to the current version, rather than losing its outcome.
                await RecordLifecycleAsync(client, commandId, status, at, resultJson, cancellationToken, conflicts + 1).ConfigureAwait(false);
            }
        }
    }

    private ClientServiceOperationDto ToDto(ClientServiceOperationRecord operation)
    {
        var status = !ClientServiceControlRules.IsTerminal(operation.Status) && clock.GetUtcNow() > operation.ExpiresAtUtc.Add(ClientServiceControlRules.ExecutionTimeout)
            ? "OutcomeUnknown" : operation.Status;
        var result = operation.ResultJson is null ? null : JsonSerializer.Deserialize<ClientServiceControlResult>(operation.ResultJson, JsonOptions);
        return new(operation.Id, operation.TenantId, operation.AgentId, operation.ServiceName, operation.Platform, operation.Action, status,
            operation.CreatedAtUtc, operation.UpdatedAtUtc, result, operation.FailureCode,
            status == "OutcomeUnknown" || operation.FailureCode is "service_operation_timeout" or "service_result_unconfirmed" or "service_operation_cancelled");
    }

    private void AddAudit(ClientServiceOperationRecord operation, string type, string message, DateTimeOffset at) => db.OutboxMessages.Add(new OutboxMessage
    {
        Id = Guid.NewGuid(), OccurredUtc = at, Type = type, Source = "Agent", CorrelationId = Correlation(operation.Id),
        TenantId = operation.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture), EntityId = operation.AgentId.ToString("D"),
        Severity = operation.Status == "Failed" ? "Warning" : "Info", Message = message, Status = OutboxStatuses.Pending, NextAttemptUtc = at,
        PayloadJson = JsonSerializer.Serialize(new
        {
            operation.TenantId, operation.AgentId, OperationId = operation.Id, operation.ServiceName, operation.Platform,
            operation.Action, Actor = operation.RequestedBy, operation.Reason, operation.Status, operation.FailureCode
        }, JsonOptions)
    });

    private static string Correlation(Guid operationId) => $"client-service-{operationId:N}";
    private static int Rank(string status) => status switch { "Pending" => 0, "Created" => 1, "Dispatched" => 2, "Accepted" => 3, "Started" => 4, _ => 5 };
}
