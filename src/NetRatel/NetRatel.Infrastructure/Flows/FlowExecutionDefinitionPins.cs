using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>Source text and input schemas live in protected version storage, never in editable browser graphs.</summary>
public sealed class FlowExecutionDefinitionPins(OrchestratorDbContext db, IJobExecutionSnapshotCapture jobs,
    IScriptService scripts, IFlowExecutionAuthorityVerifier verifier, IEffectiveAccessService access, IDataProtectionProvider protection)
    : IFlowExecutionDefinitionPins
{
    private sealed record Pins(int SchemaVersion, int TenantId, Guid VersionId, IReadOnlyList<FlowPinnedExecutionDefinition> Definitions);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<FlowExecutionPinsCapture> CaptureAsync(int tenantId, FlowExecutionAuthorityDto authority,
        Guid versionId, FlowGraphDto graph, CancellationToken cancellationToken = default)
    {
        if (graph.SchemaVersion != FlowGraphV2Limits.SchemaVersion)
            return new(graph, null);
        var nodes = graph.Nodes.Where(node => node.Kind is FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob).ToArray();
        if (nodes.Length == 0) return new(graph, null);
        if (versionId == Guid.Empty || !await verifier.AuthorizeAsync(tenantId, authority, cancellationToken))
            throw new FlowExecutionPinException("execution-pins-authority");
        var principal = Principal(authority);
        var pins = new List<FlowPinnedExecutionDefinition>();
        var updated = new Dictionary<Guid, FlowNodeDto>();
        await using var transaction = await ScriptReferenceFence.BeginAsync(db, cancellationToken);
        // Parse aliases before locking: every publisher takes numeric job IDs, then the numeric
        // union of direct and job-referenced script IDs. Node layout/GUIDs cannot change lock order.
        var selectedIds = nodes.ToDictionary(node => node.Id, node =>
            ulong.TryParse(node.ExecutionAction?.Definition.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
            id > 0 && id <= long.MaxValue ? checked((long)id) : throw new FlowExecutionPinException("execution-definition-id"));
        var jobIds = nodes.Where(node => node.Kind == FlowNodeKind.RunJob).Select(node => selectedIds[node.Id])
            .Distinct().Order().ToArray();
        if (jobIds.Length > 0 &&
            !await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, tenantId, cancellationToken))
            throw new FlowExecutionPinException("execution-definition-denied");
        if (nodes.Any(node => node.Kind == FlowNodeKind.RunLibraryScript) &&
            !await access.AuthorizeAsync(principal, NetRatelPermissions.ScriptExecute, tenantId, cancellationToken))
            throw new FlowExecutionPinException("execution-definition-denied");
        foreach (var jobId in jobIds)
        {
            if (!await db.Jobs.AsNoTracking().AnyAsync(job => job.Id == jobId && job.TenantId == tenantId, cancellationToken) ||
                !await JobDefinitionMutationFence.LockActiveAsync(db, jobId, cancellationToken))
                throw new FlowExecutionPinException("execution-definition-unavailable");
        }
        // The job fences make this reference set stable until the publication commits.
        var referenced = await db.JobSteps.AsNoTracking().Where(step => jobIds.Contains(step.JobId) &&
            step.Enabled && step.Type == (int)JobStepKind.LibraryScript)
            .Select(step => new { step.ScriptId, step.PayloadJson }).ToArrayAsync(cancellationToken);
        var referencedIds = referenced.Select(step => step.ScriptId is { } id ? checked((ulong)id) :
            PinnedJobExecutionSnapshotCapture.LegacyScriptId(step.PayloadJson) ??
                throw new FlowExecutionPinException("execution-definition-unavailable"))
            .Select(id => id is > 0 and <= long.MaxValue ? checked((long)id) :
                throw new FlowExecutionPinException("execution-definition-id"));
        var scriptIds = nodes.Where(node => node.Kind == FlowNodeKind.RunLibraryScript).Select(node => selectedIds[node.Id])
            .Concat(referencedIds).Distinct().Order().ToArray();
        foreach (var scriptId in scriptIds)
            if (!await ScriptReferenceFence.LockActiveAsync(db, scriptId, cancellationToken))
                throw new FlowExecutionPinException("execution-definition-unavailable");
        foreach (var node in nodes.OrderBy(node => node.Id))
        {
            var selected = node.ExecutionAction ?? throw new FlowExecutionPinException("execution-action-required");
            if (!ulong.TryParse(selected.Definition.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                id == 0 || id > long.MaxValue)
                throw new FlowExecutionPinException("execution-definition-id");
            FlowPinnedExecutionDefinition pin;
            IReadOnlyList<FlowParameterDefinitionDto> parameters;
            if (node.Kind == FlowNodeKind.RunJob)
            {
                if (selected.SourceKind != FlowExecutionSourceKind.Job ||
                    !await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, tenantId, cancellationToken))
                    throw new FlowExecutionPinException("execution-definition-denied");
                var snapshot = await jobs.CaptureAsync(id, tenantId, cancellationToken);
                var digest = snapshot.ComputeDigest();
                var definition = new FlowExecutionDefinitionDto(id.ToString(CultureInfo.InvariantCulture),
                    snapshot.Definition.Job.Name, digest, digest);
                if (definition.Name is { Length: > 512 })
                    throw new FlowExecutionPinException("execution-definition-name-limit");
                RequireSelectedRevision(selected.Definition, definition);
                var target = await TargetAsync(tenantId, snapshot.Definition.Job.AgentId!.Value, cancellationToken);
                if (selected.Target?.AgentId != target.AgentId)
                    throw new FlowExecutionPinException("job-bound-target-conflict");
                pin = new(node.Id, node.Kind, definition, target, Job: snapshot);
                parameters = snapshot.Definition.Params.Select(parameter =>
                    new FlowParameterDefinitionDto(parameter.Name, DataType(parameter.Type), parameter.Required)).ToArray();
            }
            else
            {
                if (selected.SourceKind != FlowExecutionSourceKind.Script ||
                    !await access.AuthorizeAsync(principal, NetRatelPermissions.ScriptExecute, tenantId, cancellationToken))
                    throw new FlowExecutionPinException("execution-definition-denied");
                if (!await ScriptReferenceFence.LockActiveAsync(db, checked((long)id), cancellationToken))
                    throw new FlowExecutionPinException("execution-definition-unavailable");
                var script = await scripts.GetAsync(id, cancellationToken)
                    ?? throw new FlowExecutionPinException("execution-definition-unavailable");
                var scriptParameters = await scripts.GetParamsAsync(id, cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Content)));
                var definition = new FlowExecutionDefinitionDto(id.ToString(CultureInfo.InvariantCulture), script.Name,
                    script.SourceRevision.ToString(CultureInfo.InvariantCulture), hash);
                if (definition.Name is { Length: > 512 })
                    throw new FlowExecutionPinException("execution-definition-name-limit");
                RequireSelectedRevision(selected.Definition, definition);
                var target = selected.Target?.AgentId is { } agentId
                    ? await TargetAsync(tenantId, agentId, cancellationToken) : null;
                pin = new(node.Id, node.Kind, definition, target, Script: new(script, scriptParameters, hash));
                parameters = scriptParameters.Select(parameter =>
                    new FlowParameterDefinitionDto(parameter.Name, DataType(parameter.Type), parameter.Required)).ToArray();
            }
            FlowExecutionSecretReferences.Validate(node, pin);
            pins.Add(pin);
            updated.Add(node.Id, node with
            {
                ExecutionAction = selected with
                {
                    Definition = pin.Definition, Target = pin.Target,
                    PinnedSnapshotReference = Reference(versionId, node.Id), Parameters = parameters
                }
            });
        }
        var serialized = JsonSerializer.Serialize(new Pins(1, tenantId, versionId, pins), Json);
        if (Encoding.UTF8.GetByteCount(serialized) > ExecutionAdmissionSubmission.MaximumPayloadBytes)
            throw new FlowExecutionPinException("execution-pins-limit");
        var protectedPins = Protector(tenantId, versionId).Protect(serialized);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(graph with { Nodes = graph.Nodes.Select(node => updated.GetValueOrDefault(node.Id) ?? node).ToArray() }, protectedPins);
    }

    public async Task<FlowPinnedExecutionDefinition?> ResolveAsync(int tenantId, Guid versionId, Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        var version = await db.FlowVersions.AsNoTracking()
            .SingleOrDefaultAsync(version => version.TenantId == tenantId && version.Id == versionId, cancellationToken);
        if (version?.ProtectedExecutionPinsJson is null) return null;
        Pins pins;
        try
        {
            pins = JsonSerializer.Deserialize<Pins>(Protector(tenantId, versionId).Unprotect(version.ProtectedExecutionPinsJson), Json)
                ?? throw new FlowExecutionPinException("execution-pins-unavailable");
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { throw new FlowExecutionPinException("execution-pins-unavailable"); }
        if (pins.SchemaVersion != 1 || pins.TenantId != tenantId || pins.VersionId != versionId ||
            pins.Definitions.Count > FlowGraphV2Limits.MaximumNodes)
            throw new FlowExecutionPinException("execution-pins-context");
        var pin = pins.Definitions.SingleOrDefault(pin => pin.NodeId == nodeId);
        if (pin is null) return null;
        var graph = JsonSerializer.Deserialize<FlowGraphDto>(version.GraphJson, Json)
            ?? throw new FlowExecutionPinException("execution-pins-graph");
        var node = graph.Nodes.SingleOrDefault(node => node.Id == nodeId);
        if (node?.Kind != pin.Kind || node.ExecutionAction?.Definition != pin.Definition ||
            node.ExecutionAction.PinnedSnapshotReference != Reference(versionId, nodeId) ||
            node.ExecutionAction.Target != pin.Target)
            throw new FlowExecutionPinException("execution-pins-context");
        return pin;
    }

    private async Task<FlowExecutionTargetDto> TargetAsync(int tenantId, Guid agentId, CancellationToken ct)
    {
        var target = await db.Agents.AsNoTracking().SingleOrDefaultAsync(agent => agent.TenantId == tenantId &&
            agent.Id == agentId && agent.IsEnabled && agent.Status == AgentStatus.Active && agent.DeletedAtUtc == null &&
            agent.RevokedAtUtc == null && agent.SupersededAtUtc == null && agent.SupersededByAgentId == null, ct)
            ?? throw new FlowExecutionPinException("execution-target-unavailable");
        return new(agentId, target.FriendlyName ?? target.Name ?? agentId.ToString("D"), target.Name);
    }

    private static void RequireSelectedRevision(FlowExecutionDefinitionDto selected, FlowExecutionDefinitionDto captured)
    {
        if (selected.Revision is not null && selected.Revision != captured.Revision ||
            selected.ContentHash is not null && !string.Equals(selected.ContentHash, captured.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new FlowExecutionPinException("execution-definition-revision-conflict");
    }

    private static FlowDataType DataType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "secret" or "secretref" or "secret-reference" or "secret_reference" => FlowDataType.SecretReference,
        "bool" or "boolean" => FlowDataType.Boolean,
        "number" or "int" or "int32" or "integer" or "long" or "float" or "double" or "decimal" => FlowDataType.Number,
        "json" or "object" or "array" => FlowDataType.Json,
        _ => FlowDataType.Text
    };

    private static ClaimsPrincipal Principal(FlowExecutionAuthorityDto authority)
    {
        var claims = new List<Claim> { new("netratel_principal_id", authority.PrincipalId) };
        if (authority.IntegrationCredentialId is not null) claims.Add(new("netratel_integration_credential_id", authority.IntegrationCredentialId));
        return new(new ClaimsIdentity(claims, "FlowExecutionPins"));
    }
    public static string Reference(Guid versionId, Guid nodeId) => $"flow-version:{versionId:N}/node:{nodeId:N}";
    private IDataProtector Protector(int tenantId, Guid versionId) => protection.CreateProtector(
        "NetRatel.FlowExecutionPins.v1", tenantId.ToString(CultureInfo.InvariantCulture), versionId.ToString("N"));
}
