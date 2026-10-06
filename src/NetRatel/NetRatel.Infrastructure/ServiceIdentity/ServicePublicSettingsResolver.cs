using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceIdentity;

namespace NetRatel.Infrastructure.ServiceIdentity;

public sealed class ServiceIdentityConfiguration
{
    public int Id { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public bool Enabled { get; set; }
    public string WebBaseUrl { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "netratel.services";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed record ServicePublicSettingsEffective(ServiceIdentityOptions Identity, ServiceLinkOptions Linking,
    long Revision, string[] LockedFields);
public interface IServicePublicSettingsResolver
{
    Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default);
    Task<ServicePublicSettingsEffective> UpdateAsync(ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default);
}

/// <summary>Durable UI settings and explicit deployment locks form one fresh public identity. Request Host is never input.</summary>
public sealed class ServicePublicSettingsResolver(OrchestratorDbContext db, IConfiguration configuration,
    IOptionsMonitor<ServiceIdentityOptions> identityOptions, IOptionsMonitor<ServiceLinkOptions> linkingOptions,
    IDeploymentBrandingService branding, IOptionsMonitor<ClientInstallationEndpointOptions> installation, ServiceLinkIdentityStore identities, TimeProvider clock) : IServicePublicSettingsResolver
{
    public async Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) =>
        await Resolve(await db.Set<ServiceIdentityConfiguration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct), ct);

    public async Task<ServicePublicSettingsEffective> UpdateAsync(ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default)
    {
        var row = await db.Set<ServiceIdentityConfiguration>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if ((row?.Revision ?? 0) != update.ExpectedRevision) throw new ServiceClientConflictException("Public service settings changed; reload the current revision.");
        var current = await Resolve(row, ct);
        foreach (var field in current.LockedFields)
        {
            var changed = field switch
            {
                "enabled" => update.Enabled != current.Identity.Enabled,
                "webBaseUrl" => update.WebBaseUrl != current.Identity.WebBaseUrl,
                "apiBaseUrl" => update.ApiBaseUrl != current.Identity.ApiBaseUrl,
                "issuer" => update.Issuer != current.Identity.Issuer,
                "audience" => update.Audience != current.Identity.Audience,
                _ => false
            };
            if (changed) throw new ArgumentException($"The {field} field is managed by deployment configuration.");
        }
        var candidate = new ServiceIdentityConfiguration { Revision = (row?.Revision ?? 0) + 1, Enabled = update.Enabled,
            WebBaseUrl = update.WebBaseUrl, ApiBaseUrl = update.ApiBaseUrl, Issuer = update.Issuer,
            Audience = update.Audience, UpdatedBy = actorId, UpdatedAtUtc = clock.GetUtcNow() };
        _ = await Resolve(candidate, ct); // Validate the effective complete profile before writing anything.
        if (row is null) db.Set<ServiceIdentityConfiguration>().Add(candidate);
        else
        {
            row.Enabled = candidate.Enabled; row.WebBaseUrl = candidate.WebBaseUrl; row.ApiBaseUrl = candidate.ApiBaseUrl;
            row.Issuer = candidate.Issuer; row.Audience = candidate.Audience; row.UpdatedBy = actorId;
            row.UpdatedAtUtc = candidate.UpdatedAtUtc; row.Revision = candidate.Revision;
        }
        await db.SaveChangesAsync(ct);
        return await ResolveAsync(ct);
    }

