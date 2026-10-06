using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.ServiceLinks;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.M2M;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ServiceIdentityManagedPostgresHttpTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Actual_http_managed_tokens_observe_rotation_revocation_current_resources_and_durable_key_ring()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var directory = Path.Combine(Path.GetTempPath(), "netratel-service-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            await using var first = await StartAsync(connection, directory);
            CreatedServiceClient created; Guid agent;
            await using (var scope = first.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); await db.Database.MigrateAsync();
                agent = Guid.NewGuid(); db.Tenants.Add(new() { Id = 71, Name = "Synthetic tenant", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                db.Agents.Add(new() { Id = agent, TenantId = 71, Name = "Synthetic resource", Status = AgentStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow });
                db.Jobs.Add(new() { Id = 7001, TenantId = 71, AgentId = agent, Name = "Synthetic definition", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
                created = await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().CreateAsync(new("Synthetic peer", 71, Guid.NewGuid().ToString("D"), "peer-tenant",
                    [ServiceIdentityScopes.OrchestrationRead], Constraints(agent)), "synthetic-administrator");
                Assert.DoesNotContain(created.ClientSecret, (await db.Set<ServicePrincipalSecret>().SingleAsync()).SecretHash);
            }
            var client = first.GetTestClient(); var token = await Token(client, created.Principal.ClientId, created.ClientSecret);
            Assert.Equal("RS256", new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Alg);
            Assert.Equal(HttpStatusCode.OK, await Protected(client, token, "/managed"));
            Assert.Equal(HttpStatusCode.Forbidden, await Protected(client, token, "/legacy"));
            Assert.Equal(HttpStatusCode.BadRequest, (await TokenResponse(client, created.Principal.ClientId, created.ClientSecret, "netratel.api")).StatusCode);
            var jwks = await client.GetStringAsync("/.well-known/service-jwks.json"); Assert.DoesNotContain("\"d\":", jwks);
            await using var replica = await StartAsync(connection, directory);
            Assert.Equal(jwks, await replica.GetTestClient().GetStringAsync("/.well-known/service-jwks.json"));
            CreatedServiceClient successor;
            await using (var scope = replica.Services.CreateAsyncScope()) successor = await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().RotateAsync(created.Principal.Id, 1);
            Assert.Equal(HttpStatusCode.OK, (await TokenResponse(client, created.Principal.ClientId, created.ClientSecret)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await TokenResponse(client, created.Principal.ClientId, successor.ClientSecret)).StatusCode);
            await using (var scope = replica.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); (await db.Agents.SingleAsync(x => x.Id == agent)).IsEnabled = false; await db.SaveChangesAsync();
            }
            Assert.Equal(HttpStatusCode.Unauthorized, await Protected(client, token, "/managed"));
            Assert.Equal(HttpStatusCode.BadRequest, (await TokenResponse(client, created.Principal.ClientId, successor.ClientSecret)).StatusCode);
            await using (var scope = replica.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().RevokeAsync(created.Principal.Id);
            Assert.Equal(HttpStatusCode.Unauthorized, (await TokenResponse(client, created.Principal.ClientId, created.ClientSecret)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await TokenResponse(client, created.Principal.ClientId, successor.ClientSecret)).StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Separate_contexts_enforce_alias_and_single_pending_successor_constraints()
    {
        var connection = await postgres.CreateDatabaseAsync(); var directory = Path.Combine(Path.GetTempPath(), "netratel-service-constraint-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            await using var app = await StartAsync(connection, directory); Guid principalId; Guid agent = Guid.NewGuid();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); await db.Database.MigrateAsync();
                db.Tenants.Add(new() { Id = 72, Name = "Synthetic tenant", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                db.Agents.Add(new() { Id = agent, TenantId = 72, CreatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
                var registry = scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>();
                principalId = (await registry.CreateAsync(new("Synthetic", 72, "synthetic-peer", "tenant", [ServiceIdentityScopes.OrchestrationRead], Constraints(agent, 72, false), ClientId: "synthetic-client"), "administrator")).Principal.Id;
                await registry.CreateSuccessorAsync(principalId, 1);
            }
            await using var other = app.Services.CreateAsyncScope(); var otherRegistry = other.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>();
            await Assert.ThrowsAsync<ServiceClientConflictException>(() => otherRegistry.CreateSuccessorAsync(principalId, 1));
            await Assert.ThrowsAsync<ServiceClientConflictException>(() => otherRegistry.CreateAsync(new("Synthetic alias", 72, "synthetic-peer", "tenant", [ServiceIdentityScopes.OrchestrationRead], Constraints(agent, 72, false), ClientId: "synthetic_client"), "administrator"));
            var otherDb = other.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); otherDb.Set<ServicePrincipalSecret>().Add(new() { ServicePrincipalId = principalId, CredentialRevision = 3, Status = "pending", Salt = new string('a', 64), SecretHash = new string('b', 64), CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) });
            await Assert.ThrowsAsync<DbUpdateException>(() => otherDb.SaveChangesAsync());
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task Empty_deployment_settings_can_be_enabled_and_manually_provisioned_over_http_without_restart()
    {
        var connection = await postgres.CreateDatabaseAsync(); var directory = Path.Combine(Path.GetTempPath(), "netratel-service-public-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            await using var app = await StartAsync(connection, directory, fresh: true); var resource = Guid.NewGuid();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); await db.Database.MigrateAsync();
                db.Tenants.Add(new() { Id = 73, Name = "Synthetic tenant", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                db.Agents.Add(new() { Id = resource, TenantId = 73, CreatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
            }
            var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("X-Synthetic-Administrator", "yes");
            var initial = await client.GetFromJsonAsync<ServicePublicSettingsResponse>("/api/v2/account/service-clients/settings");
            Assert.NotNull(initial); Assert.False(initial.Enabled); Assert.Equal("", initial.ApiBaseUrl); Assert.Equal(0, initial.Revision);
            Assert.False(initial.ReciprocalLinkEnabled);
            var initialAuthority = await client.GetFromJsonAsync<ServiceClientManagementAuthority>("/api/v2/account/service-clients/authority");
            Assert.NotNull(initialAuthority); Assert.False(initialAuthority.ReciprocalLinkEnabled);
            var saved = await client.PutAsJsonAsync("/api/v2/account/service-clients/settings", new ServicePublicSettingsUpdate(true, initial.WebBaseUrl, "https://api.example.test", "", "netratel.services", 0));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var enabled = await saved.Content.ReadFromJsonAsync<ServicePublicSettingsResponse>(); Assert.Equal(1, enabled!.Revision); Assert.True(enabled.Enabled); Assert.Equal("https://api.example.test/services", enabled.Issuer);
            Assert.True(enabled.ReciprocalLinkEnabled);
            var enabledAuthority = await client.GetFromJsonAsync<ServiceClientManagementAuthority>("/api/v2/account/service-clients/authority");
            Assert.NotNull(enabledAuthority); Assert.True(enabledAuthority.ReciprocalLinkEnabled);
            var request = new ServiceClientCreateRequest("Synthetic peer", 73, "synthetic-peer", "peer-tenant", [ServiceIdentityScopes.OrchestrationRead], Constraints(resource, 73, false));
            var response = await client.PostAsJsonAsync("/api/v2/account/service-clients/", request); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var reveal = await response.Content.ReadFromJsonAsync<ServiceClientReveal>(); Assert.NotNull(reveal);
            var token = await Token(client, reveal.Client.ClientId, reveal.ClientSecret); Assert.Equal(HttpStatusCode.OK, await Protected(client, token, "/managed"));
            var stale = await client.PutAsJsonAsync("/api/v2/account/service-clients/settings", new ServicePublicSettingsUpdate(false, initial.WebBaseUrl, "https://api.example.test", "", "netratel.services", 0));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var disabledResponse = await client.PutAsJsonAsync("/api/v2/account/service-clients/settings", new ServicePublicSettingsUpdate(false, enabled.WebBaseUrl, enabled.ApiBaseUrl, enabled.Issuer, enabled.Audience, enabled.Revision));
            Assert.Equal(HttpStatusCode.OK, disabledResponse.StatusCode);
            var disabled = await disabledResponse.Content.ReadFromJsonAsync<ServicePublicSettingsResponse>();
            Assert.NotNull(disabled); Assert.False(disabled.ReciprocalLinkEnabled);
            var disabledAuthority = await client.GetFromJsonAsync<ServiceClientManagementAuthority>("/api/v2/account/service-clients/authority");
            Assert.NotNull(disabledAuthority); Assert.False(disabledAuthority.ReciprocalLinkEnabled);
            Assert.Equal(HttpStatusCode.Unauthorized, await Protected(client, token, "/managed"));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static string Constraints(Guid agent, int tenant = 71, bool definition = true) => JsonSerializer.Serialize(new
    { organization_id = (string?)null, customer_ids = Array.Empty<string>(), request_ids = Array.Empty<string>(), task_ids = Array.Empty<string>(), tenant_id = tenant.ToString(System.Globalization.CultureInfo.InvariantCulture), resource_ids = new[] { agent.ToString("D") }, request_definition_ids = definition ? new[] { "7001" } : [] });
    private static async Task<WebApplication> StartAsync(string connection, string directory, bool fresh = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        if (!fresh) builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ServiceIdentity:Enabled"] = "true", ["ServiceIdentity:Issuer"] = "https://api.example.test/services", ["ServiceIdentity:ApiBaseUrl"] = "https://api.example.test", ["ServiceIdentity:WebBaseUrl"] = "https://web.example.test", ["ServiceIdentity:InstanceId"] = "c0c8e681-b1d0-4e44-92c2-50dce9d0c2ce", ["ServiceIdentity:Audience"] = "netratel.services" });
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(directory)).SetApplicationName("NetRatel.ServiceIdentity.Tests");
        builder.Services.AddNetRatelServiceIdentityApi(builder.Configuration);
        builder.Services.AddOptions<ServiceLinkOptions>(); builder.Services.AddOptions<ClientInstallationEndpointOptions>();
        builder.Services.AddScoped<ServiceLinkIdentityStore>();
        builder.Services.AddSingleton<IDeploymentBrandingService, SyntheticBranding>();
        builder.Services.AddSingleton<IEffectiveAccessService, SyntheticAccess>();
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SyntheticHuman>("SyntheticHuman", _ => { });
        builder.Services.Configure<AgentAuthOptions>(_ => { }); builder.Services.AddScoped<OidcSigningService>();
        builder.Services.AddAuthentication(o => o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = ServiceIdentityAuthenticationHandler.SchemeName);
        builder.Services.AddAuthorization(o => { o.AddPolicy("InteractiveAccount", p => p.AddAuthenticationSchemes("SyntheticHuman").RequireAuthenticatedUser()); o.AddPolicy("NarrowRead", p => p.AddAuthenticationSchemes(ServiceIdentityAuthenticationHandler.SchemeName).RequireAuthenticatedUser().RequireClaim("scope", ServiceIdentityScopes.OrchestrationRead)); o.AddPolicy("LegacyBroad", p => p.RequireAuthenticatedUser().AddRequirements(new AllowedClientRequirement(["synthetic-client"]))); });
        builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AllowedClientHandler>();
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapM2MTokenEndpoints(); app.MapServiceIdentityMetadataEndpoints(); app.MapServiceClientEndpoints();
        app.MapGet("/managed", () => Results.Ok()).RequireAuthorization("NarrowRead"); app.MapGet("/legacy", () => Results.Ok()).RequireAuthorization("LegacyBroad"); await app.StartAsync(); return app;
    }
    private sealed class SyntheticHuman(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers["X-Synthetic-Administrator"] == "yes"
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "synthetic-administrator")], Scheme.Name)), Scheme.Name)) : AuthenticateResult.NoResult());
    }
    private sealed class SyntheticAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal p, string permission, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(p.HasClaim("netratel_principal_id", "synthetic-administrator"));
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal p, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(new EffectiveAccessSnapshot("synthetic-administrator", false, true, new HashSet<string>()));
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal p, string permission, CancellationToken cancellationToken = default) => Task.FromResult<int[]?>(null);
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class SyntheticBranding : IDeploymentBrandingService
    {
        public Task<EffectiveDeploymentBranding> GetEffectiveAsync(CancellationToken ct = default)
        {
            var empty = new BrandingField("", BrandingValueSource.Default, false);
            return Task.FromResult(new EffectiveDeploymentBranding(empty, empty, empty, empty, empty, empty, empty, empty,
                new BrandingField("https://web.example.test", BrandingValueSource.Administrator, false), 0, empty));
        }
        public Task<EffectiveDeploymentBranding> UpdateAsync(UpdateDeploymentBrandingRequest request, string? actorPrincipalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BrandingAssetUploadResult> UploadAssetAsync(BrandingAssetUpload upload, string? actorPrincipalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DeploymentBrandingAsset?> FindAssetAsync(string id, CancellationToken ct = default) => Task.FromResult<DeploymentBrandingAsset?>(null);
    }
    private static Task<HttpResponseMessage> TokenResponse(HttpClient client, string id, string secret, string scope = ServiceIdentityScopes.OrchestrationRead) => client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = id, ["client_secret"] = secret, ["scope"] = scope }));
    private static async Task<string> Token(HttpClient client, string id, string secret) { var response = await TokenResponse(client, id, secret); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!; }
    private static async Task<HttpStatusCode> Protected(HttpClient client, string token, string path) { using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return (await client.SendAsync(request)).StatusCode; }
}
