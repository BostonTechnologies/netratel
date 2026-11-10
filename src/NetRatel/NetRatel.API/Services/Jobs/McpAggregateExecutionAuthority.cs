using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Integration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

/// <summary>Captures a reviewed whole-set policy and rechecks its current caller and every native target.</summary>
public sealed class McpAggregateExecutionAuthority(IJobNodeDirectory directory, IJobTargetResolver resolver,
    IMcpOperatorRouteAdmission routes, IEffectiveAccessService access, IApplicationPrincipalResolver principals,
    IIntegrationCredentialCurrentVerifier credentials, McpJobTargetPolicyReader policyReader) : IMcpAggregateExecutionAuthority
{
    public async Task<McpOperatorJobExecutionSnapshot> ReviewAsync(McpOperatorJobExecutionSnapshot snapshot,
        McpOperatorRouteAccessRequest route, IReadOnlyDictionary<string, string>? inputs,
        JobRuntimeTargetInput? runtime, HttpContext http, CancellationToken ct)
    {
        var target = snapshot.Definition.Job.Target ?? (snapshot.Definition.Job.AgentId is { } fixedAgent
            ? JobTargetDefinition.FromLegacy(fixedAgent) : throw new McpOperatorAdmissionRejectedException("job_target_invalid"));
        var authority = CaptureAuthority(route, http);
        await RequireApplicationAsync(authority, ct).ConfigureAwait(false);
        var available = await directory.ReadAsync(route.TenantId, null, false, ct).ConfigureAwait(false);
        var permitted = await policyReader.AllowedAsync(route, available, ct).ConfigureAwait(false);
        var parameters = snapshot.InputSchema.Where(parameter => parameter.Type != "secret_reference")
            .Where(parameter => inputs?.ContainsKey(parameter.Name) == true || parameter.DefaultValue is not null)
            .ToDictionary(parameter => parameter.Name, parameter => new[] { inputs?.TryGetValue(parameter.Name, out var value) == true
                ? value : parameter.DefaultValue! }, StringComparer.Ordinal);
        var resolution = await resolver.ResolveAsync(route.TenantId, target, JobExecutionContextPolicy.FromJob(snapshot.Definition.Job),
            parameters, runtime, permitted.Keys.ToHashSet(), ct).ConfigureAwait(false);
        var policy = snapshot.Definition.Job.ExecutionPolicy ?? new JobExecutionPolicy();
        var plan = new JobResolvedTargetPlan(1, route.TenantId, snapshot.Definition.Job.Id,
            snapshot.ComputeDefinitionDigest(), target, resolution.GroupRevision, resolution.Bindings, resolution.Nodes, policy);
        var grants = new List<McpAggregateTargetGrant>(plan.Nodes.Count);
        foreach (var node in plan.Nodes)
        {
            var decision = permitted[node.AgentId];
            RequirePlanBounds(snapshot, decision, plan.Nodes.Count);
            grants.Add(new(node.AgentId, decision.MatchingPolicyIds.Single(), decision.SelectedPolicyVersion!.Value,
                decision.Request.TargetSetDigest!, ConstraintDigest(decision.EffectiveConstraints!))
            { MaximumConcurrentJobTargets = decision.EffectiveConstraints!.MaxConcurrentJobTargets,
                QuotaPolicyId = decision.ConstraintsPolicyId, QuotaPolicyVersion = decision.ConstraintsPolicyVersion });
        }
        // Request IDs are transport facts, not semantics. Canonicalize them so
        // a later confirmation can reproduce the same protected review hash.
        var digest = plan.ComputeDigest();
        return snapshot with { TargetPlan = plan, ReviewedAuthority = authority with
        { Route = route with { CorrelationId = digest, RequestId = $"job-targets:{digest}",
            Principal = route.Principal with
            {
                Groups = route.Principal.Groups.Order(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal),
                Roles = route.Principal.Roles.Order(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal),
                Scopes = route.Principal.Scopes.Order(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal)
            } }, Targets = grants } };
    }

    public async Task RequireAllowedAsync(McpOperatorJobExecutionSnapshot snapshot, Guid agentId, CancellationToken ct)
    {
        try { await RequireCurrentAllowedAsync(snapshot, agentId, ct).ConfigureAwait(false); }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            throw new ExecutionAdmissionDeniedException($"Current reviewed target authority was denied: {rejection.FailureCode}.");
        }
    }

    private async Task RequireCurrentAllowedAsync(McpOperatorJobExecutionSnapshot snapshot, Guid agentId, CancellationToken ct)
    {
        if (snapshot.TargetPlan is not { } plan || snapshot.ReviewedAuthority is not { } authority ||
            snapshot.Definition.Job.TenantId != plan.TenantId || snapshot.Definition.Job.Id != plan.JobId ||
            plan.DefinitionDigest != snapshot.ComputeDefinitionDigest() || authority.Route.TenantId != plan.TenantId ||
            authority.Route.Principal.Subject != snapshot.Subject || (authority.Route.Principal.ClientId ?? string.Empty) != snapshot.ClientId ||
            authority.Route.McpResource != snapshot.McpResource || authority.Route.McpInstance != snapshot.McpInstance ||
            authority.Route.Tool != "netratel_job_runs" || authority.Route.Operation != "start" ||
            !plan.Nodes.Any(node => node.AgentId == agentId) ||
            authority.Targets.SingleOrDefault(grant => grant.AgentId == agentId) is not { } grant)
            throw new ExecutionAdmissionDeniedException("The protected reviewed target binding is unavailable.");
        await RequireApplicationAsync(authority, ct).ConfigureAwait(false);
        var current = (await directory.ReadAsync(plan.TenantId, new HashSet<Guid> { agentId }, true, ct).ConfigureAwait(false)).SingleOrDefault();
        if (current is not { IsEnabled: true })
            throw new ExecutionAdmissionDeniedException("The selected client is no longer eligible for execution.");
        var decision = (await routes.EvaluateAsync(authority.Route with { AgentId = agentId,
            TargetOnline = current.IsOnline, CapabilityAvailable = true }, ct).ConfigureAwait(false)).Decision;
        if (!decision.IsAllowed || decision.MatchingPolicyIds.Count != 1 || decision.MatchingPolicyIds[0] != grant.PolicyId ||
            decision.SelectedPolicyVersion != grant.PolicyVersion || decision.Request.TargetSetDigest != grant.TargetFactsDigest ||
            decision.ConstraintsPolicyId != grant.QuotaPolicyId || decision.ConstraintsPolicyVersion != grant.QuotaPolicyVersion ||
            decision.EffectiveConstraints is not { } constraints || ConstraintDigest(constraints) != grant.ConstraintsDigest)
            throw new ExecutionAdmissionDeniedException("Current target authority differs from the reviewed execution grant.");
        RequirePlanBounds(snapshot, decision, plan.Nodes.Count);
    }

    public static IReadOnlyList<McpJobTargetQuota> Quotas(McpOperatorJobExecutionSnapshot snapshot)
    {
        if (snapshot.ReviewedAuthority is not { } authority) return [];
        return authority.Targets.GroupBy(grant => new { PolicyId = grant.QuotaPolicyId ?? grant.PolicyId,
            PolicyVersion = grant.QuotaPolicyVersion ?? grant.PolicyVersion })
            .Select(group => new McpJobTargetQuota(group.Key.PolicyId, group.Key.PolicyVersion, group.Count(),
                // The limit is separately captured server-side below; the
                // digest still binds every effective constraint in the review.
                group.First().MaximumConcurrentJobTargets)).OrderBy(quota => quota.PolicyId).ToArray();
    }

    private static McpAggregateReviewedAuthority CaptureAuthority(McpOperatorRouteAccessRequest route, HttpContext http)
    {
        if (!http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null)
            throw new McpOperatorAdmissionRejectedException("delegated_identity_required");
        return new(route, delegation.Identity.ExternalIssuer, delegation.IngressCredentialId, []);
    }

    internal async Task RequireApplicationAsync(McpAggregateReviewedAuthority authority, CancellationToken ct)
    {
        var route = authority.Route;
        ClaimsIdentity identity;
        if (authority.IngressCredentialId is { } credentialId)
        {
            var current = await credentials.CheckCurrentAsync(credentialId, IntegrationCredentialPurpose.HttpMcp, ct).ConfigureAwait(false);
            if (current.Credential is not { } credential || credential.OwnerPrincipalId != route.Principal.Subject ||
                credential.Resource != route.McpResource)
                throw new McpOperatorAdmissionRejectedException("delegated_identity_revoked");
            identity = new ClaimsIdentity([new Claim("netratel_principal_id", credential.OwnerPrincipalId),
                new Claim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, credentialId)], "McpLocalExecution");
        }
        else if (authority.ExternalIssuer is { Length: > 0 } issuer)
        {
            identity = new ClaimsIdentity([new Claim("iss", issuer), new Claim("sub", route.Principal.Subject)], "Oidc");
            var principalId = await principals.ResolveExternalAsync(new ClaimsPrincipal(identity), ct).ConfigureAwait(false);
            if (principalId is null) throw new McpOperatorAdmissionRejectedException("application_permission_denied");
            identity.AddClaim(new Claim("netratel_principal_id", principalId));
        }
        else throw new McpOperatorAdmissionRejectedException("application_permission_denied");
        var principal = new ClaimsPrincipal(identity);
        if (!await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, route.TenantId, ct).ConfigureAwait(false) ||
            !await access.AuthorizeAsync(principal, NetRatelPermissions.ScriptEdit, route.TenantId, ct).ConfigureAwait(false))
            throw new McpOperatorAdmissionRejectedException("application_permission_denied");
    }

    private static void RequirePlanBounds(McpOperatorJobExecutionSnapshot snapshot, McpOperatorDecision decision, int count)
    {
        if (!decision.IsAllowed || decision.MatchingPolicyIds.Count != 1 || decision.SelectedPolicyVersion is not > 0 ||
            decision.EffectiveConstraints is not { MaxJobTargetCount: > 0, MaxFanOut: > 0 } constraints ||
            count > constraints.MaxJobTargetCount || count > constraints.MaxFanOut)
            throw new McpOperatorAdmissionRejectedException("job_target_limit_exceeded");
        foreach (var script in snapshot.Scripts)
        {
            var shell = script.Script.ScriptType?.Trim().ToLowerInvariant() switch
            { "sh" or "bash" => "sh", "powershell" or "pwsh" => "powershell", _ => null };
            if (shell is null || constraints.AllowedShells is { Count: > 0 } shells && !shells.Contains(shell, StringComparer.OrdinalIgnoreCase) ||
                constraints.MaxScriptBytes is { } bytes && System.Text.Encoding.UTF8.GetByteCount(script.Script.Content) > bytes ||
                constraints.MaxCommandDurationSeconds is { } timeout && script.TimeoutSeconds > timeout ||
                constraints.WorkingDirectories is { Count: > 0 } directories && !directories.AllowsWorkingDirectory(script.WorkingDirectory))
                throw new McpOperatorAdmissionRejectedException("job_target_policy_conflict");
        }
    }

    private static string ConstraintDigest(McpOperatorConstraints constraints) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(constraints, McpOperatorJsonContext.Default.McpOperatorConstraints)));
}
