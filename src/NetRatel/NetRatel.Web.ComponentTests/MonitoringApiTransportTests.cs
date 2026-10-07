using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Monitoring;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class MonitoringApiTransportTests
{
    [Theory]
    [InlineData("rule", false)]
    [InlineData("rule", true)]
    [InlineData("clear", false)]
    [InlineData("clear", true)]
    public async Task LostResponseAndUnavailableWritesAreNeverReplayed(string operation, bool unavailable)
    {
        var wire = new RecordingHandler(_ => unavailable
            ? new(HttpStatusCode.ServiceUnavailable)
            : throw new HttpRequestException("Response lost after the server accepted the write"));
        using var services = Services(wire); using var scope = services.CreateScope();
        Bind(scope, "alice", "fixture-alice");
        var api = scope.ServiceProvider.GetRequiredService<IMonitoringApiService>();
        var rule = Rule();
        await Assert.ThrowsAsync<HttpRequestException>(() => operation == "rule"
            ? api.SaveRuleAsync(1, new(rule, 1, "explicit rule configuration"))
            : api.ClearAsync(1, Series(rule), "validated manually"));
        var request = Assert.Single(wire.Requests);
        Assert.Equal(operation == "rule" ? HttpMethod.Put : HttpMethod.Post, request.Method);
        Assert.Contains(operation == "rule" ? "/rules/" : "/clear?resourceKey=cpu", request.Uri);
    }

    [Fact]
    public async Task RedirectedWriteIsNotReissuedWithOperatorCredentials()
    {
        var wire = new RecordingHandler(_ => new(HttpStatusCode.TemporaryRedirect)
        { Headers = { Location = new("https://other.example/redirected-write") } });
        using var services = Services(wire); using var scope = services.CreateScope();
        Bind(scope, "alice", "fixture-alice");
        await Assert.ThrowsAsync<HttpRequestException>(() => scope.ServiceProvider.GetRequiredService<IMonitoringApiService>()
            .SaveRuleAsync(1, new(Rule(), 1, "explicit rule configuration")));
        var request = Assert.Single(wire.Requests);
        Assert.Equal("NetRatel.Local=fixture-alice", request.Cookie);
        Assert.StartsWith("https://fixture.example/", request.Uri);
    }

    [Fact]
    public async Task SavedResponseRetainsPendingStatusAndExactCommittedRevision()
    {
        var saved = new MonitoringConfigurationDto(1, 2, [], [], [], DateTimeOffset.UtcNow, WatchPolicyUpdatePending: true);
        var wire = new RecordingHandler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(saved) });
        using var services = Services(wire); using var scope = services.CreateScope();
        Bind(scope, "alice", "fixture-alice");
        var response = await scope.ServiceProvider.GetRequiredService<IMonitoringApiService>()
            .SaveRuleAsync(1, new(Rule(), 1, "explicit configuration"));
        Assert.Equal((ulong)2, response.Revision); Assert.Equal(1, response.TenantId); Assert.True(response.WatchPolicyUpdatePending);
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task PooledMonitoringPipelineKeepsDistinctCircuitCredentialsAndRejectsAnonymousScope()
    {
        var wire = new RecordingHandler(_ => new(HttpStatusCode.OK)
        { Content = JsonContent.Create(new MonitoringConfigurationDto(1, 1, [], [], [], DateTimeOffset.UtcNow)) });
        using var services = Services(wire);
        using var alice = services.CreateScope(); using var bob = services.CreateScope(); using var anonymous = services.CreateScope();
        Bind(alice, "alice", "fixture-alice"); Bind(bob, "bob", "fixture-bob");
        var aliceApi = alice.ServiceProvider.GetRequiredService<IMonitoringApiService>();
        var bobApi = bob.ServiceProvider.GetRequiredService<IMonitoringApiService>();
        await Task.WhenAll(aliceApi.GetConfigurationAsync(1), bobApi.GetConfigurationAsync(1));
        await aliceApi.GetConfigurationAsync(1);
        await Assert.ThrowsAsync<ReauthRequiredException>(() => anonymous.ServiceProvider.GetRequiredService<IMonitoringApiService>().GetConfigurationAsync(1));
        Assert.Equal(3, wire.Requests.Count);
        Assert.Equal(2, wire.Requests.Count(request => request.Cookie == "NetRatel.Local=fixture-alice"));
        Assert.Single(wire.Requests, request => request.Cookie == "NetRatel.Local=fixture-bob");
        Assert.All(wire.Requests, request => Assert.Null(request.Authorization));
    }

    private static ServiceProvider Services(RecordingHandler wire)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddHttpContextAccessor();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "https://fixture.example/" }).Build();
        services.AddSingleton<IConfiguration>(configuration); services.AddScoped<OperatorApiCredentialState>();
        services.AddScoped<OperatorApiCredentialProvider>(); services.AddScoped<ITokenService>(_ => new NoTokenService());
        services.AddTransient<TokenAuthorizationHandler>();
        // Exercise the same global default that Monitoring must explicitly remove.
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());
        services.AddMonitoringApiClient(configuration);
        services.AddHttpClient(MonitoringApiService.ClientName).ConfigurePrimaryHttpMessageHandler(() => wire);
        services.AddScoped<IHttpClientFactory, OperatorApiHttpClientFactory>();
        services.AddScoped<IMonitoringApiService, MonitoringApiService>();
        return services.BuildServiceProvider(validateScopes: true);
    }
    private static void Bind(IServiceScope scope, string subject, string cookie)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, subject), new("auth_mode", "local")], "local"));
        var state = scope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>(); state.BindPrincipal(principal);
        Assert.True(state.SetCredential(principal, new(OperatorApiCredentialKind.LocalSessionCookie, cookie, CookieName: "NetRatel.Local")));
    }
    private static MonitoringRuleDto Rule()
    {
        var draft = MonitoringRuleDraft.Create(); draft.Name = "CPU"; draft.AgentIds.Add(Guid.NewGuid()); return draft.Build(1);
    }
    private static MonitoringSeriesState Series(MonitoringRuleDto rule)
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = new MonitoringEvidenceDto(new(1, 1), Guid.NewGuid(), now, now, MonitoringEvidenceQuality.Fresh, MonitoringClassification.Breach, 95, 0, null);
        return new(new(1, rule.RuleId, rule.Targets.AgentIds[0], "cpu"), 1, 1, MonitoringPhase.Firing, MonitoringEvidenceQuality.Fresh,
            Occurrence: new(Guid.NewGuid(), Guid.NewGuid(), now, now.AddMinutes(-1), rule, evidence, MonitoringFlowDispatchDisposition.NoFlowSelected));
    }
    private sealed class NoTokenService : ITokenService
    {
        public Task<string> GetValidAccessTokenAsync() => throw new InvalidOperationException("A local circuit must not borrow any bearer or system credential.");
    }
    private sealed record Request(HttpMethod Method, string Uri, string? Cookie, string? Authorization);
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<Request> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}
