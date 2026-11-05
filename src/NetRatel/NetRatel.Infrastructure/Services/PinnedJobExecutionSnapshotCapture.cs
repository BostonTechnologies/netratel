using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Infrastructure.Services;

/// <summary>Captures the same canonical definitions used by normal job admission under their existing source fences.</summary>
public sealed class PinnedJobExecutionSnapshotCapture(OrchestratorDbContext db, IJobDefinitionService jobs, IScriptService scripts)
    : IJobExecutionSnapshotCapture
{
    public async Task<PinnedJobExecutionSnapshot> CaptureAsync(ulong jobId, int tenantId, CancellationToken cancellationToken)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(db, cancellationToken);
        if (!await JobDefinitionMutationFence.LockActiveAsync(db, checked((long)jobId), cancellationToken))
            throw new KeyNotFoundException("The job definition is unavailable.");
        var definition = await jobs.GetDetailsAsync(jobId, cancellationToken)
            ?? throw new KeyNotFoundException("The job definition is unavailable.");
        if (definition.Job.TenantId != tenantId)
            throw new InvalidOperationException("The job definition belongs to another tenant.");
        definition = definition with
        {
            Steps = definition.Steps.Select(step => step.Enabled && step.Type == JobStepKind.LibraryScript && step.ScriptId is null
                ? step with { ScriptId = LegacyScriptId(step.PayloadJson) } : step).ToArray()
        };
        var pinnedScripts = new List<PinnedJobScriptSnapshot>();
        foreach (var scriptId in definition.Steps.Where(step => step.Enabled && step.Type == JobStepKind.LibraryScript)
            .Select(step => step.ScriptId ?? throw new InvalidOperationException("A library step lacks its canonical script identity."))
            .Distinct().OrderBy(id => id))
        {
            if (!await ScriptReferenceFence.LockActiveAsync(db, checked((long)scriptId), cancellationToken))
                throw new InvalidOperationException("The job references an unavailable library script.");
            var script = await scripts.GetAsync(scriptId, cancellationToken)
                ?? throw new InvalidOperationException("The job references an unavailable library script.");
            var parameters = await scripts.GetParamsAsync(scriptId, cancellationToken);
            pinnedScripts.Add(new(script, parameters,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Content)))));
        }
        var snapshot = new PinnedJobExecutionSnapshot(definition, pinnedScripts);
        snapshot.Validate(tenantId);
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > ExecutionAdmissionSubmission.MaximumPayloadBytes)
            throw new InvalidOperationException("The pinned job source exceeds the execution payload bound.");
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static ulong? LegacyScriptId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            var invocation = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return invocation?.ScriptId is > 0 ? checked((ulong)invocation.ScriptId) : null;
        }
        catch (JsonException) { return null; }
    }
}
