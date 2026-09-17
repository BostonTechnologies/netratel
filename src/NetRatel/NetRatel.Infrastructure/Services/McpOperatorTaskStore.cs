using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Security;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Durable owner boundary for V2 one-shot tasks. The gateway-compatible task
/// activity projection is created in the same transaction as its owner row so
/// an unowned legacy activity can never become visible through this surface.
/// </summary>
public sealed class McpOperatorTaskStore(OrchestratorDbContext db) : IMcpOperatorTaskStore
{
    private const int MaximumResultSummaryBytes = 48 * 1024;
    private const int MaximumLogsPerRead = 100;
    private const int MaximumLifecycleWriteAttempts = 3;
    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorTaskLease> CreateOrGetAsync(McpOperatorTaskCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCreate(request);

        var existing = await FindByIdempotencyAsync(request.IdempotencyId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return SameAdmission(existing, request)
                ? ToLease(existing)
                : throw new InvalidOperationException("The task idempotency admission is already bound to another task.");

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            existing = await FindByIdempotencyAsync(request.IdempotencyId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SameAdmission(existing, request)
                    ? ToLease(existing)
                    : throw new InvalidOperationException("The task idempotency admission is already bound to another task.");
            }

            var access = request.Decision.Request;
            var constraints = request.Decision.EffectiveConstraints!;
            var active = await _db.McpOperatorTasks.CountAsync(candidate =>
                candidate.TenantId == access.TenantId &&
                candidate.AgentId == access.AgentId &&
                candidate.Subject == access.Principal.Subject &&
                candidate.ClientId == ClientId(access.Principal) &&
                candidate.State != "Completed" && candidate.State != "Failed" && candidate.State != "Cancelled", cancellationToken).ConfigureAwait(false);
            if (active >= constraints.MaxConcurrentCommands!.Value)
                throw new McpOperatorTaskLimitException("task_concurrency_limit_reached");

            var taskId = (await _db.JobTaskActivities.MaxAsync(candidate => (long?)candidate.Id, cancellationToken).ConfigureAwait(false) ?? 0L) + 1L;
            var activity = new JobTaskActivityRecord
            {
                Id = taskId,
                RequestId = request.CommandId,
                ClientIdentity = $"agent:{access.AgentId!.Value:D}",
                TenantId = access.TenantId,
                AgentId = access.AgentId,
                TaskType = request.TaskType,
                Status = "Pending",
                CreatedAtUtc = request.OccurredAtUtc
            };
            var record = new McpOperatorTaskRecord
            {
                Id = Guid.NewGuid(),
                TaskActivityId = taskId,
                CommandId = request.CommandId,
                TenantId = access.TenantId,
                AgentId = access.AgentId.Value,
                Subject = access.Principal.Subject,
                ClientId = ClientId(access.Principal),
                McpResource = access.McpResource!,
                McpInstance = access.McpInstance!,
                PolicyId = request.Decision.MatchingPolicyIds.Single(),
                PolicyVersion = request.Decision.SelectedPolicyVersion!.Value,
                TargetSetDigest = access.TargetSetDigest!,
                AcceptedAuditId = request.AcceptedAudit.AuditId,
                IdempotencyId = request.IdempotencyId,
                CorrelationId = request.CorrelationId,
                TaskType = request.TaskType,
                ShellType = request.ShellType,
                CommandHash = request.CommandHash,
                CommandLength = request.CommandLength,
                ScriptId = request.ScriptId,
                ScriptVersion = request.ScriptVersion,
                ScriptContentHash = request.ScriptContentHash,
                TimeoutSeconds = request.TimeoutSeconds,
                MaximumOutputBytes = request.MaximumOutputBytes,
                State = "Pending",
                CreatedAtUtc = request.OccurredAtUtc,
                UpdatedAtUtc = request.OccurredAtUtc,
                Version = 1
            };
            _db.JobTaskActivities.Add(activity);
            _db.McpOperatorTasks.Add(record);
            _db.McpOperatorTaskAudits.Add(new McpOperatorTaskAuditRecord
            {
                Id = Guid.NewGuid(),
                TaskRecordId = record.Id,
                Action = "create",
                AcceptedAuditId = request.AcceptedAudit.AuditId,
                OccurredAtUtc = request.OccurredAtUtc
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToLease(record);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            existing = await FindByIdempotencyAsync(request.IdempotencyId, cancellationToken).ConfigureAwait(false);
            if (existing is not null && SameAdmission(existing, request))
                return ToLease(existing);
            throw;
        }
    }

    public async Task<McpOperatorTaskLease?> GetOwnedAsync(long taskId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken)
    {
        if (taskId <= 0 || !IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance))
            return null;
        var record = await FindOwnedAsync(taskId, tenantId, agentId, principal, mcpResource, mcpInstance, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToLease(record);
    }

    public async Task<McpOperatorTaskLease?> GetOwnedByRequestIdAsync(string requestId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken)
    {
        if (!IsCommandId(requestId) || !IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance))
            return null;
        var record = await _db.McpOperatorTasks.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.CommandId == requestId &&
            candidate.TenantId == tenantId && candidate.AgentId == agentId &&
            candidate.Subject == principal.Subject && candidate.ClientId == ClientId(principal) &&
            candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance,
            cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToLease(record);
    }

    public async Task<IReadOnlyList<McpOperatorTaskLease>> ListOwnedAsync(int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, string? state, DateTimeOffset? sinceUtc, int limit, CancellationToken cancellationToken)
    {
        if (!IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance))
            return [];
        limit = Math.Clamp(limit, 1, MaximumLogsPerRead);
        var query = _db.McpOperatorTasks.AsNoTracking().Where(candidate =>
            candidate.TenantId == tenantId && candidate.AgentId == agentId &&
            candidate.Subject == principal.Subject && candidate.ClientId == ClientId(principal) &&
            candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance);
        if (!string.IsNullOrWhiteSpace(state))
            query = query.Where(candidate => candidate.State == state);
        if (sinceUtc.HasValue)
            query = query.Where(candidate => candidate.CreatedAtUtc >= sinceUtc.Value);
        var rows = await query.OrderByDescending(candidate => candidate.UpdatedAtUtc).ThenByDescending(candidate => candidate.TaskActivityId)
            .Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToLease).ToArray();
    }

    public async Task<IReadOnlyList<McpOperatorTaskLogLease>> ListLogsOwnedAsync(long taskId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, long sinceLogId, string? stream, int limit, CancellationToken cancellationToken)
    {
        if (taskId <= 0 || sinceLogId < 0 || !IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance))
            return [];
        var task = await FindOwnedAsync(taskId, tenantId, agentId, principal, mcpResource, mcpInstance, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return [];
        limit = Math.Clamp(limit, 1, MaximumLogsPerRead);
        var query = _db.JobTaskLogs.AsNoTracking().Where(log => log.JobTaskActivityId == taskId && log.Id > sinceLogId);
        if (!string.IsNullOrWhiteSpace(stream) && !string.Equals(stream, "all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(log => log.Stream == stream);
        var rows = await query.OrderBy(log => log.Id).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > 0)
            return rows.Select(log => new McpOperatorTaskLogLease(log.Id, taskId, task.CommandId, log.TimestampUtc, log.Stream, log.Message, log.Sequence)).ToArray();
        if (!IsTerminal(task.State) || string.IsNullOrWhiteSpace(task.ResultSummary) ||
            await _db.JobTaskLogs.AsNoTracking().AnyAsync(log => log.JobTaskActivityId == taskId, cancellationToken).ConfigureAwait(false))
            return [];

        // The command gateway persists terminal output as a result snapshot.
        // Project it only when no streamed rows exist, including outside this
        // page/filter. Ordinals belong to this immutable task snapshot and are
        // allocated before filtering so subsequent cursor reads stay stable.
        return ProjectTerminalLogs(task, sinceLogId, stream, limit);
    }

    private static IReadOnlyList<McpOperatorTaskLogLease> ProjectTerminalLogs(
        McpOperatorTaskRecord task, long sinceLogId, string? requestedStream, int limit)
    {
        var logs = new List<McpOperatorTaskLogLease>();
        try
        {
            using var document = JsonDocument.Parse(task.ResultSummary!);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            long ordinal = 0;
            foreach (var stream in new[] { "stdout", "stderr" })
            {
                if (!document.RootElement.TryGetProperty(stream, out var output)) continue;
                if (output.ValueKind == JsonValueKind.String) Add(output, stream);
                else if (output.ValueKind == JsonValueKind.Array)
                    foreach (var line in output.EnumerateArray())
                    {
                        Add(line, stream);
                        if (logs.Count == limit) break;
                    }
                if (logs.Count == limit) break;
            }

            void Add(JsonElement value, string stream)
            {
                if (value.ValueKind != JsonValueKind.String) return;
                var id = ++ordinal;
                if (id <= sinceLogId || (!string.IsNullOrWhiteSpace(requestedStream) &&
                    !string.Equals(requestedStream, "all", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(requestedStream, stream, StringComparison.Ordinal))) return;
                logs.Add(new McpOperatorTaskLogLease(id, task.TaskActivityId, task.CommandId,
                    task.CompletedAtUtc ?? task.UpdatedAtUtc, stream, value.GetString()!, id));
            }
        }
        catch (JsonException)
        {
            // Bounded summaries can be truncated. An unavailable or malformed
            // payload establishes no output, even if its prefix looks valid.
            return [];
        }
        return logs;
    }

    public async Task<McpOperatorTaskLease?> RequestCancellationAsync(McpOperatorTaskCancelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCancel(request);
        var record = await _db.McpOperatorTasks.SingleOrDefaultAsync(candidate => candidate.TaskActivityId == request.TaskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return null;
        var entry = _db.Entry(record);
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            if (entry.State == EntityState.Detached || !MatchesDecision(record, request.Decision))
                return null;
            if (IsTerminal(record.State) || record.CancellationRequested)
                return ToLease(record);

            record.CancellationRequested = true;
            record.CancellationRequestedAtUtc = request.OccurredAtUtc;
            record.State = "CancelRequested";
            record.UpdatedAtUtc = request.OccurredAtUtc;
            record.Version++;
            var audit = new McpOperatorTaskAuditRecord
            {
                Id = Guid.NewGuid(),
                TaskRecordId = record.Id,
                Action = "cancel",
                AcceptedAuditId = request.AcceptedAudit.AuditId,
                OccurredAtUtc = request.OccurredAtUtc
            };
            _db.McpOperatorTaskAudits.Add(audit);
            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToLease(record);
            }
            catch (DbUpdateConcurrencyException conflict) when (IsTaskConflict(conflict, record))
            {
                // SaveChanges rolls back the task and audit together. Remove
                // only this uncommitted audit so a retry cannot duplicate it or
                // leave it pending after observing another writer's terminal state.
                _db.Entry(audit).State = EntityState.Detached;
                if (attempt >= MaximumLifecycleWriteAttempts)
                    throw;
                await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task RecordLifecycleAsync(string commandId, int tenantId, Guid agentId, string state, string? resultSummary, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        if (!IsCommandId(commandId) || tenantId <= 0 || agentId == Guid.Empty || !IsKnownState(state))
            return;
        var record = await _db.McpOperatorTasks.SingleOrDefaultAsync(candidate =>
            candidate.CommandId == commandId && candidate.TenantId == tenantId && candidate.AgentId == agentId,
            cancellationToken).ConfigureAwait(false);
        if (record is null)
            return;
        var entry = _db.Entry(record);
        // The gateway retains this context across lifecycle frames, while HTTP
        // cancellation updates the same task in a separate scope.
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            if (entry.State == EntityState.Detached || IsTerminal(record.State))
                return;
            if (record.CancellationRequested && !IsTerminal(state))
                return;

            record.State = state;
            record.ResultSummary = BoundSummary(resultSummary);
            record.UpdatedAtUtc = occurredAtUtc;
            if (IsTerminal(state))
                record.CompletedAtUtc = occurredAtUtc;
            record.Version++;
            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException conflict) when (
                attempt < MaximumLifecycleWriteAttempts && IsTaskConflict(conflict, record))
            {
                // Cancellation may commit after our reload. Re-evaluate state
                // and retain cancellation metadata against the current version.
                await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTaskConflict(DbUpdateConcurrencyException conflict, McpOperatorTaskRecord record) =>
        conflict.Entries.Count == 1 && ReferenceEquals(conflict.Entries[0].Entity, record);

    private Task<McpOperatorTaskRecord?> FindByIdempotencyAsync(Guid idempotencyId, CancellationToken cancellationToken) =>
        _db.McpOperatorTasks.SingleOrDefaultAsync(candidate => candidate.IdempotencyId == idempotencyId, cancellationToken);

    private Task<McpOperatorTaskRecord?> FindOwnedAsync(long taskId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken) =>
        _db.McpOperatorTasks.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.TaskActivityId == taskId && candidate.TenantId == tenantId && candidate.AgentId == agentId &&
            candidate.Subject == principal.Subject && candidate.ClientId == ClientId(principal) &&
            candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance,
            cancellationToken);

    private static void ValidateCreate(McpOperatorTaskCreateRequest request)
    {
        var decision = request.Decision;
        var access = decision.Request;
        var constraints = decision.EffectiveConstraints;
        var command = request.TaskType == NetRatel.Shared.Contracts.Tasks.TaskKinds.ExecShellCommand;
        var script = request.TaskType == NetRatel.Shared.Contracts.Tasks.TaskKinds.ExecLibraryScript;
        if (!IsCommandId(request.CommandId) || request.IdempotencyId == Guid.Empty ||
            !decision.IsAllowed || access.Environment is not (McpOperatorEnvironment.Development or McpOperatorEnvironment.Production) ||
            access.AgentId is null || access.AgentId == Guid.Empty || decision.MatchingPolicyIds.Count != 1 || decision.SelectedPolicyVersion is null ||
            constraints is null || constraints.MaxConcurrentCommands is not > 0 || constraints.MaxTaskTargetCount is not > 0 || constraints.MaxFanOut is not > 0 ||
            request.TimeoutSeconds is < 1 || request.TimeoutSeconds > constraints.MaxCommandDurationSeconds ||
            request.MaximumOutputBytes is < 1 || request.MaximumOutputBytes > constraints.MaxOutputBytes ||
            !IsCorrelationId(request.CorrelationId) ||
            !AcceptedAuditMatches(request.AcceptedAudit, decision) ||
            !(command || script) ||
            (command && (!IsShell(request.ShellType) || !IsSha256(request.CommandHash) || request.CommandLength is < 1 or > 32 * 1024 ||
                         request.ScriptId is not null || request.ScriptVersion is not null || request.ScriptContentHash is not null)) ||
            (script && (request.ShellType is not null || request.CommandHash is not null || request.CommandLength != 0 ||
                        request.ScriptId is not > 0 || request.ScriptVersion is not > 0 || !IsSha256(request.ScriptContentHash))))
        {
            throw new ArgumentException("The task request is not an allowed, policy-frozen Production admission.", nameof(request));
        }
    }

    private static void ValidateCancel(McpOperatorTaskCancelRequest request)
    {
        if (request.TaskId <= 0 || !request.Decision.IsAllowed || request.Decision.Request.Environment is not (McpOperatorEnvironment.Development or McpOperatorEnvironment.Production) ||
            request.Decision.Request.AgentId is null || request.Decision.Request.AgentId == Guid.Empty ||
            !AcceptedAuditMatches(request.AcceptedAudit, request.Decision))
        {
            throw new ArgumentException("The task cancellation is not an allowed Production admission.", nameof(request));
        }
    }

    private static bool AcceptedAuditMatches(McpOperatorAcceptedAudit audit, McpOperatorDecision decision) =>
        audit.PolicyId == decision.MatchingPolicyIds.Single() &&
        audit.TenantId == decision.Request.TenantId && audit.AgentId == decision.Request.AgentId &&
        string.Equals(audit.Subject, decision.Request.Principal.Subject, StringComparison.Ordinal);

    private static bool SameAdmission(McpOperatorTaskRecord record, McpOperatorTaskCreateRequest request) =>
        record.CommandId == request.CommandId && record.TenantId == request.Decision.Request.TenantId &&
        record.AgentId == request.Decision.Request.AgentId && record.PolicyId == request.Decision.MatchingPolicyIds.Single() &&
        record.PolicyVersion == request.Decision.SelectedPolicyVersion && record.TaskType == request.TaskType &&
        record.CommandHash == request.CommandHash && record.ScriptId == request.ScriptId &&
        record.ScriptVersion == request.ScriptVersion && record.ScriptContentHash == request.ScriptContentHash;

    private static bool MatchesDecision(McpOperatorTaskRecord record, McpOperatorDecision decision) =>
        record.TenantId == decision.Request.TenantId && record.AgentId == decision.Request.AgentId &&
        record.Subject == decision.Request.Principal.Subject && record.ClientId == ClientId(decision.Request.Principal) &&
        record.McpResource == decision.Request.McpResource && record.McpInstance == decision.Request.McpInstance &&
        record.TargetSetDigest == decision.Request.TargetSetDigest;

    private static McpOperatorTaskLease ToLease(McpOperatorTaskRecord record) => new(
        record.TaskActivityId, record.CommandId, record.TenantId, record.AgentId, record.Subject, record.ClientId,
        record.McpResource, record.McpInstance, record.PolicyId, record.PolicyVersion, record.TargetSetDigest,
        record.TaskType, record.ShellType, record.ScriptId, record.ScriptVersion, record.ScriptContentHash,
        record.TimeoutSeconds, record.MaximumOutputBytes, record.State, record.ResultSummary, record.CancellationRequested,
        record.CorrelationId, record.CreatedAtUtc, record.UpdatedAtUtc, record.CompletedAtUtc, record.Version);

    private static string ClientId(McpOperatorPrincipal principal) => principal.ClientId ?? string.Empty;
    private static bool IsOwnerInputValid(int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance) =>
        tenantId > 0 && agentId != Guid.Empty && !string.IsNullOrWhiteSpace(principal.Subject) &&
        !string.IsNullOrWhiteSpace(mcpResource) && !string.IsNullOrWhiteSpace(mcpInstance);
    private static bool IsCommandId(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool IsShell(string? value) => value is { Length: > 0 and <= 32 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsCorrelationId(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => !char.IsControl(character));
    private static bool IsTerminal(string state) => state is "Completed" or "Failed" or "Cancelled";
    private static bool IsKnownState(string state) => state is "Processing" or "Completed" or "Failed" or "Cancelled";

    private static string? BoundSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var bounded = OperatorOutputRedactor.Redact(value.Trim());
        while (System.Text.Encoding.UTF8.GetByteCount(bounded) > MaximumResultSummaryBytes)
            bounded = bounded[..Math.Max(1, bounded.Length / 2)];
        return bounded;
    }
}
