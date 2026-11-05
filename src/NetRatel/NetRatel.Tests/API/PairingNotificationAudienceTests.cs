using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
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
using NetRatel.Infrastructure.Services;
using NetRatel.Infrastructure.Flows;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class PairingNotificationAudienceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Personal_setup_failures_stay_actor_scoped_in_every_read_and_mutation(bool auditReader)
    {
        using var host = await HostAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var own = Message(NetRatelNotificationAudience.PairingFailure, "actor");
        var foreign = Message(NetRatelNotificationAudience.PairingFailure, "other");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_execution_facts_and_subscription_records_are_not_generic_notifications_or_retry_targets(bool auditReader)
    {
        using var host = await HostAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var request = await requests.CreateAsync(new("synthetic-source", string.Empty, "missing-definition", null, 1, null));
        await requests.UpdateAsync(new(request.Id, null, null, null, null, "Failed", "Synthetic source failure.", null, null, null));
        var native = await db.OutboxMessages.SingleAsync(row => row.Type == FlowExecutionEventLimits.OutboxType);
        var binding = Message(ExecutionLifecycleOutbox.RequestBindingOutboxType, "actor");
        var intent = Message(FlowExecutionSubscriptionLimits.OutboxType, "actor");
        var audit = Message("ordinary.audit", "actor");
        db.OutboxMessages.AddRange(binding, intent, audit);
        await db.SaveChangesAsync();
        var reserved = new[] { native.Id, binding.Id, intent.Id };
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Actor", "actor");
        if (auditReader) client.DefaultRequestHeaders.Add("X-Test-Audit", "yes");

        var page = await client.GetFromJsonAsync<PagedResult<NetRatelNotificationDto>>("/api/v1/notifications");
        Assert.DoesNotContain(page!.Items, item => reserved.Contains(item.Id));
        var summary = await client.GetFromJsonAsync<NetRatelNotificationSummaryDto>("/api/v1/notifications/summary");
        Assert.Equal(auditReader ? 1 : 0, summary!.TotalCount);
        var unread = await client.GetFromJsonAsync<NetRatelNotificationDto[]>("/api/v1/notifications/unread-errors");
        Assert.DoesNotContain(unread!, item => reserved.Contains(item.Id));
        foreach (var id in reserved)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/notifications/{id}")).StatusCode);
            Assert.Equal(auditReader ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/v1/events/{id}")).StatusCode);
            foreach (var action in new[] { "retry", "disable" })
                Assert.Equal(auditReader ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden,
                    (await client.PostAsync($"/api/v1/events/{id}/{action}", null)).StatusCode);
        }
        if (auditReader)
        {
            var events = await client.GetFromJsonAsync<PagedResult<NetRatelNotificationDto>>("/api/v1/events");
            Assert.DoesNotContain(events!.Items, item => reserved.Contains(item.Id));
        }
        using var marked = await client.PostAsJsonAsync("/api/v1/notifications/mark-read", new { Ids = reserved });
        marked.EnsureSuccessStatusCode();
        Assert.False(await db.OutboxReadReceipts.AnyAsync(row => reserved.Contains(row.EventId)));
        Assert.All(await db.OutboxMessages.AsNoTracking().Where(row => reserved.Contains(row.Id)).ToArrayAsync(),
            row => Assert.Equal(OutboxStatuses.Pending, row.Status));
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
        bus.Publish(new() { EventType = NetRatelNotificationAudience.PairingFailure, EntityId = "actor" });
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
