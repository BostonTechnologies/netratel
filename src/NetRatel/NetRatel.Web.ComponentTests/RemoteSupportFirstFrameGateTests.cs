using NetRatel.Web.Services.RemoteSupport;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RemoteSupportFirstFrameGateTests
{
    private static readonly DateTimeOffset RenderedAt = DateTimeOffset.Parse("2026-07-15T06:52:40Z");

    [Fact]
    public void FeatureDiscoveryIsCaseInsensitiveAndTokenBased()
    {
        Assert.True(RemoteSupportFirstFrameGate.HasFeature(
            "control_ack, FIRST_FRAME_GATE, capture_recovery",
            RemoteSupportFirstFrameGate.FirstFrameFeature));
        Assert.False(RemoteSupportFirstFrameGate.HasFeature(
            "control_ack,current_peer_input",
            RemoteSupportFirstFrameGate.FirstFrameFeature));
    }

    [Fact]
    public void LegacyRenderedStatsCompleteOnlyWithAllCurrentPeerEvidence()
    {
        Assert.True(RemoteSupportFirstFrameGate.CanObserveLegacyRenderedFrame(
            false, RenderedAt, 1024, 576, "connected", "open"));
        Assert.False(RemoteSupportFirstFrameGate.CanObserveLegacyRenderedFrame(
            true, RenderedAt, 1024, 576, "connected", "open"));
        Assert.False(RemoteSupportFirstFrameGate.CanObserveLegacyRenderedFrame(
            false, null, 1024, 576, "connected", "open"));
        Assert.False(RemoteSupportFirstFrameGate.CanObserveLegacyRenderedFrame(
            false, RenderedAt, 1024, 576, "connecting", "open"));
        Assert.False(RemoteSupportFirstFrameGate.CanObserveLegacyRenderedFrame(
            false, RenderedAt, 1024, 576, "connected", "closed"));
    }

    [Fact]
    public void ObservedFrameDoesNotCompleteBeforePeerAndDataChannel()
    {
        Assert.False(RemoteSupportFirstFrameGate.CanComplete(
            true, RenderedAt, 1024, 576, "connecting", "open"));
        Assert.False(RemoteSupportFirstFrameGate.CanComplete(
            true, RenderedAt, 1024, 576, "connected", "connecting"));
        Assert.True(RemoteSupportFirstFrameGate.CanComplete(
            true, RenderedAt, 1024, 576, "connected", "open"));
    }
}
