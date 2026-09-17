using NetRatel.Shared.Contracts.RemoteSupport;
using NetRatel.Web.Services.RemoteSupport;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RemoteSupportWorkflowControllerTests
{
    [Fact]
    public void DecideReplacement_HandlesZeroOneAndMultipleSessions()
    {
        var workflow = new RemoteSupportWorkflowController();

        var none = workflow.DecideReplacement(Array.Empty<RemoteSupportWindowsSessionDto>());
        Assert.Equal(RemoteSupportReplacementKind.None, none.Kind);
        Assert.Equal(RemoteSupportWorkflowState.ReconnectRequired, workflow.State);

        var one = workflow.DecideReplacement([Session(4, "sid-a")]);
        Assert.Equal(RemoteSupportReplacementKind.Single, one.Kind);
        Assert.Equal(4, one.SingleCandidate?.WindowsSessionId);

        var many = workflow.DecideReplacement([Session(8, "sid-b"), Session(4, "sid-a", active: true)]);
        Assert.Equal(RemoteSupportReplacementKind.Multiple, many.Kind);
        Assert.Equal([4, 8], many.Candidates.Select(x => x.WindowsSessionId));
        Assert.Equal(RemoteSupportWorkflowState.Selecting, workflow.State);
    }

    [Fact]
    public void DecideReplacement_ExcludesLockedDisconnectedAndWinlogonSessions()
    {
        var workflow = new RemoteSupportWorkflowController();
        var decision = workflow.DecideReplacement([
            Session(1, "sid-a", locked: true),
            Session(2, "sid-b", connected: false),
            Session(3, "sid-c", winlogon: true)
        ]);

        Assert.Equal(RemoteSupportReplacementKind.None, decision.Kind);
    }

    [Fact]
    public void DecideReplacement_AcceptsAnActiveSessionEvenWhenConnectedFlagHasNotCaughtUp()
    {
        var workflow = new RemoteSupportWorkflowController();

        var decision = workflow.DecideReplacement([
            Session(5, "sid-a", active: true, connected: false)
        ]);

        Assert.Equal(RemoteSupportReplacementKind.Single, decision.Kind);
        Assert.Equal(5, decision.SingleCandidate?.WindowsSessionId);
    }

    [Fact]
    public void TransitionBudgetIsPerCompletedTransitionAndControlIntentSurvives()
    {
        var workflow = new RemoteSupportWorkflowController();
        workflow.SetControlRequested(true);

        Assert.True(workflow.TryBeginTransition("console_to_interactive"));
        Assert.False(workflow.TryBeginTransition("console_to_interactive"));

        workflow.SelectTarget(Session(4, "sid-a"));

        Assert.True(workflow.ControlRequested);
        Assert.True(workflow.TryBeginTransition("console_to_interactive"));
        Assert.Equal("4:sid-a", workflow.SelectedTargetKey);
    }

    [Fact]
    public void PostLoginCandidatesRequireNewOrChangedAssistableSession()
    {
        var baseline = new[]
        {
            Session(4, "sid-a", connected: false, assistable: false, observed: 10),
            Session(8, "sid-old", active: true, observed: 10)
        };

        var candidates = RemoteSupportWorkflowController.FindPostLoginCandidates(
            [
                Session(4, "sid-a", active: true, observed: 20),
                Session(8, "sid-old", active: true, observed: 20),
                Session(5, "sid-a", active: true, observed: 20)
            ],
            baseline);

        Assert.Equal([4, 5], candidates.Select(x => x.WindowsSessionId).OrderBy(x => x));
    }

    private static RemoteSupportWindowsSessionDto Session(
        int id,
        string sidHash,
        bool active = false,
        bool connected = true,
        bool locked = false,
        bool winlogon = false,
        bool assistable = true,
        long observed = 1) =>
        new(
            ClientIdentityHex: "abc",
            WindowsSessionId: id,
            State: connected ? "Active" : "Disconnected",
            Username: $"user{id}",
            Domain: "TEST",
            DisplayLabel: $"TEST\\user{id}",
            UserSidHash: sidHash,
            IsConsoleSession: id == 1,
            IsActive: active,
            IsConnected: connected,
            IsLocked: locked,
            IsWinlogon: winlogon,
            IsAssistable: assistable,
            SessionType: "rdp",
            Provider: "interactive_user_helper",
            HelperConnected: true,
            HelperVersionMatches: true,
            HelperLaunchable: true,
            HelperRepairable: true,
            HelperPid: 123,
            HelperVersion: "0.4.87",
            InventorySequence: 9,
            ObservedUnixMs: observed,
            ExpiresUnixMs: 2,
            Source: "test",
            Stale: false,
            DisabledReason: null);
}
