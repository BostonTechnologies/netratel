using FluentAssertions;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class RemoteSupportTargetTransitionCoordinatorTests
{
    [Fact]
    public void ConsoleLogin_ExactlyOneStableUser_SelectsFreshExactTarget()
    {
        var (coordinator, clock, session, console) = Create();
        var started = coordinator.Start(session, console, Guid.NewGuid(), 4, 8, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, activeConsoleMatched: true);

        started.Accepted.Should().BeTrue();
        var first = coordinator.ObserveInventory(Inventory(session, 10, User(7, "sid-a")), started.Fence!.TransitionId, 8);
        var selected = coordinator.ObserveInventory(Inventory(session, 11, User(7, "sid-a")), started.Fence!.TransitionId, 8);

        first.Code.Should().Be("converging_inventory");
        selected.Code.Should().Be("target_automatically_selected");
        selected.SelectedTarget.Should().Be(new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 7, "sid-a", 11));
        coordinator.IsControlSuspended.Should().BeTrue();
        clock.GetUtcNow().Should().BeAfter(DateTimeOffset.MinValue);
    }

    [Fact]
    public void NoUser_WaitsThenFailsWithoutPreparingAnyTarget()
    {
        var (coordinator, clock, session, console) = Create();
        var started = coordinator.Start(session, console, null, 1, 1, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true);
        coordinator.ObserveInventory(Inventory(session, 1), started.Fence!.TransitionId, 1);
        var waiting = coordinator.ObserveInventory(Inventory(session, 2), started.Fence.TransitionId, 1);
        clock.Advance(TimeSpan.FromSeconds(6));

        waiting.Code.Should().Be("waiting_for_signed_in_target");
        coordinator.Tick().FailureCode.Should().Be("transition_timeout");
        coordinator.State.Should().Be(RemoteSupportTransitionState.Failed);
    }

    [Fact]
    public void MultipleUsers_RequiresTransitionAndInventoryBoundSelection()
    {
        var (coordinator, _, session, console) = Create();
        var started = coordinator.Start(session, console, null, 1, 1, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true);
        coordinator.ObserveInventory(Inventory(session, 1, User(3, "sid-a"), User(4, "sid-b")), started.Fence!.TransitionId, 1);
        var selection = coordinator.ObserveInventory(Inventory(session, 2, User(3, "sid-a"), User(4, "sid-b")), started.Fence.TransitionId, 1);

        selection.Code.Should().Be("target_selection_required");
        coordinator.Select(started.Fence.TransitionId, 1, 1, 3, "sid-a").Accepted.Should().BeFalse();
        coordinator.Select(started.Fence.TransitionId, 1, 2, 99, "sid-missing").Code.Should().Be("target_selection_not_current");
        coordinator.Select(started.Fence.TransitionId, 1, 2, 4, "sid-b").SelectedTarget!.WindowsSessionId.Should().Be(4);
    }

    [Fact]
    public void TransientCandidate_IsNeverPreparedUntilObservedTwice()
    {
        var (coordinator, _, session, console) = Create();
        var started = coordinator.Start(session, console, null, 1, 1, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true);

        coordinator.ObserveInventory(Inventory(session, 1, User(3, "sid-a")), started.Fence!.TransitionId, 1).Code.Should().Be("converging_inventory");
        coordinator.ObserveInventory(Inventory(session, 2), started.Fence.TransitionId, 1).Code.Should().Be("converging_inventory");
        coordinator.State.Should().Be(RemoteSupportTransitionState.ConvergingInventory);
    }

    [Fact]
    public void SameSidDifferentWts_NeverAutoFollowsInteractiveTarget()
    {
        var (coordinator, _, session, _) = Create();
        var old = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 3, "sid-a", 1);
        var started = coordinator.Start(session, old, Guid.NewGuid(), 2, 4, 1, RemoteSupportTransitionReason.RdpDisconnected, true);

        coordinator.ObserveInventory(Inventory(session, 2, User(7, "sid-a")), started.Fence!.TransitionId, 4);
        var decision = coordinator.ObserveInventory(Inventory(session, 3, User(7, "sid-a")), started.Fence.TransitionId, 4);

        decision.Code.Should().Be("target_selection_required");
        decision.SelectedTarget.Should().BeNull();
    }

    [Fact]
    public void Replacement_RequiresExactFenceAndAlwaysAdvancesGeneration()
    {
        var (coordinator, _, session, console) = Create();
        var started = coordinator.Start(session, console, null, 6, 9, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true);
        coordinator.ObserveInventory(Inventory(session, 10, User(2, "sid-a")), started.Fence!.TransitionId, 9);
        var selected = coordinator.ObserveInventory(Inventory(session, 11, User(2, "sid-a")), started.Fence.TransitionId, 9);
        var prepared = Prepared(session, selected.SelectedTarget!, 11);

        coordinator.ReplacementPrepared(Guid.NewGuid(), 9, prepared).Accepted.Should().BeFalse();
        var replacement = coordinator.ReplacementPrepared(started.Fence.TransitionId, 9, prepared);
        replacement.FreshGeneration.Should().Be(7);
        coordinator.CompleteNegotiation(started.Fence.TransitionId, 9, 6).Accepted.Should().BeFalse();
        coordinator.CompleteNegotiation(started.Fence.TransitionId, 9, 7).Code.Should().Be("transition_completed");
    }

    [Fact]
    public void PresenceEpochChange_StalesReplacementAndRequiresFreshInventory()
    {
        var (coordinator, _, session, console) = Create();
        var started = coordinator.Start(session, console, null, 1, 1, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true);
        var changed = coordinator.PresenceChanged(2);

        changed.Code.Should().Be("presence_changed_reacquire");
        coordinator.ObserveInventory(Inventory(session, 1, User(3, "sid-a")), started.Fence!.TransitionId, 1).Accepted.Should().BeFalse();
    }

    [Fact]
    public void HelperRepair_IsAtMostOnceAndCloseCancelsTransition()
    {
        var (coordinator, _, session, console) = Create();
        var started = coordinator.Start(session, console, null, 1, 1, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true);
        coordinator.ObserveInventory(Inventory(session, 1, User(3, "sid-a")), started.Fence!.TransitionId, 1);
        coordinator.ObserveInventory(Inventory(session, 2, User(3, "sid-a")), started.Fence.TransitionId, 1);

        coordinator.RequestExactRepair(started.Fence.TransitionId, 1).RequestRepair.Should().BeTrue();
        coordinator.RequestExactRepair(started.Fence.TransitionId, 1).Accepted.Should().BeFalse();
        coordinator.Close().CancelPending.Should().BeTrue();
        coordinator.State.Should().Be(RemoteSupportTransitionState.Closed);
    }

    [Fact]
    public void SecondTransitionEvidence_CannotOverwriteAnActiveTransition()
    {
        var (coordinator, _, session, console) = Create();
        var first = coordinator.Start(session, console, null, 1, 1, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true);

        coordinator.Start(session, console, null, 1, 1, 2, RemoteSupportTransitionReason.LockDetected, true).Accepted.Should().BeFalse();
        coordinator.Fence!.TransitionId.Should().Be(first.Fence!.TransitionId);
    }

    private static (RemoteSupportTargetTransitionCoordinator Coordinator, FakeTimeProvider Clock, RemoteSupportSessionKey Session, RemoteSupportTargetDescriptor Console) Create()
    {
        var clock = new FakeTimeProvider();
        return (new RemoteSupportTargetTransitionCoordinator(clock, new RemoteSupportTransitionOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))), clock,
            new RemoteSupportSessionKey(1, Guid.NewGuid(), Guid.NewGuid()), new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.ConsoleLogin, 1));
    }

    private static RemoteSupportTargetInventorySnapshot Inventory(RemoteSupportSessionKey session, ulong sequence, params RemoteSupportTargetInventoryEntry[] entries) =>
        new(RemoteSupportV2ContractVersions.Current, session.TenantId, session.AgentId, sequence, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), entries);

    private static RemoteSupportTargetInventoryEntry User(int sessionId, string sid) => new(sessionId, "active", sid, false, true, false, false, true, true, "1.0");

    private static RemoteSupportPreparedTargetResult Prepared(RemoteSupportSessionKey session, RemoteSupportTargetDescriptor target, ulong sequence) =>
        new(RemoteSupportV2ContractVersions.Current, session.TenantId, session.AgentId, Guid.NewGuid(), Guid.NewGuid(), target, sequence, true, true, "ready", "ready", new RemoteSupportHelperRoute(Guid.NewGuid(), target.WindowsSessionId!.Value, target.UserSidHash!, "1.0"), DateTimeOffset.UtcNow, session);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
