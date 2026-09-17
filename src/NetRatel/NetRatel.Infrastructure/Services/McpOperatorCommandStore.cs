using System.Data;
using System.Text;
using NetRatel.Shared.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Durable ownership and frozen-policy state for one-shot Production commands.
/// Raw command text and environment values remain outside this store. Curated,
/// bounded terminal output follows the same durable caller ownership as state.
/// </summary>
public sealed class McpOperatorCommandStore(OrchestratorDbContext db) : IMcpOperatorCommandStore
{
    private const int MaximumCommandIdLength = 64;
    private const int MaximumLifecycleWriteAttempts = 3;
    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorCommandLease> CreateOrGetAsync(
        McpOperatorCommandCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCreate(request);

        var existing = await FindByIdempotencyAsync(request.IdempotencyId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return SameAdmission(existing, request)
                ? ToLease(existing)
                : throw new InvalidOperationException("The command idempotency admission is already bound to another command.");

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
                    : throw new InvalidOperationException("The command idempotency admission is already bound to another command.");
            }

            var access = request.Decision.Request;
            var constraints = request.Decision.EffectiveConstraints!;
            var concurrent = await _db.McpOperatorCommands.CountAsync(candidate =>
                candidate.TenantId == access.TenantId &&
                candidate.AgentId == access.AgentId &&
                candidate.Subject == access.Principal.Subject &&
                candidate.ClientId == ClientId(access.Principal) &&
                (candidate.State == McpOperatorCommandState.Pending ||
                 candidate.State == McpOperatorCommandState.Dispatched ||
                 candidate.State == McpOperatorCommandState.Accepted ||
                 candidate.State == McpOperatorCommandState.Started ||
                 candidate.State == McpOperatorCommandState.CancelRequested),
                cancellationToken).ConfigureAwait(false);
            if (concurrent >= constraints.MaxConcurrentCommands!.Value)
                throw new McpOperatorCommandLimitException("command_concurrency_limit_reached");

            var record = new McpOperatorCommandRecord
            {
                Id = Guid.NewGuid(),
                CommandId = request.CommandId,
                TenantId = access.TenantId,
                AgentId = access.AgentId!.Value,
                Subject = access.Principal.Subject,
                ClientId = ClientId(access.Principal),
                McpResource = access.McpResource!,
                McpInstance = access.McpInstance!,
                PolicyId = request.Decision.MatchingPolicyIds.Single(),
                PolicyVersion = request.Decision.SelectedPolicyVersion!.Value,
                AcceptedAuditId = request.AcceptedAudit.AuditId,
                IdempotencyId = request.IdempotencyId,
                CorrelationId = request.CorrelationId,
                ShellType = request.ShellType,
                WorkingDirectory = request.WorkingDirectory,
                CommandHash = request.CommandHash,
                CommandLength = request.CommandLength,
                EnvironmentReferencesJson = JsonSerializer.Serialize(request.EnvironmentReferences.ToArray(), McpOperatorJsonContext.Default.StringArray),
                TimeoutSeconds = request.TimeoutSeconds,
                MaximumOutputBytes = request.MaximumOutputBytes,
                EffectiveConstraintsJson = JsonSerializer.Serialize(constraints, McpOperatorJsonContext.Default.McpOperatorConstraints),
                State = McpOperatorCommandState.Pending,
                CreatedAtUtc = request.CreatedAtUtc,
                LastUpdatedAtUtc = request.CreatedAtUtc,
                Version = 1
            };
            _db.McpOperatorCommands.Add(record);
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

    public async Task<McpOperatorCommandLease?> GetAsync(string commandId, CancellationToken cancellationToken)
    {
        if (!IsCommandId(commandId))
            return null;
        var record = await _db.McpOperatorCommands.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.CommandId == commandId,
            cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToLease(record);
    }

    public async Task<McpOperatorCommandLease?> GetOwnedAsync(
        string commandId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken)
    {
        if (!IsCommandId(commandId) || tenantId <= 0 || agentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(principal.Subject) || string.IsNullOrWhiteSpace(mcpResource) || string.IsNullOrWhiteSpace(mcpInstance))
        {
            return null;
        }

        var record = await _db.McpOperatorCommands.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.CommandId == commandId &&
            candidate.TenantId == tenantId &&
            candidate.AgentId == agentId &&
            candidate.Subject == principal.Subject &&
            candidate.ClientId == ClientId(principal) &&
            candidate.McpResource == mcpResource &&
            candidate.McpInstance == mcpInstance,
            cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToLease(record);
    }

    public async Task<McpOperatorCommandLease?> RequestCancelAsync(
        string commandId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = await FindMutableAsync(commandId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return null;
        var entry = _db.Entry(record);
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            if (entry.State == EntityState.Detached)
                return null;
            if (IsTerminal(record.State) || record.State == McpOperatorCommandState.CancelRequested)
                return ToLease(record);

            record.State = McpOperatorCommandState.CancelRequested;
            record.LastUpdatedAtUtc = now;
            record.Version++;
            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToLease(record);
            }
            catch (DbUpdateConcurrencyException conflict) when (
                attempt < MaximumLifecycleWriteAttempts && IsCommandConflict(conflict, record))
            {
                // A terminal gateway update may win after the reload. Re-read
                // and re-evaluate it instead of overwriting its final result.
                await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task RecordLifecycleAsync(
        string commandId,
        int tenantId,
        Guid agentId,
        McpOperatorCommandState state,
        DateTimeOffset now,
        string? failureCode,
        CancellationToken cancellationToken,
        string? resultJson = null,
        int? exitCode = null)
    {
        if (!IsCommandId(commandId) || tenantId <= 0 || agentId == Guid.Empty)
            return;
        var record = await _db.McpOperatorCommands.SingleOrDefaultAsync(candidate =>
            candidate.CommandId == commandId && candidate.TenantId == tenantId && candidate.AgentId == agentId,
            cancellationToken).ConfigureAwait(false);
        if (record is null)
            return;
        var entry = _db.Entry(record);
        // This store can live for an entire gRPC connection. A tracking query
        // alone returns old values after a separate HTTP scope cancels a command.
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            if (entry.State == EntityState.Detached || IsTerminal(record.State))
                return;

            // A delayed nonterminal notification must not revive a cancelled
            // command. The fenced router still supplies its terminal status.
            if (record.State == McpOperatorCommandState.CancelRequested &&
                state is McpOperatorCommandState.Pending or McpOperatorCommandState.Dispatched or McpOperatorCommandState.Accepted or McpOperatorCommandState.Started)
            {
                return;
            }

            if (IsTerminal(state))
                record.OutputJson = JsonSerializer.Serialize(CurateOutput(resultJson, exitCode, record.MaximumOutputBytes),
                    McpOperatorJsonContext.Default.McpOperatorCommandOutput);
            record.State = state;
            record.LastUpdatedAtUtc = now;
            record.FailureCode = state == McpOperatorCommandState.Failed ? BoundedFailureCode(failureCode) : null;
            record.Version++;
            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException conflict) when (
                attempt < MaximumLifecycleWriteAttempts && IsCommandConflict(conflict, record))
            {
                // Preserve optimistic concurrency when cancellation races this
                // write; each retry applies the state guards to current values.
                await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsCommandConflict(DbUpdateConcurrencyException conflict, McpOperatorCommandRecord record) =>
        conflict.Entries.Count == 1 && ReferenceEquals(conflict.Entries[0].Entity, record);

    private Task<McpOperatorCommandRecord?> FindByIdempotencyAsync(Guid idempotencyId, CancellationToken cancellationToken) =>
        _db.McpOperatorCommands.SingleOrDefaultAsync(candidate => candidate.IdempotencyId == idempotencyId, cancellationToken);

    private Task<McpOperatorCommandRecord?> FindMutableAsync(string commandId, CancellationToken cancellationToken) =>
        !IsCommandId(commandId)
            ? Task.FromResult<McpOperatorCommandRecord?>(null)
            : _db.McpOperatorCommands.SingleOrDefaultAsync(candidate => candidate.CommandId == commandId, cancellationToken);

    private static void ValidateCreate(McpOperatorCommandCreateRequest request)
    {
        var decision = request.Decision;
        var access = decision.Request;
        var constraints = decision.EffectiveConstraints;
        if (!IsCommandId(request.CommandId) || request.IdempotencyId == Guid.Empty ||
            !decision.IsAllowed || access.Environment is not (McpOperatorEnvironment.Development or McpOperatorEnvironment.Production) ||
            access.AgentId is null || access.AgentId == Guid.Empty || decision.MatchingPolicyIds.Count != 1 || decision.SelectedPolicyVersion is null ||
            constraints is null || constraints.MaxCommandDurationSeconds is not > 0 || constraints.MaxConcurrentCommands is not > 0 ||
            constraints.MaxOutputBytes is not > 0 || constraints.MaxTaskTargetCount is not > 0 || constraints.MaxFanOut is not > 0 ||
            constraints.AllowedShells is not { Count: > 0 } || constraints.WorkingDirectories is not { Count: > 0 } ||
            !IsSafeShell(request.ShellType) || !constraints.AllowedShells.Contains(request.ShellType, StringComparer.OrdinalIgnoreCase) ||
            !IsWorkingDirectoryAllowed(request.WorkingDirectory, constraints.WorkingDirectories) ||
            !IsSha256(request.CommandHash) || request.CommandLength is < 1 or > 32 * 1024 ||
            request.TimeoutSeconds is < 1 || request.TimeoutSeconds > constraints.MaxCommandDurationSeconds ||
            request.MaximumOutputBytes is < 1 || request.MaximumOutputBytes > constraints.MaxOutputBytes ||
            !IsCorrelationId(request.CorrelationId) || !AreEnvironmentReferencesSafe(request.EnvironmentReferences) ||
            request.AcceptedAudit.PolicyId != decision.MatchingPolicyIds[0] ||
            request.AcceptedAudit.TenantId != access.TenantId || request.AcceptedAudit.AgentId != access.AgentId ||
            !string.Equals(request.AcceptedAudit.Subject, access.Principal.Subject, StringComparison.Ordinal))
        {
            throw new ArgumentException("The command request is not an allowed, policy-frozen Production admission.", nameof(request));
        }
    }

    private static bool SameAdmission(McpOperatorCommandRecord record, McpOperatorCommandCreateRequest request) =>
        record.CommandId == request.CommandId &&
        record.TenantId == request.Decision.Request.TenantId &&
        record.AgentId == request.Decision.Request.AgentId &&
        record.PolicyId == request.Decision.MatchingPolicyIds.Single() &&
        record.PolicyVersion == request.Decision.SelectedPolicyVersion &&
        record.AcceptedAuditId == request.AcceptedAudit.AuditId &&
        record.CommandHash == request.CommandHash;

    private static bool IsTerminal(McpOperatorCommandState state) =>
        state is McpOperatorCommandState.Completed or McpOperatorCommandState.Failed or McpOperatorCommandState.Cancelled;

    private static bool IsCommandId(string? value) =>
        value is { Length: 32 and <= MaximumCommandIdLength } && value.All(char.IsAsciiHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool IsSafeShell(string? value) =>
        value is { Length: > 0 and <= 32 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsWorkingDirectoryAllowed(string? value, IReadOnlyList<string> allowed) =>
        value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl) && allowed.AllowsWorkingDirectory(value);

    private static bool IsCorrelationId(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(character => !char.IsControl(character));

    private static bool AreEnvironmentReferencesSafe(IReadOnlyList<string>? values) =>
        values is { Count: <= 32 } && values.All(value =>
            value is { Length: > 0 and <= 128 } &&
            (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static string ClientId(McpOperatorPrincipal principal) => principal.ClientId ?? string.Empty;

    private static string? BoundedFailureCode(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : null;

    private static McpOperatorCommandOutput CurateOutput(string? json, int? exitCode, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new(exitCode, [], [], false, "agent_result_missing");
        if (json.Length > 65536)
            return new(exitCode, [], [], true, "agent_result_too_large");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(exitCode, [], [], false, "agent_result_invalid");
            var remaining = Math.Clamp(maximumBytes, 0, 49152);
            var truncated = root.TryGetProperty("outputTruncated", out var flag) && flag.ValueKind == JsonValueKind.True;
            var stdout = ReadLines(root, "stdout", ref remaining, ref truncated);
            var stderr = ReadLines(root, "stderr", ref remaining, ref truncated);
            return new(exitCode, stdout, stderr, truncated);
        }
        catch (JsonException) { return new(exitCode, [], [], false, "agent_result_invalid"); }
    }

    private static string[] ReadLines(JsonElement root, string property, ref int remaining, ref bool truncated)
    {
        if (!root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        var lines = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            if (remaining <= 0 || lines.Count >= 256) { truncated = true; break; }
            var text = OperatorOutputRedactor.Redact(value.GetString()!);
            var builder = new StringBuilder();
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Utf8SequenceLength > remaining) { truncated = true; remaining = 0; break; }
                builder.Append(rune.ToString());
                remaining -= rune.Utf8SequenceLength;
            }
            lines.Add(builder.ToString());
            remaining = Math.Max(0, remaining - 1);
        }
        return lines.ToArray();
    }

    private static McpOperatorCommandLease ToLease(McpOperatorCommandRecord record) => new(
        record.CommandId,
        record.TenantId,
        record.AgentId,
        record.Subject,
        record.ClientId,
        record.McpResource,
        record.McpInstance,
        record.PolicyId,
        record.PolicyVersion,
        record.AcceptedAuditId,
        record.IdempotencyId,
        record.CorrelationId,
        record.ShellType,
        record.WorkingDirectory,
        record.CommandHash,
        record.CommandLength,
        JsonSerializer.Deserialize(record.EnvironmentReferencesJson, McpOperatorJsonContext.Default.StringArray) ?? [],
        record.TimeoutSeconds,
        record.MaximumOutputBytes,
        JsonSerializer.Deserialize(record.EffectiveConstraintsJson, McpOperatorJsonContext.Default.McpOperatorConstraints)
            ?? throw new InvalidOperationException("The command lease has invalid frozen constraints."),
        record.State,
        record.CreatedAtUtc,
        record.LastUpdatedAtUtc,
        record.FailureCode,
        record.Version)
    {
        Output = record.OutputJson is null ? null : JsonSerializer.Deserialize(record.OutputJson,
            McpOperatorJsonContext.Default.McpOperatorCommandOutput)
    };
}
