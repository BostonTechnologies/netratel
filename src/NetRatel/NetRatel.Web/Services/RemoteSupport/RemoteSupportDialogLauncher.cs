using MudBlazor;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Models.Clients;

namespace NetRatel.Web.Services.RemoteSupport;

/// <summary>Single operator entry point for Remote Support V2 from client cards and grids.</summary>
public static class RemoteSupportDialogLauncher
{
    public static DialogParameters<GatewayRemoteSupportDialog> CreateParameters(ClientPresentationModel client) =>
        new()
        {
            { dialog => dialog.TenantId, client.TenantId },
            { dialog => dialog.AgentId, client.AgentId },
            { dialog => dialog.HostLabel, client.HostName }
        };

    public static DialogOptions CreateOptions() => new()
    {
        MaxWidth = MaxWidth.False,
        FullWidth = true,
        CloseOnEscapeKey = false
    };
}
