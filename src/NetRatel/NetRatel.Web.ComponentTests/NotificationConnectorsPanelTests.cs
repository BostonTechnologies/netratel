using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Notifications;
using NetRatel.Web.Components.Shared.Notifications;
using NetRatel.Web.Services.Notifications;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class NotificationConnectorsPanelTests : AsyncBunitContext
{
    private readonly FakeNotifications _api = new();

    public NotificationConnectorsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<INotificationConnectorApiService>(_api);
    }

    [Fact]
    public void Saving_a_webhook_does_not_send_and_clears_the_secret_from_the_form()
    {
        var page = Render<NotificationConnectorsPanel>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='notification-card']")));
        page.Find("[data-testid='add-slack']").Click();
        page.Find("input[data-testid='notification-name']").Input("Operations channel");
        page.Find("input[data-testid='notification-webhook']").Input("https://hooks.slack.com/services/TEAM/CHANNEL/synthetic-secret");
        page.Find("[data-testid='notification-save']").Click();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid='notification-card']").Count));
        Assert.NotNull(_api.Saved);
        Assert.Equal(NotificationProvider.Slack, _api.Saved.Configuration.Provider);
        Assert.Equal("Operations channel", _api.Saved.Configuration.Name);
        Assert.Contains("synthetic-secret", _api.Saved.WebhookUrl);
        Assert.DoesNotContain("synthetic-secret", page.Markup);
        Assert.Empty(page.FindAll("[data-testid='notification-form']"));
        Assert.Equal(0, _api.Sends);
    }

    [Fact]
    public void Editing_preserves_write_only_webhook_and_test_requires_its_explicit_button()
    {
        var page = Render<NotificationConnectorsPanel>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='notification-card']")));
        Assert.Equal(0, _api.Sends);
        page.Find("[data-testid='notification-edit']").Click();
        Assert.True(string.IsNullOrEmpty(page.Find("input[data-testid='notification-webhook']").GetAttribute("value")));
        page.Find("input[data-testid='notification-name']").Input("Updated target");
        page.Find("[data-testid='notification-save']").Click();
        page.WaitForAssertion(() => Assert.NotNull(_api.Saved));
        Assert.Null(_api.Saved!.WebhookUrl);
        Assert.Equal(0, _api.Sends);
        page.Find("[data-testid='notification-test']").Click();
        page.WaitForAssertion(() => Assert.Contains("Test notification sent", page.Find("[data-testid='notification-notice']").TextContent));
        Assert.Equal(1, _api.Sends);
    }

    [Fact]
    public void Disabled_target_cannot_send_a_test()
    {
        _api.Targets[0] = _api.Targets[0] with { Configuration = _api.Targets[0].Configuration with { Enabled = false } };
        var page = Render<NotificationConnectorsPanel>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='notification-card']")));
        Assert.True(page.Find("[data-testid='notification-test']").HasAttribute("disabled"));
        Assert.Equal(0, _api.Sends);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(999, 0)]
    public void Flow_link_selects_only_an_authorized_tenant(int requestedTenant, int expectedTenant)
    {
        _api.Tenants.Add(new(2, "Branch site"));
        _api.Targets.Add(new(Guid.NewGuid(), 2, 1, new("Branch Slack", NotificationProvider.Slack, true), true));
        var page = Render<NotificationConnectorsPanel>(parameters => parameters.Add(component => component.InitialTenantId, requestedTenant));
        if (expectedTenant > 0) page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='notification-card']")));
        else
        {
            page.WaitForAssertion(() => Assert.Contains("linked tenant is unavailable", page.Find("[data-testid='notification-error']").TextContent));
            Assert.Empty(page.FindAll("[data-testid='notification-card']"));
            Assert.True(page.Find("[data-testid='add-slack']").HasAttribute("disabled"));
        }
        Assert.Equal(expectedTenant, _api.LastListedTenant);
        Assert.Equal(0, _api.Sends);
    }

    private sealed class FakeNotifications : INotificationConnectorApiService
    {
        public List<NotificationConnectorDto> Targets { get; } = [new(Guid.NewGuid(), 1, 1, new("Discord operations", NotificationProvider.Discord, true), true)];
        public SaveNotificationConnectorRequest? Saved { get; private set; }
        public int Sends { get; private set; }
        public List<NotificationConnectorTenantDto> Tenants { get; } = [new(1, "Operations")];
        public int LastListedTenant { get; private set; }
        public Task<IReadOnlyList<NotificationConnectorTenantDto>> GetTenantsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationConnectorTenantDto>>(Tenants);
        public Task<IReadOnlyList<NotificationConnectorDto>> ListAsync(int tenantId, CancellationToken ct = default)
        {
            LastListedTenant = tenantId;
            return Task.FromResult<IReadOnlyList<NotificationConnectorDto>>(Targets.Where(x => x.TenantId == tenantId).ToArray());
        }
        public Task<NotificationConnectorDto> SaveAsync(int tenantId, Guid id, SaveNotificationConnectorRequest request, CancellationToken ct = default)
        {
            Saved = request;
            var result = new NotificationConnectorDto(id, tenantId, request.ExpectedRevision + 1, request.Configuration, true);
            Targets.RemoveAll(x => x.Id == id); Targets.Add(result); return Task.FromResult(result);
        }
        public Task DeleteAsync(int tenantId, Guid id, long expectedRevision, CancellationToken ct = default)
        { Targets.RemoveAll(x => x.TenantId == tenantId && x.Id == id); return Task.CompletedTask; }
        public Task<NotificationDeliveryResult> SendTestAsync(int tenantId, Guid id, CancellationToken ct = default)
        { Sends++; return Task.FromResult(new NotificationDeliveryResult(NotificationDeliveryStatus.Succeeded, "notification-delivered")); }
    }
}
