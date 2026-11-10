using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

/// <summary>Reuses bounded search-policy batches before target pagination or admission; never dispatches execution.</summary>
public sealed class McpJobTargetPolicyReader(OrchestratorDbContext db, IMcpOperatorSearchAuthorization authorization)
{
    public Task<IReadOnlyDictionary<Guid, McpOperatorDecision>> AllowedAsync(McpOperatorRouteAccessRequest route,
        IReadOnlyList<JobNodeInventoryDto> nodes, CancellationToken ct) => AllowedAsync(new McpJobTargetPolicyContext(
            route.Environment, route.Principal, route.ServicePrincipal, route.McpResource, route.McpInstance, route.Tool,
            route.Operation, route.TenantId, route.RequiredScopes, route.CorrelationId, route.RequestId), nodes, ct);

    public async Task<IReadOnlyDictionary<Guid, McpOperatorDecision>> AllowedAsync(McpJobTargetPolicyContext route,
        IReadOnlyList<JobNodeInventoryDto> nodes, CancellationToken ct)
    {
        var operation = McpOperatorOperationCatalog.Find(route.Tool, route.Operation)
            ?? throw new McpOperatorAdmissionRejectedException("delegated_identity_invalid");
        var ids = nodes.Select(node => node.AgentId).ToArray();
        var profiles = await db.McpOperatorTargetProfiles.AsNoTracking().Where(profile => profile.TenantId == route.TenantId && ids.Contains(profile.AgentId))
            .ToDictionaryAsync(profile => profile.AgentId, ct).ConfigureAwait(false);
        var allowed = new Dictionary<Guid, McpOperatorDecision>();
        var principal = route.Principal with { ServicePrincipal = route.ServicePrincipal };
        foreach (var batch in nodes.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            var requests = batch.Select(node =>
            {
                profiles.TryGetValue(node.AgentId, out var profile);
                var facts = McpOperatorTargetFacts.Read(route.TenantId, node.AgentId, profile);
                return new McpOperatorAccessRequest(route.Environment, principal, route.TenantId, node.AgentId,
                    facts.Classification, operation.OperationFamily, $"{route.Tool}/{route.Operation}", route.RequiredScopes,
                    operation.ConfirmationClass, route.CorrelationId, route.RequestId, facts.Digest, facts.Tags,
                    route.McpResource, route.McpInstance, route.Tool, TargetEnabled: node.IsEnabled,
                    TargetOnline: node.IsOnline, CapabilityAvailable: true) { TargetProfileInvalid = facts.Invalid };
            }).ToArray();
            var decisions = await authorization.EvaluateAsync(requests, ct).ConfigureAwait(false);
            if (decisions.Count != requests.Length)
                throw new InvalidOperationException("The target policy batch did not return every requested decision.");
            for (var index = 0; index < decisions.Count; index++)
                if (decisions[index].IsAllowed)
                    allowed.Add(batch[index].AgentId, decisions[index]);
        }
        return allowed;
    }
}

public sealed record McpJobTargetPolicyContext(McpOperatorEnvironment Environment, McpOperatorPrincipal Principal,
    string ServicePrincipal, string McpResource, string McpInstance, string Tool, string Operation, int TenantId,
    IReadOnlySet<string> RequiredScopes, string CorrelationId, string RequestId);
