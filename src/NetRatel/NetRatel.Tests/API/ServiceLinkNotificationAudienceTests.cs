using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ServiceLinkNotificationAudienceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Personal_setup_failures_stay_actor_scoped_in_every_read_and_mutation(bool auditReader)
    {
        using var host = await HostAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var own = Message(NetRatelNotificationAudience.ServiceLinkFailure, "actor");
        var foreign = Message(NetRatelNotificationAudience.ServiceLinkFailure, "other");
        var audit = Message("ordinary.audit", "anything");
        db.OutboxMessages.AddRange(own, foreign, audit);
        await db.SaveChangesAsync();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Actor", "actor");
        if (auditReader) client.DefaultRequestHeaders.Add("X-Test-Audit", "yes");
        client.DefaultRequestHeaders.Add("X-NetRatel-UserId", "other");

        var page = await client.GetFromJsonAsync<PagedResult<NetRatelNotificationDto>>("/api/v1/notifications");
        Assert.Contains(page!.Items, x => x.Id == own.Id);
        Assert.DoesNotContain(page.Items, x => x.Id == foreign.Id);
        Assert.Equal(auditReader, page.Items.Any(x => x.Id == audit.Id));
        var summary = await client.GetFromJsonAsync<NetRatelNotificationSummaryDto>("/api/v1/notifications/summary");
        Assert.Equal(auditReader ? 2 : 1, summary!.TotalCount);
        var unread = await client.GetFromJsonAsync<NetRatelNotificationDto[]>("/api/v1/notifications/unread-errors");
        Assert.DoesNotContain(unread!, x => x.Id == foreign.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/notifications/{own.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/notifications/{foreign.Id}")).StatusCode);
        using var marked = await client.PostAsJsonAsync("/api/v1/notifications/mark-read", new { Ids = new[] { own.Id, foreign.Id, audit.Id } });
        marked.EnsureSuccessStatusCode();
        Assert.Equal(auditReader ? 2 : 1, await db.OutboxReadReceipts.CountAsync());
        Assert.False(await db.OutboxReadReceipts.AnyAsync(x => x.EventId == foreign.Id || x.UserId != "actor"));
        foreach (var path in new[] { $"/api/v1/events/{own.Id}/retry", $"/api/v1/events/{own.Id}/disable" })
            Assert.Equal(auditReader ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, (await client.PostAsync(path, null)).StatusCode);
    }

    [Fact]
    public async Task No_current_integration_or_audit_authority_cannot_read_personal_failures()
    {
        using var host = await HostAsync();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Actor", "unauthorized");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/notifications")).StatusCode);
    }

    [Fact]
    public void Personal_failure_is_never_broadcast_on_global_bus()
    {
        var bus = new NetRatelNotificationEventBus();
        var reader = bus.Subscribe(null);
        bus.Publish(new() { EventType = NetRatelNotificationAudience.ServiceLinkFailure, EntityId = "actor" });
        Assert.False(reader.TryRead(out _));
        bus.Publish(new() { EventType = "ordinary.audit" });
        Assert.True(reader.TryRead(out _));
        bus.Unsubscribe(null, reader);
    }

    private static OutboxMessage Message(string type, string actor) => new()
    {
        Id = Guid.NewGuid(), Type = type, EntityId = actor, Source = "test", CorrelationId = Guid.NewGuid().ToString("N"),
        PayloadJson = "{}", Message = "Safe fixture guidance", Severity = "Warning", OccurredUtc = DateTimeOffset.UtcNow
    };

    private static Task<IHost> HostAsync()
    {
        var database = Guid.NewGuid().ToString("N");
        return new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            services.AddAuthorization(options => options.AddPolicy("AuditReader", policy => policy.RequireAuthenticatedUser().RequireClaim("audit", "yes")));
            services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(database));
            services.AddScoped<NetRatelNotificationAudience>();
            services.AddScoped<INetRatelNotificationService, NetRatelNotificationService>();
            services.AddSingleton(new NetRatelNotificationDisplaySanitizer(new Names()));
            services.AddSingleton<IEffectiveAccessService, Access>();
            services.AddSingleton<INetRatelNotificationEventBus, NetRatelNotificationEventBus>();
            services.AddSingleton<INetRatelExternalServiceCallbackReplayService, Replay>();
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapNotificationEndpoints()); })).StartAsync();
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Actor", out var actor)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor.ToString()) };
            if (Request.Headers.ContainsKey("X-Test-Audit")) claims.Add(new("audit", "yes"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
    private sealed class Names : IClientDisplayNameResolver
    {
        public IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> identities) => new Dictionary<string, string>();
    }
    private sealed class Access : IEffectiveAccessService
    {
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default) =>
            Task.FromResult<int[]?>(actor.FindFirstValue(ClaimTypes.NameIdentifier) == "actor" ? [1] : []);
        public Task<bool> AuthorizeAsync(ClaimsPrincipal actor, string permission, int? tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal actor, int? tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Replay : INetRatelExternalServiceCallbackReplayService
    {
        public bool CanReplay(NetRatelNotificationDto notification) => false;
        public Task ReplayAsync(NetRatelNotificationDto notification, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
