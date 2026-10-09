using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.ServiceIdentity;
namespace NetRatel.Infrastructure.ServiceIdentity;
public sealed class ServiceIdentityConfiguration
{
    public int Id { get; set; } = 1; public long Revision { get; set; } = 1; public bool Enabled { get; set; }
    public string WebBaseUrl { get; set; } = ""; public string ApiBaseUrl { get; set; } = ""; public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "netratel.services"; public string UpdatedBy { get; set; } = ""; public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed record ServicePublicSettingsEffective(ServiceIdentityOptions Identity, long Revision, string[] LockedFields);
public interface IServicePublicSettingsResolver { Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default); }
public sealed class ServicePublicSettingsResolver(OrchestratorDbContext db, IConfiguration configuration,
    IOptionsMonitor<ServiceIdentityOptions> identityOptions, IDeploymentBrandingService branding,
    IOptionsMonitor<ClientInstallationEndpointOptions> installation, InstallationIdentityStore identities) : IServicePublicSettingsResolver
{
    public async Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default)
    {
        var settings = identityOptions.CurrentValue;
        var stable = await identities.GetAsync(ct);
        var stored = await db.Set<ServiceIdentityConfiguration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        var brand = await branding.GetEffectiveAsync(ct);
        var web = First(settings.WebBaseUrl, stored?.WebBaseUrl, brand.SiteUrl.Value);
        var api = First(settings.ApiBaseUrl, stored?.ApiBaseUrl, installation.CurrentValue.PublicBaseUrl);
        var issuer = First(settings.Issuer, stored?.Issuer, string.IsNullOrEmpty(api) ? "" : api + "/services");
        return new(new ServiceIdentityOptions { Enabled = true, InstanceId = stable.InstanceId.ToString("D"), WebBaseUrl = web,
            ApiBaseUrl = api, Issuer = issuer, Audience = First(configuration["ServiceIdentity:Audience"], stored?.Audience, settings.Audience),
            AllowPrivateHttp = true, AccessTokenLifetimeSeconds = settings.AccessTokenLifetimeSeconds, ClockSkewSeconds = settings.ClockSkewSeconds,
            CredentialMaximumAgeDays = settings.CredentialMaximumAgeDays }, stored?.Revision ?? 0, []);
    }
    private static string First(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.TrimEnd('/') ?? "";
}
