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
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientMetadataEndpointTests
{
    private static readonly Guid AgentId = Guid.Parse("ed9b6001-cf7e-44de-908e-476c25132578");
    private static string Path => $"/api/v2/agents/7/{AgentId:D}/metadata";

    [Fact]
    public async Task Metadata_read_and_update_require_their_distinct_permissions_for_the_exact_tenant_and_visible_client()
    {
        using var host = await HostAsync();
        using var http = host.GetTestClient();
        (await http.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "ungranted");
        (await http.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.PutAsJsonAsync(Path, new UpdateClientMetadataRequest("Unauthorized", []))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        (await http.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.PutAsJsonAsync(Path, new UpdateClientMetadataRequest("Unauthorized", []))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var foreignPath = Path.Replace("/agents/7/", "/agents/8/", StringComparison.Ordinal);
        (await http.GetAsync(foreignPath)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "manager");
        (await http.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.PutAsJsonAsync(foreignPath, new UpdateClientMetadataRequest("Unauthorized", []))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var missingPath = Path.Replace(AgentId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        (await http.GetAsync(missingPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents.SingleAsync();
        agent.FriendlyName.Should().BeNull();
        (await db.OutboxMessages.CountAsync()).Should().Be(0);
        agent.DeletedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        (await http.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "manager");
        (await http.PutAsJsonAsync(Path, new UpdateClientMetadataRequest("Removed", []))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Offline_labels_are_normalized_and_clearable_without_changing_reported_or_enrolled_identity()
    {
        using var host = await HostAsync();
        using var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "manager");
        using var saved = await http.PutAsJsonAsync(Path, new
        {
            friendlyName = "  Dispatch Server  ", tags = new[] { " Prod ", "prod", "Factory1", "" },
            hostName = "must-not-change-hostname", agentId = Guid.NewGuid()
        });
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadata = await saved.Content.ReadFromJsonAsync<ClientMetadataDto>();
        metadata!.TenantId.Should().Be(7);
        metadata.AgentId.Should().Be(AgentId);
        metadata.HostName.Should().Be("actual-host");
        metadata.FriendlyName.Should().Be("Dispatch Server");
        metadata.Tags.Should().Equal("Prod", "Factory1");

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var agent = await db.Agents.SingleAsync();
            agent.Name.Should().Be("enrollment-name");
            agent.DeviceInfoJson.Should().Be("{\"hostName\":\"actual-host\",\"os\":\"Linux\"}");
            agent.IsEnabled.Should().BeFalse();
            agent.PublicKey.Should().Be("unchanged-device-key");
            (await db.AgentCredentials.SingleAsync()).RevokedAtUtc.Should().BeNull();
            var audit = await db.OutboxMessages.SingleAsync();
            audit.Type.Should().Be(NetRatelEventTypes.Agent.MetadataUpdated);
            audit.PayloadJson.Should().Contain("manager").And.NotContain("Dispatch Server").And.NotContain("Factory1");
        }

        using var cleared = await http.PutAsJsonAsync(Path, new UpdateClientMetadataRequest(" ", []));
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        var read = await http.GetFromJsonAsync<ClientMetadataDto>(Path);
        read!.FriendlyName.Should().BeNull();
        read.Tags.Should().BeEmpty();
        read.HostName.Should().Be("actual-host");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("tag")]
    [InlineData("tag-count")]
    public async Task Invalid_labels_leave_the_client_and_audit_unchanged(string invalidField)
    {
        using var host = await HostAsync();
        using var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "manager");
        var request = invalidField switch
        {
            "name" => new UpdateClientMetadataRequest(new string('x', ClientMetadataValidation.MaximumNameLength + 1), []),
            "tag" => new UpdateClientMetadataRequest(null, ["Prod\nFactory1"]),
            _ => new UpdateClientMetadataRequest(null, Enumerable.Range(0, ClientMetadataValidation.MaximumTags + 1).Select(index => $"tag-{index}").ToArray())
        };
        (await http.PutAsJsonAsync(Path, request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents.SingleAsync();
        agent.FriendlyName.Should().BeNull();
        agent.Tags.Should().BeEmpty();
        (await db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    private static async Task<IHost> HostAsync()
    {
        var database = Guid.NewGuid().ToString("N");
        var host = await Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { });
                services.AddAuthorization(options => options.AddPolicy("ClientManager", policy => policy.RequireAuthenticatedUser()));
                services.AddSingleton<IEffectiveAccessService, Access>();
                services.AddSingleton(TimeProvider.System);
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(database));
            });
            web.Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapClientMetadataEndpoints());
            });
        }).StartAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(new Agent
        {
            Id = AgentId, TenantId = 7, Name = "enrollment-name", IsEnabled = false, Status = AgentStatus.Disabled,
            PublicKey = "unchanged-device-key", DeviceInfoJson = "{\"hostName\":\"actual-host\",\"os\":\"Linux\"}"
        });
        db.AgentCredentials.Add(new AgentCredential
        {
            Id = Guid.NewGuid(), AgentId = AgentId, RefreshTokenHash = "unchanged-credential", CreatedAtUtc = DateTimeOffset.UtcNow
        });
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
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, header[5..])], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class Access : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId,
            CancellationToken cancellationToken = default) => Task.FromResult(tenantId == 7 &&
                (permission, principal.FindFirstValue(ClaimTypes.NameIdentifier)) is
                    (NetRatelPermissions.ClientManagement, "manager") or (NetRatelPermissions.TelemetryRead, "reader"));
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
