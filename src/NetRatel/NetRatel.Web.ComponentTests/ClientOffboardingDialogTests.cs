using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Models.Clients;
using NetRatel.Web.Services.Clients;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientOffboardingDialogTests : AsyncBunitContext
{
    private static readonly Guid AgentId = Guid.Parse("ed9b6001-cf7e-44de-908e-476c25132578");
    private readonly Api _api = new();

    public ClientOffboardingDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IClientOffboardingApiService>(_api);
    }

    [Theory]
    [InlineData(false, "client_offline")]
    [InlineData(true, "uninstall_not_supported")]
    public async Task Offline_and_old_clients_allow_record_removal_and_disable_uninstall(bool online, string reason)
    {
        _api.Details = _api.Details with { Online = online, CanUninstall = false, UnavailableReason = reason };
        var provider = Render<MudDialogProvider>();
        var dialog = await OpenAsync(online);
        provider.WaitForAssertion(() => provider.Find("[data-testid='client-offboarding-confirm']").HasAttribute("disabled").Should().BeFalse());
        provider.Find("input[data-testid='client-offboarding-uninstall-option']").HasAttribute("disabled").Should().BeTrue();
        provider.Find("[data-testid='client-offboarding-dialog']").TextContent.Should().Contain(AgentId.ToString("D"));
        provider.Find("[data-testid='client-offboarding-confirm']").TextContent.Should().Be("Remove client");
        provider.Find("[data-testid='client-offboarding-confirm']").Click();
        provider.WaitForAssertion(() => _api.Removals.Should().ContainSingle());
        _api.Removals.Single().Should().Be((7, AgentId));
        _api.Uninstalls.Should().BeEmpty();
        (await dialog.Result)!.Canceled.Should().BeFalse();
    }

    [Fact]
    public async Task Accepted_uninstall_keeps_dialog_and_record_until_explicit_removal_or_completion()
    {
        var provider = Render<MudDialogProvider>();
        var dialog = await OpenAsync(online: true);
        provider.WaitForAssertion(() => provider.Find("[data-testid='client-offboarding-confirm']").HasAttribute("disabled").Should().BeFalse());
        await provider.InvokeAsync(() => provider.FindComponent<MudRadioGroup<string>>().Instance.ValueChanged.InvokeAsync("uninstall"));
        provider.Find("[data-testid='client-offboarding-confirm']").Click();
        provider.WaitForAssertion(() => _api.Uninstalls.Should().ContainSingle());
        var request = _api.Uninstalls.Single();
        request.Tenant.Should().Be(7);
        request.Agent.Should().Be(AgentId);
        request.Request.OperationId.Should().NotBeEmpty();
        provider.Find("[data-testid='client-offboarding-progress']").TextContent.Should().Contain("Uninstall in progress");
        dialog.Result.IsCompleted.Should().BeFalse();
        _api.Removals.Should().BeEmpty();

        provider.Find("[data-testid='client-offboarding-remove-pending']").Click();
        _api.Removals.Should().BeEmpty("choosing the fallback must still require its explicit confirmation");
        provider.Find("[data-testid='client-offboarding-confirm']").TextContent.Should().Be("Remove client");
        provider.Find("[data-testid='client-offboarding-confirm']").Click();
        provider.WaitForAssertion(() => _api.Removals.Should().ContainSingle());
        _api.Removals.Single().Should().Be((7, AgentId));
        (await dialog.Result)!.Canceled.Should().BeFalse();
    }

    [Fact]
    public async Task Reopened_pending_operation_retries_same_id_and_closing_does_not_remove_record()
    {
        var operationId = Guid.NewGuid();
        _api.Details = _api.Details with { Operation = Operation(operationId, "confirmation_pending") };
        var provider = Render<MudDialogProvider>();
        var dialog = await OpenAsync(online: true);
        provider.WaitForAssertion(() => provider.Find("[data-testid='client-offboarding-retry']").HasAttribute("disabled").Should().BeFalse());
        provider.Find("[data-testid='client-offboarding-retry']").Click();
        provider.WaitForAssertion(() => _api.Uninstalls.Should().ContainSingle());
        _api.Uninstalls.Single().Request.OperationId.Should().Be(operationId);
        _api.Removals.Should().BeEmpty();
        provider.Find("[data-testid='client-offboarding-close']").Click();
        (await dialog.Result)!.Canceled.Should().BeTrue();
        _api.Details.Removed.Should().BeFalse();
        _api.Details.Operation!.OperationId.Should().Be(operationId);
    }

    private Task<IDialogReference> OpenAsync(bool online) => Services.GetRequiredService<IDialogService>().ShowAsync<ClientOffboardingDialog>("Remove client",
        new DialogParameters<ClientOffboardingDialog>
        {
            { component => component.Client, new ClientPresentationModel(7, AgentId, "hv01", "hv01", "Factory1", "Linux", "x64",
                online, true, DateTimeOffset.UtcNow, "fixture", [], null, null, FriendlyName: "Dispatch Server", CanManage: true) }
        });

    private static ClientOffboardingOperationDto Operation(Guid id, string state) => new(id, state, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24), null, false, false);

    private sealed class Api : IClientOffboardingApiService
    {
        public ClientOffboardingDetailsDto Details { get; set; } = new(7, AgentId, false, true, true, null, null);
        public List<(int Tenant, Guid Agent)> Removals { get; } = [];
        public List<(int Tenant, Guid Agent, ClientUninstallRequest Request)> Uninstalls { get; } = [];
        public Task<ClientOffboardingDetailsDto> GetAsync(int tenantId, Guid agentId, CancellationToken ct = default) => Task.FromResult(Details);
        public Task<ClientOffboardingDetailsDto> UninstallAsync(int tenantId, Guid agentId, ClientUninstallRequest request, CancellationToken ct = default)
        {
            Uninstalls.Add((tenantId, agentId, request));
            Details = Details with { Operation = Operation(request.OperationId, "accepted") };
            return Task.FromResult(Details);
        }
        public Task<ClientOffboardingDetailsDto> RemoveAsync(int tenantId, Guid agentId, ClientRemovalRequest request, CancellationToken ct = default)
        {
            Removals.Add((tenantId, agentId));
            Details = Details with { Removed = true };
            return Task.FromResult(Details);
        }
    }
}
