namespace NetRatel.Web.Services.RemoteSupport;

public static class RemoteSupportHandoverStateClassifier
{
    private static readonly HashSet<string> TransitionStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop_context_switching",
        "handover_waiting_for_target_provider",
        "handover_renegotiating",
        "handover_failed",
        "handover_reconnect_required",
        "handover_connected"
    };

    public static bool IsStable(string? state) =>
        string.Equals(state, "stable", StringComparison.OrdinalIgnoreCase);

    public static bool IsTransition(string? state) =>
        !string.IsNullOrWhiteSpace(state) && TransitionStates.Contains(state);
}
