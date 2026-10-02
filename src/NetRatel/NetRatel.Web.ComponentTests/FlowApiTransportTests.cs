using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowApiTransportTests
{
    [Theory]
    [InlineData("save", "lost-response")]
    [InlineData("publish", "lost-response")]
    [InlineData("save", "unavailable")]
    [InlineData("publish", "unavailable")]
    [InlineData("save", "302")]
    [InlineData("publish", "302")]
    [InlineData("save", "307")]
    [InlineData("publish", "307")]
    public async Task Actual_Put_And_Post_Are_Sent_Once_After_Response_Loss_Server_Error_Or_Redirect(string operation, string response)
    {
        await using var server = await LoopbackServer.StartAsync(response);
        using var services = Services(server.BaseAddress); using var scope = services.CreateScope();
        Bind(scope, "alice", bearer: false);
        var api = scope.ServiceProvider.GetRequiredService<IFlowApiService>(); var flow = FlowEditorTests.Definition();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => operation == "save"
            ? api.SaveAsync(17, flow.Id, new(7, flow.Name, flow.Draft))
            : api.PublishAsync(17, flow.Id, new(7)));
        var request = Assert.Single(server.Requests);
        Assert.Equal(operation == "save" ? "PUT" : "POST", request.Method);
        Assert.Equal($"/api/v1/tenants/17/flows/{flow.Id:D}/{(operation == "save" ? "draft" : "publish")}", request.Path);
        using var submitted = JsonDocument.Parse(request.Body);
        Assert.Equal(7, submitted.RootElement.GetProperty("expectedRevision").GetInt64());
        Assert.Equal("NetRatel.Local=fixture-alice", request.Cookie);
        Assert.Null(request.Authorization);
        Assert.DoesNotContain(server.Requests, r => r.Path == "/redirected-write");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_Pooled_Pipeline_Keeps_Circuit_Identity_And_Does_Not_Store_Server_Cookies(bool bearer)
    {
        await using var server = await LoopbackServer.StartAsync("success-with-cookie");
        using var services = Services(server.BaseAddress);
        using var alice = services.CreateScope(); using var bob = services.CreateScope(); using var anonymous = services.CreateScope();
        Bind(alice, "alice", bearer); Bind(bob, "bob", bearer);
        var aliceApi = alice.ServiceProvider.GetRequiredService<IFlowApiService>(); var bobApi = bob.ServiceProvider.GetRequiredService<IFlowApiService>();
        var flow = FlowEditorTests.Definition();
        await Task.WhenAll(aliceApi.GetAsync(17, flow.Id), bobApi.GetAsync(17, flow.Id));
        await aliceApi.GetAsync(17, flow.Id);
        await Assert.ThrowsAsync<ReauthRequiredException>(() => anonymous.ServiceProvider.GetRequiredService<IFlowApiService>().GetAsync(17, flow.Id));
        Assert.Equal(3, server.Requests.Count);
        if (bearer)
        {
            Assert.Equal(2, server.Requests.Count(r => r.Authorization == "Bearer fixture-alice"));
            Assert.Single(server.Requests, r => r.Authorization == "Bearer fixture-bob");
            Assert.All(server.Requests, r => Assert.Null(r.Cookie));
        }
        else
        {
            Assert.Equal(2, server.Requests.Count(r => r.Cookie == "NetRatel.Local=fixture-alice"));
            Assert.Single(server.Requests, r => r.Cookie == "NetRatel.Local=fixture-bob");
            Assert.All(server.Requests, r => Assert.Null(r.Authorization));
        }
        Assert.All(server.Requests, r => Assert.DoesNotContain("ApiIssued", r.Cookie ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Actual_Save_Returns_The_Exact_Committed_Revision_Without_A_Second_Request()
    {
        await using var server = await LoopbackServer.StartAsync("success-with-cookie");
        using var services = Services(server.BaseAddress); using var scope = services.CreateScope(); Bind(scope, "alice", bearer: false);
        var flow = FlowEditorTests.Definition();
        var saved = await scope.ServiceProvider.GetRequiredService<IFlowApiService>().SaveAsync(17, flow.Id, new(7, flow.Name, flow.Draft));
        Assert.Equal(8, saved.Revision); Assert.Equal(17, saved.TenantId); Assert.Equal(flow.Id, saved.Id);
        Assert.Single(server.Requests);
    }

    private static ServiceProvider Services(Uri address)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddHttpContextAccessor();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = address.AbsoluteUri }).Build();
        services.AddSingleton<IConfiguration>(configuration); services.AddScoped<OperatorApiCredentialState>();
        services.AddScoped<OperatorApiCredentialProvider>(); services.AddScoped<ITokenService>(_ => new NoTokenService()); services.AddTransient<TokenAuthorizationHandler>();
        // A global resilience policy would retry both writes unless the dedicated registration removes it.
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.FromMilliseconds(1)));
        services.AddFlowsApiClient(configuration);
        services.AddScoped<IHttpClientFactory, OperatorApiHttpClientFactory>(); services.AddScoped<IFlowApiService, FlowApiService>();
        return services.BuildServiceProvider(validateScopes: true);
    }
    private static void Bind(IServiceScope scope, string subject, bool bearer)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, subject), new("auth_mode", bearer ? "oidc" : "local")], bearer ? "oidc" : "local"));
        var state = scope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>(); state.BindPrincipal(principal);
        var credential = bearer ? new OperatorApiCredential(OperatorApiCredentialKind.Bearer, $"fixture-{subject}", DateTimeOffset.UtcNow.AddHours(1))
            : new OperatorApiCredential(OperatorApiCredentialKind.LocalSessionCookie, $"fixture-{subject}", CookieName: "NetRatel.Local");
        Assert.True(state.SetCredential(principal, credential));
    }
    private sealed class NoTokenService : ITokenService
    { public Task<string> GetValidAccessTokenAsync() => throw new InvalidOperationException("A circuit must not borrow another user's token or a system credential."); }
    private sealed record Request(string Method, string Path, string Body, string? Cookie, string? Authorization);
    private sealed class LoopbackServer(WebApplication application, ConcurrentQueue<Request> requests, Uri baseAddress) : IAsyncDisposable
    {
        public ConcurrentQueue<Request> Requests { get; } = requests;
        public Uri BaseAddress { get; } = baseAddress;
        public static async Task<LoopbackServer> StartAsync(string response)
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var application = builder.Build(); var requests = new ConcurrentQueue<Request>();
            application.Run(async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                requests.Enqueue(new(context.Request.Method, context.Request.Path.Value!, body,
                    context.Request.Headers.Cookie.FirstOrDefault(), context.Request.Headers.Authorization.FirstOrDefault()));
                if (response == "lost-response") { context.Abort(); return; }
                if (response is "302" or "307")
                {
                    context.Response.StatusCode = int.Parse(response, System.Globalization.CultureInfo.InvariantCulture);
                    context.Response.Headers.Location = $"http://{context.Request.Host}/redirected-write";
                    await context.Response.WriteAsJsonAsync(new { code = "redirected" }); return;
                }
                if (response == "unavailable") { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { code = "unavailable" }); return; }
                context.Response.Headers.SetCookie = "ApiIssued=must-not-be-shared; Path=/";
                await context.Response.WriteAsJsonAsync(FlowEditorTests.Definition() with { Revision = 8 });
            });
            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(application, requests, new Uri(address + "/"));
        }
        public async ValueTask DisposeAsync() { await application.StopAsync(); await application.DisposeAsync(); }
    }
}
