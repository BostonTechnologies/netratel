using System.Net;
using System.Net.Http.Json;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Components.Pages.Tenants;
using NetRatel.Web.Services.Tenants;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class TenantUpdateChannelTests : AsyncBunitContext
{
    public TenantUpdateChannelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
    }

    [Theory]
    [InlineData("stable", "prerelease")]
    [InlineData("prerelease", "stable")]
    public async Task Tenant_api_channel_and_pin_roundtrip_through_editor_and_page_update(string originalChannel, string selectedChannel)
    {
        var transport = new TenantTransport(originalChannel);
        Services.AddSingleton<IHttpClientFactory>(transport);
        Services.AddSingleton<ITenantApiService, TenantApiService>();
        var popovers = Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var page = Render<TenantsPage>();
        page.WaitForAssertion(() => page.Find("button[aria-label='Manage Canary']").Should().NotBeNull());
        await page.InvokeAsync(() => page.Find("button[aria-label='Manage Canary']").Click());
        popovers.WaitForAssertion(() => popovers.FindAll("[role='menuitem']").Should().NotBeEmpty());
        await popovers.InvokeAsync(() => popovers.FindAll("[role='menuitem']")
            .Single(item => item.TextContent.Trim() == "Edit Tenant").Click());
        dialogs.WaitForAssertion(() => dialogs.FindComponent<TenantEditorDialog>().Should().NotBeNull());
        var channel = dialogs.FindComponents<MudSelect<string>>().Single(select => select.Instance.Label == "Release channel");
        channel.Instance.Value.Should().Be(originalChannel);
        dialogs.Find("[data-testid='tenant-target-version']").TextContent.Should().Contain("0.4.103-rc.1");
        await dialogs.InvokeAsync(() => channel.Instance.ValueChanged.InvokeAsync(selectedChannel));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").Click());

        page.WaitForAssertion(() => transport.Updated.Should().NotBeNull());
        transport.Updated.Should().BeEquivalentTo(new UpdateTenantRequest
        {
            Name = "Canary", Description = "Existing description", Location = "Existing location",
            ContactPerson = "Existing contact", ContactEmail = "contact@example.invalid", Domains = ["example.invalid"],
            AutoUpdate = true, AutoUpdateChannel = selectedChannel, AutoUpdateTargetVersion = "0.4.103-rc.1"
        });
        var reloaded = await Services.GetRequiredService<ITenantApiService>().GetTenantsAsync();
        reloaded.Single().AutoUpdateChannel.Should().Be(selectedChannel);
        reloaded.Single().AutoUpdateTargetVersion.Should().Be("0.4.103-rc.1");
    }

    [Fact]
    public async Task New_tenant_editor_keeps_stable_only_as_the_default()
    {
        var dialogs = Render<MudDialogProvider>();
        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<TenantEditorDialog>("Add Tenant");
        dialogs.WaitForAssertion(() => dialogs.FindComponent<TenantEditorDialog>().Should().NotBeNull());
        dialogs.FindComponents<MudSelect<string>>().Single(select => select.Instance.Label == "Release channel")
            .Instance.Value.Should().Be("stable");
        await dialogs.InvokeAsync(() => dialogs.Find("input").Input("New tenant"));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Create Tenant").Click());
        var result = await reference.Result;
        var created = result!.Data.Should().BeOfType<CreateTenantRequest>().Which;
        created.AutoUpdateChannel.Should().Be("stable");
        created.AutoUpdateTargetVersion.Should().BeNull();
    }

    private sealed class TenantTransport(string channel) : HttpMessageHandler, IHttpClientFactory
    {
        private string _channel = channel;
        internal UpdateTenantRequest? Updated { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("https://netratel.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/v1/tenants/7")
            {
                Updated = await request.Content!.ReadFromJsonAsync<UpdateTenantRequest>(cancellationToken);
                _channel = Updated!.AutoUpdateChannel!;
                return new(HttpStatusCode.Accepted);
            }
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be("/api/v1/tenants");
            // API response fields must survive the actual TenantDto JSON boundary before the editor sees them.
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new
                {
                    tenantId = 7, name = "Canary", description = "Existing description", location = "Existing location",
                    domains = new[] { "example.invalid" }, contactPerson = "Existing contact", contactEmail = "contact@example.invalid",
                    autoUpdate = true, autoUpdateChannel = _channel, autoUpdateTargetVersion = "0.4.103-rc.1",
                    createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow
                } })
            };
        }
    }
}
