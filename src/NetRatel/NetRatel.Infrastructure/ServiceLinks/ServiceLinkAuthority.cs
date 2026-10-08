using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.Infrastructure.ServiceLinks;

/// <summary>Current configuration and database authority must still match the immutable consent.</summary>
public static class ServiceLinkAuthority
{
    public static bool LocalIdentityMatches(ServiceLinkGrantSummary summary, string role, ServicePublicSettingsEffective current)
    {
        if (role is not ("initiator" or "responder")) return false;
        var local = role == "initiator" ? summary.InitiatorEndpointSnapshot : summary.ResponderEndpointSnapshot;
        var instance = role == "initiator" ? summary.InitiatorInstanceId : summary.ResponderInstanceId;
        return local.InstanceId == instance && LocalEndpointMatches(local, current);
    }

    public static bool LocalEndpointMatches(ServiceLinkMetadata local, ServicePublicSettingsEffective current)
    {
        var options = current.Identity; var linking = current.Linking;
        return options.Enabled && linking.Enabled && local.Product == "netratel" && local.Contract == ServiceLinkContract.Version &&
            local.InstanceId == options.InstanceId && local.SourceInstanceId == linking.SourceInstanceId &&
            local.GatewayBaseUrl == linking.GatewayBaseUrl && local.OauthIssuer == options.Issuer && local.Audience == options.Audience &&
            local.ApiBaseUrl == options.ApiBaseUrl.TrimEnd('/') && local.WebBaseUrl == options.WebBaseUrl.TrimEnd('/') &&
            local.TokenEndpoint == ServiceLinkValidation.Endpoint(options.ApiBaseUrl, "/connect/token") &&
            local.OauthMetadataUrl == ServiceLinkValidation.Endpoint(options.ApiBaseUrl, "/.well-known/oauth-authorization-server") &&
            local.JwksUri == ServiceLinkValidation.Endpoint(options.ApiBaseUrl, "/.well-known/service-jwks.json") &&
            local.ServiceLinkEndpoint == ServiceLinkValidation.Endpoint(options.ApiBaseUrl, ServiceLinkContract.EndpointPath) &&
            local.ApprovalEndpoint == ServiceLinkValidation.Endpoint(options.WebBaseUrl, "/account/integration-credentials/link/approve") &&
            local.CallbackEndpoint == ServiceLinkValidation.Endpoint(options.WebBaseUrl, "/account/integration-credentials/link/callback") &&
            local.TokenEndpointAuthMethodsSupported is ["client_secret_post"] && local.SupportedContracts.Contains(ServiceLinkContract.Version, StringComparer.Ordinal);
    }

    public static async Task<bool> InboundUsableAsync(OrchestratorDbContext db, ServiceLinkAttempt attempt, TimeProvider clock,
        ServicePublicSettingsEffective currentIdentity, CancellationToken ct)
    {
        if (!currentIdentity.Linking.Enabled) return false;
        var current = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == attempt.AttemptId && x.LinkId == attempt.LinkId && x.LinkRevision == attempt.LinkRevision, ct);
        if (current is null || current.Decision != "commit" || !current.LocalInboundActive || current.LifecycleState is not ("active" or "commit_decided") || current.InboundPrincipalId is null || current.GrantSummaryJson is null) return false;
        var principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == current.InboundPrincipalId, ct);
        if (principal is null || principal.Status != "active" || principal.LinkId != current.LinkId || principal.LinkRevision != current.LinkRevision || principal.AttemptId != current.AttemptId ||
            principal.GrantHash != current.GrantHash || principal.DescriptorHash != current.DescriptorHash || principal.PeerInstanceId != current.PeerInstanceId || principal.PeerTenantId != current.PeerTenantId ||
            principal.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture) != current.LocalTenantId) return false;
        try
        {
            var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(current.GrantSummaryJson);
            if (summary.AttemptId != current.AttemptId || summary.LinkId != current.LinkId || summary.ProposedLinkRevision != current.LinkRevision || summary.DescriptorHash != current.DescriptorHash ||
                summary.Contract != ServiceLinkContract.Version || !ServiceLinkPayloadNormalization.SummaryHashMatches(summary, current.GrantHash!) || !LocalIdentityMatches(summary, current.Role, currentIdentity)) return false;
            var identity = await db.Set<ServiceLinkRuntimeIdentity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
            var local = current.Role == "initiator" ? summary.InitiatorEndpointSnapshot : summary.ResponderEndpointSnapshot;
            if (identity is null || identity.InstanceId.ToString("D") != local.InstanceId || identity.SourceInstanceId?.ToString("D") != local.SourceInstanceId) return false;
            var direction = current.Role == "initiator" ? ServiceLinkContract.ResponderToInitiator : ServiceLinkContract.InitiatorToResponder;
            var grants = summary.Grants.Where(x => x.DirectionId == direction).ToArray();
            if (grants.Length != 1) return false;
            var grant = grants[0];
            if (grant.TargetProduct != "netratel" || grant.TargetTenantId != current.LocalTenantId || grant.TargetInstanceId != local.InstanceId || grant.CallerTenantId != current.PeerTenantId || grant.CallerInstanceId != current.PeerInstanceId || principal.DirectionId != direction) return false;
            var scopes = ServiceLinkCanonicalJson.Deserialize<string[]>(principal.AllowedScopesJson);
            var constraints = ServiceLinkCanonicalJson.Deserialize<ServiceLinkResourceConstraints>(principal.ResourceConstraintsJson);
            if (!scopes.Order(StringComparer.Ordinal).SequenceEqual(grant.Scopes.Order(StringComparer.Ordinal)) ||
                ServiceLinkCanonicalJson.HashObject(constraints) != ServiceLinkCanonicalJson.HashObject(grant.ResourceConstraints) ||
                !(ServiceLinkValidation.IncidentOnlyGrant(grant)
                    ? await ServiceLinkGrantAuthority.ControlResourcesCurrentAsync(db, constraints, ct)
                    : await ServiceLinkGrantAuthority.LocalResourcesCurrentAsync(db, constraints, ct))) return false;
        }
        catch (JsonException) { return false; }
        var secret = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x => x.ServicePrincipalId == principal.Id && x.CredentialRevision == principal.CurrentCredentialRevision, ct);
        return secret is not null && secret.Status is "active" or "retiring" && secret.ExpiresAtUtc > clock.GetUtcNow() && (secret.RetireAtUtc is null || secret.RetireAtUtc > clock.GetUtcNow());
    }
}
