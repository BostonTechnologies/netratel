using System.Data;
using System.Security.Claims;
using System.Text.Json;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using static NetRatel.Infrastructure.ServiceLinks.ServiceLinkValidation;

namespace NetRatel.Infrastructure.ServiceLinks;

public sealed partial class ServiceLinkCoordinator(
    OrchestratorDbContext db, IServicePrincipalRegistry registry, IEffectiveAccessService accessService,
    ServiceLinkProfileService providers, ServiceLinkTransport transport, IDataProtectionProvider protection,
    IOptions<ServiceLinkOptions> options, IServicePublicSettingsResolver publicSettings, TimeProvider clock,
    IServiceScopeFactory? scopes = null)
{
    private ServiceLinkOptions settings = options.Value;
    private async Task<ServicePublicSettingsEffective> CurrentSettings(CancellationToken ct)
    { var current = await publicSettings.ResolveAsync(ct); settings = current.Linking; return current; }
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Read<T>(string value) => ServiceLinkCanonicalJson.Deserialize<T>(value);
    private IDataProtector Protector(ServiceLinkAttempt a, string purpose) => protection.CreateProtector("NetRatel.ServiceLink.v1", a.AttemptId, a.PeerInstanceId, purpose,
        purpose is "verifier" or "browser-state" or "pairing-code" ? "bootstrap" : a.LocalTenantId + "/" + a.LinkId + "/" + a.GrantHash + "/" + a.LinkRevision);
    private string Protect(ServiceLinkAttempt a, string purpose, string clear) => Protector(a, purpose).Protect(clear);
    private string Unprotect(ServiceLinkAttempt a, string purpose, string cipher) => Protector(a, purpose).Unprotect(cipher);
    private async Task Save(ServiceLinkAttempt a, CancellationToken ct) { a.Revision++; a.UpdatedAtUnixSeconds = Now; await db.SaveChangesAsync(ct); }
    private async Task<ServiceLinkAttempt> Attempt(string id, CancellationToken ct) => await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.AttemptId == id, ct) ?? throw new ServiceLinkProtocolException(404, "attempt-not-found", "The attempt does not exist.");
    private async Task<ServiceLinkAttempt> Link(string id, CancellationToken ct) => await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.LinkId == id, ct) ?? throw new ServiceLinkProtocolException(404, "link-not-found", "The approved link does not exist.");
    private static ServiceLinkRequestDescriptor Descriptor(ServiceLinkAttempt a) => Read<ServiceLinkRequestDescriptor>(a.DescriptorJson);
    private static ServiceLinkGrantSummary Summary(ServiceLinkAttempt a) => Read<ServiceLinkGrantSummary>(a.GrantSummaryJson ?? throw new ServiceLinkProtocolException(409, "grant-not-approved", "The exact grant is not approved."));
    private ServiceLinkMetadata Local(ServiceLinkAttempt a) => a.Role == "initiator" ? Descriptor(a).InitiatorEndpointSnapshot : Descriptor(a).ResponderEndpointSnapshot;
    private ServiceLinkMetadata Peer(ServiceLinkAttempt a) => a.Role == "initiator" ? Descriptor(a).ResponderEndpointSnapshot : Descriptor(a).InitiatorEndpointSnapshot;
    private ServiceLinkGrant InboundGrant(ServiceLinkAttempt a) => Summary(a).Grants.Single(x => x.DirectionId == (a.Role == "initiator" ? ServiceLinkContract.ResponderToInitiator : ServiceLinkContract.InitiatorToResponder));
    private ServiceLinkGrant OutboundGrant(ServiceLinkAttempt a) => Summary(a).Grants.Single(x => x.DirectionId == (a.Role == "initiator" ? ServiceLinkContract.InitiatorToResponder : ServiceLinkContract.ResponderToInitiator));

    public async Task<ServiceLinkMetadata> MetadataAsync(CancellationToken ct)
    {
        var current = await CurrentSettings(ct); var issuer = current.Identity;
        Require(settings.Enabled && issuer.Enabled, "service-link-unavailable", "The deployment has not enabled service identities and reciprocal linking.", 503);
        Require(issuer.ApiBaseUrl.TrimEnd('/') == settings.ApiBaseUrl.TrimEnd('/') && issuer.WebBaseUrl.TrimEnd('/') == settings.WebBaseUrl.TrimEnd('/'), "service-link-configuration-invalid", "Issuer and link canonical addresses must agree.", 503);
        return ServiceLinkPayloadNormalization.Metadata(new ServiceLinkMetadata
        {
            Product = "netratel", ProductVersion = typeof(ServiceLinkCoordinator).Assembly.GetCustomAttributes(false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "runtime",
            InstanceId = issuer.InstanceId, SourceInstanceId = settings.SourceInstanceId, WebBaseUrl = settings.WebBaseUrl.TrimEnd('/'), ApiBaseUrl = settings.ApiBaseUrl.TrimEnd('/'), GatewayBaseUrl = settings.GatewayBaseUrl,
            OauthIssuer = issuer.Issuer, OauthMetadataUrl = Endpoint(settings.ApiBaseUrl, "/.well-known/oauth-authorization-server"),
            TokenEndpoint = Endpoint(settings.ApiBaseUrl, "/connect/token"), JwksUri = Endpoint(settings.ApiBaseUrl, "/.well-known/service-jwks.json"), Audience = issuer.Audience,
            ServiceLinkEndpoint = Endpoint(settings.ApiBaseUrl, ServiceLinkContract.EndpointPath),
            ApprovalEndpoint = Endpoint(settings.WebBaseUrl, "/account/integration-credentials/link/approve"), CallbackEndpoint = Endpoint(settings.WebBaseUrl, "/account/integration-credentials/link/callback"),
            PermissionProfiles =
            [
                new("netratel.orchestration.v1", [ServiceIdentityScopes.OrchestrationInvoke, ServiceIdentityScopes.OrchestrationRead],
                [new("GET", "/internal/health", ServiceIdentityScopes.OrchestrationRead), new("GET", "/api/v1/system/m2m/ping", ServiceIdentityScopes.OrchestrationRead), new("GET", "/internal/catalog/jobs", ServiceIdentityScopes.OrchestrationRead), new("GET", "/internal/catalog/tenants", ServiceIdentityScopes.OrchestrationRead), new("GET", "/internal/catalog/request-definitions", ServiceIdentityScopes.OrchestrationRead), new("POST", "/internal/ingest", ServiceIdentityScopes.OrchestrationInvoke)]),
                new(ServiceLinkContract.Version, [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope],
                [new("POST", ServiceLinkContract.EndpointPath + "/links/{link_id}/verify", ServiceLinkContract.VerifyScope), new("GET", ServiceLinkContract.EndpointPath + "/links/{link_id}/status", ServiceLinkContract.ControlScope), .. new[] { "ack", "commit", "abort", "revoke", "rotate" }.Select(x => new ServiceLinkResourceOperation("POST", ServiceLinkContract.EndpointPath + "/links/{link_id}/" + x, ServiceLinkContract.ControlScope))])
            ]
        });
    }

    private async Task<string> Authorize(ClaimsPrincipal actor, string tenantId, CancellationToken ct)
    {
        Require(actor.Identity?.IsAuthenticated == true && actor.FindFirst("netratel_integration_credential_id") is null && actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null, "administrator-required", "An interactive product administrator session is required.", 403);
        Require(ServiceLinkGrantAuthority.TryTenant(tenantId, out var tenant) && await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenant, ct) &&
            await accessService.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, tenant, ct), "administrator-required", "The selected tenant requires current integration-management authority.", 403);
        return actor.FindFirstValue("netratel_principal_id") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub") ?? throw new ServiceLinkProtocolException(403, "administrator-required", "The approving actor is unidentified.");
    }

    private async Task EnsureLocalIdentity(ServiceLinkAttempt a, CancellationToken ct)
    {
        var current = await CurrentSettings(ct);
        Require(ServiceLinkAuthority.LocalEndpointMatches(Local(a), current), "identity-configuration-drift", "Restore the approved local identity or start a new explicit consent ceremony.", 409);
    }

    public async Task<ServiceLinkRequestDescriptor> PublicDescriptorAsync(string attemptId, CancellationToken ct)
    {
        Id(attemptId); var a = await Attempt(attemptId, ct);
        Require(a.Role == "initiator" && a.ExpiresAtUnixSeconds > Now && a.Decision == "undecided" && a.LifecycleState is not ("revoked" or "failed"), "attempt-expired", "The public descriptor is unavailable.", 404);
        return Descriptor(a);
    }

    private async Task<CreatedServiceClient> CreateInbound(ServiceLinkAttempt a, string actorId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureLocalIdentity(a, ct);
        var g = InboundGrant(a);
        await ServiceLinkGrantAuthority.ValidateLocalGrantAsync(db, g, actor, accessService, ct);
        var created = await registry.CreatePendingAsync(new ServiceClientCreateRequest("RatelDesk reciprocal service", int.Parse(g.TargetTenantId, System.Globalization.CultureInfo.InvariantCulture),
            g.CallerInstanceId, g.CallerTenantId, g.Scopes, Json(g.ResourceConstraints), a.LinkId, a.AttemptId, a.GrantHash, a.DescriptorHash, g.DirectionId, a.LinkRevision), actorId, ct);
        a.InboundPrincipalId = created.Principal.Id;
        a.ProtectedInboundEscrow = Protect(a, "inbound-escrow", Json(CredentialFrom(created, g, Local(a))));
        return created;
    }

    private static ServiceDirectionalCredential CredentialFrom(CreatedServiceClient created, ServiceLinkGrant g, ServiceLinkMetadata target) => new()
    {
        ClientId = created.Principal.ClientId, ClientSecret = created.ClientSecret, CredentialRevision = created.CredentialRevision,
        Issuer = g.Issuer, TokenEndpoint = target.TokenEndpoint, Audience = g.Audience, Scopes = g.Scopes,
        CallerInstanceId = g.CallerInstanceId, CallerTenantId = g.CallerTenantId, TargetInstanceId = g.TargetInstanceId, TargetTenantId = g.TargetTenantId
    };

    private async Task<ServiceLinkAdminStatus> AdminStatus(ServiceLinkAttempt a, CancellationToken ct)
    {
        var inbound = await ServiceLinkAuthority.InboundUsableAsync(db, a, clock, await CurrentSettings(ct), ct);
        var provider = await providers.GetAsync(a.LinkId, ct);
        var sender = SenderUsable(a, provider, inbound);
        return new(a.AttemptId, a.LinkId, a.LinkRevision, a.LifecycleState,
            a.LocalTenantId, a.PeerInstanceId, a.PeerTenantId, a.Decision, a.CommitId, a.GrantHash, Descriptor(a), a.GrantSummaryJson is null ? null : Summary(a),
            a.InboundPrincipalId is not null, a.ProtectedOutboundCredential is not null, inbound, sender, a.PeerActiveAcknowledged, EffectiveError(a, inbound, sender),
            provider.ManagedByDeployment, await RotationSummaries(a, ct))
            { AutomaticRotationEnabled = settings.AutomaticRotationEnabled, RotationAgeDays = settings.RotationAgeDays, RotationOverlapSeconds = settings.RotationOverlapSeconds };
    }
    private static bool SenderUsable(ServiceLinkAttempt a, ServiceLinkProfileStatus provider, bool inbound) =>
        inbound && a.LocalBusinessSenderEnabled && a.PeerActiveAcknowledged && provider.Enabled && provider.ManagedSenderEnabled && provider.LinkId == a.LinkId &&
        provider.LinkRevision == a.LinkRevision && provider.LocalTenantId == a.LocalTenantId && provider.PeerInstanceId == a.PeerInstanceId && provider.PeerTenantId == a.PeerTenantId;
    private static string? EffectiveError(ServiceLinkAttempt a, bool inbound, bool sender) => a.LastErrorCode ??
        (a.Decision == "commit" && a.LifecycleState == "active" && (!inbound || !sender) ? "grant-unavailable" : null);

    public async Task<ServiceLinkAdminStatus> AdminStatusAsync(string attemptId, ClaimsPrincipal actor, CancellationToken ct)
    { var a = await Attempt(attemptId, ct); await AuthorizeAttempt(a, actor, ct); return await AdminStatus(a, ct); }
    private async Task AuthorizeAttempt(ServiceLinkAttempt a, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(a.LocalTenantId)) { await Authorize(actor, a.LocalTenantId, ct); return; }
        var tenants = await accessService.GetAuthorizedTenantIdsAsync(actor, NetRatelPermissions.IntegrationManagement, ct);
        var actorId = actor.FindFirstValue("netratel_principal_id") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub");
        Require(actor.Identity?.IsAuthenticated == true && a.Role == "responder" && a.LifecycleState == "awaiting_approval" && a.LocalActorId == actorId &&
            actor.FindFirst("netratel_integration_credential_id") is null && actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null && (tenants is null || tenants.Length > 0), "administrator-required", "The initiating local integration manager is required to review this unapproved descriptor.", 403);
    }
    public async Task<ServiceLinkAdminStatus[]> AdminListAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        var tenants = await accessService.GetAuthorizedTenantIdsAsync(actor, NetRatelPermissions.IntegrationManagement, ct);
        Require(actor.Identity?.IsAuthenticated == true && actor.FindFirst("netratel_integration_credential_id") is null && actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null && (tenants is null || tenants.Length > 0), "administrator-required", "Integration-management authority is required.", 403);
        var all = await db.Set<ServiceLinkAttempt>().OrderByDescending(x => x.CreatedAtUnixSeconds).Take(100).ToListAsync(ct);
        var result = new List<ServiceLinkAdminStatus>();
        foreach (var a in all)
            if (ServiceLinkGrantAuthority.TryTenant(a.LocalTenantId, out var tenant) && (tenants is null || tenants.Contains(tenant)) || string.IsNullOrEmpty(a.LocalTenantId) && a.LocalActorId == actor.FindFirstValue("netratel_principal_id")) result.Add(await AdminStatus(a, ct));
        return result.ToArray();
    }

    private async Task<IReadOnlyList<ServiceLinkRotationSummary>> RotationSummaries(ServiceLinkAttempt a, CancellationToken ct) => (await db.Set<ServiceLinkRotation>().Where(x => x.LinkId == a.LinkId).ToListAsync(ct)).Select(x => new ServiceLinkRotationSummary(x.RotationId, x.DirectionId, x.RotationState, x.ExpectedCurrentCredentialRevision, x.SuccessorCredentialRevision, x.OfferExpiresAtUnixSeconds is null ? null : Timestamp(x.OfferExpiresAtUnixSeconds.Value), x.SuccessorVerificationReceiptId, x.ActivateDecisionId, x.CallerSwitchRevision, x.PredecessorRetireAtUnixSeconds is null ? null : Timestamp(x.PredecessorRetireAtUnixSeconds.Value))).ToArray();
}
