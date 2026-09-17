using FluentAssertions;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Client.Service.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportV2CharacterizationHarnessTests
{
    [Theory]
    [MemberData(nameof(InteractiveTargetScenarios))]
    public void Assist_target_preflight_uses_the_selected_WTS_session_only(
        object sessionsValue,
        int selectedSessionId,
        string sidHash,
        object? helperValue,
        string expectedCode,
        bool targetValid,
        bool helperReady)
    {
        var sessions = (IReadOnlyList<WindowsSessionInventoryItem>)sessionsValue;
        var helper = (ConnectedUserHelper?)helperValue;
        var preflight = new RemoteSupportAssistTargetPreflight(new FakeWindowsSessionSource(sessions));

        var result = preflight.Validate(
            new OpenRemoteSupportRequest(TargetMode: "assist_user", TargetWindowsSessionId: selectedSessionId, TargetUserSidHash: sidHash),
            helper,
            "0.4.92");

        result.Code.Should().Be(expectedCode);
        result.IsTargetValid.Should().Be(targetValid);
        result.HelperReady.Should().Be(helperReady);
    }

    [Fact]
    public void Target_resolver_never_coerces_console_and_interactive_targets()
    {
        RemoteSupportTargetResolver.TryResolve(new OpenRemoteSupportRequest(TargetMode: "login"), out var console, out _).Should().BeTrue();
        RemoteSupportTargetResolver.TryResolve(
            new OpenRemoteSupportRequest(TargetMode: "assist_user", TargetWindowsSessionId: 4, TargetUserSidHash: "sid-rdp"),
            out var interactive,
            out _).Should().BeTrue();
        RemoteSupportTargetResolver.TryResolve(
            new OpenRemoteSupportRequest(TargetMode: "login", TargetWindowsSessionId: 4, TargetUserSidHash: "sid-rdp"),
            out _,
            out var error).Should().BeFalse();

        console.Should().BeOfType<ConsoleLoginTarget>();
        interactive.Should().BeOfType<InteractiveSessionTarget>();
        error.Should().Contain("cannot include a user session target");
    }

    [Fact]
    public void Helper_process_validation_requires_the_claimed_WTS_session()
    {
        var hello = new RemoteDesktopHelperHello(SessionId: 4, ProcessId: 404, Version: "0.4.92", UserInteractive: true, User: "TEST\\rdp");

        RemoteSupportHelperProcessValidation.TryValidate(hello, new FakeHelperProcessInspector(5), out var error).Should().BeFalse();

        error.Should().Be("process_session_mismatch:5");
    }

    [Fact]
    public void Immutable_route_binds_only_its_selected_WTS_session()
    {
        var route = new RemoteSupportSessionRoute("interactive_user_helper", 1, null, null, null, null, TargetWindowsSessionId: 4);

        route.IsBoundToWindowsSession(4).Should().BeTrue();
        route.IsBoundToWindowsSession(8).Should().BeFalse();
    }

    [Fact]
    public void Provider_harness_exposes_the_retained_endpoint_readiness_trace()
    {
        var provider = new FakeProviderHarness();

        provider.Prepare();
        provider.MarkReady();
        provider.MarkFirstFrame();
        provider.MarkDataChannelReady();
        provider.MarkInputReady();
        provider.RecoverCapture();
        provider.RestartHelper();
        provider.Close();

        provider.Trace.Should().Equal(
            "unavailable", "preparing", "ready", "first_frame", "data_channel_ready", "input_ready",
            "capture_recovery", "helper_restart", "closed");
    }

    public static IEnumerable<object[]> InteractiveTargetScenarios()
    {
        yield return [Array.Empty<WindowsSessionInventoryItem>(), 4, "sid-rdp", (object)null!, "target_session_not_found", false, false];
        yield return [new[] { Session(1, "sid-console", active: true, console: true) }, 1, "sid-console", Helper(1), "target_helper_ready", true, true];
        yield return [new[] { Session(1, "sid-console", active: true, console: true, locked: true) }, 1, "sid-console", Helper(1), "target_session_not_assistable", false, false];
        yield return [new[] { Session(4, "sid-rdp") }, 4, "sid-rdp", Helper(4), "target_helper_ready", true, true];
        yield return [new[] { Session(4, "sid-rdp"), Session(8, "sid-rdp-two") }, 4, "sid-rdp", Helper(8), "target_helper_missing", true, false];
        yield return [new[] { Session(4, "sid-rdp", connected: false) }, 4, "sid-rdp", Helper(4), "target_session_not_assistable", false, false];
        yield return [new[] { Session(4, "sid-new") }, 4, "sid-old", Helper(4), "target_session_identity_mismatch", false, false];
    }

    private static WindowsSessionInventoryItem Session(
        int id,
        string sidHash,
        bool active = false,
        bool connected = true,
        bool locked = false,
        bool console = false) =>
        new(id, connected ? "active" : "disconnected", $"user{id}", "TEST", $"TEST\\user{id}", sidHash,
            console, active, connected, locked, false, !locked && connected, console ? "console" : "rdp",
            "interactive_user_helper", true, true, true, true, id * 100, "0.4.92");

    private static ConnectedUserHelper Helper(int sessionId) =>
        new(sessionId, sessionId * 100, "0.4.92", $"user{sessionId}", DateTimeOffset.UtcNow, new StreamWriter(Stream.Null));

    private sealed class FakeWindowsSessionSource(IReadOnlyList<WindowsSessionInventoryItem> sessions) : IRemoteSupportWindowsSessionSource
    {
        public IReadOnlyList<WindowsSessionInventoryItem> Capture() => sessions;
    }

    private sealed class FakeHelperProcessInspector(int actualSessionId) : IRemoteSupportHelperProcessInspector
    {
        public bool TryGetSessionId(int processId, out int sessionId, out string? error)
        {
            sessionId = actualSessionId;
            error = null;
            return true;
        }
    }

    private sealed class FakeProviderHarness
    {
        public List<string> Trace { get; } = ["unavailable"];
        public void Prepare() => Trace.Add("preparing");
        public void MarkReady() => Trace.Add("ready");
        public void MarkFirstFrame() => Trace.Add("first_frame");
        public void MarkDataChannelReady() => Trace.Add("data_channel_ready");
        public void MarkInputReady() => Trace.Add("input_ready");
        public void RecoverCapture() => Trace.Add("capture_recovery");
        public void RestartHelper() => Trace.Add("helper_restart");
        public void Close() => Trace.Add("closed");
    }
}
