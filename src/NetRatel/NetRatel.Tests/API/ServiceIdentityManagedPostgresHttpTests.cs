using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.M2M;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.Services;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ServiceIdentityManagedPostgresHttpTests(PostgreSqlPersistenceFixture postgres)
{
    [Theory]
    [InlineData("mapping")]
    [InlineData("administrator")]
    public async Task Actual_tokens_and_replica_key_ring_observe_live_saved_connection_authority(string revoke)
    {
        var connection = await postgres.CreateDatabaseAsync();
        var directory = Path.Combine(Path.GetTempPath(), "netratel-paired-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var first = await StartAsync(connection, directory);
            CreatedServiceClient created; var agent = Guid.NewGuid();
            await using (var scope = first.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                await db.Database.MigrateAsync(); await identity.Database.MigrateAsync();
                await PairingBusinessAuthorityFixture.SeedAdministratorAsync(identity);
                db.Tenants.Add(new() { Id = 71, Name = "Permitted tenant", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                db.Agents.Add(new() { Id = agent, TenantId = 71, Status = AgentStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow });
                db.Jobs.Add(new() { Id = 7001, TenantId = 71, AgentId = agent, Name = "Permitted definition", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                created = await PairingBusinessAuthorityFixture.CreateAsync(db, scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>(), 71, [ServiceIdentityScopes.OrchestrationRead]);
                Assert.DoesNotContain(created.ClientSecret, (await db.Set<ServicePrincipalSecret>().SingleAsync()).SecretHash);
            }
            var client = first.GetTestClient(); var token = await TokenAsync(client, created);
            Assert.Equal("RS256", new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Alg);
            Assert.Equal(HttpStatusCode.OK, await ProtectedAsync(client, token));
            Assert.Equal(HttpStatusCode.BadRequest, (await TokenResponseAsync(client, created, "netratel.api")).StatusCode);
            var jwks = await client.GetStringAsync("/.well-known/service-jwks.json"); Assert.DoesNotContain("\"d\":", jwks);
            await using var replica = await StartAsync(connection, directory);
            Assert.Equal(jwks, await replica.GetTestClient().GetStringAsync("/.well-known/service-jwks.json"));
            Assert.Equal(HttpStatusCode.OK, await ProtectedAsync(replica.GetTestClient(), token));
            await using (var scope = replica.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                if (revoke == "mapping") (await db.Set<PairingConnectionRecord>().SingleAsync()).Active = false;
                else
                {
                    var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                    identity.ApplicationPrincipals.Remove(await identity.ApplicationPrincipals.SingleAsync()); await identity.SaveChangesAsync();
                }
                await db.SaveChangesAsync();
            }
            Assert.Equal(HttpStatusCode.Unauthorized, await ProtectedAsync(client, token));
            Assert.Equal(HttpStatusCode.Unauthorized, (await TokenResponseAsync(client, created)).StatusCode);
            await using (var scope = first.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().RevokeAsync(created.Principal.Id);
            Assert.Equal(HttpStatusCode.Unauthorized, (await TokenResponseAsync(client, created)).StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Paired_but_unsaved_authority_cannot_issue_business_tokens()
    {
        var connection = await postgres.CreateDatabaseAsync(); var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            await using var app = await StartAsync(connection, directory);
            CreatedServiceClient created;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                await db.Database.MigrateAsync(); await identity.Database.MigrateAsync(); await PairingBusinessAuthorityFixture.SeedAdministratorAsync(identity);
                var agent = Guid.NewGuid();
                db.Tenants.Add(new() { Id = 71, Name = "Tenant", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                db.Agents.Add(new() { Id = agent, TenantId = 71, Status = AgentStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow });
                db.Jobs.Add(new() { Id = 7001, TenantId = 71, AgentId = agent, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
                created = await PairingBusinessAuthorityFixture.CreateAsync(db, scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>(), 71, [ServiceIdentityScopes.OrchestrationRead], active: false);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await TokenResponseAsync(app.GetTestClient(), created)).StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<WebApplication> StartAsync(string connection, string directory)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ServiceIdentity:Enabled"] = "true", ["ServiceIdentity:Issuer"] = "https://api.example.test/services", ["ServiceIdentity:ApiBaseUrl"] = "https://api.example.test", ["ServiceIdentity:WebBaseUrl"] = "https://web.example.test", ["ServiceIdentity:Audience"] = "netratel.services" });
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(directory)).SetApplicationName("NetRatel.ServiceIdentity.Tests");
        builder.Services.AddNetRatelServiceIdentityApi(builder.Configuration);
        builder.Services.AddScoped<IServicePublicSettingsResolver, Settings>();
        builder.Services.AddSingleton<IEffectiveAccessService, PairingBusinessAuthorityFixture.Access>(); builder.Services.AddScoped<PairingAuthority>();
        builder.Services.Configure<AgentAuthOptions>(_ => { }); builder.Services.AddScoped<OidcSigningService>();
        builder.Services.AddAuthentication(o => o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = ServiceIdentityAuthenticationHandler.SchemeName);
        builder.Services.AddAuthorization(o => o.AddPolicy("NarrowRead", p => p.AddAuthenticationSchemes(ServiceIdentityAuthenticationHandler.SchemeName).RequireAuthenticatedUser().RequireClaim("scope", ServiceIdentityScopes.OrchestrationRead)));
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapM2MTokenEndpoints(); app.MapServiceIdentityMetadataEndpoints();
        app.MapGet("/managed", () => Results.Ok()).RequireAuthorization("NarrowRead"); await app.StartAsync(); return app;
    }
    private sealed class Settings(IOptionsMonitor<ServiceIdentityOptions> options) : IServicePublicSettingsResolver
    { public Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) => Task.FromResult(new ServicePublicSettingsEffective(options.CurrentValue, 1, [])); }
    private static Task<HttpResponseMessage> TokenResponseAsync(HttpClient client, CreatedServiceClient created, string? audience = null)
    {
        var body = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = created.Principal.ClientId, ["client_secret"] = created.ClientSecret, ["scope"] = ServiceIdentityScopes.OrchestrationRead };
        if (audience is not null) body["audience"] = audience;
        return client.PostAsync("/connect/token", new FormUrlEncodedContent(body));
    }
    private static async Task<string> TokenAsync(HttpClient client, CreatedServiceClient created)
    { using var response = await TokenResponseAsync(client, created); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!; }
    private static async Task<HttpStatusCode> ProtectedAsync(HttpClient client, string token)
    { using var request = new HttpRequestMessage(HttpMethod.Get, "/managed"); request.Headers.Authorization = new("Bearer", token); using var response = await client.SendAsync(request); return response.StatusCode; }
}
