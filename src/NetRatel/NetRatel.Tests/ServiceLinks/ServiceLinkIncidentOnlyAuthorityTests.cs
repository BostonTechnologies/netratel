using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.ServiceLinks;

public sealed class ServiceLinkIncidentOnlyAuthorityTests
{
    [Fact]
    public async Task Exact_bound_control_registration_activates_without_agents_and_cannot_gain_business_scopes()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.Tenants.Add(new() { Id = 71, Name = "Incident-only tenant", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var constraints = new ServiceLinkResourceConstraints { TenantId = "71" };
        var request = new ServiceClientCreateRequest("Connection control", 71, "peer-installation", "approved-organization",
            [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope],
            System.Text.Json.JsonSerializer.Serialize(constraints, ServiceLinkCanonicalJson.Json), "approved-link", "approved-attempt",
            new string('a', 64), new string('b', 64), ServiceLinkContract.ResponderToInitiator, 1);
        var options = new ServiceIdentityOptions { Enabled = true };
        var registry = new ServicePrincipalRegistry(db, new Runtime(options), new Monitor(options), null!,
            new EmptyServiceClientDeploymentCatalog(), TimeProvider.System);
        var created = await registry.CreatePendingAsync(request, "authenticated-approver");
        await registry.ActivateAsync(created.Principal.Id);
        var secret = await db.Set<ServicePrincipalSecret>().SingleAsync();
        var permitted = registry.PermittedScopes(created.Principal, secret);
        Assert.Equal(2, permitted.Length);
        Assert.Equal(request.Scopes.Order(StringComparer.Ordinal), permitted.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(permitted, scope => scope.StartsWith("netratel.", StringComparison.Ordinal));
        Assert.Empty(await db.Agents.ToListAsync());
        Assert.Empty(await db.Jobs.ToListAsync());
        Assert.Throws<ArgumentException>(() => ServicePrincipalRegistry.ValidateRequest(request with
        { LinkId = null, AttemptId = null, GrantHash = null, DescriptorHash = null, DirectionId = null }));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.CreatePendingAsync(request with
        { Scopes = [ServiceIdentityScopes.OrchestrationRead] }, "authenticated-approver"));
        db.Tenants.Remove(await db.Tenants.SingleAsync());
        await db.SaveChangesAsync();
        Assert.False(await ServiceLinkGrantAuthority.ControlResourcesCurrentAsync(db, constraints, CancellationToken.None));
    }

    private sealed class Runtime(ServiceIdentityOptions options) : IServiceIdentityRuntimeOptions
    {
        public Task<ServiceIdentityOptions> GetAsync(CancellationToken ct = default) => Task.FromResult(options);
    }
    private sealed class Monitor(ServiceIdentityOptions options) : IOptionsMonitor<ServiceIdentityOptions>
    {
        public ServiceIdentityOptions CurrentValue => options;
        public ServiceIdentityOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<ServiceIdentityOptions, string?> listener) => null;
    }
}
