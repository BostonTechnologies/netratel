using System.Net;
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

public sealed class ClientMetadataDialogTests : AsyncBunitContext
{
    private readonly Api _api = new();
    private static readonly Guid AgentId = Guid.Parse("ed9b6001-cf7e-44de-908e-476c25132578");

    public ClientMetadataDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IClientMetadataApiService>(_api);
    }

    [Fact]
    public async Task Offline_editor_keeps_host_readonly_and_saves_normalized_labels_for_selected_client()
    {
        var provider = Render<MudDialogProvider>();
        var dialog = await OpenAsync();
        provider.WaitForAssertion(() => provider.Find("[data-testid='client-metadata-save']").HasAttribute("disabled").Should().BeFalse());
        provider.Find("input[data-testid='client-metadata-hostname']").HasAttribute("readonly").Should().BeTrue();
        provider.Find("input[data-testid='client-metadata-name']").Input(" Dispatch Server ");
        provider.Find("input[data-testid='client-metadata-tags']").Input("Prod, PROD, Factory1 ");
        provider.Find("[data-testid='client-metadata-tag-preview']").TextContent.Should().Contain("Prod").And.Contain("Factory1");
        provider.Find("[data-testid='client-metadata-save']").Click();
        provider.WaitForAssertion(() => _api.Writes.Should().ContainSingle());
        var write = _api.Writes.Single();
        write.Tenant.Should().Be(7);
        write.Agent.Should().Be(AgentId);
        write.Request.FriendlyName.Should().Be("Dispatch Server");
        write.Request.Tags.Should().Equal("Prod", "Factory1");
        (await dialog.Result)!.Canceled.Should().BeFalse();
    }

    [Fact]
    public async Task Clearing_fields_removes_labels_and_cancelling_never_writes()
    {
        var provider = Render<MudDialogProvider>();
        var cancelled = await OpenAsync();
        provider.WaitForAssertion(() => provider.Find("input[data-testid='client-metadata-name']").GetAttribute("value").Should().Be("Original"));
        provider.Find("input[data-testid='client-metadata-name']").Input("Discard me");
        provider.Find("[data-testid='client-metadata-cancel']").Click();
        (await cancelled.Result)!.Canceled.Should().BeTrue();
        _api.Writes.Should().BeEmpty();
        provider.WaitForAssertion(() => provider.FindAll("[data-testid='client-metadata-editor']").Should().BeEmpty());

        var saved = await OpenAsync();
        provider.WaitForAssertion(() => provider.Find("[data-testid='client-metadata-save']").HasAttribute("disabled").Should().BeFalse());
        provider.Find("input[data-testid='client-metadata-name']").Input("");
        provider.Find("input[data-testid='client-metadata-tags']").Input("");
        provider.Find("[data-testid='client-metadata-save']").Click();
        provider.WaitForAssertion(() => _api.Writes.Should().ContainSingle());
        _api.Writes.Single().Request.FriendlyName.Should().BeNull();
        _api.Writes.Single().Request.Tags.Should().BeEmpty();
        (await saved.Result)!.Canceled.Should().BeFalse();
    }

    [Fact]
    public async Task Permission_failure_preserves_the_draft_and_keeps_the_dialog_open()
    {
        _api.FailWrite = true;
        var provider = Render<MudDialogProvider>();
        var dialog = await OpenAsync();
        provider.WaitForAssertion(() => provider.Find("[data-testid='client-metadata-save']").HasAttribute("disabled").Should().BeFalse());
        provider.Find("input[data-testid='client-metadata-name']").Input("Dispatch Server");
        provider.Find("[data-testid='client-metadata-save']").Click();
        provider.WaitForAssertion(() => provider.Find("[data-testid='client-metadata-error']").TextContent.Should().Contain("permission"));
        provider.Find("input[data-testid='client-metadata-name']").GetAttribute("value").Should().Be("Dispatch Server");
        dialog.Result.IsCompleted.Should().BeFalse();
    }

    private Task<IDialogReference> OpenAsync() => Services.GetRequiredService<IDialogService>().ShowAsync<ClientMetadataDialog>("Edit client",
        new DialogParameters<ClientMetadataDialog>
        {
            { component => component.Client, new ClientPresentationModel(7, AgentId, "actual-host", "actual-host", "Tenant",
                "Linux", "x64", false, true, null, null, [], null, null, CanManage: true) }
        });

    private sealed class Api : IClientMetadataApiService
    {
        public List<(int Tenant, Guid Agent, UpdateClientMetadataRequest Request)> Writes { get; } = [];
        public bool FailWrite { get; set; }

        public Task<ClientMetadataDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ClientMetadataDto?>(new(tenantId, agentId, "actual-host", "Original", ["Old tag"]));

        public Task<ClientMetadataDto> UpdateAsync(int tenantId, Guid agentId, UpdateClientMetadataRequest request,
            CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new HttpRequestException("Denied", null, HttpStatusCode.Forbidden);
            Writes.Add((tenantId, agentId, request));
            return Task.FromResult(new ClientMetadataDto(tenantId, agentId, "actual-host", request.FriendlyName, request.Tags ?? []));
        }
    }
}