    private async Task<ServicePublicSettingsEffective> Resolve(ServiceIdentityConfiguration? stored, CancellationToken ct)
    {
        var identity = identityOptions.CurrentValue; var linking = linkingOptions.CurrentValue;
        var stable = await identities.GetAsync(ct);
        var brand = await branding.GetEffectiveAsync(ct); var artifacts = installation.CurrentValue;
        var locks = new List<string>();
        string Select(string field, string? configured, string? local, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(configured)) { locks.Add(field); return field is "issuer" or "audience" ? configured : configured.TrimEnd('/'); }
            return field is "issuer" or "audience" ? (!string.IsNullOrWhiteSpace(local) ? local : fallback) : (!string.IsNullOrWhiteSpace(local) ? local.TrimEnd('/') : fallback.TrimEnd('/'));
        }
        if (!string.IsNullOrWhiteSpace(identity.WebBaseUrl) && !string.IsNullOrWhiteSpace(linking.WebBaseUrl) && identity.WebBaseUrl.TrimEnd('/') != linking.WebBaseUrl.TrimEnd('/') ||
            !string.IsNullOrWhiteSpace(identity.ApiBaseUrl) && !string.IsNullOrWhiteSpace(linking.ApiBaseUrl) && identity.ApiBaseUrl.TrimEnd('/') != linking.ApiBaseUrl.TrimEnd('/'))
            throw new ArgumentException("ServiceIdentity and ServiceLinks public identities disagree; configure one coherent complete profile.");
        var webConfigured = !string.IsNullOrWhiteSpace(identity.WebBaseUrl) ? identity.WebBaseUrl : linking.WebBaseUrl;
        if (string.IsNullOrWhiteSpace(webConfigured) && brand.SiteUrl.IsLocked) webConfigured = brand.SiteUrl.Value;
        var web = Select("webBaseUrl", webConfigured, stored?.WebBaseUrl, brand.SiteUrl.Value);
        var apiConfigured = !string.IsNullOrWhiteSpace(identity.ApiBaseUrl) ? identity.ApiBaseUrl : !string.IsNullOrWhiteSpace(linking.ApiBaseUrl) ? linking.ApiBaseUrl : artifacts.PublicBaseUrl;
        var api = Select("apiBaseUrl", apiConfigured, stored?.ApiBaseUrl, "");
        var issuer = Select("issuer", identity.Issuer, stored?.Issuer, string.IsNullOrEmpty(api) ? "" : api + "/services");
        var audience = Select("audience", configuration["ServiceIdentity:Audience"], stored?.Audience, identity.Audience);
        var enabled = stored?.Enabled ?? false;
        if (bool.TryParse(configuration["ServiceIdentity:Enabled"], out var explicitIdentity)) { enabled = explicitIdentity; locks.Add("enabled"); }
        var linkEnabled = enabled;
        if (bool.TryParse(configuration["ServiceLinks:Enabled"], out var explicitLink)) { linkEnabled = explicitLink; locks.Add("enabled"); }
        var resolved = new ServiceIdentityOptions
        {
            Enabled = enabled, InstanceId = stable.InstanceId, WebBaseUrl = web, ApiBaseUrl = api, Issuer = issuer, Audience = audience,
            AllowPrivateHttp = identity.AllowPrivateHttp || linking.AllowPrivateHttp, AccessTokenLifetimeSeconds = identity.AccessTokenLifetimeSeconds,
            ClockSkewSeconds = identity.ClockSkewSeconds, CredentialMaximumAgeDays = identity.CredentialMaximumAgeDays,
            ManualRotationOverlapSeconds = identity.ManualRotationOverlapSeconds, TerminalControlRecoverySeconds = identity.TerminalControlRecoverySeconds
        };
        var linked = new ServiceLinkOptions
        {
            Enabled = linkEnabled && enabled, WebBaseUrl = web, ApiBaseUrl = api,
            GatewayBaseUrl = linking.GatewayBaseUrl ?? artifacts.PublicGatewayBaseUrl ?? (string.IsNullOrWhiteSpace(brand.GatewayUrl?.Value) ? null : brand.GatewayUrl!.Value),
            SourceInstanceId = stable.SourceInstanceId, AllowPrivateHttp = resolved.AllowPrivateHttp,
            BootstrapLifetimeSeconds = linking.BootstrapLifetimeSeconds, TerminalControlRecoverySeconds = linking.TerminalControlRecoverySeconds,
            WorkerIntervalSeconds = linking.WorkerIntervalSeconds, MaximumPayloadBytes = linking.MaximumPayloadBytes,
            AutomaticRotationEnabled = linking.AutomaticRotationEnabled, RotationAgeDays = linking.RotationAgeDays,
            RotationOfferLifetimeSeconds = linking.RotationOfferLifetimeSeconds, RotationOverlapSeconds = linking.RotationOverlapSeconds,
            RotationPolicyRevision = linking.RotationPolicyRevision
        };
        if (resolved.Enabled && new[] { resolved.WebBaseUrl, resolved.ApiBaseUrl, resolved.Issuer }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Configure the public Web and API addresses before enabling service identities.");
        var validation = new ServiceIdentityOptionsValidator().Validate(null, resolved);
        if (validation.Failed) throw new ArgumentException(string.Join(" ", validation.Failures));
        var linkValidation = new ServiceLinkOptionsValidator(Options.Create(resolved)).Validate(null, linked);
        if (linkValidation.Failed) throw new ArgumentException(string.Join(" ", linkValidation.Failures));
        return new(resolved, linked, stored?.Revision ?? 0, locks.Distinct(StringComparer.Ordinal).ToArray());
    }
}
