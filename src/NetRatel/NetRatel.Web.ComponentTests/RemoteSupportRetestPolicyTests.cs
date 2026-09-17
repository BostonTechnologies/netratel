using NetRatel.Shared.Contracts.RemoteSupport;
using NetRatel.Web.Services.RemoteSupport;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RemoteSupportRetestPolicyTests
{
    [Theory]
    [InlineData("stable")]
    [InlineData("STABLE")]
    public void StableProviderReadinessIsNotAHandover(string state)
    {
        Assert.True(RemoteSupportHandoverStateClassifier.IsStable(state));
        Assert.False(RemoteSupportHandoverStateClassifier.IsTransition(state));
    }

    [Theory]
    [InlineData("desktop_context_switching")]
    [InlineData("handover_waiting_for_target_provider")]
    [InlineData("handover_renegotiating")]
    [InlineData("handover_reconnect_required")]
    public void ExplicitProviderTransitionsAreClassifiedAsHandovers(string state)
    {
        Assert.True(RemoteSupportHandoverStateClassifier.IsTransition(state));
    }

    [Fact]
    public void CurrentAssistTargetsDeduplicateSessionAndExposeTypeAndId()
    {
        var targets = RemoteSupportSessionPresentation.CurrentAssistTargets([
            Session(5, sequence: 134, observed: 10),
            Session(3, sequence: 1, observed: 20),
            Session(7, sequence: 1, observed: 20)
        ]).ToArray();

        Assert.Equal([3, 7], targets.Select(x => x.WindowsSessionId));
        Assert.All(targets, target => Assert.Equal((ulong)1, target.InventorySequence));
        Assert.Equal("Assist: TEST\\admin — RDP session 3", RemoteSupportSessionPresentation.AssistLabel(targets[0]));
    }

    [Fact]
    public void ResolveFreshAssistTargetNeverMovesTechnicianSelectionAcrossSessions()
    {
        var selected = Session(4, sequence: 6, observed: 10);

        var resolved = RemoteSupportSessionPresentation.ResolveFreshAssistTarget(selected, [
            Session(4, sequence: 6, observed: 10),
            Session(5, sequence: 7, observed: 20)
        ]);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveFreshAssistTargetDoesNotGuessWhenIdentityHasMultipleCurrentSessions()
    {
        var selected = Session(4, sequence: 6, observed: 10);

        var resolved = RemoteSupportSessionPresentation.ResolveFreshAssistTarget(selected, [
            Session(5, sequence: 7, observed: 20),
            Session(6, sequence: 7, observed: 20)
        ]);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveFreshAssistTargetAdoptsHashRolloverForSameSessionAndAccount()
    {
        var selected = Session(4, sequence: 6, observed: 10, sidHash: "account-hash");

        var resolved = RemoteSupportSessionPresentation.ResolveFreshAssistTarget(selected, [
            Session(4, sequence: 7, observed: 20, sidHash: "sid-hash")
        ]);

        Assert.NotNull(resolved);
        Assert.Equal("sid-hash", resolved.UserSidHash);
    }

    [Fact]
    public void ResolveFreshAssistTargetRejectsSameSessionWhenAccountChanged()
    {
        var selected = Session(4, sequence: 6, observed: 10, sidHash: "old", username: "first");

        var resolved = RemoteSupportSessionPresentation.ResolveFreshAssistTarget(selected, [
            Session(4, sequence: 7, observed: 20, sidHash: "new", username: "second")
        ]);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveFreshAssistTargetNeverUsesAccountOnlyAcrossSessions()
    {
        var selected = Session(4, sequence: 6, observed: 10, sidHash: "old");

        var resolved = RemoteSupportSessionPresentation.ResolveFreshAssistTarget(selected, [
            Session(5, sequence: 7, observed: 20, sidHash: "new")
        ]);

        Assert.Null(resolved);
    }

    private static RemoteSupportWindowsSessionDto Session(
        int id,
        ulong sequence,
        long observed,
        string sidHash = "sid",
        string username = "admin") =>
        new(
            ClientIdentityHex: "abc",
            WindowsSessionId: id,
            State: "Active",
            Username: username,
            Domain: "TEST",
            DisplayLabel: $"TEST\\{username}",
            UserSidHash: sidHash,
            IsConsoleSession: false,
            IsActive: true,
            IsConnected: true,
            IsLocked: false,
            IsWinlogon: false,
            IsAssistable: true,
            SessionType: "rdp",
            Provider: "interactive_user_helper",
            HelperConnected: true,
            HelperVersionMatches: true,
            HelperLaunchable: true,
            HelperRepairable: true,
            HelperPid: 123,
            HelperVersion: "0.4.87",
            InventorySequence: sequence,
            ObservedUnixMs: observed,
            ExpiresUnixMs: long.MaxValue,
            Source: "test",
            Stale: false,
            DisabledReason: null);
}
