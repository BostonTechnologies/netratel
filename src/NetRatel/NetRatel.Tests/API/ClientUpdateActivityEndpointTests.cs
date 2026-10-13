using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientUpdateActivityEndpointTests
{
    [Fact]
    public async Task Stream_requires_authorized_tenant_and_stops_after_permission_revocation()
    {
        var access = new FixtureAccess();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("Fixture", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("ArtifactPublisher", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IEffectiveAccessService>(access);
        builder.Services.AddSingleton<ClientUpdateActivityBroker>();
        builder.Services.AddSingleton(TimeProvider.System);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapClientUpdateActivityEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var denied = await client.GetAsync("/api/v1/client-updates/activity/stream?tenantId=8");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var broker = app.Services.GetRequiredService<ClientUpdateActivityBroker>();
        broker.SetConnected(true);
        using var response = await client.GetAsync("/api/v1/client-updates/activity/stream?tenantId=7", HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await reader.ReadLineAsync(deadline.Token))!.Should().Contain("ready");
        (await reader.ReadLineAsync(deadline.Token)).Should().BeEmpty();
        access.Permitted = false;
        broker.Publish(new(Guid.NewGuid(), 7));
        (await reader.ReadLineAsync(deadline.Token)).Should().BeNull("a revoked viewer receives no further notification data");
    }

    private sealed class FixtureAccess : IEffectiveAccessService
    {
        public bool Permitted { get; set; } = true;
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Permitted && (tenantId is null or 7));
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken = default) =>
            Task.FromResult<int[]?>(Permitted ? [7] : []);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("fixture", false, false, new HashSet<string>()));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture")], "Fixture")), Scheme.Name)));
    }
}
