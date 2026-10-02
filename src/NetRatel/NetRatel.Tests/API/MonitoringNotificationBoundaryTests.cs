using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Notifications;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Monitoring;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class MonitoringNotificationBoundaryTests
{
    [Fact]
    public async Task ForeignTenantAuditReaderCannotReceiveMonitoringThroughLegacyGlobalBus()
    {
        var bus = new Bus();
        bus.Publish(new() { Id = Guid.NewGuid(), TenantId = "7", EventType = MonitoringLimits.NotificationEventPrefix + "AlertRaised", Message = "foreign-monitoring-secret" });
        bus.Publish(new() { Id = Guid.NewGuid(), TenantId = "8", EventType = "legacy.audit", Message = "ordinary-authorized-event" });
        using var host = await HostAsync(bus, new LegacyNotifications());
        var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await http.GetAsync("/api/v1/notifications/stream", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
        var lines = new List<string>();
        for (var index = 0; index < 4; index++) lines.Add((await reader.ReadLineAsync(cancellation.Token)) ?? "");
        string.Join('\n', lines).Should().Contain("ordinary-authorized-event").And.NotContain("foreign-monitoring-secret").And.NotContain(MonitoringLimits.NotificationEventPrefix);
        cancellation.Cancel();
        response.Dispose();
        await bus.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task GuessedScopedNotificationIdCannotReachLegacyRetryOrDisable()
    {
        var notifications = new LegacyNotifications();
        using var host = await HostAsync(new Bus(), notifications);
        var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var scopedId = Guid.NewGuid();
        (await http.GetAsync($"/api/v1/notifications/{scopedId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.PostAsync($"/api/v1/events/{scopedId}/retry", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.PostAsync($"/api/v1/events/{scopedId}/disable", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        notifications.Retries.Should().Be(0);
        notifications.Disables.Should().Be(0);
    }

    private static Task<IHost> HostAsync(Bus bus, LegacyNotifications notifications) => new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
        .ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, AuditAuth>("Test", _ => { });
            services.AddAuthorization(options => options.AddPolicy("AuditReader", policy => policy.RequireAuthenticatedUser().RequireClaim("tenant_id", "8")));
            services.AddSingleton<INetRatelNotificationEventBus>(bus);
            services.AddSingleton<INetRatelNotificationService>(notifications);
            services.AddSingleton<INetRatelExternalServiceCallbackReplayService, CallbackReplay>();
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapNotificationEndpoints()); })).StartAsync();

    private sealed class AuditAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.ContainsKey("Authorization")
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "foreign-audit-reader"), new Claim("tenant_id", "8")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
    }
    private sealed class Bus : INetRatelNotificationEventBus
    {
        private readonly Channel<NetRatelNotificationDto> _channel = Channel.CreateBounded<NetRatelNotificationDto>(4);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChannelReader<NetRatelNotificationDto> Subscribe(string? tenantId) => _channel.Reader;
        public void Publish(NetRatelNotificationDto notification) => _channel.Writer.TryWrite(notification);
        public void Unsubscribe(string? tenantId, ChannelReader<NetRatelNotificationDto> reader) => Closed.TrySetResult();
    }
    private sealed class CallbackReplay : INetRatelExternalServiceCallbackReplayService
    {
        public bool CanReplay(NetRatelNotificationDto notification) => false;
        public Task ReplayAsync(NetRatelNotificationDto notification, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class LegacyNotifications : INetRatelNotificationService
    {
        public int Retries { get; private set; }
        public int Disables { get; private set; }
        // The real Infrastructure service excludes the reserved monitoring
        // namespace. This API seam demonstrates invisible IDs stay invisible
        // on mutation routes as well as reads.
        public Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, string user, CancellationToken ct) => Task.FromResult<NetRatelNotificationDto?>(null);
        public Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(string user, int page, int size, string? type, string? correlation, string? entity, string? status,
            DateTimeOffset? from, DateTimeOffset? to, string? search, string? source, NetRatelNotificationSeverity? severity, CancellationToken ct) => Task.FromResult(new PagedResult<NetRatelNotificationDto>([], page, size, 0));
        public Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(string user, int take, CancellationToken ct) => Task.FromResult<IReadOnlyList<NetRatelNotificationDto>>([]);
        public Task<NetRatelNotificationSummaryDto> GetSummaryAsync(string user, CancellationToken ct) => Task.FromResult(new NetRatelNotificationSummaryDto());
        public Task<int> MarkReadAsync(string user, IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.FromResult(0);
        public Task RetryAsync(Guid id, CancellationToken ct) { Retries++; return Task.CompletedTask; }
        public Task DisableAsync(Guid id, CancellationToken ct) { Disables++; return Task.CompletedTask; }
    }
}
