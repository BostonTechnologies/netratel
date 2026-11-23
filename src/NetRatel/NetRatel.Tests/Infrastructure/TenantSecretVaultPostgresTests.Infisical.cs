using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Secrets;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed partial class TenantSecretVaultPostgresTests
{
    [Fact]
    [Trait("Category", "manual-integration")]
    public async Task Infisical_live_universal_auth_crud_preserves_pinned_versions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync(ct);
        var databasePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using var database = new ContainerBuilder("postgres:16-alpine").WithNetwork(network).WithNetworkAliases("database")
            .WithEnvironment("POSTGRES_DB", "infisical").WithEnvironment("POSTGRES_USER", "fixture").WithEnvironment("POSTGRES_PASSWORD", databasePassword)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "fixture", "-d", "infisical")).Build();
        await using var cache = new ContainerBuilder("redis:7-alpine").WithNetwork(network).WithNetworkAliases("cache")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING")).Build();
        await Task.WhenAll(database.StartAsync(ct), cache.StartAsync(ct));
        await using var infisical = new ContainerBuilder("infisical/infisical:v0.167.14").WithNetwork(network).WithPortBinding(8080, true)
            .WithEnvironment("DB_CONNECTION_URI", $"postgresql://fixture:{databasePassword}@database:5432/infisical")
            .WithEnvironment("REDIS_URL", "redis://cache:6379").WithEnvironment("SITE_URL", "http://localhost:8080")
            .WithEnvironment("ENCRYPTION_KEY", Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))
            .WithEnvironment("AUTH_SECRET", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
            .WithEnvironment("NODE_ENV", "production").WithEnvironment("TELEMETRY_ENABLED", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/api/status")))
            .Build();
        await infisical.StartAsync(ct);
        var origin = $"http://{infisical.Hostname}:{infisical.GetMappedPublicPort(8080)}/";
        using var http = new HttpClient { BaseAddress = new(origin) };
        var bootstrap = await PostAsync(http, "/api/v1/admin/bootstrap", new
        { email = "admin@example.test", password = Convert.ToHexString(RandomNumberGenerator.GetBytes(18)) + "a!9", organization = "NetRatel disposable fixture" }, ct);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            bootstrap.GetProperty("identity").GetProperty("credentials").GetProperty("token").GetString());
        var organizationId = bootstrap.GetProperty("organization").GetProperty("id").GetString();
        var project = await PostAsync(http, "/api/v2/workspace", new { projectName = "NetRatel disposable vault", slug = "netratel-vault-fixture" }, ct);
        var projectId = project.GetProperty("project").GetProperty("id").GetString();
        var identity = await PostAsync(http, "/api/v1/identities", new { name = "NetRatel fixture", organizationId }, ct);
        var identityId = identity.GetProperty("identity").GetProperty("id").GetString();
        await PostAsync(http, $"/api/v1/projects/{projectId}/memberships/identities/{identityId}", new { role = "member" }, ct);
        var universalAuth = await PostAsync(http, $"/api/v1/auth/universal-auth/identities/{identityId}",
            new { accessTokenTTL = 300, accessTokenMaxTTL = 300 }, ct);
        var client = await PostAsync(http, $"/api/v1/auth/universal-auth/identities/{identityId}/client-secrets", new { description = "Disposable fixture" }, ct);

        await using var services = await CreateServicesAsync(ct, origin);
        await using var scope = services.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<ITenantSecretService>();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var tenant = new Tenant { Name = "Canary" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        var connection = await vault.SaveConnectionAsync(tenant.Id, null, new()
        {
            Kind = SecretProviderKind.Infisical, DisplayName = "Actual Infisical fixture", ClientSecret = client.GetProperty("clientSecret").GetString(),
            Configuration = new(origin, ClientId: universalAuth.GetProperty("identityUniversalAuth").GetProperty("clientId").GetString(),
                ProjectId: projectId, Environment: "dev")
        }, ct);
        Assert.True((await vault.TestConnectionAsync(tenant.Id, connection.Id, connection.Revision, ct)).ListedMetadata);
        var created = await vault.WriteAsync(tenant.Id, connection.Id, new() { Locator = "NETRATEL_CANARY", Value = "synthetic first" }, ct);
        Assert.Equal(created.Locator, Assert.Single((await vault.BrowseAsync(tenant.Id, connection.Id, new(), ct)).Entries).Locator);
        var latest = new SecretReference(1, connection.Id, created.Locator, null, SecretVersionMode.LatestAtExecution, null);
        var pinned = latest with { VersionMode = SecretVersionMode.Pinned, Version = created.Version };
        var updated = await vault.WriteAsync(tenant.Id, connection.Id, new()
        { Locator = created.Locator, ExpectedRevision = created.Revision, ExpectedVersion = created.Version, Value = "synthetic rotation" }, ct);
        Assert.Equal("synthetic first", (await vault.RevealAsync(tenant.Id, pinned, ct)).Value);
        Assert.Equal("synthetic rotation", (await vault.RevealAsync(tenant.Id, latest, ct)).Value);
        await vault.DeleteAsync(tenant.Id, connection.Id, connection.Revision, new(updated.Locator, updated.Revision!, updated.Version!), ct);
        Assert.Empty((await vault.BrowseAsync(tenant.Id, connection.Id, new(), ct)).Entries);
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, body, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fixture setup returned HTTP {(int)response.StatusCode} for {path}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
