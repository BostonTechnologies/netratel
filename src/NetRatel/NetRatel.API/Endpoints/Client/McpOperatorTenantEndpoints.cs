using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Typed control-plane tenant lifecycle. It is intentionally separate from
/// target-agent routes: only an explicit <see cref="McpOperatorTargetSelectorKind.ControlPlane"/>
/// policy can admit it, so a tenant-wide or agent policy can never become
/// accidental global administration authority.
/// </summary>
public static class McpOperatorTenantEndpoints
{
    private const string Tool = "netratel_tenants";
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;
    private static readonly string ControlPlaneDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("netratel-mcp-control-plane-v1")));

    public static IEndpointRouteBuilder MapMcpOperatorTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/tenants")
            .WithTags("MCP Operator Tenants")
            .RequireAuthorization("M2MOnly");

        group.MapGet("", ListAsync);
        group.MapGet("/{tenantId:int}", GetAsync);
        group.MapPost("/preview/{action}", PreviewAsync);
        group.MapPost("/confirm/{action}", ConfirmAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        int? cursor,
        int? limit,
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents,
        IMcpOperatorAuthorization authorization,
        OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        if (cursor is < 0 || limit is < 1 or > MaximumPageSize)
            return Results.BadRequest(new { code = "tenant_query_invalid" });

        var admitted = await TryContextAsync("list", http, environment, localAgents, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var take = limit ?? DefaultPageSize;
            var rows = await db.Tenants.AsNoTracking()
                .Where(tenant => !cursor.HasValue || tenant.Id > cursor.Value)
                .OrderBy(tenant => tenant.Id)
                .Take(take + 1)
                .Select(tenant => ToSummary(new TenantInfo(
                    tenant.Id, tenant.Name, tenant.Description, tenant.Location, tenant.Domains,
                    tenant.ContactPerson, tenant.ContactEmail, tenant.AutoUpdate, tenant.CreatedAtUtc,
                    tenant.UpdatedAtUtc, tenant.AutoUpdateChannel, tenant.AutoUpdateTargetVersion, tenant.Version)))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var nextCursor = rows.Count > take ? rows[take - 1].TenantId : (int?)null;
            return Results.Ok(new McpOperatorTenantPage(rows.Take(take).ToArray(), nextCursor, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> GetAsync(
        int tenantId,
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents,
        IMcpOperatorAuthorization authorization,
        ITenantService tenants,
        CancellationToken cancellationToken)
    {
        if (tenantId <= 0) return Results.BadRequest(new { code = "tenant_invalid" });
        var admitted = await TryContextAsync("get", http, environment, localAgents, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var tenant = await tenants.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (tenant is null) return Failure("tenant_not_found", context);
            SetEtag(http, tenant.Version);
            return Results.Ok(ToSummary(tenant));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewAsync(
        string action,
        McpOperatorTenantMutationRequest request,
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorConfirmationService confirmations,
        ITenantService tenants,
        OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        if (!IsMutation(action)) return Results.NotFound();
        var admitted = await TryContextAsync(action, http, environment, localAgents, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (IsActiveTenantDeleteWithoutSeparateApproval(action, request, context))
            return Failure("active_caller_tenant_delete_requires_separate_approval", context);
        var resolution = await ResolveAsync(action, request, tenants, db, cancellationToken).ConfigureAwait(false);
        if (resolution.FailureCode is { } code) return Failure(code, context);

        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, MutationHash(action, request)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorTenantPreview(
                plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass,
                action, resolution.Tenant?.TenantId, resolution.Tenant?.Version, resolution.Impact,
                context.Decision.Request.TargetSetDigest!, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmAsync(
        string action,
        McpOperatorTenantMutationRequest request,
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorConfirmationService confirmations,
        ITenantService tenants,
        OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        if (!IsMutation(action)) return Results.NotFound();
        var admitted = await TryContextAsync(action, http, environment, localAgents, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!HasPlan(request.PlanToken, request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);
        if (IsActiveTenantDeleteWithoutSeparateApproval(action, request, context))
            return Failure("active_caller_tenant_delete_requires_separate_approval", context);
        var resolution = await ResolveAsync(action, request, tenants, db, cancellationToken).ConfigureAwait(false);
        if (resolution.FailureCode is { } code) return Failure(code, context);

        var confirmed = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, MutationHash(action, request), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmed.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (confirmed.IsReplay) return await ReplayAsync(action, confirmed, tenants, context, cancellationToken).ConfigureAwait(false);
        if (!confirmed.IsNewDispatch || confirmed.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var mutation = await ApplyAsync(action, request, resolution, tenants, db, cancellationToken).ConfigureAwait(false);
            if (mutation.FailureCode is { } mutationFailure)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, mutationFailure, CancellationToken.None).ConfigureAwait(false);
                return Failure(mutationFailure, context);
            }

            var result = mutation.Tenant!;
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, result.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new McpOperatorTenantMutationResult(result.TenantId, result.Version, action == "delete", false, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (TenantConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "tenant_version_conflict", CancellationToken.None).ConfigureAwait(false);
            return Failure("tenant_version_conflict", context);
        }
        catch (DbUpdateConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "tenant_version_conflict", CancellationToken.None).ConfigureAwait(false);
            return Failure("tenant_version_conflict", context);
        }
        catch (InvalidOperationException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "tenant_invalid", CancellationToken.None).ConfigureAwait(false);
            return Failure("tenant_invalid", context);
        }
    }

    private static async Task<TenantResolution> ResolveAsync(string action, McpOperatorTenantMutationRequest request, ITenantService tenants, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        if (!IsPayloadValid(action, request)) return TenantResolution.Invalid;
        if (action == "create") return TenantResolution.Valid;
        var tenant = await tenants.GetAsync(request.TenantId!.Value, cancellationToken).ConfigureAwait(false);
        if (tenant is null) return TenantResolution.NotFound;
        if (tenant.Version != request.ExpectedVersion) return TenantResolution.VersionConflict;
        if (action != "delete") return new(tenant, null, null);
        var impact = await GetImpactAsync(tenant.TenantId, db, cancellationToken).ConfigureAwait(false);
        return new(tenant, impact, null);
    }

    private static async Task<TenantMutation> ApplyAsync(string action, McpOperatorTenantMutationRequest request, TenantResolution resolution, ITenantService tenants, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "create":
                return new(await tenants.CreateAsync(new CreateTenantCommand(
                    request.Name!.Trim(), Clean(request.Description), Clean(request.Location), request.Domains!.Select(domain => domain.Trim()).ToArray(),
                    Clean(request.ContactPerson), Clean(request.ContactEmail), request.AutoUpdate!.Value,
                    request.AutoUpdateChannel ?? "stable", Clean(request.AutoUpdateTargetVersion)), cancellationToken).ConfigureAwait(false), null);
            case "update":
                var updated = await tenants.UpdateAsync(new UpdateTenantCommand(
                    request.TenantId!.Value, request.Name!.Trim(), Clean(request.Description), Clean(request.Location), request.Domains!.Select(domain => domain.Trim()).ToArray(),
                    Clean(request.ContactPerson), Clean(request.ContactEmail), request.AutoUpdate!.Value,
                    request.AutoUpdateChannel ?? "stable", Clean(request.AutoUpdateTargetVersion), request.ExpectedVersion), cancellationToken).ConfigureAwait(false);
                return new(updated, updated is null ? "tenant_not_found" : null);
            case "delete":
                if (request.Cascade is not false) return new(null, "tenant_cascade_not_supported");
                var impact = resolution.Impact ?? await GetImpactAsync(request.TenantId!.Value, db, cancellationToken).ConfigureAwait(false);
                if (impact.TotalDependents > 0) return new(null, "tenant_has_dependents");
                var deleted = await tenants.DeleteAsync(request.TenantId!.Value, request.ExpectedVersion!.Value, cancellationToken).ConfigureAwait(false);
                return new(deleted, deleted is null ? "tenant_not_found" : null);
            default:
                return new(null, "tenant_invalid");
        }
    }

    private static async Task<IResult> ReplayAsync(string action, McpOperatorConfirmationAdmission admission, ITenantService tenants, McpOperatorTenantContext context, CancellationToken cancellationToken)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !int.TryParse(admission.ResultReference, out var tenantId)) return Failure("idempotency_replay_unavailable", context);
        var tenant = action == "delete" ? null : await tenants.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (action != "delete" && tenant is null) return Failure("idempotency_replay_unavailable", context);
        return Results.Ok(new McpOperatorTenantMutationResult(tenantId, tenant?.Version ?? 0, action == "delete", true, context.CorrelationId));
    }

    private static async Task<McpOperatorTenantImpact> GetImpactAsync(int tenantId, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["agents"] = await db.Agents.IgnoreQueryFilters().LongCountAsync(agent => agent.TenantId == tenantId, cancellationToken).ConfigureAwait(false),
            ["enrollments"] = await db.EnrollmentCodes.LongCountAsync(code => code.TenantId == tenantId, cancellationToken).ConfigureAwait(false),
            ["jobs"] = await db.Jobs.LongCountAsync(job => job.TenantId == tenantId, cancellationToken).ConfigureAwait(false),
            ["requests"] = await db.Requests.LongCountAsync(request => request.TargetTenantId == tenantId, cancellationToken).ConfigureAwait(false),
            ["operatorPolicies"] = await db.McpOperatorPolicies.LongCountAsync(policy => policy.TenantId == tenantId, cancellationToken).ConfigureAwait(false),
            ["operatorScripts"] = await db.McpOperatorScripts.LongCountAsync(script => script.TenantId == tenantId, cancellationToken).ConfigureAwait(false),
            ["operatorJobs"] = await db.McpOperatorJobs.LongCountAsync(job => job.TenantId == tenantId, cancellationToken).ConfigureAwait(false),
            ["operatorTasks"] = await db.McpOperatorTasks.LongCountAsync(task => task.TenantId == tenantId, cancellationToken).ConfigureAwait(false),
            ["operatorRequests"] = await db.McpOperatorRequests.LongCountAsync(request => request.TenantId == tenantId, cancellationToken).ConfigureAwait(false)
        };
        return new McpOperatorTenantImpact(counts, counts.Values.Sum());
    }

    private static async Task<McpOperatorTenantContextResult> TryContextAsync(string operation, HttpContext http, IHostEnvironment environment, McpOperatorLocalAgentOptions localAgents, IMcpOperatorAuthorization authorization, CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(operation, http.TraceIdentifier);
        var delegated = http.TryGetMcpOperatorDelegation(out var assertion) && assertion is not null;
        if (!delegated && !McpOperatorLocalAgentDelegation.TryCreateControlPlane(http, environment, localAgents, Tool, operation, out assertion))
            return new(null, Failure(environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required", fallback));
        var effective = assertion!;
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        var descriptor = McpOperatorOperationCatalog.Find(Tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null || descriptor is null || !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effective.Tool, Tool, StringComparison.Ordinal) || !string.Equals(effective.Operation, operation, StringComparison.Ordinal) ||
            effective.TenantId is not null || effective.AgentId is not null || string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
            return new(null, Failure("delegated_identity_invalid", fallback));

        var principal = new McpOperatorPrincipal(effective.Identity.Subject, effective.Identity.ClientId, effective.Identity.AuthorizedParty,
            effective.Identity.Groups.ToHashSet(StringComparer.Ordinal), effective.Identity.Roles.ToHashSet(StringComparer.Ordinal), effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal), effective.ServicePrincipal);
        var request = new McpOperatorAccessRequest(operatorEnvironment, principal, 0, null, null,
            descriptor.OperationFamily, $"{Tool}/{operation}", new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            descriptor.ConfirmationClass, effective.CorrelationId!, effective.RequestId, ControlPlaneDigest, null,
            effective.Resource, effective.Instance, Tool, true, true, true, true, true);
        var decision = await authorization.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorTenantContext(decision, effective.ServicePrincipal, McpOperationAccessScopeNames.Canonical(access.RequiredScope), operation, effective.CorrelationId!, effective.Identity.ActiveTenantId);
        return decision.IsAllowed ? new(context, null) : new(context, Failure(decision.FailureCode ?? "target_policy_missing", context));
    }

    private static bool IsPayloadValid(string action, McpOperatorTenantMutationRequest request)
    {
        if (action == "delete")
            return request.TenantId > 0 && request.ExpectedVersion > 0 && request.Cascade.HasValue;
        if (action is not ("create" or "update") || !IsTenantValuesValid(request)) return false;
        return action == "create"
            ? request.TenantId is null && request.ExpectedVersion is null && request.Cascade is null
            : request.TenantId > 0 && request.ExpectedVersion > 0 && request.Cascade is null;
    }

    private static bool IsTenantValuesValid(McpOperatorTenantMutationRequest request) =>
        IsText(request.Name, 160) && request.Domains is { Count: <= 64 } && request.Domains.All(domain => IsText(domain, 253)) &&
        request.Domains.Distinct(StringComparer.OrdinalIgnoreCase).Count() == request.Domains.Count && request.AutoUpdate.HasValue &&
        IsOptionalText(request.Description, 4_096) && IsOptionalText(request.Location, 256) && IsOptionalText(request.ContactPerson, 256) &&
        IsOptionalText(request.ContactEmail, 320) && IsOptionalText(request.AutoUpdateTargetVersion, 128) &&
        (request.AutoUpdateChannel is null or "stable" or "prerelease");

    private static bool IsMutation(string action) => action is "create" or "update" or "delete";
    private static bool IsActiveTenantDeleteWithoutSeparateApproval(string action, McpOperatorTenantMutationRequest request, McpOperatorTenantContext context) =>
        action == "delete" && request.TenantId == context.ActiveTenantId && context.Decision.EffectiveConstraints?.AllowActiveTenantDeletion is not true;
    private static bool IsText(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool IsOptionalText(string? value, int maximum) => value is null || (value.Length <= maximum && !value.Any(char.IsControl));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool HasPlan(string? plan, string? key) => IsOpaque(plan) && IsOpaque(key);
    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string MutationHash(string action, McpOperatorTenantMutationRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { action, request.TenantId, request.ExpectedVersion, request.Name, request.Description, request.Location, request.Domains, request.ContactPerson, request.ContactEmail, request.AutoUpdate, request.AutoUpdateChannel, request.AutoUpdateTargetVersion, request.Cascade }))));
    private static void SetEtag(HttpContext http, long version) => http.Response.Headers.ETag = $"\"{version}\"";
    private static McpOperatorTenantSummary ToSummary(TenantInfo tenant) => new(tenant.TenantId, tenant.Name, tenant.Description, tenant.Location, tenant.Domains.ToArray(), tenant.ContactPerson, tenant.ContactEmail, tenant.AutoUpdate, tenant.AutoUpdateChannel, tenant.AutoUpdateTargetVersion, tenant.CreatedAtUtc, tenant.UpdatedAtUtc, tenant.Version);
    private static McpOperatorTenantContext MinimalContext(string operation, string correlationId) => new(null!, string.Empty, McpOperationAccessScopeNames.Canonical(McpOperationAccessCatalog.Find(Tool, operation)!.RequiredScope), operation, correlationId, null);

    private static IResult Failure(string code, McpOperatorTenantContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "tenant_not_found" => StatusCodes.Status404NotFound,
            "tenant_invalid" or "tenant_query_invalid" => StatusCodes.Status400BadRequest,
            "tenant_version_conflict" or "tenant_has_dependents" or "tenant_cascade_not_supported" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_invalid" or "tenant_query_invalid" or "tenant_version_conflict" or "tenant_has_dependents" or "tenant_cascade_not_supported" or "active_caller_tenant_delete_requires_separate_approval" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator tenant access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator tenant access was not admitted.",
            ["failure"] = new { code, layer, retryable = false, requiredScopes = new[] { context.RequiredScope }, requiredOperation = $"{Tool}/{context.Operation}", target = (object?)null, remediation = "Use a delegated administrator identity with netratel.mcp.admin and an explicit active ControlPlane policy; use a current tenant ETag and resolve dependent objects before deletion." },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorTenantContext(McpOperatorDecision Decision, string ServicePrincipal, string RequiredScope, string Operation, string CorrelationId, int? ActiveTenantId);
    private sealed record McpOperatorTenantContextResult(McpOperatorTenantContext? Context, IResult? Failure);
    private sealed record TenantResolution(TenantInfo? Tenant, McpOperatorTenantImpact? Impact, string? FailureCode)
    {
        public static TenantResolution Valid { get; } = new(null, null, null);
        public static TenantResolution Invalid { get; } = new(null, null, "tenant_invalid");
        public static TenantResolution NotFound { get; } = new(null, null, "tenant_not_found");
        public static TenantResolution VersionConflict { get; } = new(null, null, "tenant_version_conflict");
    }
    private sealed record TenantMutation(TenantInfo? Tenant, string? FailureCode);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorTenantMutationRequest(
    int? TenantId = null,
    long? ExpectedVersion = null,
    string? Name = null,
    string? Description = null,
    string? Location = null,
    IReadOnlyList<string>? Domains = null,
    string? ContactPerson = null,
    string? ContactEmail = null,
    bool? AutoUpdate = null,
    string? AutoUpdateChannel = null,
    string? AutoUpdateTargetVersion = null,
    bool? Cascade = null,
    string? PlanToken = null,
    string? IdempotencyKey = null);

public sealed record McpOperatorTenantSummary(int TenantId, string Name, string? Description, string? Location, IReadOnlyList<string> Domains, string? ContactPerson, string? ContactEmail, bool AutoUpdate, string AutoUpdateChannel, string? AutoUpdateTargetVersion, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Version);
public sealed record McpOperatorTenantPage(IReadOnlyList<McpOperatorTenantSummary> Items, int? NextCursor, string CorrelationId);
public sealed record McpOperatorTenantImpact(IReadOnlyDictionary<string, long> Dependents, long TotalDependents);
public sealed record McpOperatorTenantPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string Action, int? TenantId, long? Version, McpOperatorTenantImpact? Impact, string TargetSetDigest, string CorrelationId);
public sealed record McpOperatorTenantMutationResult(int TenantId, long Version, bool Deleted, bool Replayed, string CorrelationId);
