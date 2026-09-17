using System.Net;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Pages.Clients;
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
            cut.Markup.Should().NotContain("legacy-client-should-never-render");
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
            cut.Markup.Should().NotContain("legacy-client-should-never-render");
        });

        cut.Find("[aria-label='Show offline clients']").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".client-card").Should().ContainSingle();
            cut.Markup.Should().Contain("legacy-client-should-never-render");
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
            builder.AddAttribute(3, nameof(Router.NotFound), (RenderFragment)(childBuilder => childBuilder.AddContent(0, "Not found")));
            builder.CloseComponent();
        });
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public List<string> RequestedPaths { get; } = [];
        public HttpStatusCode? TelemetryFailureStatus { get; set; }

        public HttpClient CreateClient(string name) => new(new StubHandler(this))
        {
            BaseAddress = new Uri("https://netratel.test")
        };
    }

    private sealed class StubHandler(StubHttpClientFactory factory) : HttpMessageHandler
    {
        private static readonly Guid AgentId = ClientsPageTests.AgentId;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            factory.RequestedPaths.Add(path);
            if (path == "/api/v2/agent-telemetry" && factory.TelemetryFailureStatus is { } telemetryFailureStatus)
            {
                return Task.FromResult(new HttpResponseMessage(telemetryFailureStatus));
            }

            object payload = path switch
            {
                "/api/v2/client-presence" => new ClientPresenceListDto("AkkaDevCanary", 12,
                [
                    new ClientPresenceDto($"gateway:3:{AgentId:D}", 3, AgentId, "gateway-agent-01", "gateway-agent-01", "Linux", "x64", true, true,
                        DateTimeOffset.UtcNow, "0.4.101", ["terminal-gateway", "file-gateway", "remote-support-gateway"], "gateway", "akka-dev-canary", true, 12,
                        new GatewayTerminalCapabilityDto(true, ["bash", "sh"], true, null, DateTimeOffset.UtcNow), "NetRatel"),
                    new ClientPresenceDto("gateway:3:legacy", 3, Guid.Parse("99f5a0b0-5e61-4039-8d09-6c9d44c7c100"), "legacy-client-should-never-render", null, "Linux", "x64", false, true,
                        DateTimeOffset.UtcNow, "0.4.101", [], "gateway", "akka-dev-canary", true, 12, null, "NetRatel")
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions))
            });
        }
    }

    private static readonly Guid AgentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");
}
