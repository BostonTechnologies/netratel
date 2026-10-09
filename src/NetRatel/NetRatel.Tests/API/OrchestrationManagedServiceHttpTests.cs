using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Endpoints;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Endpoints.Systems;
using NetRatel.API.Security.M2M;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Infrastructure.Requests;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.SystemPairing;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class OrchestrationManagedServiceHttpTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Actual_tokens_filter_catalog_bound_invocation_retry_and_live_revocation_without_legacy_authority()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var directory = Path.Combine(Path.GetTempPath(), "netratel-managed-orchestration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var legacyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            await using var app = await StartAsync(connection, directory, legacyKey);
            CreatedServiceClient read; CreatedServiceClient invoke;
            var allowedAgent = Guid.NewGuid(); var otherAgent = Guid.NewGuid();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); await db.Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>().Database.MigrateAsync();
                var now = DateTimeOffset.UtcNow;
                db.Tenants.AddRange(new Tenant { Id = 71, Name = "Permitted tenant", CreatedAtUtc = now, UpdatedAtUtc = now }, new Tenant { Id = 72, Name = "Other tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Agents.AddRange(new Agent { Id = allowedAgent, TenantId = 71, CreatedAtUtc = now }, new Agent { Id = otherAgent, TenantId = 72, CreatedAtUtc = now });
                db.Jobs.AddRange(new JobDefinition { Id = 7001, TenantId = 71, AgentId = allowedAgent, ClientIdentity = allowedAgent.ToString("D"), Name = "Permitted job", FolderPath = "/", CreatedAtUtc = now, UpdatedAtUtc = now },
                    new JobDefinition { Id = 7002, TenantId = 72, AgentId = otherAgent, ClientIdentity = otherAgent.ToString("D"), Name = "Other job", FolderPath = "/", CreatedAtUtc = now, UpdatedAtUtc = now });
                await db.SaveChangesAsync();
                await PairingBusinessAuthorityFixture.SeedAdministratorAsync(scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>());
                var registry = scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>();
                read = await PairingBusinessAuthorityFixture.CreateAsync(db, registry, 71, [ServiceIdentityScopes.OrchestrationRead], protection: scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
                invoke = await PairingBusinessAuthorityFixture.CreateAsync(db, registry, 71, [ServiceIdentityScopes.OrchestrationInvoke], protection: scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
            }
            var client = app.GetTestClient();
            var readToken = await TokenAsync(client, read, ServiceIdentityScopes.OrchestrationRead);
            var invokeToken = await TokenAsync(client, invoke, ServiceIdentityScopes.OrchestrationInvoke);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readToken);
            foreach (var path in new[] { "/internal/catalog/jobs", "/internal/catalog/request-definitions" })
            {
                var catalog = await client.GetFromJsonAsync<JsonElement>(path);
                Assert.Single(catalog.EnumerateArray()); Assert.Equal(71, catalog[0].GetProperty("tenantId").GetInt32());
            }
            var tenants = await client.GetFromJsonAsync<JsonElement>("/internal/catalog/tenants");
            Assert.Single(tenants.EnumerateArray()); Assert.Equal(71, tenants[0].GetProperty("tenantId").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/internal/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/system/m2m/ping")).StatusCode);
            Assert.Equal(0, app.Services.GetRequiredService<LegacyHandlerCounter>().Authentications);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/internal/ingest", Ingest("7001"))).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await client.PostAsJsonAsync("/internal/catalog/request-definitions", new { name = "Injected", tenantId = 71, clientIdentity = allowedAgent.ToString("D") })).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/broad-legacy")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invokeToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/internal/catalog/jobs")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/internal/ingest", Ingest("7002"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/internal/ingest", Ingest("7001", callback: "https://attacker.example/callback"))).StatusCode);
            var accepted = await client.PostAsJsonAsync("/internal/ingest", Ingest("7001")); accepted.EnsureSuccessStatusCode();
            var acknowledgement = await accepted.Content.ReadFromJsonAsync<NetRatelIngestResponse>(); Assert.NotNull(acknowledgement);
            var duplicate = await client.PostAsJsonAsync("/internal/ingest", Ingest("7001")); duplicate.EnsureSuccessStatusCode();
            Assert.Equal(acknowledgement.ExecutionId, (await duplicate.Content.ReadFromJsonAsync<NetRatelIngestResponse>())!.ExecutionId);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/ingest", Ingest("7001", payload: "{\"value\":2}"))).StatusCode);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                Assert.Single(await db.Set<ManagedOrchestrationRequestBinding>().ToArrayAsync()); Assert.Single(await db.Requests.ToArrayAsync());
                Assert.Equal(1, app.Services.GetRequiredService<InvocationCounter>().Starts);
                // Reproduce a crash after durable run registration and before the request ACK projection.
                (await db.Requests.SingleAsync()).ExecutionId = null; await db.SaveChangesAsync();
            }
            var recovered = await client.PostAsJsonAsync("/internal/ingest", Ingest("7001")); recovered.EnsureSuccessStatusCode();
            Assert.Equal(acknowledgement.ExecutionId, (await recovered.Content.ReadFromJsonAsync<NetRatelIngestResponse>())!.ExecutionId);
            Assert.Equal(1, app.Services.GetRequiredService<InvocationCounter>().Starts);
            await using (var scope = app.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().RevokeAsync(invoke.Principal.Id);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/internal/ingest", Ingest("7001"))).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readToken);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/internal/catalog/jobs")).StatusCode);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); (await db.Agents.SingleAsync(x => x.Id == allowedAgent)).IsEnabled = false; await db.SaveChangesAsync();
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readToken);
            Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/internal/catalog/jobs")).EnumerateArray());
            // Independent deployment-client authentication retains its own policy, without the retired catalog fallback.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LegacyToken(legacyKey));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/broad-legacy")).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/internal/catalog/jobs")).StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Empty_saved_tenant_catalog_gains_current_jobs_and_removal_denies_only_that_resource()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var directory = Path.Combine(Path.GetTempPath(), "netratel-current-orchestration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var legacyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            await using var app = await StartAsync(connection, directory, legacyKey);
            CreatedServiceClient credential;
            var agent = Guid.NewGuid(); var foreignAgent = Guid.NewGuid();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                await db.Database.MigrateAsync();
                var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                await identity.Database.MigrateAsync(); await PairingBusinessAuthorityFixture.SeedAdministratorAsync(identity);
                var now = DateTimeOffset.UtcNow;
                db.Tenants.AddRange(new Tenant { Id = 71, Name = "Empty authorized tenant", CreatedAtUtc = now, UpdatedAtUtc = now },
                    new Tenant { Id = 72, Name = "Foreign tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Agents.Add(new() { Id = foreignAgent, TenantId = 72, CreatedAtUtc = now });
                db.Jobs.Add(new() { Id = 7002, TenantId = 72, AgentId = foreignAgent, ClientIdentity = foreignAgent.ToString("D"), Name = "Foreign job", FolderPath = "/", CreatedAtUtc = now, UpdatedAtUtc = now });
                await db.SaveChangesAsync();
                credential = await PairingBusinessAuthorityFixture.CreateAsync(db, scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>(),
                    71, ServiceIdentityScopes.Business, protection: scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
                var stored = ServicePrincipalRegistry.ReadConstraints(credential.Principal);
                Assert.Equal("71", stored.TenantId); Assert.Empty(stored.ResourceIds); Assert.Empty(stored.RequestDefinitionIds);
            }
            var client = app.GetTestClient();
            var token = await TokenAsync(client, credential, string.Join(' ', ServiceIdentityScopes.Business));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/internal/catalog/jobs")).EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/internal/ingest", Ingest("7002"))).StatusCode);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); var now = DateTimeOffset.UtcNow;
                db.Agents.Add(new() { Id = agent, TenantId = 71, CreatedAtUtc = now });
                db.Jobs.Add(new() { Id = 7001, TenantId = 71, AgentId = agent, ClientIdentity = agent.ToString("D"), Name = "Permitted job", FolderPath = "/", CreatedAtUtc = now, UpdatedAtUtc = now });
                await db.SaveChangesAsync();
            }
            var catalog = await client.GetFromJsonAsync<JsonElement>("/internal/catalog/jobs");
            Assert.Single(catalog.EnumerateArray()); Assert.Equal(71, catalog[0].GetProperty("tenantId").GetInt32());
            var accepted = await client.PostAsJsonAsync("/internal/ingest", Ingest("7001")); accepted.EnsureSuccessStatusCode();
            Assert.Equal(1, app.Services.GetRequiredService<InvocationCounter>().Starts);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                (await db.Agents.SingleAsync(x => x.Id == agent)).SupersededAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/internal/catalog/jobs")).EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/internal/ingest", Ingest("7001"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/internal/health")).StatusCode);
            Assert.NotEmpty(await TokenAsync(client, credential, string.Join(' ', ServiceIdentityScopes.Business)));
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var original = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync();
                Assert.Empty(ServicePrincipalRegistry.ReadConstraints(original).ResourceIds);
                Assert.Empty(ServicePrincipalRegistry.ReadConstraints(original).RequestDefinitionIds);
            }
            Assert.Equal(1, app.Services.GetRequiredService<InvocationCounter>().Starts);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static NetRatelIngestRequest Ingest(string definition, string payload = "{\"value\":1}", string? callback = null) => new()
    { RequestId = "parent-request", RequestTaskId = "parent-task", CorrelationId = "corr-managed", NetRatelRequestDefinitionId = definition, NetRatelJobDefinitionId = definition, JobName = "Permitted job", PayloadJson = payload, CallbackUrl = callback };

    private static async Task<WebApplication> StartAsync(string connection, string directory, ECDsa legacyKey)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production }); builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ServiceIdentity:Enabled"] = "true", ["ServiceIdentity:Issuer"] = "https://api.example.test/services", ["ServiceIdentity:ApiBaseUrl"] = "https://api.example.test", ["ServiceIdentity:WebBaseUrl"] = "https://web.example.test", ["ServiceIdentity:InstanceId"] = "c0c8e681-b1d0-4e44-92c2-50dce9d0c2ce", ["ServiceIdentity:Audience"] = "netratel.services" });
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection));
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseNpgsql(connection));
        builder.Services.AddClientInstallationOptions(builder.Configuration);
        builder.Services.AddScoped<PairingAuthority>();
        builder.Services.AddSingleton<NetRatel.Infrastructure.Flows.FlowPersistenceService>();
        builder.Services.AddSingleton<NetRatel.Application.RatelDesk.IFlowSourceIdentityResolver>(p => p.GetRequiredService<NetRatel.Infrastructure.Flows.FlowPersistenceService>());
        builder.Services.AddScoped<InstallationIdentityStore>();
        builder.Services.AddScoped<PairingBusinessProfileService>();
        builder.Services.AddSingleton(new PairingTransport(new HttpClient()));
        builder.Services.AddScoped<IDeploymentBrandingService, DeploymentBrandingService>();
        builder.Services.AddSingleton<IEffectiveAccessService, PairingBusinessAuthorityFixture.Access>();

        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(directory)).SetApplicationName("NetRatel.Orchestration.Tests");
        builder.Services.AddNetRatelServiceIdentityApi(builder.Configuration);
        builder.Services.AddScoped<IServicePublicSettingsResolver, Settings>();
        builder.Services.Configure<NetRatel.Application.Agents.AgentAuthOptions>(_ => { }); builder.Services.AddScoped<OidcSigningService>();
        builder.Services.AddScoped<NetRatel.Application.Agents.IAgentTokenService, AgentTokenService>(); builder.Services.AddScoped<AgentNonceReplayService>();
        builder.Services.AddSingleton<LegacyHandlerCounter>(); builder.Services.AddSingleton<InvocationCounter>();
        builder.Services.AddAuthentication(options => options.DefaultForbidScheme = ServiceIdentityAuthenticationHandler.SchemeName).AddJwtBearer("M2M", options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new() { ValidIssuer = "legacy-issuer", ValidAudience = "legacy-audience", IssuerSigningKey = new ECDsaSecurityKey(legacyKey), ValidateLifetime = true };
            options.Events = new JwtBearerEvents { OnMessageReceived = context => { context.HttpContext.RequestServices.GetRequiredService<LegacyHandlerCounter>().Authentications++; return Task.CompletedTask; } };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.AddAuthenticationSchemes("M2M").RequireAuthenticatedUser().AddRequirements(new AllowedClientRequirement(["legacy-client"]))));
        builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AllowedClientHandler>();
        builder.Services.AddScoped<IJobDefinitionService, JobDefinitionService>(); builder.Services.AddScoped<IJobRunService, JobRunService>();
        builder.Services.AddScoped<IRequestService, RequestService>(); builder.Services.AddSingleton<IRequestEventBus, RequestEventBus>();
        builder.Services.AddSingleton<JobAuthorityIdGenerator>();
        builder.Services.AddScoped<IAkkaJobAuthorityService, RecordingAuthority>(); builder.Services.AddOrchestrationManagedServices();
        foreach (var worker in builder.Services.Where(item => item.ServiceType == typeof(OrchestrationCallbackReconciler) || item.ServiceType == typeof(IHostedService) && (item.ImplementationType == typeof(OrchestrationCallbackWorker) || item.ImplementationType == typeof(ManagedJobRunRecoveryWorker))).ToArray()) builder.Services.Remove(worker);
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapInternalEndpoints(); app.MapSystemEndpoints(); app.MapM2MTokenEndpoints(); app.MapServiceIdentityMetadataEndpoints();
        app.MapGet("/broad-legacy", () => Results.Ok()).RequireAuthorization("M2MOnly"); await app.StartAsync(); return app;
    }

    private sealed class Settings(IOptionsMonitor<ServiceIdentityOptions> options) : IServicePublicSettingsResolver
    { public Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) => Task.FromResult(new ServicePublicSettingsEffective(options.CurrentValue, 1, [])); }

    private sealed class LegacyHandlerCounter { public int Authentications; }
    private sealed class InvocationCounter { public int Starts; }
    private sealed class RecordingAuthority(IJobDefinitionService definitions, IJobRunService runs, ManagedOrchestrationInvocationGuard guard, InvocationCounter counter, OrchestratorDbContext db) : IAkkaJobAuthorityService
    {
        public Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken ct) => throw new InvalidOperationException("Legacy dispatch is outside this boundary fixture.");
        public async Task<JobRunInfo> StartManagedAsync(ulong jobId, RunJobRequest request, int recordedRequestId, ClaimsPrincipal principal, CancellationToken ct)
        {
            var job = (await definitions.GetAsync(jobId, ct))!;
            var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleAsync(x => x.RequestId == recordedRequestId, ct);
            var runId = ulong.Parse(binding.ExecutionId!, CultureInfo.InvariantCulture);
            var pending = (await runs.GetAsync(runId, ct))!;
            Assert.Equal(JobRunState.Pending, pending.Status);
            Assert.Null(db.Database.CurrentTransaction);
            await using var independent = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseNpgsql(db.Database.GetConnectionString()).Options);
            Assert.Equal((int)JobRunState.Pending, (await independent.JobRuns.AsNoTracking().SingleAsync(x => x.Id == checked((long)runId), ct)).Status);
            Assert.Equal(binding.ExecutionId, (await independent.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleAsync(x => x.RequestId == recordedRequestId, ct)).ExecutionId);
            Assert.NotNull(await db.Set<JobRunControlRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.RunId == checked((long)runId), ct));
            await guard.AuthorizeStartAsync(recordedRequestId, job, principal, ct, runId);
            var run = await runs.UpsertRunAsync(new UpsertJobRunCommand(runId, job.Id, job.TenantId, job.ClientIdentity, request.StartedBy!, JobRunState.Running, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, request.InputsJson, null, job.AgentId), ct);
            await guard.BindRunAsync(recordedRequestId, run, ct); await guard.AuthorizeDispatchAsync(run, job, ct); counter.Starts++; return run;
        }
        public Task RecordLifecycleAsync(NetRatel.Application.Presence.ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CancelAsync(ulong id, string reason, CancellationToken ct) => Task.FromResult(false);
    }
    private static async Task<string> TokenAsync(HttpClient client, CreatedServiceClient credential, string scope)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "client_credentials", ["client_id"] = credential.Principal.ClientId, ["client_secret"] = credential.ClientSecret, ["scope"] = scope }));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }
    private static string LegacyToken(ECDsa key) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("legacy-issuer", "legacy-audience",
        [new Claim("client_id", "legacy-client")], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256)));
}
