using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Services;

/// <summary>Commits a stable canonical execution identity before a dispatcher can expose physical work.</summary>
public sealed class ExecutionAdmissionStore(OrchestratorDbContext db, IDataProtectionProvider protection, TimeProvider clock) : IExecutionAdmissionStore
{
    public async Task<ExecutionAdmissionBinding?> FindByIdentityAsync(ExecutionAdmissionIdentity identity,
        ExecutionAdmissionKind kind, CancellationToken cancellationToken)
    {
        identity.Validate();
        var row = await db.Set<ExecutionAdmissionRecord>().AsNoTracking()
            .SingleOrDefaultAsync(record => record.ActionKey == identity.ActionKey, cancellationToken);
        if (row is null) return null;
        var binding = Project(row);
        if (binding.Identity != identity || binding.Kind != kind)
            throw new ExecutionAdmissionDeniedException("The action key belongs to another execution identity.");
        return binding;
    }

    public async Task<ExecutionAdmissionBinding> ReserveAsync(ExecutionAdmissionSubmission submission,
        Func<string> allocateExecutionId, CancellationToken cancellationToken)
    {
        submission.Validate();
        ArgumentNullException.ThrowIfNull(allocateExecutionId);
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL" || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Canonical execution admission must own its PostgreSQL transaction.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (submission.Identity.Causation?.SourceExecutionId is not null)
        {
            var runKey = BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(submission.Identity.FlowRunId.ToByteArray()));
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({0x4E524652}, {runKey})", cancellationToken);
        }
        // The two-int advisory namespace is independent from job ownership's bigint locks.
        var lockKey = BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(submission.Identity.ActionKey.ToByteArray()));
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({0x4E52454B}, {lockKey})", cancellationToken);
        var identity = submission.Identity;
        var existing = await db.Set<ExecutionAdmissionRecord>().AsNoTracking()
            .SingleOrDefaultAsync(record => record.ActionKey == identity.ActionKey, cancellationToken);
        if (existing is not null)
        {
            if (!Matches(existing, submission))
                throw new InvalidOperationException("The execution action key is already bound to another identity or semantic payload.");
            var replay = Project(existing);
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }
        await RequireRerunBudgetAsync(submission.Identity, cancellationToken);
        var executionId = allocateExecutionId();
        if (string.IsNullOrWhiteSpace(executionId) || executionId.Length > 128 ||
            submission.Kind == ExecutionAdmissionKind.Job &&
            (!ulong.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var jobRunId) || jobRunId == 0 || jobRunId > long.MaxValue))
            throw new InvalidOperationException("The allocated canonical execution identity is invalid.");
        var now = clock.GetUtcNow();
        var row = new ExecutionAdmissionRecord
        {
            ActionKey = identity.ActionKey, TenantId = identity.TenantId, PrincipalId = identity.PrincipalId,
            IntegrationCredentialId = identity.IntegrationCredentialId, FlowVersionId = identity.FlowVersionId,
            FlowRunId = identity.FlowRunId, NodeId = identity.NodeId, NodeAttempt = identity.NodeAttempt,
            CausationJson = identity.Causation is null ? null : JsonSerializer.Serialize(identity.Causation),
            Kind = (int)submission.Kind, ExecutionId = executionId, PayloadHash = submission.PayloadHash,
            CreatedAtUtc = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero)
        };
        row.ProtectedPayloadJson = Protector(row).Protect(submission.PayloadJson);
        db.Set<ExecutionAdmissionRecord>().Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Project(row);
    }

    public async Task<ExecutionAdmissionBinding?> FindByExecutionAsync(ExecutionAdmissionKind kind, string executionId,
        CancellationToken cancellationToken)
    {
        var row = await db.Set<ExecutionAdmissionRecord>().AsNoTracking()
            .SingleOrDefaultAsync(record => record.Kind == (int)kind && record.ExecutionId == executionId, cancellationToken);
        return row is null ? null : Project(row);
    }

    public async Task RecordAuthorityDeniedAsync(ExecutionAdmissionKind kind, string executionId, CancellationToken cancellationToken)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL" || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Authority denial evidence requires the canonical owner transaction.");
        var deniedAt = clock.GetUtcNow();
        var updated = await db.Set<ExecutionAdmissionRecord>().Where(record => record.Kind == (int)kind && record.ExecutionId == executionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.AuthorityDeniedAtUtc,
                record => record.AuthorityDeniedAtUtc ?? deniedAt), cancellationToken);
        if (updated != 1) throw new InvalidOperationException("The denied execution admission disappeared.");
    }

    public async Task<bool> TryBeginDispatchAsync(ExecutionAdmissionBinding binding, Func<CancellationToken, Task> prepare, Func<CancellationToken, Task> validate,
        CancellationToken cancellationToken)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL" || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Canonical physical dispatch must own its PostgreSQL transaction.");
        var submission = new ExecutionAdmissionSubmission(binding.Identity, binding.Kind, binding.PayloadJson);
        submission.Validate();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(binding.Identity.ActionKey.ToByteArray()));
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({0x4E52454B}, {lockKey})", cancellationToken);
        var existing = await db.Set<ExecutionAdmissionRecord>().AsNoTracking()
            .SingleOrDefaultAsync(record => record.ActionKey == binding.Identity.ActionKey, cancellationToken);
        if (existing is null || existing.ExecutionId != binding.ExecutionId || !Matches(existing, submission))
            throw new InvalidOperationException("The physical dispatch does not match its canonical admission.");
        if (existing.DispatchStartedAtUtc is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        ArgumentNullException.ThrowIfNull(validate);
        await prepare(cancellationToken);
        await validate(cancellationToken);
        var startedAt = clock.GetUtcNow();
        var updated = await db.Set<ExecutionAdmissionRecord>().Where(record => record.ActionKey == binding.Identity.ActionKey &&
                record.DispatchStartedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DispatchStartedAtUtc, startedAt), cancellationToken);
        if (updated != 1) throw new InvalidOperationException("The canonical dispatch claim was lost.");
        await validate(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task RecordDispatchAcceptedAsync(ExecutionAdmissionBinding binding, CancellationToken cancellationToken)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL" || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Canonical dispatch acceptance must commit independently of physical dispatch.");
        var acceptedAt = clock.GetUtcNow();
        var updated = await db.Set<ExecutionAdmissionRecord>().Where(record => record.ActionKey == binding.Identity.ActionKey &&
                record.Kind == (int)binding.Kind && record.ExecutionId == binding.ExecutionId && record.DispatchStartedAtUtc != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DispatchAcceptedAtUtc,
                record => record.DispatchAcceptedAtUtc ?? acceptedAt), cancellationToken);
        if (updated != 1) throw new InvalidOperationException("The canonical physical dispatch claim disappeared.");
    }

    private async Task RequireRerunBudgetAsync(ExecutionAdmissionIdentity identity, CancellationToken ct)
    {
        if (identity.Causation?.SourceExecutionId is not { } sourceId) return;
        var version = await db.FlowVersions.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == identity.TenantId && row.Id == identity.FlowVersionId, ct);
        var graph = version is null ? null : JsonSerializer.Deserialize<FlowGraphDto>(version.GraphJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var node = graph?.Nodes.SingleOrDefault(node => node.Id == identity.NodeId);
        if (node?.Kind != FlowNodeKind.RerunOriginalExecution)
            throw new ExecutionAdmissionDeniedException("Only the published original-rerun node can claim a linked rerun budget.");
        var maximum = graph!.Nodes.Where(node => node.Kind == FlowNodeKind.RerunOriginalExecution)
            .Max(node => node.Rerun?.MaximumAttempts ?? 1);
        if (maximum is < 1 or > FlowExecutionEventLimits.MaximumRecoveryAttempts || identity.Causation.RecoveryAttempt > maximum)
            throw new ExecutionAdmissionBudgetExhaustedException();
        var causalRows = await db.Set<ExecutionAdmissionRecord>().AsNoTracking().Where(row =>
            row.TenantId == identity.TenantId && row.FlowRunId == identity.FlowRunId && row.CausationJson != null)
            .Select(row => row.CausationJson).Take(FlowGraphV2Limits.MaximumNodes * 8 + 1).ToArrayAsync(ct);
        if (causalRows.Length > FlowGraphV2Limits.MaximumNodes * 8 ||
            causalRows.Count(json => JsonSerializer.Deserialize<ExecutionAdmissionCausation>(json!)?.SourceExecutionId == sourceId) >= maximum)
            throw new ExecutionAdmissionBudgetExhaustedException();
    }

    private static bool Matches(ExecutionAdmissionRecord row, ExecutionAdmissionSubmission submission)
    {
        var identity = submission.Identity;
        return row.TenantId == identity.TenantId && row.PrincipalId == identity.PrincipalId &&
            row.IntegrationCredentialId == identity.IntegrationCredentialId && row.FlowVersionId == identity.FlowVersionId &&
            row.FlowRunId == identity.FlowRunId && row.NodeId == identity.NodeId && row.NodeAttempt == identity.NodeAttempt &&
            row.CausationJson == (identity.Causation is null ? null : JsonSerializer.Serialize(identity.Causation)) &&
            row.Kind == (int)submission.Kind && row.PayloadHash == submission.PayloadHash;
    }

    private ExecutionAdmissionBinding Project(ExecutionAdmissionRecord row)
    {
        var payload = Protector(row).Unprotect(row.ProtectedPayloadJson);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), row.PayloadHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The protected canonical execution payload failed its integrity check.");
        return new(new(row.ActionKey, row.TenantId, row.PrincipalId, row.FlowVersionId, row.FlowRunId, row.NodeId,
            row.NodeAttempt, row.IntegrationCredentialId)
            { Causation = row.CausationJson is null ? null : JsonSerializer.Deserialize<ExecutionAdmissionCausation>(row.CausationJson) },
            (ExecutionAdmissionKind)row.Kind, row.ExecutionId, payload, row.CreatedAtUtc)
            { DispatchStartedAtUtc = row.DispatchStartedAtUtc, DispatchAcceptedAtUtc = row.DispatchAcceptedAtUtc,
                AuthorityDeniedAtUtc = row.AuthorityDeniedAtUtc };
    }

    private IDataProtector Protector(ExecutionAdmissionRecord row) => protection.CreateProtector(
        "NetRatel.ExecutionAdmission.v1", row.TenantId.ToString(CultureInfo.InvariantCulture), row.ActionKey.ToString("N"),
        row.Kind.ToString(CultureInfo.InvariantCulture), row.ExecutionId);
}
