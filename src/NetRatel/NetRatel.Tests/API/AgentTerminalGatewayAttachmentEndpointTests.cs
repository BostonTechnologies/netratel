using System.Net;
using System.Net.Http.Json;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Endpoints;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTerminalGatewayAttachmentEndpointTests
{
    [Fact]
    public async Task GatewayPollingAndSse_DoNotRenewAnAttachmentWithoutABrowserHeartbeat()
    {
        var clock = new ManualTimeProvider();
        var session = CreateSession();
        var terminals = new AttachmentRegistry(session);
        var attachments = new GatewayTerminalBrowserAttachmentLeaseRegistry(
            clock,
            NullLogger<GatewayTerminalBrowserAttachmentLeaseRegistry>.Instance);
        attachments.Track(session);

        using var host = await BuildHostAsync(terminals, attachments);
        using var client = CreateClient(host);
        clock.Advance(TimeSpan.FromSeconds(90));

        var status = await client.GetFromJsonAsync<TerminalSessionDto>($"/api/v2/gateway-terminal/{session.SessionId}");
        status.Should().NotBeNull();
        status!.Generation.Should().Be(session.Generation, "the browser needs the fence for its later heartbeat");
        status.AttachmentLeaseId.Should().NotBeNullOrWhiteSpace("the browser needs an exact ownership fence too");

        using var stream = await client.GetAsync($"/api/v2/gateway-terminal/{session.SessionId}/stream");
        stream.StatusCode.Should().Be(HttpStatusCode.OK);

        clock.Advance(TimeSpan.FromSeconds(31));
        await attachments.CloseDueAsync(terminals, CancellationToken.None);

        terminals.CloseRequests.Should().ContainSingle().Which.Should().Be((
            session.SessionId,
            session.Generation,
            "terminal_browser_attachment_expired"));
    }

    [Fact]
    public async Task AttachmentHeartbeatRoute_RenewsOnlyTheCurrentGenerationAndRejectsClosingLeases()
    {
        var clock = new ManualTimeProvider();
        var session = CreateSession();
        var terminals = new AttachmentRegistry(session);
        var attachments = new GatewayTerminalBrowserAttachmentLeaseRegistry(
            clock,
            NullLogger<GatewayTerminalBrowserAttachmentLeaseRegistry>.Instance);
        attachments.Track(session);

        using var host = await BuildHostAsync(terminals, attachments);
        using var client = CreateClient(host);
        var status = await client.GetFromJsonAsync<TerminalSessionDto>($"/api/v2/gateway-terminal/{session.SessionId}");
        var attachmentLeaseId = status!.AttachmentLeaseId;
        attachmentLeaseId.Should().NotBeNullOrWhiteSpace();

        using var stale = await client.PostAsJsonAsync(
            $"/api/v2/gateway-terminal/{session.SessionId}/attachment/renew",
            Renewal(session.Generation + 1, attachmentLeaseId!, "browser-current", claimOwnership: true));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadFromJsonAsync<TerminalActionResponse>())!.Code
            .Should().Be("terminal_browser_attachment_stale_generation");

        clock.Advance(TimeSpan.FromSeconds(90));
        using var renewed = await client.PostAsJsonAsync(
            $"/api/v2/gateway-terminal/{session.SessionId}/attachment/renew",
            Renewal(session.Generation, attachmentLeaseId!, "browser-current", claimOwnership: true));
        renewed.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var renewal = await renewed.Content.ReadFromJsonAsync<TerminalActionResponse>();
        renewal!.AttachmentLeaseId.Should().Be(attachmentLeaseId);

        using var clone = await client.PostAsJsonAsync(
            $"/api/v2/gateway-terminal/{session.SessionId}/attachment/renew",
            Renewal(session.Generation, attachmentLeaseId!, "browser-clone", claimOwnership: true));
        clone.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var cloneRenewal = await clone.Content.ReadFromJsonAsync<TerminalActionResponse>();
        cloneRenewal!.AttachmentLeaseId.Should().NotBe(attachmentLeaseId);

        using var staleOwner = await client.PostAsJsonAsync(
            $"/api/v2/gateway-terminal/{session.SessionId}/attachment/renew",
            Renewal(session.Generation, attachmentLeaseId!, "browser-current", claimOwnership: false));
        staleOwner.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await staleOwner.Content.ReadFromJsonAsync<TerminalActionResponse>())!.Code
            .Should().Be("terminal_browser_attachment_stale");

        clock.Advance(TimeSpan.FromSeconds(31));
        await attachments.CloseDueAsync(terminals, CancellationToken.None);
        terminals.CloseRequests.Should().BeEmpty("the browser-originated, current-generation heartbeat extended the lease");

        attachments.MarkClosePending(session);
        using var closing = await client.PostAsJsonAsync(
            $"/api/v2/gateway-terminal/{session.SessionId}/attachment/renew",
            Renewal(session.Generation, cloneRenewal!.AttachmentLeaseId!, "browser-clone", claimOwnership: false));
        closing.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await closing.Content.ReadFromJsonAsync<TerminalActionResponse>())!.Code
            .Should().Be("terminal_browser_attachment_closing");
    }

    private static TerminalAttachmentRenewalRequest Renewal(
        ulong generation,
        string attachmentLeaseId,
        string browserAttachmentId,
        bool claimOwnership) =>
        new(generation)
        {
            AttachmentLeaseId = attachmentLeaseId,
            BrowserAttachmentId = browserAttachmentId,
            ClaimOwnership = claimOwnership
        };

    private static HttpClient CreateClient(IHost host)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");
        return client;
    }

    private static GatewayTerminalSession CreateSession() => new(
        SessionId: "browser-heartbeat-984",
        TenantId: 984,
        AgentId: Guid.Parse("1a0abcc1-84f4-435a-b9f6-cac1d761ae01"),
        Generation: 7,
        ShellType: "bash",
        Columns: 120,
        Rows: 32,
        State: "opened",
        CreatedAtUtc: DateTimeOffset.UtcNow,
        Authority: "test");

    private static async Task<IHost> BuildHostAsync(
        AttachmentRegistry terminals,
        GatewayTerminalBrowserAttachmentLeaseRegistry attachments)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options =>
                    options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser()));
                services.AddSingleton<IAgentTerminalSessionRegistry>(terminals);
                services.AddSingleton(attachments);
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    TerminalGatewayEnabled = true,
                    PresenceAuthorityEnabled = true,
                    TerminalAuthorityEnabled = true
                });
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapAgentTerminalGatewayEndpoints());
            });
        });

        return await builder.StartAsync();
    }

    private sealed class AttachmentRegistry(GatewayTerminalSession session) : IAgentTerminalSessionRegistry
    {
        private GatewayTerminalSession _session = session;

        public List<(string SessionId, ulong Generation, string Reason)> CloseRequests { get; } = [];

        public AgentTerminalGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
            new(Channel.CreateUnbounded<GatewayTerminalFrame>().Reader, static () => { });

        public GatewayTerminalAvailability? GetAvailability(ClientKey client) => null;

        public Task<GatewayTerminalSession> OpenAsync(ClientKey client, string shellType, string? workingDirectory, int columns, int rows, CancellationToken ct) =>
            Task.FromResult(_session);

        public Task SendInputAsync(string sessionId, ulong generation, ReadOnlyMemory<byte> content, CancellationToken ct) => Task.CompletedTask;

        public Task ResizeAsync(string sessionId, ulong generation, int columns, int rows, CancellationToken ct) => Task.CompletedTask;

        public Task CloseAsync(string sessionId, ulong generation, string reason, CancellationToken ct)
        {
            CloseRequests.Add((sessionId, generation, reason));
            _session = _session with { State = "closing" };
            return Task.CompletedTask;
        }

        public GatewayTerminalOutputSubscription Subscribe(string sessionId, ulong generation)
        {
            var output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
            output.Writer.TryComplete();
            return new GatewayTerminalOutputSubscription(output.Reader);
        }

        public GatewayTerminalSession? Get(string sessionId) =>
            string.Equals(_session.SessionId, sessionId, StringComparison.Ordinal) ? _session : null;

        public bool TryReceiveOpened(ClientKey client, TerminalSessionOpened opened) => false;

        public Task<bool> TryReceiveOutputAsync(ClientKey client, TerminalOutput output, CancellationToken ct) => Task.FromResult(false);

        public bool TryReceiveResizeApplied(ClientKey client, TerminalResizeApplied resize) => false;

        public bool TryReceiveClosed(ClientKey client, TerminalSessionClosed closed) => false;

        public bool TryReceiveFailed(ClientKey client, TerminalSessionFailed failed) => false;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "terminal-test-admin")], Scheme.Name)),
                Scheme.Name)));
    }
}
