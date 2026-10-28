using System.Text.Json;
using AwesomeAssertions;
using ModelContextProtocol.Protocol;
using NetRatel.Client.Service.ComputerUse;
using NetRatel.Shared.Contracts.ComputerUse;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ComputerUseProviderTests
{
    private static readonly ComputerUseTarget Target = new(2, new string('a', 64), 123, "456");
    private static readonly ComputerUseOptions Options = new() { Enabled = true };

    [Fact]
    public async Task UntrustedPipeAndDisabledEndpointNeverCreateProvider()
    {
        var creations = 0;
        await using var untrusted = new ComputerUseInteractiveHost(false, () => false, new FakeDesktop(), (_, _) => { creations++; return new FakeProvider(); });
        (await untrusted.ExecuteAsync(new(Command(), Options))).Code.Should().Be("untrusted_service_pipe");
        await using var disabled = new ComputerUseInteractiveHost(true, () => false, new FakeDesktop(), (_, _) => { creations++; return new FakeProvider(); });
        (await disabled.ExecuteAsync(new(Command(), Options with { Enabled = false }))).Code.Should().Be("feature_disabled");
        creations.Should().Be(0);
    }

    [Fact]
    public async Task ExactSessionAndControllerAreFenced()
    {
        var provider = new FakeProvider();
        await using var host = Host(provider);
        var open = Command();
        (await host.ExecuteAsync(new(open, Options))).Status.Should().Be(ComputerUseStatuses.Observed);
        (await host.ExecuteAsync(new(Command(), Options))).Code.Should().Be("controller_conflict");
        var wrongTarget = Next(open, ComputerUseOperations.Observe, 2) with { Target = Target with { WindowsSessionId = 9 } };
        (await host.ExecuteAsync(new(wrongTarget, Options))).Code.Should().Be("session_binding_mismatch");
        provider.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false, "session_binding_mismatch")]
    [InlineData(true, "stale_session_revision")]
    public async Task QueuedCommandRechecksTheLeaseEstablishedByOpen(bool staleRevision, string expectedCode)
    {
        var desktop = new FakeDesktop();
        var provider = new FakeProvider();
        await using var host = Host(provider, desktop);
        var open = Command();
        var queuedCommand = Next(open, ComputerUseOperations.Pause, staleRevision ? 1 : 2) with
        {
            Target = staleRevision ? Target : Target with { WindowId = "789" }
        };
        Task<ComputerUseResult>? queued = null;
        desktop.OnValidate = () =>
        {
            desktop.OnValidate = null;
            // Open holds the operation gate but has not installed its lease yet.
            queued = host.ExecuteAsync(new(queuedCommand, Options));
        };
        (await host.ExecuteAsync(new(open, Options))).Status.Should().Be(ComputerUseStatuses.Observed);
        queued.Should().NotBeNull();
        (await queued!).Code.Should().Be(expectedCode);
        (await host.ExecuteAsync(new(Next(open, ComputerUseOperations.Observe, 3), Options))).Status.Should().Be(ComputerUseStatuses.Observed);
        provider.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task ObservationIsSingleUseAndReplayDoesNotRepeatInputOrRetainDesktopContent()
    {
        var provider = new FakeProvider();
        await using var host = Host(provider);
        var open = Command();
        var observed = await host.ExecuteAsync(new(open, Options));
        var action = Next(open, ComputerUseOperations.Act, 2) with
        {
            ObservationId = observed.Observation!.ObservationId,
            Action = new(ComputerUseActionKinds.Invoke, ElementId: "button")
        };
        var executed = await host.ExecuteAsync(new(action, Options));
        executed.Status.Should().Be(ComputerUseStatuses.AppliedUnverified);
        executed.Observation.Should().NotBeNull();
        var replay = await host.ExecuteAsync(new(action, Options));
        replay.Status.Should().Be(executed.Status);
        replay.Observation.Should().BeNull("replay receipts must not retain desktop contents");
        provider.Actions.Should().Be(1);
        var stale = action with { OperationId = Guid.NewGuid(), SessionRevision = 3 };
        (await host.ExecuteAsync(new(stale, Options))).Code.Should().Be("stale_observation");
        provider.Actions.Should().Be(1);
    }

    [Fact]
    public async Task ManualInputInvalidatesObservedStateBeforeProviderDispatch()
    {
        var desktop = new FakeDesktop();
        var provider = new FakeProvider();
        await using var host = Host(provider, desktop);
        var open = Command();
        var observed = await host.ExecuteAsync(new(open, Options));
        desktop.InputTick++;
        var action = Next(open, ComputerUseOperations.Act, 2) with
        {
            ObservationId = observed.Observation!.ObservationId,
            Action = new(ComputerUseActionKinds.Invoke, ElementId: "button")
        };
        (await host.ExecuteAsync(new(action, Options))).Code.Should().Be("user_interference");
        provider.Actions.Should().Be(0);
    }

    [Fact]
    public async Task PauseCancelsInFlightInputAndResumeRequiresNewObservation()
    {
        var provider = new FakeProvider { BlockAction = true };
        var replacement = new FakeProvider();
        var created = 0;
        await using var host = new ComputerUseInteractiveHost(true, () => false, new FakeDesktop(), (_, _) => ++created == 1 ? provider : replacement);
        var open = Command();
        var observed = await host.ExecuteAsync(new(open, Options));
        var action = Next(open, ComputerUseOperations.Act, 2) with
        {
            ObservationId = observed.Observation!.ObservationId,
            Action = new(ComputerUseActionKinds.Invoke, ElementId: "button")
        };
        var inFlight = host.ExecuteAsync(new(action, Options));
        await provider.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var paused = await host.ExecuteAsync(new(Next(open, ComputerUseOperations.Pause, 3), Options));
        (await inFlight).MayHaveApplied.Should().BeTrue();
        paused.Code.Should().Be("session_paused");
        provider.Disposed.Should().BeTrue();
        (await host.ExecuteAsync(new(Next(open, ComputerUseOperations.Resume, 4), Options))).Status.Should().Be(ComputerUseStatuses.Completed);
        (await host.ExecuteAsync(new(action with { OperationId = Guid.NewGuid(), SessionRevision = 5 }, Options))).Code.Should().Be("stale_observation");
        (await host.ExecuteAsync(new(Next(open, ComputerUseOperations.Observe, 6), Options))).Status.Should().Be(ComputerUseStatuses.Observed);
        created.Should().Be(2);
    }

    [Fact]
    public async Task ManualTakeoverRevokesProviderAndPreventsReplayOrNewController()
    {
        var manual = false;
        var provider = new FakeProvider();
        await using var host = new ComputerUseInteractiveHost(true, () => manual, new FakeDesktop(), (_, _) => provider);
        var open = Command();
        await host.ExecuteAsync(new(open, Options));
        manual = true;
        await host.RevokeAllAsync();
        provider.Disposed.Should().BeTrue();
        (await host.ExecuteAsync(new(open with { OperationId = Guid.NewGuid() }, Options))).Code.Should().Be("session_revoked");
        (await host.ExecuteAsync(new(Command(), Options))).Code.Should().Be("manual_support_active");
        provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueuedOpenCannotStartAfterRevokeAcknowledgement()
    {
        var creations = 0;
        await using var host = new ComputerUseInteractiveHost(true, () => false, new FakeDesktop(), (_, _) => { creations++; return new FakeProvider(); });
        var open = Command();
        var queued = host.RegisterOperation(open.OperationId);
        var mayExecute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = Task.Run(async () =>
        {
            await mayExecute.Task;
            return await host.ExecuteRegisteredAsync(new(open, Options), queued);
        });
        await host.RevokeAllAsync(); // The equivalent of the service receiving its revoke acknowledgement.
        mayExecute.SetResult();
        (await execution).Status.Should().Be(ComputerUseStatuses.Cancelled);
        creations.Should().Be(0);
        (await host.ExecuteAsync(new(Command(), Options))).Status.Should().Be(ComputerUseStatuses.Observed);
        creations.Should().Be(1, "a newly authorized command belongs to the new generation");
    }

    [Fact]
    public async Task CancelBeforeDispatchPreventsProviderCreation()
    {
        var creations = 0;
        await using var host = new ComputerUseInteractiveHost(true, () => false, new FakeDesktop(), (_, _) => { creations++; return new FakeProvider(); });
        var open = Command();
        var queued = host.RegisterOperation(open.OperationId);
        host.Cancel(open.OperationId);
        (await host.ExecuteRegisteredAsync(new(open, Options), queued)).Status.Should().Be(ComputerUseStatuses.Cancelled);
        creations.Should().Be(0);
    }

    [Fact]
    public async Task DiscoveryFailureDoesNotRevokeAnotherOwnersActiveLease()
    {
        var provider = new FakeProvider();
        var creations = 0;
        await using var host = new ComputerUseInteractiveHost(true, () => false, new FakeDesktop(), (_, _) =>
            ++creations == 1 ? provider : new RefusingProvider());
        var open = Command();
        await host.ExecuteAsync(new(open, Options));
        var discovery = Command(ComputerUseOperations.Capabilities) with { Target = Target with { ProcessId = null, WindowId = null } };
        (await host.ExecuteAsync(new(discovery, Options))).Code.Should().Be("driver_not_installed");
        provider.Disposed.Should().BeFalse();
        (await host.ExecuteAsync(new(Next(open, ComputerUseOperations.Observe, 2), Options))).Status.Should().Be(ComputerUseStatuses.Observed);
    }

    [Theory]
    [InlineData("confirmed", false, "[]", ComputerUseStatuses.AppliedUnverified)]
    [InlineData("confirmed", false, "[{\"kind\":\"value_readback\"}]", ComputerUseStatuses.Verified)]
    [InlineData("partial", false, "[]", ComputerUseStatuses.AppliedUnverified)]
    [InlineData("unverifiable", false, "[]", ComputerUseStatuses.AppliedUnverified)]
    [InlineData("refused", true, "[]", ComputerUseStatuses.Refused)]
    [InlineData("partial", true, "[]", ComputerUseStatuses.Unknown)]
    public void DriverEffectsRequireExplicitReadbackEvidence(string effect, bool error, string evidence, string expected)
    {
        using var document = JsonDocument.Parse($$"""{"effect":"{{effect}}","route":"accessibility","evidence":{{evidence}}} """);
        var result = CuaDriverComputerUseProvider.MapActionResult(Command(ComputerUseOperations.Act), new CallToolResult
        {
            IsError = error,
            StructuredContent = document.RootElement.Clone()
        });
        result.Status.Should().Be(expected);
        result.MayHaveApplied.Should().Be(effect != "refused");
    }

    [Fact]
    public async Task DriverObservationRejectsWrongWindowAndDoesNotSilentlyCropState()
    {
        await using var provider = new CuaDriverComputerUseProvider(Options);
        using var wrongWindow = JsonDocument.Parse("{\"pid\":123,\"window_id\":999}");
        Action wrong = () => provider.ParseObservation(Target, new CallToolResult { StructuredContent = wrongWindow.RootElement.Clone() });
        wrong.Should().Throw<ComputerUseRefusalException>().Which.Code.Should().Be("provider_wrong_target");
        using var large = JsonDocument.Parse(JsonSerializer.Serialize(new { pid = 123, window_id = 456, tree = new string('x', ComputerUseContract.MaximumStateLength) }));
        Action oversized = () => provider.ParseObservation(Target, new CallToolResult { StructuredContent = large.RootElement.Clone() });
        oversized.Should().Throw<ComputerUseRefusalException>().Which.Code.Should().Be("provider_result_too_large");
    }

    [Theory]
    [InlineData(ComputerUseActionKinds.Invoke, "click")]
    [InlineData(ComputerUseActionKinds.Click, "click")]
    [InlineData(ComputerUseActionKinds.Type, "type_text")]
    [InlineData(ComputerUseActionKinds.Key, "press_key")]
    [InlineData(ComputerUseActionKinds.Scroll, "scroll")]
    public async Task DriverRequestsMatchPinnedWindowsRuntimeSchemas(string actionKind, string expectedTool)
    {
        // Independent fixture extracted from the Windows tool schemas at the pinned source commit:
        // https://github.com/trycua/cua/blob/85e2b1318bd77ccf3cfa88ccdf875a4bb7eaed19/libs/cua-driver/rust/crates/platform-windows/src/tools/impl_.rs
        // The portable SDK manifest uses target objects; these live Windows MCP schemas use flat pid/window_id.
        var schemas = new Dictionary<string, (string[] Allowed, string[] Required)>
        {
            ["click"] = (["session", "pid", "window_id", "element_token", "action", "capture_id", "x", "y", "button", "count", "modifier", "from_zoom", "scope", "delivery_mode"], []),
            ["type_text"] = (["session", "scope", "pid", "text", "window_id", "element_token", "x", "y", "delay_ms", "delivery_mode"], ["text"]),
            ["press_key"] = (["session", "scope", "pid", "key", "modifiers", "element_token", "x", "y", "window_id", "delivery_mode"], ["key"]),
            ["scroll"] = (["session", "pid", "direction", "by", "amount", "x", "y", "window_id", "element_token", "scope", "delivery_mode"], ["direction"])
        };
        await using var provider = new CuaDriverComputerUseProvider(Options);
        using var observation = JsonDocument.Parse("""{"pid":123,"window_id":456,"capture_id":"capture-1","screenshot_width":1,"screenshot_height":1,"elements":[{"element_token":"s00000001:1","role":"button"}]}""");
        provider.ParseObservation(Target, new CallToolResult
        {
            StructuredContent = observation.RootElement.Clone(),
            Content = [ImageContentBlock.FromBytes(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0ioAAAAASUVORK5CYII="), "image/png")]
        });
        var action = actionKind switch
        {
            ComputerUseActionKinds.Invoke => new ComputerUseAction(actionKind, ElementId: "s00000001:1"),
            ComputerUseActionKinds.Type => new ComputerUseAction(actionKind, ElementId: "s00000001:1", Text: "verified field"),
            ComputerUseActionKinds.Key => new ComputerUseAction(actionKind, Key: "Enter"),
            ComputerUseActionKinds.Scroll => new ComputerUseAction(actionKind, X: 0, Y: 0, ScrollY: 2),
            _ => new ComputerUseAction(actionKind, X: 0, Y: 0, Button: "left")
        };
        var request = provider.BuildActionRequest(Command(ComputerUseOperations.Act) with { Action = action });
        request.Tool.Should().Be(expectedTool);
        request.Arguments.Keys.Except(schemas[request.Tool].Allowed).Should().BeEmpty();
        schemas[request.Tool].Required.Except(request.Arguments.Keys).Should().BeEmpty();
        request.Arguments["pid"].Should().Be(123);
        request.Arguments["window_id"].Should().Be(456L);
        request.Arguments["delivery_mode"].Should().Be("background");
        request.Arguments.Keys.Should().NotContain("target");
        if (actionKind == ComputerUseActionKinds.Click) request.Arguments["capture_id"].Should().Be("capture-1");
    }

    private static ComputerUseInteractiveHost Host(FakeProvider provider, FakeDesktop? desktop = null) =>
        new(true, () => false, desktop ?? new FakeDesktop(), (_, _) => provider);
    private static ComputerUseCommand Command(string operation = ComputerUseOperations.Open) =>
        new(ComputerUseContract.Version, Guid.NewGuid(), Guid.NewGuid(), operation, Target, ComputerUseProviders.CuaDriver,
            true, DateTimeOffset.UtcNow.AddMinutes(1), SessionRevision: 1);
    private static ComputerUseCommand Next(ComputerUseCommand prior, string operation, long revision) =>
        prior with { OperationId = Guid.NewGuid(), Operation = operation, SessionRevision = revision };

    private sealed class FakeDesktop : IComputerUseDesktop
    {
        internal uint InputTick { get; set; } = 1;
        internal Action? OnValidate { get; set; }
        public uint LastInputTick => InputTick;
        public string Fingerprint(ComputerUseTarget target) => $"{target.ProcessId}:{target.WindowId}:same-process-start-time";
        public void Validate(ComputerUseTarget target, bool requireWindow)
        {
            OnValidate?.Invoke();
            if (target.WindowsSessionId != Target.WindowsSessionId || target.UserSidHash != Target.UserSidHash)
                throw new ComputerUseRefusalException("wrong_windows_session", "Wrong session.");
        }
    }

    private sealed class FakeProvider : IComputerUseProvider
    {
        internal int Calls { get; private set; }
        internal int Actions { get; private set; }
        internal bool BlockAction { get; init; }
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource ActionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource ActionCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ComputerUseResult> ExecuteAsync(ComputerUseCommand command, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (command.Operation == ComputerUseOperations.Act)
            {
                Actions++;
                ActionEntered.TrySetResult();
                if (BlockAction) await ActionCompletion.Task.WaitAsync(cancellationToken);
            }
            return new(ComputerUseContract.Version, command.OperationId, command.SessionId,
                command.Operation == ComputerUseOperations.Act ? ComputerUseStatuses.AppliedUnverified : ComputerUseStatuses.Observed,
                Provider: command.Provider,
                Observation: new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, command.Target, "current state"),
                MayHaveApplied: command.Operation == ComputerUseOperations.Act);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class RefusingProvider : IComputerUseProvider
    {
        public Task<ComputerUseResult> ExecuteAsync(ComputerUseCommand command, CancellationToken cancellationToken = default) =>
            throw new ComputerUseRefusalException("driver_not_installed", "Driver is unavailable.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
