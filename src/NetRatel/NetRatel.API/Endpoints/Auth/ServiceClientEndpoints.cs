using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Security.M2M;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceIdentity;

namespace NetRatel.API.Endpoints.Auth;

public static class ServiceClientEndpoints
{
    public static IEndpointRouteBuilder MapServiceClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/account/service-clients").RequireAuthorization("InteractiveAccount").WithTags("Service clients");
        group.MapGet("/settings", async (ClaimsPrincipal actor, IServicePublicSettingsResolver resolver,
            IServiceIdentityRuntimeOptions runtime, IEffectiveAccessService access, CancellationToken ct) =>
        {
            if (!Interactive(actor) || !await access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, null, ct)) return Results.Forbid();
            try
            {
                var settings = await resolver.ResolveAsync(ct); var identity = await runtime.GetAsync(ct);
                return Results.Ok(Settings(settings, identity.InstanceId));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        group.MapPut("/settings", async (ClaimsPrincipal actor, ServicePublicSettingsUpdate update,
            IServicePublicSettingsResolver resolver, IServiceIdentityRuntimeOptions runtime, IEffectiveAccessService access,
            IRatelDeskConnectorSetupService setup, CancellationToken ct) =>
        {
            if (!Interactive(actor) || !await access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, null, ct)) return Results.Forbid();
            try
            {
                var settings = await resolver.UpdateAsync(update, ActorId(actor)!, ct);
                // Only validated authorized Enable connections prepares the actual persistent Flow producer.
                if (settings.Identity.Enabled && settings.Linking.Enabled) await setup.PrepareProducerAsync(actor, ct);
                var identity = await runtime.GetAsync(ct);
                return Results.Ok(Settings(settings, identity.InstanceId));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ServiceLinkProtocolException ex) { return Results.Json(new { error = ex.Code }, statusCode: ex.StatusCode); }
            catch (ServiceClientConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "public-settings-revision-conflict" }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "public-settings-revision-conflict" }); }
        });
        group.MapGet("/authority", async (ClaimsPrincipal actor, OrchestratorDbContext db, IEffectiveAccessService access,
            IServicePublicSettingsResolver publicSettings, CancellationToken ct) =>
        {
            if (!Interactive(actor)) return Results.Forbid();
            var permitted = await access.GetAuthorizedTenantIdsAsync(actor, NetRatelPermissions.IntegrationManagement, ct);
            var tenants = await db.Tenants.AsNoTracking().Where(x => permitted == null || permitted.Contains(x.Id)).OrderBy(x => x.Name).ToListAsync(ct);
            var result = new List<ServiceClientTenantAuthority>();
            foreach (var tenant in tenants)
            {
                if (!await CanManage(actor, tenant.Id, access, ct)) continue;
                var jobAuthority = await access.AuthorizeAsync(actor, NetRatelPermissions.JobManagement, tenant.Id, ct);
                var agents = await db.Agents.AsNoTracking().Where(x => jobAuthority && x.TenantId == tenant.Id && x.IsEnabled && x.Status == AgentStatus.Active && x.RevokedAtUtc == null).ToListAsync(ct);
                var agentIds = agents.Select(x => x.Id).ToArray();
                var jobs = await db.Jobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.AgentId != null && agentIds.Contains(x.AgentId.Value)).OrderBy(x => x.Name).ToListAsync(ct);
                result.Add(new(tenant.Id, tenant.Name, jobAuthority ? ServiceIdentityScopes.Business : [],
                    agents.Select(x => new ServiceClientResourceChoice(x.Id.ToString("D"), x.Name ?? x.Id.ToString("D"))).ToArray(),
                    jobs.Select(x => new ServiceClientDefinitionChoice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, x.AgentId?.ToString("D"))).ToArray()));
            }
            var reciprocalEnabled = false;
            try
            {
                var effective = await publicSettings.ResolveAsync(ct);
                reciprocalEnabled = effective.Identity.Enabled && effective.Linking.Enabled;
            }
            catch (Exception exception) when (exception is ArgumentException or Microsoft.Extensions.Options.OptionsValidationException or ServiceLinkProtocolException)
            {
                // A broken optional service profile must not turn tenant grant
                // authority into a claim of guided availability or a global edit right.
            }
            return Results.Ok(new ServiceClientManagementAuthority(result.Count > 0, result.ToArray(), reciprocalEnabled));
        });
        group.MapGet("/", async (ClaimsPrincipal actor, IServicePrincipalRegistry registry, IEffectiveAccessService access, CancellationToken ct) =>
        {
            if (!Interactive(actor)) return Results.Forbid();
            var clients = new List<ServiceClientMetadata>();
            foreach (var client in await registry.ListAsync(ct)) if (await CanManage(actor, client.TenantId, access, ct)) clients.Add(client);
            return Results.Ok(clients);
        });
        group.MapGet("/deployment", async (ClaimsPrincipal actor, IEffectiveAccessService access, IM2MDeploymentProfileResolver deployment, CancellationToken ct) =>
        {
            if (!Interactive(actor) || !await access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, null, ct)) return Results.Forbid();
            try { return Results.Ok(deployment.List().Select(x => new ServiceClientDeploymentMetadata(x.ClientId, x.Audience, x.AllowedScopes))); }
            catch (ServiceClientConflictException) { return Results.Conflict(new { error = "deployment-alias-conflict" }); }
        });
        group.MapPost("/", async (HttpContext context, ClaimsPrincipal actor, ServiceClientCreateRequest request, IServicePrincipalRegistry registry,
            IServiceIdentityRuntimeOptions options, IEffectiveAccessService access, CancellationToken ct) =>
        {
            if (!await CanManage(actor, request.TenantId, access, ct) || !await access.AuthorizeAsync(actor, NetRatelPermissions.JobManagement, request.TenantId, ct)) return Results.Forbid();
            if (request.LinkId is not null || request.AttemptId is not null || request.GrantHash is not null || request.DescriptorHash is not null || request.DirectionId is not null) return Results.BadRequest(new { error = "guided-grants-require-link-workflow" });
            try
            {
                var created = await registry.CreateAsync(request, ActorId(actor)!, ct: ct);
                var reveal = await Reveal(created, options, ct); context.Response.Headers.CacheControl = "no-store";
                return Results.Created($"/api/v2/account/service-clients/{created.Principal.Id:D}", reveal);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ServiceClientConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "service-client-registration-conflict" }); }
            catch (InvalidOperationException) { return Results.Json(new { error = "service-issuer-unavailable" }, statusCode: 503); }
        });
        group.MapPost("/{id:guid}/rotate", async (Guid id, ServiceClientRotateRequest request, HttpContext context, ClaimsPrincipal actor,
            OrchestratorDbContext db, IServicePrincipalRegistry registry, IServiceIdentityRuntimeOptions options, IEffectiveAccessService access, CancellationToken ct) =>
        {
            var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (!await CanManage(actor, row.TenantId, access, ct)) return Results.Forbid();
            try { var result = await registry.RotateAsync(id, request.ExpectedCredentialRevision, ct); context.Response.Headers.CacheControl = "no-store"; return Results.Ok(await Reveal(result, options, ct)); }
            catch (ServiceClientConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "credential-revision-conflict" }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "credential-revision-conflict" }); }
        });
        group.MapPost("/{id:guid}/revoke", async (Guid id, ServiceClientRevokeRequest request, ClaimsPrincipal actor, OrchestratorDbContext db,
            IServicePrincipalRegistry registry, IEffectiveAccessService access, CancellationToken ct) =>
        {
            var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (!await CanManage(actor, row.TenantId, access, ct)) return Results.Forbid();
            if (row.Revision != request.ExpectedRevision) return Results.Conflict(new { error = "grant-revision-conflict" });
            if (row.LinkId is not null) return Results.Conflict(new { error = "use-reciprocal-workflow" });
            try { await registry.RevokeAsync(id, ct); return Results.NoContent(); }
            catch (ServiceClientConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "grant-revision-conflict" }); }
        });
        return app;
    }
    private static bool Interactive(ClaimsPrincipal actor) => actor.Identity?.IsAuthenticated == true && ActorId(actor) is not null &&
        actor.FindFirst("netratel_integration_credential_id") is null && !actor.HasClaim("auth_mode", "service") &&
        !actor.HasClaim("auth_mode", "machine_token") && actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null;
    private static string? ActorId(ClaimsPrincipal actor) => actor.FindFirstValue("netratel_principal_id") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub");
    private static ServicePublicSettingsResponse Settings(ServicePublicSettingsEffective effective, string instanceId) =>
        new(effective.Identity.Enabled, effective.Identity.WebBaseUrl, effective.Identity.ApiBaseUrl, effective.Identity.Issuer,
            effective.Identity.Audience, instanceId, effective.Linking.GatewayBaseUrl, effective.Revision, effective.LockedFields,
            effective.Identity.Enabled && effective.Linking.Enabled);
    private static async Task<bool> CanManage(ClaimsPrincipal actor, int tenantId, IEffectiveAccessService access, CancellationToken ct) =>
        Interactive(actor) && tenantId > 0 && await access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, tenantId, ct);
    private static async Task<ServiceClientReveal> Reveal(CreatedServiceClient created, IServiceIdentityRuntimeOptions runtime, CancellationToken ct)
    {
        var options = await runtime.GetAsync(ct);
        return new(ServicePrincipalRegistry.Metadata(created.Principal, created.CredentialExpiresAtUtc), created.ClientSecret, options.Issuer,
            options.ApiBaseUrl.TrimEnd('/') + "/connect/token", options.Audience, ServicePrincipalRegistry.ReadArray(created.Principal.AllowedScopesJson));
    }
}
