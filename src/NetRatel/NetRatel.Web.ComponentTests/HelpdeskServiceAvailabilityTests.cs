using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Web.Components.Shared.ServiceLinks;
using NetRatel.Web.Services.ServiceLinks;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class HelpdeskServiceAvailabilityTests : AsyncBunitContext
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Effective_issuer_and_reciprocal_availability_remain_separate_in_the_owner_panel(
        bool issuerEnabled, bool reciprocalEnabled)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new SettingsTransport(issuerEnabled, reciprocalEnabled);
        Services.AddSingleton<IHttpClientFactory>(transport);
        Services.AddScoped<HelpdeskM2MApiClient>();
        bool? manualReady = null, guidedReady = null;

        var panel = Render<ServicePublicSettingsPanel>(parameters => parameters
            .Add(component => component.ReadyChanged, (bool value) => manualReady = value)
            .Add(component => component.ReciprocalReadyChanged, (bool value) => guidedReady = value));

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(issuerEnabled, manualReady);
            Assert.Equal(issuerEnabled && reciprocalEnabled, guidedReady);
        });
        panel.Find(".mud-expand-panel-header").Click();
        panel.WaitForAssertion(() =>
        {
            var toggle = panel.FindComponent<MudCheckBox<bool>>().Instance;
            Assert.Equal("Enable service identity", toggle.Label);
            Assert.Equal(issuerEnabled, toggle.GetState(component => component.Value));
            Assert.True(toggle.Disabled);
            if (issuerEnabled && !reciprocalEnabled)
            {
                Assert.Contains("Reciprocal setup is disabled by current deployment settings.", panel.Find("[data-testid=helpdesk-reciprocal-unavailable]").TextContent);
                Assert.Contains("Manual service clients remain available", panel.Markup);
            }
            else
            {
                Assert.Empty(panel.FindAll("[data-testid=helpdesk-reciprocal-unavailable]"));
                Assert.Contains("Reciprocal setup: " + (reciprocalEnabled ? "Enabled" : "Disabled"), panel.Markup);
            }
        });
    }

    [Fact]
    public void Saving_an_editable_address_preserves_the_exact_locked_audience()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new SavingSettingsTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        Services.AddScoped<HelpdeskM2MApiClient>();

        var panel = Render<ServicePublicSettingsPanel>();
        panel.Find(".mud-expand-panel-header").Click();
        panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<MudTextField<string>>(),
            field => field.Instance.Label == "Canonical NetRatel Web base URL"));
        panel.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "Canonical NetRatel Web base URL")
            .Find("input").Input("https://new-web.example.test");
        panel.Find("[data-testid=helpdesk-save-public-settings]").Click();

        panel.WaitForAssertion(() =>
        {
            Assert.NotNull(transport.Submitted);
            Assert.Equal("https://new-web.example.test", transport.Submitted.WebBaseUrl);
            Assert.Equal(transport.Initial.Enabled, transport.Submitted.Enabled);
            Assert.Equal(transport.Initial.ApiBaseUrl, transport.Submitted.ApiBaseUrl);
            Assert.Equal(transport.Initial.Issuer, transport.Submitted.Issuer);
            Assert.Equal(" deployed-audience ", transport.Submitted.Audience);
            Assert.Equal(transport.Initial.Revision, transport.Submitted.ExpectedRevision);
            Assert.Contains("The service settings are saved.", panel.Markup);
        });
    }

    // Production panel/client, deterministic local admin response only. This
    // verifies presentation/readiness, not deployed issuer or peer provisioning.
    private sealed class SettingsTransport(bool issuerEnabled, bool reciprocalEnabled)
        : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("ServiceLinkApi", name);
            return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test/") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v2/account/service-clients/settings", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ServicePublicSettingsResponse(issuerEnabled,
                    "https://web.example.test", "https://api.example.test", "https://issuer.example.test",
                    "component-services", "5ba7f148-0e77-4fb5-98c9-dbf76c4a71bd", null, 4, ["enabled"], reciprocalEnabled))
            });
        }
    }

    private sealed class SavingSettingsTransport : HttpMessageHandler, IHttpClientFactory
    {
        public ServicePublicSettingsResponse Initial { get; } = new(true,
            "https://web.example.test", "https://api.example.test", "https://issuer.example.test",
            " deployed-audience ", "5ba7f148-0e77-4fb5-98c9-dbf76c4a71bd", null, 4,
            ["enabled", "apiBaseUrl", "issuer", "audience"], true);
        public ServicePublicSettingsUpdate? Submitted { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("ServiceLinkApi", name);
            return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test/") };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v2/account/service-clients/settings", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Initial) };
            Assert.Equal(HttpMethod.Put, request.Method);
            Submitted = await request.Content!.ReadFromJsonAsync<ServicePublicSettingsUpdate>(cancellationToken);
            Assert.NotNull(Submitted);
            if (Submitted.Audience != Initial.Audience)
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { code = "deployment-locked" }) };
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Initial with { WebBaseUrl = Submitted.WebBaseUrl, Revision = Initial.Revision + 1 })
            };
        }
    }
}
