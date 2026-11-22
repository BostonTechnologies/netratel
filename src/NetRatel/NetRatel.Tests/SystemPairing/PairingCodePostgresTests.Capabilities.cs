using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
using NetRatel.Tests.API;
using Xunit;

namespace NetRatel.Tests.SystemPairing;

public sealed partial class PairingCodePostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Setup_peer_saves_only_authorized_capabilities_and_integration_only_owner_can_delete(bool automation)
    {
        const int tenant = 71;
        const string incidentScope = "rateldesk.incidents.create";
        const string callbackScope = "rateldesk.orchestration.callback";
        var owner = PairingBusinessAuthorityFixture.Administrator;
        await using var rig = await Rig.CreateAsync(postgres);
        rig.Access.SetTenantGrants(owner, tenant, NetRatelPermissions.IntegrationManagement,
            automation ? NetRatelPermissions.SecretUse : NetRatelPermissions.JobManagement);
        await using var scope = rig.Scope();
        var service = scope.ServiceProvider.GetRequiredService<PairingService>();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Tenants.Add(new() { Id = tenant, Name = "Incident tenant" });
        await db.SaveChangesAsync();

        var exchange = rig.Peer.Request((await service.GenerateAsync(Human, default)).Code);
        var paired = await service.ExchangeAsync(exchange, default);
        var pair = await service.AuthenticateSetupAsync(rig.Peer.Metadata.InstallationId, paired.InboundSecret, default,
            PairingService.Hash(exchange.InboundSecret));
        var mapping = new PairingMapping(Guid.NewGuid(), pair.Id, "Incident mapping", "71",
            PairingBusinessAuthorityFixture.Organization, "synthetic-customer", true, automation);
        string[] incidentScopes = [incidentScope, "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read"];
        var metadata = await service.MetadataAsync(default);
        var credential = new PairingBusinessCredential("fixture-incident-client", new string('s', 43),
            "https://peer.example.test/connect/token", "rateldesk-api", "https://peer.example.test/services",
            automation ? [.. incidentScopes, callbackScope] : incidentScopes, metadata.ProducerInstanceId, mapping.Id.ToString("D"));
        var save = new PairingSaveRequest(Guid.NewGuid(), 1, mapping, credential);
        var neither = mapping with { CreateIncidents = false, RunAutomation = false, RatelDeskCustomerId = null };
        var invalid = await Assert.ThrowsAsync<PairingException>(() => service.ReceiveSaveAsync(pair, mapping.Id, save with { Mapping = neither }, default));
        Assert.Equal((422, "mapping-invalid"), (invalid.StatusCode, invalid.Code));
        var denied = await Assert.ThrowsAsync<PairingException>(() => service.ReceiveSaveAsync(pair, mapping.Id, save, default));
        Assert.Equal((403, "mapping-not-authorized"), (denied.StatusCode, denied.Code));
        Assert.Empty(await db.Set<PairingConnectionRecord>().AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToArrayAsync());

        rig.Access.SetTenantGrants(owner, tenant, automation
            ? [NetRatelPermissions.IntegrationManagement, NetRatelPermissions.SecretUse, NetRatelPermissions.JobManagement]
            : [NetRatelPermissions.IntegrationManagement, NetRatelPermissions.SecretUse]);
        var saved = await service.ReceiveSaveAsync(pair, mapping.Id, save, default);
        var profileId = mapping.Id.ToString("D");
        var profiles = scope.ServiceProvider.GetRequiredService<PairingBusinessProfileService>();
        var registry = scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>();
        var connectors = scope.ServiceProvider.GetRequiredService<IRatelDeskConnectorStore>();
        Assert.Equal(mapping.Id, (await profiles.ResolveAsync(tenant, profileId, incidentScope, default)).MappingId);
        Assert.True((await connectors.GetAsync(tenant, mapping.Id, default))?.Configuration.Enabled);
        if (automation)
        {
            Assert.NotNull(saved.Credential);
            Assert.NotNull(await registry.AuthenticateClientAsync(saved.Credential.ClientId, saved.Credential.ClientSecret));
            Assert.Equal(mapping.Id, (await profiles.ResolveAsync(tenant, profileId, callbackScope, default)).MappingId);
        }
        else
        {
            Assert.Null(saved.Credential);
            Assert.Empty(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToArrayAsync());
            Assert.Equal("connection-authority-removed", (await Assert.ThrowsAsync<PairingException>(() =>
                profiles.ResolveAsync(tenant, profileId, callbackScope, default))).Code);
        }

        rig.Access.SetTenantGrants(owner, tenant, NetRatelPermissions.IntegrationManagement);
        Assert.Equal("connection-authority-removed", (await Assert.ThrowsAsync<PairingException>(() =>
            profiles.ResolveAsync(tenant, profileId, incidentScope, default))).Code);
        if (saved.Credential is { } issued)
            Assert.Null(await registry.AuthenticateClientAsync(issued.ClientId, issued.ClientSecret));
        Assert.Equal(mapping, Assert.Single(await service.ListAsync(Human, default)).Mapping);
        var peerReads = rig.Peer.Reads;
        await service.DeleteAsync(pair.Id, mapping.Id, Human, default);
        Assert.Equal(peerReads, rig.Peer.Reads);
        Assert.Empty(await service.ListAsync(Human, default));
        Assert.False((await connectors.GetAsync(tenant, mapping.Id, default))?.Configuration.Enabled);
    }

    [Theory]
    [InlineData("mapping-owner")]
    [InlineData("pair-owner")]
    [InlineData("previous-tenant")]
    public async Task Rejected_interactive_edits_preserve_the_active_mapping_and_credential(string deniedAuthority)
    {
        const int tenant = 71;
        const int otherTenant = 72;
        const string mappingOwner = "synthetic-mapping-owner";
        var pairOwner = PairingBusinessAuthorityFixture.Administrator;
        string[] automationGrants = [NetRatelPermissions.IntegrationManagement, NetRatelPermissions.JobManagement];
        await using var rig = await Rig.CreateAsync(postgres);
        rig.Access.SetTenantGrants(pairOwner, tenant, automationGrants);
        rig.Access.SetTenantGrants(mappingOwner, tenant, automationGrants);
        await using var scope = rig.Scope();
        await PairingBusinessAuthorityFixture.SeedAdministratorAsync(scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>(), mappingOwner);
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Tenants.AddRange(new Tenant { Id = tenant, Name = "Original tenant" }, new Tenant { Id = otherTenant, Name = "Other tenant" });
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<PairingService>();
        var paired = await service.ExchangeAsync(rig.Peer.Request((await service.GenerateAsync(Human, default)).Code), default);
        var actor = PairingAuthority.Retained(mappingOwner);
        var mapping = new PairingMapping(Guid.NewGuid(), paired.PairId, "Existing mapping", "71",
            PairingBusinessAuthorityFixture.Organization, null, false, true);
        Assert.Equal("Connected", (await service.SaveAsync(paired.PairId, mapping.Id, mapping, actor, default)).Status);
        var before = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleAsync();

        var proposed = mapping with { CreateIncidents = true, RatelDeskCustomerId = "synthetic-customer" };
        if (deniedAuthority == "previous-tenant")
        {
            rig.Access.SetTenantGrants(mappingOwner, tenant);
            rig.Access.SetTenantGrants(mappingOwner, otherTenant, automationGrants);
            rig.Access.SetTenantGrants(pairOwner, otherTenant, automationGrants);
            proposed = mapping with { NetRatelTenantId = "72" };
        }
        else
        {
            var permittedOwner = deniedAuthority == "pair-owner" ? mappingOwner : pairOwner;
            rig.Access.SetTenantGrants(permittedOwner, tenant, [.. automationGrants, NetRatelPermissions.SecretUse]);
        }

        var denied = await Assert.ThrowsAsync<PairingException>(() => service.SaveAsync(paired.PairId, mapping.Id, proposed, actor, default));
        Assert.Equal((403, "mapping-not-authorized"), (denied.StatusCode, denied.Code));
        var after = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleAsync();
        Assert.True(after.Active);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.MappingJson, after.MappingJson);
        Assert.Equal(before.InboundPrincipalId, after.InboundPrincipalId);
        Assert.Equal("active", (await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync()).Status);
        Assert.Equal("active", (await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, rig.Peer.Saves);

        rig.Access.SetTenantGrants(mappingOwner, tenant, automationGrants);
        var profiles = scope.ServiceProvider.GetRequiredService<PairingBusinessProfileService>();
        Assert.Equal(mapping, (await profiles.ResolveAsync(tenant, mapping.Id.ToString("D"), "rateldesk.orchestration.callback", default)).Mapping);
        var authorizedEdit = deniedAuthority == "previous-tenant" ? proposed : mapping with { Name = "Authorized edit" };
        Assert.Equal("Connected", (await service.SaveAsync(paired.PairId, mapping.Id, authorizedEdit, actor, default)).Status);
        Assert.Equal("revoked", (await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync(p => p.Id == before.InboundPrincipalId)).Status);
    }
}
