using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class RemoteSupportV2BrowserWorkflowSourceTests
{
    [Fact]
    public void V2Dialog_waits_for_authoritative_ready_before_creating_an_offer()
    {
        var source = ReadRepoFile("NetRatel.Web", "Components", "Dialogs", "GatewayRemoteSupportDialog.razor");

        source.Should().Contain("OpenV2Async");
        source.Should().Contain("PrepareV2MediaAsync");
        source.Should().Contain("StreamV2LifecycleEventsAsync");
        source.Should().Contain("snapshot.State == RemoteSupportV2SessionStates.ReadyForOffer");
        source.Should().Contain("if (_offerCreated)");
        source.Should().Contain("SendV2NegotiationAsync");
        source.Should().Contain("GetV2IceConfigurationAsync");
        source.Should().Contain("_iceConfiguration.Servers");
        source.Should().NotContain("RemoteSupportIceServerOptions");
        source.Should().Contain("media negotiation is not replayed");
        source.Should().NotContain("SendSignalAsync(_sessionId");
    }

    [Fact]
    public void Browser_does_not_reintroduce_static_stun_fallbacks()
    {
        var source = ReadRepoFile("NetRatel.Web", "wwwroot", "js", "remote-support-dialog.js");

        source.Should().Contain("return [];");
        source.Should().NotContain("stun.l.google.com");
        source.Should().NotContain("stun.cloudflare.com");
    }

    [Fact]
    public void Browser_generation_callbacks_are_fenced_and_report_real_first_frame()
    {
        var source = ReadRepoFile("NetRatel.Web", "Components", "Dialogs", "GatewayRemoteSupportDialog.razor");

        source.Should().Contain("generation == _generation");
        source.Should().Contain("OnFirstVideoFrame");
        source.Should().Contain("first_frame");
        source.Should().Contain("data_channel_open");
        source.Should().Contain("resetTransition");
        source.Should().Contain("RenegotiationRequired");
    }

    [Fact]
    public void Failed_open_cancels_its_pumps_closes_its_lifecycle_and_cannot_restore_the_session()
    {
        var source = ReadRepoFile("NetRatel.Web", "Components", "Dialogs", "GatewayRemoteSupportDialog.razor");

        source.Should().Contain("await CleanupAttemptAsync(attempt)");
        source.Should().Contain("attempt.Cancellation.Cancel()");
        source.Should().Contain("await RemoteSupport.CloseV2Async(closeSnapshot, closeCancellation.Token, requireCurrentRevision: false)");
        source.Should().Contain("await AwaitPumpShutdownAsync(attempt)");
        source.Should().Contain("if (!IsCurrentAttempt(attempt)) return;");
        source.Should().Contain("_attempt = null;");
        source.IndexOf("_attempt = null;", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("attempt.Cancellation.Cancel();", StringComparison.Ordinal));
        source.IndexOf("attempt.Cancellation.Cancel();", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("await RemoteSupport.CloseV2Async(closeSnapshot, closeCancellation.Token, requireCurrentRevision: false);", StringComparison.Ordinal));
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        return File.ReadAllText(Path.Combine([root, "src", "NetRatel", .. segments]));
    }
}
