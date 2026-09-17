namespace NetRatel.Web.Services.RemoteSupport;

public static class RemoteSupportFirstFrameGate
{
    public const string FirstFrameFeature = "first_frame_gate";

    public static bool HasFeature(string? featureSet, string feature)
    {
        if (string.IsNullOrWhiteSpace(featureSet) || string.IsNullOrWhiteSpace(feature))
        {
            return false;
        }

        return featureSet
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => string.Equals(candidate, feature, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanObserveLegacyRenderedFrame(
        bool browserHasFirstFrameFeature,
        DateTimeOffset? renderedAt,
        int videoWidth,
        int videoHeight,
        string? peerState,
        string? dataChannelState) =>
        !browserHasFirstFrameFeature &&
        renderedAt.HasValue &&
        videoWidth > 0 &&
        videoHeight > 0 &&
        IsConnected(peerState) &&
        IsOpen(dataChannelState);

    public static bool CanComplete(
        bool frameObserved,
        DateTimeOffset? renderedAt,
        int videoWidth,
        int videoHeight,
        string? peerState,
        string? dataChannelState) =>
        frameObserved &&
        renderedAt.HasValue &&
        videoWidth > 0 &&
        videoHeight > 0 &&
        IsConnected(peerState) &&
        IsOpen(dataChannelState);

    private static bool IsConnected(string? state) =>
        string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpen(string? state) =>
        string.Equals(state, "open", StringComparison.OrdinalIgnoreCase);
}
