using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RemoteSupportBrowserAssetTests
{
    [Fact]
    public void RemoteSupportBrowserAsset_AdvertisesProtocolCapabilitiesWithoutClientVersionCoupling()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/wwwroot/js/remote-support-dialog.js"));
        var script = File.ReadAllText(path);

        script.Should().Contain("const protocolRevision = 1");
        script.Should().Contain("return {");
        script.Should().Contain("protocolRevision,");
        script.Should().NotContain("        version,");
        script.Should().Contain("assetUrl");
        script.Should().NotContain("const version = \"0.4.");
        script.Should().Contain("sasControl: true");
        script.Should().Contain("function sendSas(sessionId)");
        script.Should().Contain("type: \"sas\"");
        script.Should().Contain("action: \"send_ctrl_alt_del\"");
        script.Should().Contain("inputAcknowledgements: true");
        script.Should().Contain("peerInstanceId");
        script.Should().Contain("remote_input_browser_sent");
        script.Should().Contain("remote_input_ack_received");
        script.Should().Contain("sessions.get(session.sessionId) !== session");
        script.Should().Contain("function sendInputProbe(sessionId)");
        script.Should().Contain("0.4.92-desktop-context-recovery");
        script.Should().Contain("desktop_context_recovery");
        script.Should().Contain("first_frame_gate,capture_recovery");
        script.Should().Contain("HTMLMediaElement.HAVE_CURRENT_DATA");
        script.Should().NotContain("0.4.89-control-login");
        script.Should().NotContain("const version =");
    }

    [Fact]
    public void App_UsesFingerprintedRemoteSupportAsset()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/Components/App.razor"));
        var component = File.ReadAllText(path);

        component.Should().Contain("Assets[\"js/remote-support-dialog.js\"]");
        component.Should().NotContain("remote-support-dialog.js?v=0.4.");
        component.IndexOf("id=\"netratel-remote-support-script\"", StringComparison.Ordinal)
            .Should().BeLessThan(component.IndexOf("src=\"_framework/blazor.web.js\"", StringComparison.Ordinal));
        component.Should().Contain("netratelRemoteSupportAssetStatus");
        component.Should().Contain("remote_support_asset_load_failed");

        var programPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/Program.cs"));
        var program = File.ReadAllText(programPath);
        program.Should().Contain("app.MapStaticAssets();");
        program.Should().Contain("no-cache, max-age=0, must-revalidate");
    }

    [Fact]
    public void GatewayDialog_CreatesAnOfferOnlyAfterAuthoritativeReadiness()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/Components/Dialogs/GatewayRemoteSupportDialog.razor"));
        var dialog = File.ReadAllText(path);

        dialog.Should().Contain("OpenV2Async");
        dialog.Should().Contain("PrepareV2MediaAsync");
        dialog.Should().Contain("snapshot.State == RemoteSupportV2SessionStates.ReadyForOffer");
        dialog.IndexOf("OpenV2Async", StringComparison.Ordinal)
            .Should().BeLessThan(dialog.IndexOf("var offer = await JS.InvokeAsync<string>", StringComparison.Ordinal));
        dialog.Should().Contain("SendV2NegotiationAsync");
        dialog.Should().Contain("OnDataChannelStateChanged(int generation, string state)");
        dialog.Should().Contain("OnRemoteVideoFrame(int generation, long renderedFrames, double fps) => Task.CompletedTask");
        dialog.Should().Contain("OnControlInputAcknowledged(int generation, object acknowledgement) => Task.CompletedTask");
    }

    [Fact]
    public void ScenarioCatalog_RecordsTheCurrentGatewayAndBrowserGapsForLaterV2Phases()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../tests/fixtures/remote-support-v2-scenarios.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var names = document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();

        names.Should().Contain([
            "target_prepare_ready_for_offer", "offer_answer_ice", "duplicate_signal", "reordered_signal",
            "browser_reconnect", "agent_gateway_reconnect", "first_frame_timeout", "data_channel_missing",
            "helper_loss", "console_to_user_transition"]);
    }

}
