using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Netclaw;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed class NetclawProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_client_authenticates_negotiate_and_transport_then_uses_exact_session_message_and_approval_contract(bool rejectWebSockets)
    {
        var calls = new ConcurrentQueue<HubCall>(); var fallback = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders(); builder.Services.AddSingleton(calls); builder.Services.AddSignalR();
        builder.Services.AddAuthentication("Device").AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>("Device", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (!context.WebSockets.IsWebSocketRequest && context.Request.Path == "/hub/session" && context.Request.Query.ContainsKey("id"))
                fallback.Enqueue(context.Request.Method);
            if (rejectWebSockets && context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            await next();
        });
        app.MapHub<ContractHub>("/hub/session").RequireAuthorization(); await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var endpoint = new Uri(new Uri(address), "/hub/session").AbsoluteUri;
            var profile = new NetclawRuntimeProfile(17, Guid.NewGuid(), 1, endpoint, "test-device-token", true, "test-profile");
            await using var client = new AiAssistantChatClientFactory().Create(profile);
            var output = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await client.ConnectAsync("persisted-session", item =>
            { if (item.GetProperty("type").GetString() == "text") output.TrySetResult(item.Clone()); return Task.CompletedTask; }, deadline.Token);
            Assert.Equal("persisted-session", result.SessionId); Assert.False(result.Created);
            await client.SendAsync(result.SessionId, "Investigate selected systems", deadline.Token);
            await client.RespondAsync(result.SessionId, "call-1", "deny", deadline.Token);
            Assert.Equal("Evidence ready", (await output.Task.WaitAsync(deadline.Token)).GetProperty("text").GetString());
            Assert.Contains(calls, call => call.Method == "EnsureSession" && call.Arguments.SequenceEqual(["persisted-session", "signalr"]));
            Assert.Contains(calls, call => call.Method == "SendMessage" && call.Arguments.SequenceEqual(["persisted-session", "Investigate selected systems"]));
            Assert.Contains(calls, call => call.Method == "RespondToInteraction" && call.Arguments.SequenceEqual(["persisted-session", "call-1", "deny"]));
            Assert.All(calls, call => Assert.Equal("device", call.User));
            if (rejectWebSockets) Assert.NotEmpty(fallback); else Assert.Empty(fallback);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Pair_exchange_has_only_code_and_unique_device_name_and_does_not_retry_uncertain_sends()
    {
        var count = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            count++; Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://daemon.example/api/pair/exchange", request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(["code", "deviceName"], json.RootElement.EnumerateObject().Select(x => x.Name));
            Assert.Equal("one-time-test-code", json.RootElement.GetProperty("code").GetString());
            Assert.StartsWith("netratel-api-", json.RootElement.GetProperty("deviceName").GetString());
            throw new HttpRequestException("Secret upstream detail must not be returned.");
        }));
        var error = await Assert.ThrowsAsync<NetclawPairingException>(() => new NetclawPairingService(client)
            .ExchangeCodeAsync(new(new Uri("https://daemon.example/hub/session"), false), "one-time-test-code"));
        Assert.Equal(1, count); Assert.Equal("pairing_outcome_uncertain", error.Code); Assert.DoesNotContain("Secret upstream", error.Message);
    }

    [Fact]
    public async Task Explicit_device_name_conflict_is_the_only_retried_exchange()
    {
        var names = new HashSet<string>(); var sends = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            sends++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.True(names.Add(body.RootElement.GetProperty("deviceName").GetString()!));
            return sends == 1 ? new(HttpStatusCode.Conflict) : new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"test-device-token\"}") };
        }));
        Assert.Equal("test-device-token", await new NetclawPairingService(client).ExchangeCodeAsync(new(new Uri("https://daemon.example/hub/session"), false), "test-code"));
        Assert.Equal(2, sends);
    }

    [Theory]
    [InlineData("https://metadata.google.internal/hub/session", false)]
    [InlineData("http://203.0.113.10/hub/session", true)]
    [InlineData("http://10.23.45.67/hub/session", false)]
    [InlineData("https://daemon.example/outside/hub", false)]
    [InlineData("https://user:password@daemon.example/hub/session", false)]
    public async Task Invalid_pairing_target_is_rejected_before_any_http_send(string endpoint, bool allowPrivate)
    {
        var sends = 0;
        using var client = new HttpClient(new Handler((_, _) => { sends++; throw new InvalidOperationException("Unsafe target reached transport."); }));
        await Assert.ThrowsAsync<NetclawPairingException>(() => new NetclawPairingService(client).ExchangeCodeAsync(new(new Uri(endpoint), allowPrivate), "test-code"));
        Assert.Equal(0, sends);
    }

    [Fact]
    public void Recovery_requires_one_exact_user_marker_and_approvals_never_offer_global_grants()
    {
        var id = Guid.NewGuid(); var message = Guid.NewGuid(); var marker = NetclawInvestigationWorker.MessageMarker(id, message);
        JsonElement History(params (string Role, string Content)[] items) => JsonSerializer.SerializeToElement(new
        { recentMessages = items.Select(x => new { role = x.Role, content = x.Content }).ToArray() });
        Assert.True(NetclawInvestigationWorker.ProvesAdmission(History(("user", marker + "Question")), id, message));
        Assert.False(NetclawInvestigationWorker.ProvesAdmission(History(("assistant", marker + "Quoted")), id, message));
        Assert.False(NetclawInvestigationWorker.ProvesAdmission(History(("user", "Quoted " + marker)), id, message));
        Assert.False(NetclawInvestigationWorker.ProvesAdmission(History(("user", marker), ("user", marker)), id, message));
        Assert.False(NetclawInvestigationWorker.ProvesAdmission(History(("user", marker)), id, Guid.NewGuid()));
        var output = JsonSerializer.SerializeToElement(new { interactionOptions = new[] { "approve_everywhere", "approve_always", "approve_once", "approve_once", "approve_session", "deny", "invented" }.Select(key => new { key }) });
        Assert.Equal(["approve_once", "approve_session", "deny"], NetclawInvestigationWorker.OfferedOptions(output).Select(x => x.Key));
    }

    public sealed class ContractHub(ConcurrentQueue<HubCall> calls) : Hub
    {
        public Task<SessionEnsureResult> EnsureSession(string? sessionId, string protocol)
        { calls.Enqueue(new("EnsureSession", Context.User!.Identity!.Name!, [sessionId ?? "", protocol])); return Task.FromResult(new SessionEnsureResult(sessionId ?? "new-session", sessionId is null)); }
        public async Task SendMessage(string sessionId, string text)
        { calls.Enqueue(new("SendMessage", Context.User!.Identity!.Name!, [sessionId, text])); await Clients.Caller.SendAsync("ReceiveOutput", new { type = "text", sessionId, text = "Evidence ready" }); }
        public Task RespondToInteraction(string sessionId, string callId, string key)
        { calls.Enqueue(new("RespondToInteraction", Context.User!.Identity!.Name!, [sessionId, callId, key])); return Task.CompletedTask; }
    }
    public sealed record HubCall(string Method, string User, IReadOnlyList<string> Arguments);
    private sealed class DeviceAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.Authorization.ToString() == "Bearer test-device-token"
            ? AuthenticateResult.Success(new(new(new ClaimsIdentity([new(ClaimTypes.Name, "device")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.Fail("Invalid test device token."));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
