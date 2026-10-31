using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.SoftwareInventory;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class SoftwareInventoryEndpointTests
{
    private static readonly Guid AgentId = Guid.Parse("8d091448-7719-49b8-91cb-381e9bd99ed1");
    private static string ClientPath => $"/api/v2/agents/7/{AgentId:D}/software-inventory";

    [Fact]
    public async Task Http_contract_keeps_reads_inert_refresh_authorized_and_agent_uploads_bound_to_authenticated_tenant()
    {
        using var host = await StartAsync();
        using var http = host.GetTestClient();
        (await http.GetAsync(ClientPath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        var empty = await http.GetFromJsonAsync<SoftwareInventoryStatus>(ClientPath);
        empty!.LatestScan.Should().BeNull();
        (await http.GetAsync(ClientPath.Replace("/7/", "/8/"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.PostAsJsonAsync(ClientPath + "/refresh", new SoftwareRefreshRequest())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.GetAsync("/api/v2/tenants/7/software-inventory/installations?pageSize=101")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "manager");
        var requested = await (await http.PostAsJsonAsync(ClientPath + "/refresh", new SoftwareRefreshRequest())).Content.ReadFromJsonAsync<SoftwareInventoryStatus>();
        requested!.LatestScan!.Status.Should().Be("Pending");
        var id = requested.LatestScan.CollectionId;
        var upload = SoftwareInventoryWire.Encode(id, new([new SoftwarePackage("=Example", "1", "Publisher", "x64", "example", "rpm")], true, "linux", []), DateTimeOffset.UtcNow).Single();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "agent8");
        (await http.PostAsJsonAsync("/api/v2/agent/software-inventory/chunks", upload)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "agent7");
        (await http.PostAsync("/api/v2/agent/software-inventory/work", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await http.PostAsJsonAsync("/api/v2/agent/software-inventory/chunks", upload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SoftwareChunkAcknowledgement>())!.Status.Should().Be("Complete");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        var snapshot = await http.GetFromJsonAsync<SoftwarePage<SoftwareInstallationDto>>(ClientPath + "/snapshot");
        snapshot!.Items.Should().ContainSingle().Which.Package.Name.Should().Be("=Example");
        var csv = await http.GetStringAsync("/api/v2/tenants/7/software-inventory/export.csv");
        csv.Should().Contain("\"'=Example\"");
        (await http.GetAsync("/api/v2/tenants/8/software-inventory/export.csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.Services.GetRequiredService<Events>().Items.Should().Contain(item => item.EventType == "SoftwareInventory.scan.request");
    }

    private static async Task<IHost> StartAsync()
    {
        var database = Guid.NewGuid().ToString();
        var host = await Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("TelemetryReader", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("ClientManager", policy => policy.RequireAuthenticatedUser().RequireClaim("manage", "true"));
                    options.AddPolicy("TenantAdministrator", policy => policy.RequireAuthenticatedUser().RequireClaim("manage", "true"));
                    options.AddPolicy("SoftwareInventoryDiscovery", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("AgentGatewayAccess", policy => policy.RequireAuthenticatedUser().RequireClaim("role", "agent"));
                });
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(database));
                services.AddScoped<SoftwareInventoryService>();
                services.AddSingleton<TimeProvider>(TimeProvider.System);
                services.AddSingleton<IEffectiveAccessService, Access>();
                services.AddSingleton<Events>();
                services.AddSingleton<IEventRecorder>(provider => provider.GetRequiredService<Events>());
            });
            web.Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapSoftwareInventoryEndpoints());
            });
        }).StartAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.AddRange(new Tenant { Id = 7, Name = "Example" }, new Tenant { Id = 8, Name = "Other" }, new Agent { Id = AgentId, TenantId = 7, Name = "Example client" });
        await db.SaveChangesAsync();
        return host;
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Test ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());
            var actor = header[5..];
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor) };
            if (actor == "manager") claims.Add(new("manage", "true"));
            if (actor.StartsWith("agent", StringComparison.Ordinal))
                claims.AddRange([new("sub", AgentId.ToString()), new("agent_id", AgentId.ToString()), new("tenant_id", actor[5..]), new("role", "agent"), new("scope", "netratel:connect")]);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
        }
    }
    private sealed class Access : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantId == 7 && (permission == NetRatelPermissions.TelemetryRead || principal.HasClaim("manage", "true")));
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Events : IEventRecorder
    {
        public List<DomainEvent> Items { get; } = [];
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) { Items.Add(domainEvent); return Task.CompletedTask; }
    }
}
