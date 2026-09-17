using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Clients;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class GatewayLogLiveStreamServiceTests : IAsyncLifetime
{
    private WebApplication? _application;
    private Uri? _baseAddress;

    [Fact]
    public async Task SubscribeAsync_PassesCancellationOutsideHubArgumentsAndDeliversInitialAndFollowUpBatches()
    {
        var application = _application ?? throw new InvalidOperationException("Test server is not initialized.");
        var baseAddress = _baseAddress ?? throw new InvalidOperationException("Test server address is not initialized.");
        var expectedInitial = CreatePage("initial");
        TestOperationsHub.InitialPage = expectedInitial;
        var received = new TaskCompletionSource<GatewayLogBatchDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new GatewayLogLiveStreamService(
            new StaticHttpClientFactory(baseAddress),
            new StaticTokenService(),
            NullLogger<GatewayLogLiveStreamService>.Instance);

        await using var subscription = await service.SubscribeAsync(
            9,
            AgentId,
            "netratel-runtime",
            batch =>
            {
                received.TrySetResult(batch);
                return Task.CompletedTask;
            });

        subscription.InitialPage.Should().BeEquivalentTo(expectedInitial);

        var expectedBatch = new GatewayLogBatchDto(
            9,
            AgentId,
            "netratel-runtime",
            CreatePage("follow-up").Records,
            null,
            0,
            false);
        var hub = application.Services.GetRequiredService<IHubContext<TestOperationsHub>>();
        await hub.Clients.All.SendAsync("LogBatch", expectedBatch);

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeEquivalentTo(expectedBatch);
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        _application = builder.Build();
        _application.MapHub<TestOperationsHub>("/hubs/operations");
        await _application.StartAsync();
        _baseAddress = new Uri(_application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    public async Task DisposeAsync()
    {
        if (_application is null) return;
        await _application.StopAsync();
        await _application.DisposeAsync();
    }

    private static readonly Guid AgentId = Guid.Parse("51dd14ef-45a1-410b-97ff-cc0239aa1b30");

    private static GatewayLogPageDto CreatePage(string cursor) => new(
        [new GatewayLogRecordDto(cursor, 1, DateTimeOffset.UtcNow, "Information", "netratel-runtime", "Agent", "Gateway", null, null, null, null, cursor, null, false)],
        cursor,
        null,
        false,
        0,
        false);

    private sealed class StaticHttpClientFactory(Uri baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = baseAddress };
    }

    private sealed class StaticTokenService : ITokenService
    {
        public Task<string> GetValidAccessTokenAsync() => Task.FromResult("test-token");
    }

    public sealed class TestOperationsHub : Hub
    {
        public static GatewayLogPageDto InitialPage { get; set; } = CreatePage("initial");

        public Task<GatewayLogPageDto> SubscribeLogs(int tenantId, string agentId, string sourceId, string? cursor, GatewayLogQueryFilters? filters) =>
            Task.FromResult(InitialPage);

        public Task UnsubscribeLogs(int tenantId, string agentId, string sourceId) => Task.CompletedTask;
    }
}
