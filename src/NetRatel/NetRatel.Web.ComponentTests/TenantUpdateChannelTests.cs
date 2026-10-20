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
        var transport = new TenantTransport(originalChannel, "0.4.103");
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
        dialogs.Find("[data-testid='tenant-target-version']").TextContent.Should().Contain("0.4.103");
        await dialogs.InvokeAsync(() => channel.Instance.ValueChanged.InvokeAsync(selectedChannel));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").Click());

        page.WaitForAssertion(() => transport.Updated.Should().NotBeNull());
        transport.Updated.Should().BeEquivalentTo(new UpdateTenantRequest
        {
            Name = "Canary", Description = "Existing description", Location = "Existing location",
            ContactPerson = "Existing contact", ContactEmail = "contact@example.invalid", Domains = ["example.invalid"],
            AutoUpdate = true, AutoUpdateChannel = selectedChannel, AutoUpdateTargetVersion = "0.4.103", ExpectedVersion = 4
        });
        var reloaded = await Services.GetRequiredService<ITenantApiService>().GetTenantsAsync();
        reloaded.Single().AutoUpdateChannel.Should().Be(selectedChannel);
        reloaded.Single().AutoUpdateTargetVersion.Should().Be("0.4.103");
    }

    [Theory]
    [InlineData(null, "1.2.3", true, "1.2.3")]
    [InlineData("1.2.3", "1.2.4", true, "1.2.4")]
    [InlineData("1.2.3", null, false, null)]
    public async Task Tenant_target_can_be_set_changed_and_cleared_through_the_actual_page_request(
        string? originalPin, string? enteredPin, bool pinEnabled, string? expectedPin)
    {
        var transport = new TenantTransport("stable", originalPin);
        Services.AddSingleton<IHttpClientFactory>(transport);
        Services.AddSingleton<ITenantApiService, TenantApiService>();
        var popovers = Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var page = Render<TenantsPage>();
        page.WaitForAssertion(() => page.Find("button[aria-label='Manage Canary']").Should().NotBeNull());
        await page.InvokeAsync(() => page.Find("button[aria-label='Manage Canary']").Click());
        await popovers.InvokeAsync(() => popovers.FindAll("[role='menuitem']")
            .Single(item => item.TextContent.Trim() == "Edit Tenant").Click());
        dialogs.WaitForAssertion(() => dialogs.FindComponent<TenantEditorDialog>().Should().NotBeNull());
        var policy = dialogs.FindComponent<MudSelect<bool>>();
        policy.Instance.Value.Should().Be(originalPin is not null);
        await dialogs.InvokeAsync(() => policy.Instance.ValueChanged.InvokeAsync(pinEnabled));
        if (pinEnabled)
            await dialogs.InvokeAsync(() => dialogs.Find("[data-testid='tenant-target-version-input'] input").Input(enteredPin));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").Click());

        page.WaitForAssertion(() => transport.Updated.Should().NotBeNull());
        transport.Updated!.AutoUpdateTargetVersion.Should().Be(expectedPin ?? string.Empty);
        transport.Updated.ExpectedVersion.Should().Be(4);
        transport.Updated.Description.Should().Be("Existing description");
        var reloaded = (await Services.GetRequiredService<ITenantApiService>().GetTenantsAsync()).Single();
        reloaded.AutoUpdateTargetVersion.Should().Be(expectedPin);
        reloaded.Version.Should().Be(5);
        page.WaitForAssertion(() => page.Find("[data-testid='tenant-update-policy-summary']").TextContent
            .Should().Contain(expectedPin is null ? "No pin" : $"Pinned: {expectedPin}"));

        await page.InvokeAsync(() => page.Find("button[aria-label='Manage Canary']").Click());
        popovers.WaitForAssertion(() => popovers.FindAll("[role='menuitem']").Should().NotBeEmpty());
        await popovers.InvokeAsync(() => popovers.FindAll("[role='menuitem']")
            .Single(item => item.TextContent.Trim() == "Edit Tenant").Click());
        dialogs.WaitForAssertion(() => dialogs.FindComponent<MudSelect<bool>>().Instance.Value.Should().Be(pinEnabled));
        dialogs.Find("[data-testid='tenant-effective-update-policy']").TextContent
            .Should().Contain(expectedPin is null ? "No pin / follow selected channel" : expectedPin);
    }

    [Theory]
    [InlineData("latest", "semantic version")]
    [InlineData("1.2.3-beta.1", "prerelease channel")]
    public async Task New_pin_validation_keeps_invalid_or_channel_excluded_selections_in_the_editor(string target, string error)
    {
        var dialogs = Render<MudDialogProvider>();
        await Services.GetRequiredService<IDialogService>().ShowAsync<TenantEditorDialog>("Add Tenant");
        dialogs.WaitForAssertion(() => dialogs.FindComponent<TenantEditorDialog>().Should().NotBeNull());
        await dialogs.InvokeAsync(() => dialogs.Find("input").Input("New tenant"));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<MudSelect<bool>>().Instance.ValueChanged.InvokeAsync(true));
        await dialogs.InvokeAsync(() => dialogs.Find("[data-testid='tenant-target-version-input'] input").Input(target));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Create Tenant").Click());
        dialogs.WaitForAssertion(() => dialogs.FindComponent<TenantEditorDialog>().Markup.Should().Contain(error));
        dialogs.FindComponent<MudForm>().Instance.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Changing_channel_rejects_an_excluded_existing_pin_until_it_is_cleared()
    {
        var dialogs = Render<MudDialogProvider>();
        var tenant = new TenantDto(7, "Canary", null, null, [], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { AutoUpdateChannel = "prerelease", AutoUpdateTargetVersion = "1.2.3-beta.1" };
        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<TenantEditorDialog>("Edit Tenant", new DialogParameters
        {
            [nameof(TenantEditorDialog.Tenant)] = tenant,
            [nameof(TenantEditorDialog.IsEditMode)] = true
        });
        dialogs.WaitForAssertion(() => dialogs.FindComponent<TenantEditorDialog>().Should().NotBeNull());
        await dialogs.InvokeAsync(() => dialogs.FindComponents<MudSelect<string>>().Single(select => select.Instance.Label == "Release channel")
            .Instance.ValueChanged.InvokeAsync("stable"));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").Click());
        dialogs.WaitForAssertion(() => dialogs.FindComponent<MudForm>().Instance.IsValid.Should().BeFalse());
        dialogs.FindComponent<TenantEditorDialog>().Markup.Should().Contain("Select the prerelease channel");
        await dialogs.InvokeAsync(() => dialogs.FindComponent<MudSelect<bool>>().Instance.ValueChanged.InvokeAsync(false));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").Click());
        var result = (await reference.Result)!.Data.Should().BeOfType<CreateTenantRequest>().Which;
        result.AutoUpdateChannel.Should().Be("stable");
        result.AutoUpdateTargetVersion.Should().BeNull();
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

    private sealed class TenantTransport(string channel, string? pin = "0.4.103-rc.1") : HttpMessageHandler, IHttpClientFactory
    {
        private string _channel = channel;
        private string? _pin = pin;
        private long _version = 4;
        internal UpdateTenantRequest? Updated { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("https://netratel.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/v1/tenants/7")
            {
                Updated = await request.Content!.ReadFromJsonAsync<UpdateTenantRequest>(cancellationToken);
                _channel = Updated!.AutoUpdateChannel!;
                if (Updated.AutoUpdateTargetVersion is { } target)
                    _pin = string.IsNullOrWhiteSpace(target) ? null : target;
                _version++;
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
                    autoUpdate = true, autoUpdateChannel = _channel, autoUpdateTargetVersion = _pin, version = _version,
                    createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow
                } })
            };
        }
    }
}
