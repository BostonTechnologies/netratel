using FluentAssertions;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportTargetResolverTests
{
    [Fact]
    public void LoginTarget_RejectsUserSessionFields()
    {
        var request = new OpenRemoteSupportRequest(
            TargetMode: "login",
            TargetWindowsSessionId: 4,
            TargetUserSidHash: "sid-hash");

        RemoteSupportTargetResolver.TryResolve(request, out var target, out var error).Should().BeFalse();
        target.Should().BeNull();
        error.Should().Contain("cannot include");
    }

    [Fact]
    public void AssistTarget_RequiresSessionAndIdentity()
    {
        var request = new OpenRemoteSupportRequest(TargetMode: "assist_user");

        RemoteSupportTargetResolver.TryResolve(request, out var target, out var error).Should().BeFalse();
        target.Should().BeNull();
        error.Should().Contain("session id");
    }

    [Fact]
    public void AssistTarget_PreservesImmutableSessionIdentity()
    {
        var request = new OpenRemoteSupportRequest(
            TargetMode: "assist_user",
            TargetWindowsSessionId: 7,
            TargetUserSidHash: "sid-hash",
            InventorySequence: 42);

        RemoteSupportTargetResolver.TryResolve(request, out var target, out var error).Should().BeTrue();
        error.Should().BeNull();
        target.Should().Be(new InteractiveSessionTarget(7, "sid-hash", 42));
    }

    [Fact]
    public void AutoTarget_RemainsAvailableForRollingCompatibility()
    {
        RemoteSupportTargetResolver.TryResolve(
            new OpenRemoteSupportRequest(TargetMode: "auto"),
            out var target,
            out var error).Should().BeTrue();

        error.Should().BeNull();
        target.Should().BeOfType<LegacyAutomaticTarget>();
    }
}
