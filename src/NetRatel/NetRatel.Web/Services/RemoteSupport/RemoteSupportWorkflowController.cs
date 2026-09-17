using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Web.Services.RemoteSupport;

public enum RemoteSupportWorkflowState
{
    Selecting,
    Preparing,
    Opening,
    Connected,
    ReplacementReady,
    ReconnectRequired,
    Failed,
    Closed,
    ConsoleConnected,
    LoginTransitionDetected,
    RefreshingWindowsSessions,
    WaitingForNewUserSession,
    SelectingUserSession,
    PreparingUserHelper,
    OpeningAssistSession,
    AssistConnected,
    LoginTransitionFailed
}

public enum RemoteSupportReplacementKind
{
    None,
    Single,
    Multiple
}

public sealed record RemoteSupportReplacementDecision(
    RemoteSupportReplacementKind Kind,
    IReadOnlyList<RemoteSupportWindowsSessionDto> Candidates)
{
    public RemoteSupportWindowsSessionDto? SingleCandidate =>
        Kind == RemoteSupportReplacementKind.Single ? Candidates[0] : null;
}

/// <summary>
/// Owns dialog-lifetime technician intent while each media child remains a
/// single-provider, generation-1 session.
/// </summary>
public sealed class RemoteSupportWorkflowController
{
    private readonly HashSet<string> _attemptedTransitions = new(StringComparer.Ordinal);

    public RemoteSupportWorkflowState State { get; private set; } = RemoteSupportWorkflowState.Selecting;
    public bool ControlRequested { get; private set; }
    public string? SelectedTargetKey { get; private set; }

    public void SetControlRequested(bool requested) => ControlRequested = requested;

    public void SetState(RemoteSupportWorkflowState state) => State = state;

    public bool TryBeginTransition(string transitionKey)
    {
        if (!_attemptedTransitions.Add(transitionKey))
        {
            return false;
        }

        State = RemoteSupportWorkflowState.Preparing;
        return true;
    }

    public void ResetTransitionBudget() => _attemptedTransitions.Clear();

    public void SelectTarget(RemoteSupportWindowsSessionDto session)
    {
        SelectedTargetKey = $"{session.WindowsSessionId}:{session.UserSidHash}";
        State = RemoteSupportWorkflowState.ReplacementReady;
        ResetTransitionBudget();
    }

    public RemoteSupportReplacementDecision DecideReplacement(IEnumerable<RemoteSupportWindowsSessionDto>? sessions)
    {
        var candidates = (sessions ?? Array.Empty<RemoteSupportWindowsSessionDto>())
            .Where(IsAssistCandidate)
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.WindowsSessionId)
            .ToArray();

        State = candidates.Length switch
        {
            0 => RemoteSupportWorkflowState.ReconnectRequired,
            1 => RemoteSupportWorkflowState.ReplacementReady,
            _ => RemoteSupportWorkflowState.Selecting
        };

        return new RemoteSupportReplacementDecision(
            candidates.Length switch
            {
                0 => RemoteSupportReplacementKind.None,
                1 => RemoteSupportReplacementKind.Single,
                _ => RemoteSupportReplacementKind.Multiple
            },
            candidates);
    }

    public static IReadOnlyList<RemoteSupportWindowsSessionDto> FindPostLoginCandidates(
        IEnumerable<RemoteSupportWindowsSessionDto> current,
        IEnumerable<RemoteSupportWindowsSessionDto> baseline)
    {
        var baselineRows = baseline.ToArray();
        var candidates = current.Where(IsAssistCandidate).ToArray();
        if (baselineRows.Length == 0)
        {
            return candidates;
        }

        return candidates.Where(candidate =>
        {
            var previous = baselineRows.FirstOrDefault(x => x.WindowsSessionId == candidate.WindowsSessionId);
            if (previous is null)
            {
                return true;
            }

            return candidate.ObservedUnixMs > previous.ObservedUnixMs &&
                ((!previous.IsActive && candidate.IsActive) ||
                 (!previous.IsConnected && candidate.IsConnected) ||
                 (!previous.IsAssistable && candidate.IsAssistable) ||
                 !string.Equals(previous.UserSidHash, candidate.UserSidHash, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }

    public static bool IsAssistCandidate(RemoteSupportWindowsSessionDto session) =>
        (session.IsActive || session.IsConnected) &&
        !session.IsLocked &&
        !session.IsWinlogon &&
        (session.IsAssistable || session.HelperLaunchable || session.HelperRepairable);
}
