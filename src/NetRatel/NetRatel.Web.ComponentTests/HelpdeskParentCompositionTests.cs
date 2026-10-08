using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using NetRatel.Web.Components.Pages;
using NetRatel.Web.Components.Shared.ServiceLinks;
using NetRatel.Web.Services.ServiceLinks;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class HelpdeskParentCompositionTests : AsyncBunitContext
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("https://desk.example.test", "organization-exact-fixture", "/flows?flowId=7")]
    public void Actual_parent_preserves_optional_presets_in_the_native_start_form(string? peer, string? organization, string? returnUrl)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddScoped<HelpdeskM2MApiClient>();
        Services.AddSingleton<IHttpClientFactory, CompositionTransport>();
        ComponentFactories.AddStub<AntiforgeryToken>();
        ComponentFactories.AddStub<HelpdeskConnectionsPanel>();
        var dialogs = Render<MudDialogProvider>();
        var parent = Render<IntegrationCredentials>();
        // Set the real parent's query-supplied values; never supply child parameters.
        typeof(IntegrationCredentials).GetProperty(nameof(IntegrationCredentials.PresetPeerUrl))!.SetValue(parent.Instance, peer);
        typeof(IntegrationCredentials).GetProperty(nameof(IntegrationCredentials.PresetPeerOrganizationId))!.SetValue(parent.Instance, organization);
        typeof(IntegrationCredentials).GetProperty(nameof(IntegrationCredentials.ReturnUrl))!.SetValue(parent.Instance, returnUrl);
        typeof(IntegrationCredentials).GetProperty(nameof(IntegrationCredentials.PresetTenantId))!.SetValue(parent.Instance, 7);
        parent.WaitForAssertion(() => Assert.False(parent.Find("button").HasAttribute("disabled")));
        parent.FindAll("button").Single(button => button.TextContent.Trim() == "Connect RatelDesk").Click();

        dialogs.WaitForAssertion(() =>
        {
            var form = dialogs.Find("form[data-testid='helpdesk-link-start']");
            Assert.Equal("/account/integration-credentials/link/start", form.GetAttribute("action"));
            Assert.Equal(peer ?? "", Value("peerWebBaseUrl"));
            Assert.Equal(organization ?? "", Value("requestedResponderTenantId"));
            Assert.Equal(returnUrl ?? "", Value("returnUrl"));
            Assert.Equal("7", Value("localTenantId"));
            Assert.Equal(new[] { ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope },
                form.QuerySelectorAll("input[name='inboundScopes']").Select(input => input.GetAttribute("value")));
            Assert.Empty(form.QuerySelectorAll("input[name='resourceIds'],input[name='requestDefinitionIds']"));
            Assert.DoesNotContain("PresetPeerOrganizationId", form.OuterHtml);

            string Value(string name) => form.QuerySelector($"input[name='{name}']")!.GetAttribute("value") ?? "";
        });
    }

    private sealed class CompositionTransport : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object result = request.RequestUri!.AbsolutePath switch
            {
                "/api/v2/account/integration-credentials/authority" => new { tenants = Array.Empty<object>(), instancePermissions = Array.Empty<object>() },
                "/api/v2/account/service-clients/authority" => new ServiceClientManagementAuthority(true,
                    [new(7, "Fixture tenant", [], [], [])], true),
                "/api/v2/account/service-clients/settings" => new ServicePublicSettingsResponse(true, "https://web.example.test",
                    "https://api.example.test", "https://issuer.example.test", "fixture-api", "local", null, 1, [], true),
                "/api/v1/admin/service-links/identity" => new ServiceLinkIdentityDto("local", null, 1),
                _ => Array.Empty<object>()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}
