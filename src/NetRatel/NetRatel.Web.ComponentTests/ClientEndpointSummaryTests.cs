using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Client;
using NetRatel.Web.Components.Pages.Clients.ClientsMgmt;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientEndpointSummaryTests : AsyncBunitContext
{
    public ClientEndpointSummaryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
    }

    [Fact]
    public void SharedGatewayShowsTheEffectiveOriginAndItsDerivation()
    {
        var cut = Render<ClientEndpointSummary>(parameters => parameters.Add(component => component.Endpoints,
            new("https://web.example.invalid", "https://api.example.invalid", "https://api.example.invalid",
                "branding-site-url:administrator", "client-artifacts-public-base-url", "shared-api-origin")));

        cut.FindAll("code").Select(element => element.TextContent).Should().Equal(
            "https://web.example.invalid", "https://api.example.invalid", "https://api.example.invalid");
        cut.Markup.Should().Contain("Branding Site URL");
        cut.Markup.Should().Contain("ClientArtifacts:PublicBaseUrl override");
        cut.Markup.Should().Contain("Derived from the API origin");
    }

    [Fact]
    public void BrandingGatewayShowsItsStoredSource()
    {
        var cut = Render<ClientEndpointSummary>(parameters => parameters.Add(component => component.Endpoints,
            new("https://web.example.test", "https://web.example.test", "https://gateway.example.test",
                "branding-site-url:administrator", "branding-site-url", "branding-gateway-url:administrator")));
        cut.Markup.Should().Contain("Branding Gateway URL · administrator");
        cut.FindAll("code")[2].TextContent.Should().Be("https://gateway.example.test");
    }

    [Fact]
    public void HistoricalUnknownsAreShownWithoutInventingAnEffectiveGateway()
    {
        var cut = Render<ClientEndpointSummary>(parameters => parameters.Add(component => component.Endpoints,
            new("https://old-web.example.invalid", "https://old-api.example.invalid", null, null, null, null)));

        cut.FindAll("code")[2].TextContent.Should().Be("Not recorded in this older link");
        cut.FindAll("small").Should().HaveCount(3);
        cut.FindAll("small").Should().OnlyContain(element => element.TextContent == "Source not recorded in this older link");
    }
}
