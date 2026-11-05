using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>Commits a stable canonical execution identity before a dispatcher can expose physical work.</summary>
public sealed class ExecutionAdmissionStore(OrchestratorDbContext db, IDataProtectionProvider protection, TimeProvider clock) : IExecutionAdmissionStore
{
    public async Task<ExecutionAdmissionBinding> ReserveAsync(ExecutionAdmissionSubmission submission,
        Func<string> allocateExecutionId, CancellationToken cancellationToken)
    {
        submission.Validate();
        ArgumentNullException.ThrowIfNull(allocateExecutionId);
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL" || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Canonical execution admission must own its PostgreSQL transaction.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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
            (ExecutionAdmissionKind)row.Kind, row.ExecutionId, payload, row.CreatedAtUtc);
    }

    private IDataProtector Protector(ExecutionAdmissionRecord row) => protection.CreateProtector(
        "NetRatel.ExecutionAdmission.v1", row.TenantId.ToString(CultureInfo.InvariantCulture), row.ActionKey.ToString("N"),
        row.Kind.ToString(CultureInfo.InvariantCulture), row.ExecutionId);
}
