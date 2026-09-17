using FluentAssertions;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class TerminalAttachmentHeartbeatAssetTests
{
    [Fact]
    public void BrowserHeartbeat_IsTimerOwnedAndStopsOnUnloadOrComponentCleanup()
    {
        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/wwwroot/js/terminalAttachmentHeartbeat.js"));
        var dialogPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/Components/Dialogs/ClientTerminalSessionDialog.razor"));

        var script = File.ReadAllText(scriptPath);
        var dialog = File.ReadAllText(dialogPath);

        script.Should().Contain("window.setInterval");
        script.Should().Contain("pagehide");
        script.Should().Contain("pageshow");
        script.Should().Contain("beforeunload");
        script.Should().Contain("stopTerminalAttachmentHeartbeat");
        script.Should().Contain("OnGatewayTerminalAttachmentHeartbeat");
        script.Should().Contain("Promise.race");
        script.Should().Contain("heartbeatAttemptTimeoutMilliseconds");
        script.Should().Contain("browserAttachmentId");
        script.Should().Contain("claimOwnership");
        dialog.Should().Contain("DotNetObjectReference.Create(this)");
        dialog.Should().Contain("RenewGatewayAttachmentAsync");
        dialog.Should().Contain("Generation = handle.Generation");
        dialog.Should().Contain("AttachmentLeaseId = handle.AttachmentLeaseId");
        dialog.Should().Contain("AttachmentHeartbeatRequestTimeout");
        dialog.Should().Contain("await StopGatewayTerminalAttachmentHeartbeatAsync(cleanupCt);");
    }

    [Fact]
    public void GatewayEndpoints_ReserveAttachmentRenewalForTheFencedHeartbeatRoute()
    {
        var endpointPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.API/Endpoints/RemoteAccess/AgentTerminalGatewayEndpoints.cs"));
        var endpoints = File.ReadAllText(endpointPath);

        endpoints.Should().Contain("/attachment/renew");
        endpoints.Should().Contain("TerminalAttachmentRenewalRequest");
        endpoints.Should().Contain("request.AttachmentLeaseId");
        endpoints.Should().Contain("request.BrowserAttachmentId");
        endpoints.Should().Contain("request.ClaimOwnership");
        endpoints.Should().Contain("attachments.TryRenew(");
        endpoints.Should().NotContain("BrowserAttachments(services)?.Renew");
    }
}
