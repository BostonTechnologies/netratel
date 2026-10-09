using Microsoft.Extensions.Options;

namespace NetRatel.Infrastructure.ServiceIdentity;

public interface IServiceIdentityRuntimeOptions
{
    Task<ServiceIdentityOptions> GetAsync(CancellationToken ct = default);
}

public sealed class ServiceIdentityRuntimeOptions(IServicePublicSettingsResolver publicSettings) : IServiceIdentityRuntimeOptions
{
    public async Task<ServiceIdentityOptions> GetAsync(CancellationToken ct = default) => (await publicSettings.ResolveAsync(ct)).Identity;
}

/// <summary>The API's single typed deployment boundary owns legacy aliases; no database secret is mixed with it.</summary>
public interface IServiceClientDeploymentCatalog
{
    bool OwnsIdentity(string clientId);
}
public sealed class EmptyServiceClientDeploymentCatalog : IServiceClientDeploymentCatalog
{
    public bool OwnsIdentity(string clientId) => false;
}
