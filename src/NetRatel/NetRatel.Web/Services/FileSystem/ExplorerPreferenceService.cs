using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace NetRatel.Web.Services.FileSystem;

public sealed class ExplorerPreferenceService(AuthenticationStateProvider authentication, IJSRuntime js, ILogger<ExplorerPreferenceService> logger)
{
    public async Task<string> ScopeAsync(int tenantId)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        var identity = FileBrowserContentRegistry.OperatorId(user);
        return $"netratel.explorer.v1.{tenantId}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }
    public async Task<bool> IsFullAsync(int tenantId)
    {
        try { return await js.InvokeAsync<string?>("netratelExplorer.get", await ScopeAsync(tenantId), "mode") == "full"; }
        catch (JSException exception) { logger.LogDebug(exception, "Explorer preference unavailable; opening Basic"); return false; }
    }
    public async Task RememberAsync(int tenantId, bool full)
    {
        try { await js.InvokeVoidAsync("netratelExplorer.set", await ScopeAsync(tenantId), "mode", full ? "full" : "basic"); }
        catch (JSException exception) { logger.LogDebug(exception, "Explorer choice could not be persisted"); }
    }
}
