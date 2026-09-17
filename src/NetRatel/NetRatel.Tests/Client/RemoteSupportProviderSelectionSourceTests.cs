using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportProviderSelectionSourceTests
{
    [Fact]
    public void LoggedInHelperMissingOrStale_SelectsInteractivePreparationBeforeUnsupported()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var diagnostics = File.ReadAllText(Path.Combine(
            repoRoot,
            "src/NetRatel/NetRatel.Client/Service/RemoteSupport/RemoteSupportDesktopDiagnostics.cs"));
        var manager = File.ReadAllText(Path.Combine(
            repoRoot,
            "src/NetRatel/NetRatel.Client/Service/RemoteSupport/RemoteSupportSessionManager.cs"));

        diagnostics.Should().Contain("RemoteSupportProviderKinds.InteractiveUserHelper");
        diagnostics.Should().Contain("InteractiveHelperMissing");
        diagnostics.Should().Contain("InteractiveHelperVersionMismatchDetected");
        diagnostics.Should().Contain("Active console user is logged in, but no interactive helper is connected yet.");
        diagnostics.Should().Contain("Active console interactive helper version does not match the service version.");

        manager.Should().Contain("ReadTargetMode(signal.SessionId)");
        manager.Should().Contain("PrepareInteractiveHelperAsync");
        manager.Should().Contain("TryLaunchAsync");
        manager.Should().Contain("TryRepairAsync");
        manager.Should().Contain("IsOfferStillCurrent(signal)");
        manager.Should().NotContain("handover_request");
    }
}
