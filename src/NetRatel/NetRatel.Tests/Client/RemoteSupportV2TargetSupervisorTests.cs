using FluentAssertions;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Client.Service.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportV2TargetSupervisorTests
{
    [Fact]
    public async Task PrepareAsync_does_not_fall_back_to_a_helper_for_another_WTS_session()
    {
        var requestedRepairSession = 0;
        var supervisor = CreateSupervisor(
            [Session(4, "sid-four"), Session(8, "sid-eight")],
            sessionId => sessionId == 8 ? Helper(8) : null,
            (_, sessionId, _, _) =>
            {
                requestedRepairSession = sessionId;
                return Task.FromResult<ConnectedUserHelper?>(null);
            });

        var result = await supervisor.PrepareAsync(Command(4, "sid-four"), CancellationToken.None);

        result.TargetValid.Should().BeTrue();
        result.ProviderReady.Should().BeFalse();
        result.Code.Should().Be("target_helper_unavailable");
        result.HelperRoute.Should().BeNull();
        requestedRepairSession.Should().Be(4);
    }

    [Fact]
    public async Task PrepareAsync_binds_the_repaired_helper_to_the_exact_target_and_nonce()
    {
        var repaired = Helper(4);
        var supervisor = CreateSupervisor(
            [Session(4, "sid-four"), Session(8, "sid-eight")],
            _ => null,
            (_, sessionId, _, _) => Task.FromResult<ConnectedUserHelper?>(sessionId == 4 ? repaired : null));
        var command = Command(4, "sid-four");

        var result = await supervisor.PrepareAsync(command, CancellationToken.None);

        result.TargetValid.Should().BeTrue();
        result.ProviderReady.Should().BeTrue();
        result.Code.Should().Be("target_helper_ready");
        result.RouteNonce.Should().Be(command.RouteNonce);
        result.HelperRoute.Should().Be(new RemoteSupportHelperRoute(repaired.RouteId, 4, "sid-four", "1.2.3"));
    }

    [Fact]
    public async Task PrepareAsync_passes_a_version_mismatched_exact_helper_to_repair()
    {
        var stale = new ConnectedUserHelper(4, 40, "0.4.119-rc.1", "user4", DateTimeOffset.UtcNow, new StreamWriter(Stream.Null));
        var repaired = Helper(4);
        ConnectedUserHelper? repairCandidate = null;

        var supervisor = CreateSupervisor(
            [Session(4, "sid-four")],
            sessionId => sessionId == 4 ? stale : null,
            (_, sessionId, helper, _) =>
            {
                sessionId.Should().Be(4);
                repairCandidate = helper;
                return Task.FromResult<ConnectedUserHelper?>(repaired);
            });

        var result = await supervisor.PrepareAsync(Command(4, "sid-four"), CancellationToken.None);

        repairCandidate.Should().BeSameAs(stale);
        result.ProviderReady.Should().BeTrue();
        result.HelperRoute.Should().Be(new RemoteSupportHelperRoute(repaired.RouteId, 4, "sid-four", "1.2.3"));
    }

    [Fact]
    public async Task PrepareAsync_accepts_a_prerelease_helper_matching_the_service_assembly_version()
    {
        var repairAttempts = 0;
        var helper = new ConnectedUserHelper(4, 40, "0.4.119-rc.1+c5f84a2", "user4", DateTimeOffset.UtcNow, new StreamWriter(Stream.Null));
        var supervisor = new RemoteSupportV2TargetSupervisor(
            new FakeWindowsSessionSource([Session(4, "sid-four")]),
            sessionId => sessionId == 4 ? helper : null,
            (_, _, _, _) =>
            {
                repairAttempts++;
                return Task.FromResult<ConnectedUserHelper?>(null);
            },
            "0.4.119.0",
            TimeProvider.System);

        var result = await supervisor.PrepareAsync(Command(4, "sid-four"), CancellationToken.None);

        result.TargetValid.Should().BeTrue();
        result.ProviderReady.Should().BeTrue();
        result.Code.Should().Be("target_helper_ready");
        repairAttempts.Should().Be(0);
    }

    [Fact]
    public async Task PrepareAsync_rejects_identity_rollover_without_attempting_repair()
    {
        var repairAttempts = 0;
        var supervisor = CreateSupervisor(
            [Session(4, "sid-new")],
            _ => null,
            (_, _, _, _) =>
            {
                repairAttempts++;
                return Task.FromResult<ConnectedUserHelper?>(null);
            });

        var result = await supervisor.PrepareAsync(Command(4, "sid-old"), CancellationToken.None);

        result.Code.Should().Be("target_session_identity_mismatch");
        result.TargetValid.Should().BeFalse();
        repairAttempts.Should().Be(0);
    }

    [Fact]
    public async Task PrepareAsync_rejects_console_media_until_an_exact_active_console_provider_is_proven()
    {
        var supervisor = CreateSupervisor([Session(1, "sid-console", isConsole: true)], _ => null, (_, _, _, _) => Task.FromResult<ConnectedUserHelper?>(null));
        var command = new RemoteSupportPrepareTargetCommand(
            RemoteSupportV2ContractVersions.Current,
            7,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new RemoteSupportOperatorBinding("operator"),
            new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.Console),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(20));

        var result = await supervisor.PrepareAsync(command, CancellationToken.None);

        result.Code.Should().Be("console_helper_unavailable");
        result.TargetValid.Should().BeTrue();
        result.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsync_binds_console_login_to_the_current_active_console_provider_only()
    {
        var providerRoute = Guid.NewGuid();
        var supervisor = new RemoteSupportV2TargetSupervisor(
            new FakeWindowsSessionSource([Session(3, "sid-console", isConsole: true)]),
            _ => null,
            (_, _, _, _) => Task.FromResult<ConnectedUserHelper?>(null),
            "1.2.3",
            TimeProvider.System,
            (_, _) => Task.FromResult<RemoteSupportConsoleProviderReadiness?>(
                new(providerRoute, 3, 1234, "1.2.3")));
        var command = new RemoteSupportPrepareTargetCommand(
            RemoteSupportV2ContractVersions.Current,
            7,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new RemoteSupportOperatorBinding("operator"),
            new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.ConsoleLogin),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(20));

        var result = await supervisor.PrepareAsync(command, CancellationToken.None);

        result.Target.Kind.Should().Be(RemoteSupportV2TargetKinds.ConsoleLogin);
        result.TargetValid.Should().BeTrue();
        result.ProviderReady.Should().BeTrue();
        result.Code.Should().Be("console_helper_ready");
        result.HelperRoute.Should().Be(new RemoteSupportHelperRoute(providerRoute, 3, "console-login", "1.2.3"));
    }

    [Fact]
    public async Task PrepareAsync_rejects_console_provider_when_it_reports_another_WTS_session()
    {
        var supervisor = new RemoteSupportV2TargetSupervisor(
            new FakeWindowsSessionSource([Session(3, "sid-console", isConsole: true)]),
            _ => null,
            (_, _, _, _) => Task.FromResult<ConnectedUserHelper?>(null),
            "1.2.3",
            TimeProvider.System,
            (_, _) => Task.FromResult<RemoteSupportConsoleProviderReadiness?>(new(Guid.NewGuid(), 8, 1234, "1.2.3")));
        var command = new RemoteSupportPrepareTargetCommand(
            RemoteSupportV2ContractVersions.Current, 7, Guid.NewGuid(), Guid.NewGuid(), new("operator"),
            new(RemoteSupportV2TargetKinds.ConsoleLogin), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(20));

        var result = await supervisor.PrepareAsync(command, CancellationToken.None);

        result.TargetValid.Should().BeTrue();
        result.ProviderReady.Should().BeFalse();
        result.HelperRoute.Should().BeNull();
    }

    private static RemoteSupportV2TargetSupervisor CreateSupervisor(
        IReadOnlyList<WindowsSessionInventoryItem> sessions,
        Func<int, ConnectedUserHelper?> getHelper,
        Func<Guid, int, ConnectedUserHelper?, CancellationToken, Task<ConnectedUserHelper?>> repair) =>
        new(new FakeWindowsSessionSource(sessions), getHelper, repair, "1.2.3", TimeProvider.System);

    private static RemoteSupportPrepareTargetCommand Command(int sessionId, string sidHash) =>
        new(
            RemoteSupportV2ContractVersions.Current,
            7,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new RemoteSupportOperatorBinding("operator"),
            new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, sessionId, sidHash, 1),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(20));

    private static WindowsSessionInventoryItem Session(int id, string sidHash, bool isConsole = false) =>
        new(id, "active", $"user{id}", "TEST", $"TEST\\user{id}", sidHash, isConsole, true, true, false, false, false,
            isConsole ? "console" : "rdp", null, false, false, true, false, null, null);

    private static ConnectedUserHelper Helper(int sessionId) =>
        new(sessionId, sessionId * 10, "1.2.3", $"user{sessionId}", DateTimeOffset.UtcNow, new StreamWriter(Stream.Null));

    private sealed class FakeWindowsSessionSource(IReadOnlyList<WindowsSessionInventoryItem> sessions) : IRemoteSupportWindowsSessionSource
    {
        public IReadOnlyList<WindowsSessionInventoryItem> Capture() => sessions;
    }
}
