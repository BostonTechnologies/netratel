using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Pages.Clients;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Services.Telemetry;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientsPageTests : AsyncBunitContext
{
    public ClientsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IHttpClientFactory, StubHttpClientFactory>();
        Services.AddScoped<ClientPresenceApiService>();
        Services.AddScoped<GatewayTelemetryApiService>();
        Services.AddScoped<ClientPresentationService>();
        Services.AddScoped<GatewayClientActionApiService>();
    }

    [Fact]
    public void ClientsPage_Uses_Only_V2_Records_And_Applies_Search()
    {
        var cut = RenderClientsRoute(search: "gateway-agent-01");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("gateway-agent-01");
            cut.Markup.Should().NotContain("registered-offline-agent");
            cut.FindAll(".client-grid-view").Should().BeEmpty();
            cut.FindAll(".client-card").Should().ContainSingle();
            cut.Find(".client-meta-grid").TextContent.Should().Contain("Tenant");
            cut.Find(".telemetry-split-grid").TextContent.Should().Contain("37.5%");
            cut.Find(".telemetry-split-grid").TextContent.Should().Contain("61.0%");
        });
    }

    [Fact]
    public void ClientsPage_Refreshes_Only_V2_Routes()
    {
        var factory = Services.GetRequiredService<IHttpClientFactory>();
        var cut = RenderClientsRoute();

        cut.WaitForAssertion(() => cut.Find("button[aria-label='Refresh clients']").Should().NotBeNull());
        ((StubHttpClientFactory)factory).RequestedPaths.Should().OnlyContain(path => path.StartsWith("/api/v2/", StringComparison.Ordinal));
    }

    [Fact]
    public void ClientsPage_Renders_Table_When_View_Query_Is_Table()
    {
        var cut = RenderClientsRoute(view: "table");

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".client-grid-view").Should().ContainSingle();
            cut.Markup.Should().Contain("gateway-agent-01");
            cut.Markup.Should().NotContain(AgentId.ToString("D"));
        });
    }

    [Fact]
    public void ClientsPage_Status_Filters_Use_The_Same_V2_Presentation_Set()
    {
        var cut = RenderClientsRoute();

        cut.WaitForAssertion(() => cut.FindAll(".client-card").Should().HaveCount(2));
        cut.Find("[aria-label='Show online clients']").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".client-card").Should().ContainSingle();
            cut.Markup.Should().Contain("gateway-agent-01");
            cut.Markup.Should().NotContain("registered-offline-agent");
        });

        cut.Find("[aria-label='Show offline clients']").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".client-card").Should().ContainSingle();
            cut.Markup.Should().Contain("registered-offline-agent");
        });
    }

    [Fact]
    public void ClientsPage_Renders_Authoritative_Directory_When_Telemetry_Enrichment_Fails()
    {
        var factory = (StubHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>();
        factory.TelemetryFailureStatus = HttpStatusCode.BadGateway;

        var cut = RenderClientsRoute();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("gateway-agent-01");
            cut.Markup.Should().NotContain("The authoritative client directory is unavailable");
        });
    }

    [Fact]
    public void ClientsPage_Distinguishes_A_Successful_Empty_Directory()
    {
        var factory = (StubHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>();
        factory.EmptyDirectory = true;

        var cut = RenderClientsRoute();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No registered clients yet.");
            cut.Markup.Should().NotContain("No clients match the active filters.");
        });
    }

    [Fact]
    public void ClientsPage_Distinguishes_Filtered_No_Results_From_An_Empty_Directory()
    {
        var cut = RenderClientsRoute(search: "not-registered");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No clients match the active filters.");
            cut.Markup.Should().NotContain("No registered clients yet.");
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Sign in again")]
    [InlineData(HttpStatusCode.Forbidden, "does not have permission")]
    [InlineData(HttpStatusCode.NotFound, "does not expose the client directory route")]
    [InlineData(HttpStatusCode.GatewayTimeout, "did not respond in time")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "could not load the client directory")]
    public void ClientsPage_Reports_Actionable_Directory_Failures_And_Retry_Works(HttpStatusCode statusCode, string expectedMessage)
    {
        var factory = (StubHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>();
        factory.DirectoryFailureStatus = statusCode;

        var cut = RenderClientsRoute();
        cut.WaitForAssertion(() =>
        {
            cut.Find("[role='alert']").TextContent.Should().Contain(expectedMessage);
            cut.Find("[data-testid='retry-client-directory']").Should().NotBeNull();
        });

        factory.DirectoryFailureStatus = null;
        cut.Find("[data-testid='retry-client-directory']").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("gateway-agent-01"));
    }

    [Fact]
    public void Optional_Telemetry_Timeout_Does_Not_Erase_The_Directory()
    {
        var factory = (StubHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>();
        factory.TelemetryTimesOut = true;

        var cut = RenderClientsRoute();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("gateway-agent-01");
            cut.Markup.Should().NotContain("could not load the client directory");
            cut.FindAll(".client-card").Should().HaveCount(2);
            cut.Markup.Should().NotContain("37.5%");
            cut.Markup.Should().NotContain("61.0%");
        });
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"tenantId\":3,\"agentId\":\"4886c6c0-e486-4f59-a006-40e4043a4a46\",\"disks\":null,\"networks\":null}]")]
    public void Malformed_Optional_Telemetry_Does_Not_Erase_The_Directory(string responseJson)
    {
        var factory = (StubHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>();
        factory.TelemetryResponseJson = responseJson;

        var cut = RenderClientsRoute();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("gateway-agent-01");
            cut.Markup.Should().Contain("registered-offline-agent");
            cut.Markup.Should().NotContain("could not load the client directory");
            cut.FindAll(".client-card").Should().HaveCount(2);
            cut.Markup.Should().NotContain("37.5%");
            cut.Markup.Should().NotContain("61.0%");
        });
    }

    [Fact]
    public async Task Optional_Telemetry_Preserves_Caller_Cancellation()
    {
        var factory = (StubHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>();
        factory.BlockTelemetryRequest = true;
        using var cancellation = new CancellationTokenSource();
        var load = Services.GetRequiredService<ClientPresentationService>().GetClientsAsync(cancellation.Token);

        await factory.TelemetryRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    [Fact]
    public async Task Real_Orchestrator_Auth_Handler_Maps_Missing_Operator_Session_To_Actionable_Authentication_Failure()
    {
        var tokenAuthorization = new TokenAuthorizationHandler(
            NullLogger<TokenAuthorizationHandler>.Instance)
        {
            InnerHandler = new UnreachableApiHandler()
        };
        var redirectReissue = new RedirectReissueHandler { InnerHandler = tokenAuthorization };
        using var httpClient = new HttpClient(redirectReissue) { BaseAddress = new Uri("https://netratel.test") };
        var clientFactory = new SingleHttpClientFactory(httpClient);
        var presentation = new ClientPresentationService(
            new ClientPresenceApiService(clientFactory),
            new GatewayTelemetryApiService(clientFactory),
            NullLogger<ClientPresentationService>.Instance);

        var result = await presentation.GetClientsAsync();

        result.IsAvailable.Should().BeFalse();
        result.Failure.Should().Be(new ClientPresentationFailure(ClientPresentationFailureKind.AuthenticationRequired, HttpStatusCode.Unauthorized));
        result.Clients.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mode\":\"Akka\",\"revision\":12,\"items\":null}")]
    [InlineData("{\"mode\":\"Akka\",\"revision\":12,\"items\":[null]}")]
    [InlineData("{\"mode\":\"Akka\",\"revision\":12,\"items\":[{}]}")]
    public async Task Malformed_directory_payload_is_reported_as_invalid_response(string json)
    {
        using var client = new HttpClient(new StaticResponseHandler(json))
        {
            BaseAddress = new Uri("https://netratel.test")
        };
        var clientFactory = new SingleHttpClientFactory(client);
        var presentation = new ClientPresentationService(
            new ClientPresenceApiService(clientFactory),
            new GatewayTelemetryApiService(clientFactory),
            NullLogger<ClientPresentationService>.Instance);

        var result = await presentation.GetClientsAsync();

        result.IsAvailable.Should().BeFalse();
        result.Clients.Should().BeEmpty();
        result.Failure.Should().Be(new ClientPresentationFailure(ClientPresentationFailureKind.InvalidResponse));
    }

    [Fact]
    public async Task Optional_telemetry_Reauthentication_preserves_a_successful_directory()
    {
        using var client = new HttpClient(new DirectoryThenReauthHandler())
        {
            BaseAddress = new Uri("https://netratel.test")
        };
        var clientFactory = new SingleHttpClientFactory(client);
        var presentation = new ClientPresentationService(
            new ClientPresenceApiService(clientFactory),
            new GatewayTelemetryApiService(clientFactory),
            NullLogger<ClientPresentationService>.Instance);

        var result = await presentation.GetClientsAsync();

        result.IsAvailable.Should().BeTrue();
        result.Clients.Should().ContainSingle(client => client.DisplayName == "registered-agent");
        result.Clients[0].Telemetry.Should().BeNull();
    }

    [Fact]
    public void ClientsPage_Preserves_Search_When_Switching_Views()
    {
        var cut = RenderClientsRoute(search: "gateway-agent-01");
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Switch client view']").Should().NotBeNull());

        cut.Find("button[aria-label='Switch client view']").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("search=gateway-agent-01");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("view=table");
    }

    private IRenderedComponent<IComponent> RenderClientsRoute(string? search = null, string? view = null)
    {
        var query = new List<string>();
        if (search is not null) query.Add($"search={Uri.EscapeDataString(search)}");
        if (view is not null) query.Add($"view={Uri.EscapeDataString(view)}");
        var uri = query.Count == 0 ? "/clients" : $"/clients?{string.Join('&', query)}";
        Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        return Render(builder =>
        {
            builder.OpenComponent<Router>(0);
            builder.AddAttribute(1, nameof(Router.AppAssembly), typeof(ClientsPage).Assembly);
            builder.AddAttribute(2, nameof(Router.Found), (RenderFragment<RouteData>)(routeData => childBuilder =>
            {
                childBuilder.OpenComponent<RouteView>(0);
                childBuilder.AddAttribute(1, nameof(RouteView.RouteData), routeData);
                childBuilder.CloseComponent();
            }));
            builder.CloseComponent();
        });
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public List<string> RequestedPaths { get; } = [];
        public HttpStatusCode? TelemetryFailureStatus { get; set; }
        public HttpStatusCode? DirectoryFailureStatus { get; set; }
        public bool EmptyDirectory { get; set; }
        public bool TelemetryTimesOut { get; set; }
        public bool BlockTelemetryRequest { get; set; }
        public string? TelemetryResponseJson { get; set; }
        public TaskCompletionSource TelemetryRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient CreateClient(string name) => new(new StubHandler(this))
        {
            BaseAddress = new Uri("https://netratel.test")
        };
    }

    private sealed class SingleHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class UnreachableApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The API handler must stop when the operator session needs reauthentication.");
    }

    private sealed class StaticResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class DirectoryThenReauthHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/v2/agent-telemetry")
            {
                throw new ReauthRequiredException("The operator session expired during optional enrichment.");
            }

            var agent = new ClientPresenceDto(
                "gateway:3:registered",
                3,
                AgentId,
                "registered-agent",
                "registered-agent",
                "Linux",
                "x64",
                false,
                true,
                DateTimeOffset.UtcNow,
                "0.4.101",
                [],
                "gateway",
                "akka",
                true,
                1);
            var directory = new ClientPresenceListDto("Akka", 1, [agent]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(directory, JsonOptions), System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHandler(StubHttpClientFactory factory) : HttpMessageHandler
    {
        private static readonly Guid AgentId = ClientsPageTests.AgentId;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            factory.RequestedPaths.Add(path);
            if (path == "/api/v2/client-presence" && factory.DirectoryFailureStatus is { } directoryFailureStatus)
            {
                return new HttpResponseMessage(directoryFailureStatus);
            }

            if (path == "/api/v2/agent-telemetry" && factory.TelemetryFailureStatus is { } telemetryFailureStatus)
            {
                return new HttpResponseMessage(telemetryFailureStatus);
            }

            if (path == "/api/v2/agent-telemetry" && factory.TelemetryTimesOut)
            {
                throw new TaskCanceledException("The optional telemetry request timed out.");
            }

            if (path == "/api/v2/agent-telemetry" && factory.TelemetryResponseJson is { } telemetryResponseJson)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(telemetryResponseJson, System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (path == "/api/v2/agent-telemetry" && factory.BlockTelemetryRequest)
            {
                factory.TelemetryRequestStarted.TrySetResult();
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            }

            object payload = path switch
            {
                "/api/v2/client-presence" when factory.EmptyDirectory => new ClientPresenceListDto("Akka", 12, []),
                "/api/v2/client-presence" => new ClientPresenceListDto("Akka", 12,
                [
                    new ClientPresenceDto($"gateway:3:{AgentId:D}", 3, AgentId, "gateway-agent-01", "gateway-agent-01", "Linux", "x64", true, true,
                        DateTimeOffset.UtcNow, "0.4.101", ["terminal-gateway", "file-gateway", "remote-support-gateway"], "gateway", "akka", true, 12,
                        new GatewayTerminalCapabilityDto(true, ["bash", "sh"], true, null, DateTimeOffset.UtcNow), "NetRatel"),
                    new ClientPresenceDto("gateway:3:offline", 3, Guid.Parse("99f5a0b0-5e61-4039-8d09-6c9d44c7c100"), "registered-offline-agent", null, "Linux", "x64", false, true,
                        null, null, [], "gateway", "unobserved", false, 12, null, "NetRatel")
                ]),
                "/api/v2/agent-telemetry" => new[]
                {
                    new GatewayTelemetrySummary(3, AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                        new GatewayTelemetryCpu(37.5, 0.25, 100), new GatewayTelemetryMemory(1024, 624, 400, 61),
                        [new GatewayTelemetryDisk("/", 80, 20, 60, 25)],
                        [new GatewayTelemetryNetwork("eth0", 1000, 500)],
                        new GatewayTelemetryTransportHealth(3600, "0.4.101", "Linux", DateTimeOffset.UtcNow), "gateway", true)
                },
                _ => Array.Empty<object>()
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions))
            };
        }
    }

    private static readonly Guid AgentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");
}
