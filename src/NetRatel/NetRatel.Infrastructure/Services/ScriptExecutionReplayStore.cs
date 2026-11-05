using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class ScriptExecutionReplayStore(OrchestratorDbContext db, IDataProtectionProvider protection, TimeProvider clock)
    : IScriptExecutionReplayStore
{
    public async Task CaptureAsync(string requestId, ScriptExecutionReplaySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL" || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Native script replay capture requires the activity admission transaction.");
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256)
            throw new ArgumentException("A bounded native script request identifier is required.");
        var json = JsonSerializer.Serialize(snapshot);
        // Legacy native submissions outside flow replay bounds retain their ordinary execution behavior;
        // absence of protected replay material is explicitly Unsupported at the lifecycle boundary.
        if (Encoding.UTF8.GetByteCount(json) > ExecutionAdmissionSubmission.MaximumPayloadBytes || snapshot.Script.Parameters.Count > 128)
            return;
        var invocation = JsonSerializer.Deserialize<NetRatel.Shared.Contracts.Tasks.ExecLibraryScriptPayload>(snapshot.NativePayloadJson);
        if (invocation?.TimeoutSeconds is < 1 or > 86400 || invocation?.WorkingDirectory?.Length > 1024 ||
            invocation?.Parameters?.Count > 128) return;
        snapshot.Validate();
        if (await db.Set<ScriptExecutionReplayRecord>().AnyAsync(record => record.RequestId == requestId, cancellationToken))
            throw new InvalidOperationException("The native script request already has replay material.");
        var row = new ScriptExecutionReplayRecord
        {
            RequestId = requestId, TenantId = snapshot.TenantId, AgentId = snapshot.AgentId,
            ScriptId = checked((long)snapshot.Script.Script.Id), ScriptName = snapshot.Script.Script.Name,
            ScriptRevision = snapshot.Script.Script.SourceRevision, ScriptContentHash = snapshot.Script.ContentHash,
            PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            CreatedAtUtc = clock.GetUtcNow()
        };
        row.ProtectedSnapshotJson = Protector(row).Protect(json);
        db.Set<ScriptExecutionReplayRecord>().Add(row);
        // The native activity owner saves and commits both records before first transport.
    }

    public async Task<ScriptExecutionReplaySnapshot?> ResolveAsync(int tenantId, string requestId, CancellationToken cancellationToken = default)
    {
        var row = await db.Set<ScriptExecutionReplayRecord>().AsNoTracking()
            .SingleOrDefaultAsync(record => record.TenantId == tenantId && record.RequestId == requestId, cancellationToken);
        if (row is null) return null;
        var json = Protector(row).Unprotect(row.ProtectedSnapshotJson);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), row.PayloadHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The native script replay snapshot failed its integrity check.");
        var snapshot = JsonSerializer.Deserialize<ScriptExecutionReplaySnapshot>(json)
            ?? throw new InvalidOperationException("The native script replay snapshot is unavailable.");
        snapshot.Validate();
        if (snapshot.TenantId != row.TenantId || snapshot.AgentId != row.AgentId ||
            snapshot.Script.Script.Id != (ulong)row.ScriptId || snapshot.Script.Script.SourceRevision != row.ScriptRevision ||
            snapshot.Script.ContentHash != row.ScriptContentHash || snapshot.Script.Script.Name != row.ScriptName)
            throw new InvalidOperationException("The native script replay metadata differs from its protected snapshot.");
        return snapshot;
    }
    private IDataProtector Protector(ScriptExecutionReplayRecord row) => protection.CreateProtector(
        "NetRatel.ScriptExecutionReplay.V1", row.TenantId.ToString(CultureInfo.InvariantCulture), row.RequestId,
        row.AgentId.ToString("N"), row.PayloadHash);
}
