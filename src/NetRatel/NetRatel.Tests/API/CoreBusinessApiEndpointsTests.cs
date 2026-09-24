using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Endpoints.Systems;
using NetRatel.API.Models.TenantModels;
using NetRatel.Application.Agents;
using NetRatel.Application.Events;
using NetRatel.Application.Scripts;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Shared.Contracts.Scripts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class CoreBusinessApiEndpointsTests
{
    [Fact]
    public async Task TenantEndpoints_UsePostgresServiceForCrudWithoutSpacetime()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var created = await client.PostAsJsonAsync("/api/v1/tenants", new CreateTenantRequest
        {
            Name = "Acme",
            Description = "Managed tenant",
            Location = "Cape Town",
            Domains = ["acme.example"],
            ContactPerson = "Ada",
            ContactEmail = "ada@acme.example",
            AutoUpdate = true,
            AutoUpdateChannel = "prerelease",
            AutoUpdateTargetVersion = "0.4.131-rc.1"
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = await created.Content.ReadFromJsonAsync<TenantResponse>();
        tenant.Should().NotBeNull();
        tenant!.Name.Should().Be("Acme");
        tenant.AutoUpdateChannel.Should().Be("prerelease");
        tenant.AutoUpdateTargetVersion.Should().Be("0.4.131-rc.1");

        var collection = await client.GetFromJsonAsync<List<TenantResponse>>("/api/v1/tenants");
        collection.Should().ContainSingle(row => row.TenantId == tenant.TenantId);

        var lookup = await client.GetFromJsonAsync<TenantResponse>($"/api/v1/tenants/{tenant.TenantId}");
        lookup!.ContactEmail.Should().Be("ada@acme.example");
        lookup.AutoUpdateChannel.Should().Be("prerelease");
        lookup.AutoUpdateTargetVersion.Should().Be("0.4.131-rc.1");

        var update = await client.PutAsJsonAsync($"/api/v1/tenants/{tenant.TenantId}", new UpdateTenantRequest
        {
            Name = "Acme Updated",
            Description = "Updated",
            Location = "Johannesburg",
            Domains = ["updated.acme.example"],
            ContactPerson = "Grace",
            ContactEmail = "grace@acme.example",
            AutoUpdate = false
        });
        update.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var delete = await client.DeleteAsync($"/api/v1/tenants/{tenant.TenantId}");
        delete.StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await client.GetAsync($"/api/v1/tenants/{tenant.TenantId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ScriptEndpoints_PersistCrudWithoutSpacetimeMirror()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var create = await client.PostAsJsonAsync("/api/v1/script-library", new CreateScriptRequest(
            "Collect disk facts",
            "/operations",
            "Collects disk state",
            "echo disk",
            "bash"));
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await create.Content.ReadFromJsonAsync<ScriptCreatedResponse>();
        created.Should().NotBeNull();

        var list = await client.GetFromJsonAsync<List<ScriptResponse>>("/api/v1/script-library");
        list.Should().ContainSingle(script => script.Id == created!.Id && script.Name == "Collect disk facts");

        var update = await client.PutAsJsonAsync($"/api/v1/script-library/{created!.Id}", new UpdateScriptRequest(
            "Collect storage facts",
            "/operations",
            "Collects storage state",
            "echo storage",
            "bash",
            ExpectedSourceRevision: list!.Single(script => script.Id == created!.Id).SourceRevision));
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await client.GetFromJsonAsync<ScriptResponse>($"/api/v1/script-library/{created.Id}");
        updated!.Name.Should().Be("Collect storage facts");
        updated.SourceRevision.Should().BeGreaterThan(list!.Single(script => script.Id == created.Id).SourceRevision);

        var delete = await client.DeleteAsync($"/api/v1/script-library/{created.Id}?expectedSourceRevision={updated.SourceRevision}");
        delete.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.GetAsync($"/api/v1/script-library/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SystemEndpoints_ExposeCoreRoutesAndKeepSpacetimeDiagnosticsRetired()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var version = await client.GetFromJsonAsync<SystemVersionResponse>("/api/v1/system/version");
        version.Should().NotBeNull();
        version!.ServiceName.Should().Be("NetRatel.API");
        var assemblyVersion = typeof(SystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        version.DisplayVersion.Should().Be($"v{assemblyVersion}");

        (await client.GetAsync("/api/v1/system/spacetime-health")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/system/spacetime-connection-debug")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await client.GetAsync("/api/v1/system/m2m/ping")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        (await client.GetAsync("/api/v1/system/m2m/ping")).StatusCode.Should().Be(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("X-System-Secret", "test-system-secret-for-hs256-signing");
        var token = await client.PostAsync("/api/v1/system/token", content: null);
        token.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<IHost> BuildAppAsync()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevelopmentOperator:Enabled"] = "true",
                ["SystemTokenSecret"] = "test-system-secret-for-hs256-signing",
                ["SystemToken:Issuer"] = "https://netratel.test",
                ["SystemToken:Audience"] = "netratel-test"
            })
            .Build();

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("M2M", _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("InstanceAdministrator", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("TenantAdministrator", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("ScriptEditor", policy => policy.RequireAuthenticatedUser());
                });
                services.AddSingleton<IConfiguration>(configuration);
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.AddScoped<ITenantService, TenantService>();
                services.AddScoped<IScriptService, ScriptService>();
                services.AddScoped<IEventRecorder, NoopEventRecorder>();
                services.AddScoped<ICorrelationContext, TestCorrelationContext>();
                services.Configure<AgentAuthOptions>(options =>
                {
                    options.Issuer = "https://netratel.test";
                    options.Audience = "netratel-test";
                });
                services.Configure<SecurityHardeningOptions>(_ => { });
                services.AddScoped<AgentNonceReplayService>();
                services.AddScoped<IAgentTokenService, AgentTokenService>();
                services.AddScoped<OidcSigningService>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapTenantEndpoints();
                    endpoints.MapScriptEndpoints();
                    endpoints.MapSystemEndpoints();
                });
            });
        });

        return await builder.StartAsync();
    }

    private sealed record ScriptCreatedResponse(ulong Id);

    private sealed record SystemVersionResponse(
        string ServiceName,
        string DisplayVersion,
        string InformationalVersion,
        string AssemblyVersion,
        string Environment);

    private sealed class NoopEventRecorder : IEventRecorder
    {
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestCorrelationContext : ICorrelationContext
    {
        public string? Current => "test-correlation";

        public string GetOrCreate() => Current!;
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return Task.FromResult(AuthenticateResult.Fail("Missing authorization header."));
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-admin")], Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
