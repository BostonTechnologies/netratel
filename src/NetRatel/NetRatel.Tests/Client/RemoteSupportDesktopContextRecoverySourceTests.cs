using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportDesktopContextRecoverySourceTests
{
    [Fact]
    public void Interactive_capture_uses_a_stable_desktop_bound_worker_and_bounded_recovery()
    {
        var manager = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportInteractiveWebRtcManager.cs");
        var providers = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportCaptureProviders.cs");

        manager.Should().Contain("new Thread(() => DesktopCaptureLoop");
        manager.Should().NotContain("Task.Run(() => DesktopCaptureLoopAsync");
        manager.Should().Contain("IRemoteSupportThreadBoundCaptureProvider");
        providers.Should().Contain("InteractiveDesktopCaptureProvider : IRemoteSupportCaptureProvider, IRemoteSupportThreadBoundCaptureProvider");
        providers.Should().Contain("InitialFrameDeadline = TimeSpan.FromSeconds(30)");
        providers.Should().Contain("DesktopTransitionDeadline = TimeSpan.FromSeconds(15)");
    }

    [Fact]
    public void Gateway_provider_runtime_repairs_only_the_immutable_selected_session()
    {
        var manager = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportSessionManager.cs");

        manager.Should().Contain("RepairExhaustedInteractiveDesktopAsync");
        manager.Should().Contain("route.TargetWindowsSessionId != helper.SessionId");
        manager.Should().Contain("route.Provider, RemoteSupportProviderKinds.InteractiveUserHelper");
        manager.Should().Contain("TryRepairAsync(");
        manager.Should().Contain("_desktopRecoveryRepairs.GetOrAdd");
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        return File.ReadAllText(Path.Combine([root, "src", "NetRatel", .. segments]));
    }
}
