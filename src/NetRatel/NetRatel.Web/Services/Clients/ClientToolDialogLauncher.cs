using MudBlazor;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Models.Clients;
using NetRatel.Web.Services.FileSystem;
using NetRatel.Web.Services.Telemetry;

namespace NetRatel.Web.Services.Clients;

public static class ClientToolDialogLauncher
{
    public static async Task<IDialogReference> SoftwareAsync(IDialogService dialogs, ClientPresentationModel client)
    {
        var parameters = new DialogParameters<SoftwareInventoryDialog>
        {
            { dialog => dialog.TenantId, client.TenantId }, { dialog => dialog.AgentId, client.AgentId },
            { dialog => dialog.HostLabel, client.HostName ?? client.DisplayName }
        };
        return await dialogs.ShowAsync<SoftwareInventoryDialog>("Software Inventory", parameters,
            new DialogOptions { FullScreen = true, FullWidth = true, CloseOnEscapeKey = true, CloseOnNavigation = true });
    }

    public static async Task<IDialogReference> TelemetryAsync(IDialogService dialogs, ClientPresentationModel client, GatewayTelemetrySummary? telemetry)
    {
        var parameters = new DialogParameters<GatewayTelemetryDialog>
        {
            { dialog => dialog.TenantId, client.TenantId }, { dialog => dialog.AgentId, client.AgentId },
            { dialog => dialog.HostLabel, client.HostName ?? client.DisplayName }, { dialog => dialog.TenantLabel, client.TenantName ?? $"Tenant {client.TenantId}" },
            { dialog => dialog.InitialSnapshot, telemetry }
        };
        return await dialogs.ShowAsync<GatewayTelemetryDialog>("Telemetry", parameters,
            new DialogOptions { FullScreen = true, FullWidth = true, CloseOnEscapeKey = true, CloseButton = false });
    }

    public static async Task<IDialogReference> FilesAsync(IDialogService dialogs, IServiceProvider services, ClientPresentationModel client, string initialPath, bool? explicitFull = null)
    {
        if (!client.CanUseFilesystem) throw new InvalidOperationException(client.FilesystemUnavailableReason);
        var preference = services.GetService<ExplorerPreferenceService>();
        var full = explicitFull ?? (preference is not null && await preference.IsFullAsync(client.TenantId));
        if (explicitFull.HasValue && preference is not null) await preference.RememberAsync(client.TenantId, full);
        if (full)
        {
            return await dialogs.ShowAsync<FullFileExplorerDialog>("Full Explorer", new DialogParameters<FullFileExplorerDialog>
            {
                { dialog => dialog.TenantId, client.TenantId }, { dialog => dialog.AgentId, client.AgentId },
                { dialog => dialog.HostLabel, client.HostName ?? client.DisplayName }, { dialog => dialog.InitialPath, initialPath }
            }, new DialogOptions { FullScreen = true, CloseOnEscapeKey = true, CloseButton = false });
        }
        var parameters = new DialogParameters<ClientFileSystemViewer>
        {
            { dialog => dialog.TenantId, client.TenantId }, { dialog => dialog.AgentId, client.AgentId },
            { dialog => dialog.HostLabel, client.HostName ?? client.DisplayName }, { dialog => dialog.InitialPath, initialPath }
        };
        return await dialogs.ShowAsync<ClientFileSystemViewer>("File Browser", parameters,
            new DialogOptions { FullScreen = true, CloseOnEscapeKey = true, CloseButton = false });
    }

    public static async Task<IDialogReference> TerminalAsync(IDialogService dialogs, ClientPresentationModel client, string shell)
    {
        if (!client.CanUseTerminal || !client.AvailableShells.Contains(shell, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected terminal shell is unavailable.");
        var parameters = new DialogParameters<ClientTerminalSessionDialog>
        {
            { dialog => dialog.TenantId, client.TenantId }, { dialog => dialog.AgentId, client.AgentId }, { dialog => dialog.HostLabel, client.DisplayName },
            { dialog => dialog.Shell, shell }
        };
        return await dialogs.ShowAsync<ClientTerminalSessionDialog>($"{shell} terminal", parameters,
            new DialogOptions { CloseOnEscapeKey = true, FullWidth = true, MaxWidth = MaxWidth.ExtraExtraLarge, CloseButton = false });
    }

    public static async Task<IDialogReference> LogsAsync(IDialogService dialogs, ClientPresentationModel client)
    {
        if (!client.CanUseLogs) throw new InvalidOperationException(client.LogsUnavailableReason);
        var parameters = new DialogParameters<GatewayClientLogExplorerDialog>
        {
            { dialog => dialog.TenantId, client.TenantId }, { dialog => dialog.AgentId, client.AgentId },
            { dialog => dialog.HostLabel, client.HostName ?? client.DisplayName },
            { dialog => dialog.TenantLabel, client.TenantName ?? $"Tenant {client.TenantId}" },
            { dialog => dialog.Online, client.Online }
        };
        return await dialogs.ShowAsync<GatewayClientLogExplorerDialog>("Client logs", parameters,
            new DialogOptions { FullScreen = true, FullWidth = true, CloseOnEscapeKey = true, CloseButton = false });
    }

    public static Task<IDialogReference> ServicesAsync(IDialogService dialogs, int tenant, Guid agent, string host, string tenantName) =>
        dialogs.ShowAsync<ClientServicesDialog>("Services", new DialogParameters<ClientServicesDialog>
        {
            { dialog => dialog.TenantId, tenant }, { dialog => dialog.AgentId, agent },
            { dialog => dialog.HostLabel, host }, { dialog => dialog.TenantLabel, tenantName }
        }, new DialogOptions { FullScreen = true, FullWidth = true, CloseOnEscapeKey = true, CloseOnNavigation = true, CloseButton = false, NoHeader = true });
}
