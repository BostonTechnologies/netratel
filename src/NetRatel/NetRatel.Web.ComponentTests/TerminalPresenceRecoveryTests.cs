using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.Terminal;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class TerminalPresenceRecoveryTests : AsyncBunitContext
{
    private static readonly Guid Agent = Guid.Parse("10a6b281-cb92-4c42-b3b5-49c4d7a57560");
    private readonly TerminalServiceStub _terminals = new();

    public TerminalPresenceRecoveryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<ITerminalService>(_terminals);
        Services.AddSingleton(new AkkaAuthorityFanoutClient(new NoEndpointFactory(), null!, NullLogger<AkkaAuthorityFanoutClient>.Instance));
    }

    [Fact]
    public async Task Own_heartbeat_rotation_makes_an_inflight_GET_stale_not_a_takeover()
    {
        var cut = CreateDialog();
        var read = new TaskCompletionSource<TerminalSessionDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _terminals.ReadOverride = _ => read.Task;
        await cut.InvokeAsync(() => cut.Instance.Queue());
        await _terminals.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() =>
        {
            var rotated = Session("old", "opened") with { AttachmentLeaseId = "rotated" };
            cut.Instance.Set("_session", rotated);
            cut.Instance.Set("_attachmentHeartbeatSessionId", "old");
            cut.Instance.Set("_attachmentHeartbeatGeneration", (ulong?)7);
            cut.Instance.Set("_attachmentHeartbeatLeaseId", "rotated");
            _terminals.Current = rotated;
            _terminals.ReadOverride = null;
            read.SetResult(Session("old", "opened"));
        });
        await FinishRecovery(cut);
        cut.Instance.Session!.AttachmentLeaseId.Should().Be("rotated");
        cut.Instance.InputEnabled.Should().BeTrue();
        cut.Instance.Notice.Should().NotContain("another browser");
        _terminals.Opens.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_observer_cleanup_does_not_dispose_a_successor()
    {
        var cut = CreateDialog();
        var oldObserver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var oldCts = new CancellationTokenSource();
        using var successorCts = new CancellationTokenSource();
        await cut.InvokeAsync(() =>
        {
            cut.Instance.Set("_statusCts", oldCts);
            cut.Instance.Set("_statusTask", oldObserver.Task);
        });
        Task first = Task.CompletedTask;
        await cut.InvokeAsync(() => { first = cut.Instance.StopObservers(); });
        oldCts.IsCancellationRequested.Should().BeTrue();
        await cut.InvokeAsync(() => cut.Instance.StopObservers());
        await cut.InvokeAsync(() =>
        {
            cut.Instance.Set("_statusCts", successorCts);
            cut.Instance.Set("_statusTask", Task.CompletedTask);
            oldObserver.SetResult(true);
        });
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        successorCts.IsCancellationRequested.Should().BeFalse();
        await cut.InvokeAsync(() => cut.Instance.StopObservers());
    }

    [Fact]
    public async Task Presence_replacement_coalesces_signals_and_opens_one_fresh_shell()
    {
        var cut = CreateDialog();
        await cut.InvokeAsync(() => cut.Instance.Observe());
        var read = new TaskCompletionSource<TerminalSessionDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _terminals.ReadOverride = _ => read.Task;
        await cut.InvokeAsync(() => cut.Instance.Queue());
        await _terminals.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(async () =>
        {
            await cut.Instance.InputFailed();
            _terminals.Streams["old"].Writer.TryWrite(new("error", Reason: "terminal_presence_fence_replaced"));
            _terminals.ReadOverride = null;
            _terminals.Current = Session("old", "failed", "terminal_presence_fence_replaced");
            read.SetResult(_terminals.Current);
        });
        await FinishRecovery(cut);

        _terminals.Opens.Should().ContainSingle().Which.Should().Be((7, Agent, new OpenTerminalRequest("bash", 132, 42, "/work")));
        cut.Instance.Session!.SessionId.Should().Be("fresh-1");
        cut.Instance.InputEnabled.Should().BeTrue();
        cut.Instance.Notice.Should().Contain("new shell has started");
        _terminals.CloseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Same_presence_transport_loss_recovers_the_existing_shell()
    {
        var cut = CreateDialog();
        _terminals.Current = Session("old", "suspended");
        await Reconcile(cut);
        cut.Instance.InputEnabled.Should().BeFalse();
        _terminals.Opens.Should().BeEmpty();
        _terminals.Current = Session("old", "opened");
        await Reconcile(cut);
        cut.Instance.InputEnabled.Should().BeTrue();
        cut.Instance.Session!.SessionId.Should().Be("old");
        _terminals.Opens.Should().BeEmpty();
    }

    [Theory]
    [InlineData("closed", null)]
    [InlineData("failed", "terminal_process_exited")]
    [InlineData("failed", "terminal_open_timeout")]
    public async Task Ordinary_final_sessions_do_not_spawn_shells(string state, string? code)
    {
        var cut = CreateDialog();
        _terminals.Current = Session("old", state, code);
        await Reconcile(cut);
        _terminals.Opens.Should().BeEmpty();
        cut.Instance.InputEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_not_open_error_requires_authoritative_state_before_replacement()
    {
        var cut = CreateDialog();
        _terminals.ReadOverride = _ => Task.FromException<TerminalSessionDto?>(
            new HttpRequestException("terminal_not_open", null, HttpStatusCode.Conflict));
        await Reconcile(cut);
        _terminals.Opens.Should().BeEmpty();
        cut.Instance.InputEnabled.Should().BeFalse();
        _terminals.ReadOverride = null;
        _terminals.Current = Session("old", "opened");
        await Reconcile(cut);
        cut.Instance.InputEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Permission_denial_does_not_offer_replacement(HttpStatusCode status)
    {
        var cut = CreateDialog();
        _terminals.ReadOverride = _ => Task.FromException<TerminalSessionDto?>(new HttpRequestException("denied", null, status));
        await Reconcile(cut);
        _terminals.Opens.Should().BeEmpty();
        cut.Instance.CanReconnect.Should().BeFalse();
        cut.Instance.InputEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Known_attachment_takeover_prevents_automatic_replacement()
    {
        var cut = CreateDialog();
        await cut.InvokeAsync(() =>
        {
            cut.Instance.Set("_attachmentHeartbeatOwnershipLostSessionId", "old");
            cut.Instance.Set("_attachmentHeartbeatOwnershipLostGeneration", (ulong?)7);
        });
        _terminals.Current = Session("old", "failed", "terminal_presence_fence_replaced");
        await Reconcile(cut);
        _terminals.Opens.Should().BeEmpty();
        cut.Instance.CanReconnect.Should().BeFalse();
    }

    [Fact]
    public async Task Manual_retry_survives_expiry_of_a_confirmed_failed_sessions_tombstone()
    {
        var cut = CreateDialog();
        _terminals.Current = Session("old", "failed", "terminal_presence_fence_replaced");
        _terminals.OpenException = new HttpRequestException("response lost");
        await Reconcile(cut);
        _terminals.Opens.Should().ContainSingle();
        _terminals.OpenException = null;
        _terminals.ReadOverride = id => Task.FromResult<TerminalSessionDto?>(id == "old" ? null : _terminals.Current);
        await cut.InvokeAsync(() => cut.Instance.Reconnect());
        await FinishRecovery(cut);
        _terminals.Opens.Should().HaveCount(2);
        cut.Instance.Session!.SessionId.Should().Be("fresh-2");
        cut.Instance.InputEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_unconfirmed_session_never_automatically_opens_a_shell()
    {
        var cut = CreateDialog();
        _terminals.ReadOverride = _ => Task.FromResult<TerminalSessionDto?>(null);
        await Reconcile(cut);
        _terminals.Opens.Should().BeEmpty();
        cut.Instance.InputEnabled.Should().BeFalse();
        cut.Instance.CanReconnect.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_open_is_not_repeated_without_reconnect_action()
    {
        var cut = CreateDialog();
        _terminals.Current = Session("old", "failed", "terminal_presence_fence_replaced");
        _terminals.OpenException = new HttpRequestException("response lost");
        await Reconcile(cut);
        await Reconcile(cut);
        _terminals.Opens.Should().ContainSingle();
        cut.Instance.CanReconnect.Should().BeTrue();
        cut.Instance.InputEnabled.Should().BeFalse();
        _terminals.OpenException = null;
        await cut.InvokeAsync(() => cut.Instance.Reconnect());
        await FinishRecovery(cut);
        _terminals.Opens.Should().HaveCount(2);
        cut.Instance.Session!.SessionId.Should().Be("fresh-2");
    }

    [Fact]
    public async Task Closing_during_open_cleans_up_the_late_shell()
    {
        var cut = CreateDialog();
        _terminals.Current = Session("old", "failed", "terminal_presence_fence_replaced");
        _terminals.OpenGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await cut.InvokeAsync(() => cut.Instance.Queue());
        await _terminals.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() =>
        {
            cut.Instance.Set("_closedByUser", true);
            _terminals.OpenGate.SetResult(new("tracking", "late", "opened"));
        });
        await FinishRecovery(cut);
        _terminals.CloseCalls.Should().ContainSingle().Which.Should().Be("late");
        cut.Instance.Session!.SessionId.Should().Be("old");
        cut.Instance.InputEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Stale_browser_and_metadata_callbacks_cannot_replace_the_fresh_session()
    {
        var cut = CreateDialog();
        _terminals.Current = Session("old", "failed", "terminal_presence_fence_replaced");
        await Reconcile(cut);
        await cut.InvokeAsync(async () =>
        {
            await cut.Instance.OnGatewayTerminalBrowserActive("old", "7", "lease-old");
            cut.Instance.Merge(Session("old", "closed"));
        });
        cut.Instance.Session!.SessionId.Should().Be("fresh-1");
        cut.Instance.InputEnabled.Should().BeTrue();
        _terminals.Opens.Should().ContainSingle();
    }

    [Fact]
    public async Task Heartbeat_final_failure_reconciles_instead_of_marking_ownership_lost()
    {
        var cut = CreateDialog();
        _terminals.Current = Session("old", "failed", "terminal_presence_fence_replaced");
        _terminals.RenewException = new HttpRequestException("terminal_session_failed", null, HttpStatusCode.Conflict);
        await cut.InvokeAsync(async () =>
        {
            cut.Instance.Set("_attachmentHeartbeatSessionId", "old");
            cut.Instance.Set("_attachmentHeartbeatGeneration", (ulong?)7);
            cut.Instance.Set("_attachmentHeartbeatLeaseId", "lease-old");
            var result = await cut.Instance.OnGatewayTerminalAttachmentHeartbeat("old", "7", "lease-old", "browser", false);
            result.ShouldContinue.Should().BeFalse();
        });
        await FinishRecovery(cut);
        _terminals.Opens.Should().ContainSingle();
    }

    private IRenderedComponent<RecoveryDialog> CreateDialog()
    {
        var cut = Render<RecoveryDialog>(parameters => parameters
            .Add(x => x.TenantId, 7).Add(x => x.AgentId, Agent)
            .Add(x => x.Shell, "bash").Add(x => x.WorkingDirectory, "/work"));
        cut.Instance.Set("_session", Session("old", "opened"));
        cut.Instance.Set("_isOpening", false);
        return cut;
    }

    private static async Task Reconcile(IRenderedComponent<RecoveryDialog> cut)
    {
        await cut.InvokeAsync(() => cut.Instance.Queue());
        await FinishRecovery(cut);
    }

    private static async Task FinishRecovery(IRenderedComponent<RecoveryDialog> cut)
    {
        var task = cut.Instance.RecoveryTask;
        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static TerminalSessionDto Session(string id, string state, string? failure = null) =>
        new(id, Agent.ToString("N"), "bash", state, true, 1, 1, null, Cols: 132, Rows: 42, FailureCode: failure)
        { Generation = 7, AttachmentLeaseId = $"lease-{id}" };

    // Exercise the actual component lifecycle on a renderer without xterm's
    // rendering side effects. Browser module execution is covered by Playwright.
    public sealed class RecoveryDialog : ClientTerminalSessionDialog
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        public void Set(string name, object? value) => Field(name).SetValue(this, value);
        private object? Get(string name) => Field(name).GetValue(this);
        private static FieldInfo Field(string name) => typeof(ClientTerminalSessionDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private object? Call(string name, params object?[] args) => typeof(ClientTerminalSessionDialog).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, args);
        public void Queue() => Call("QueueSessionReconciliation");
        public Task InputFailed() => (Task)Call("HandleInputChannelStatusChangedAsync", TerminalInputChannelStatus.Failed("terminal_not_open"))!;
        public Task Observe() => (Task)Call("StartSessionObservationAsync")!;
        public Task StopObservers() => (Task)Call("DisposeStreamAsync", CancellationToken.None)!;
        public void Reconnect() => Call("ReconnectAsync");
        public void Merge(TerminalSessionDto session) => Call("MergeSessionMetadata", session);
        public Task RecoveryTask => (Task?)Get("_reconciliationTask") ?? Task.CompletedTask;
        public TerminalSessionDto? Session => (TerminalSessionDto?)Get("_session");
        public bool InputEnabled => (bool)Get("_inputEnabled")!;
        public bool CanReconnect => (bool)Get("_canReconnect")!;
        public string? Notice => (string?)Get("_terminalNotice");
    }

    private sealed class NoEndpointFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TerminalServiceStub : ITerminalService
    {
        public TerminalSessionDto Current { get; set; } = Session("old", "opened");
        public Func<string, Task<TerminalSessionDto?>>? ReadOverride { get; set; }
        public Exception? OpenException { get; set; }
        public Exception? RenewException { get; set; }
        public TaskCompletionSource<TerminalOpenResponse>? OpenGate { get; set; }
        public TaskCompletionSource<bool> OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(int, Guid, OpenTerminalRequest)> Opens { get; } = [];
        public List<string> CloseCalls { get; } = [];
        public Dictionary<string, Channel<TerminalStreamMessage>> Streams { get; } = [];
        public Task EnsureSubscribedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TerminalOpenResponse> OpenSessionAsync(string identity, OpenTerminalRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TerminalOpenResponse> OpenGatewaySessionAsync(int tenantId, Guid agentId, OpenTerminalRequest request, CancellationToken ct = default)
        {
            Opens.Add((tenantId, agentId, request));
            OpenStarted.TrySetResult(true);
            if (OpenGate is not null) return OpenGate.Task;
            if (OpenException is not null) return Task.FromException<TerminalOpenResponse>(OpenException);
            Current = Session($"fresh-{Opens.Count}", "opened");
            return Task.FromResult(new TerminalOpenResponse("tracking", Current.SessionId, "opened"));
        }
        public Task<TerminalSessionDto?> GetGatewaySessionAsync(string id, CancellationToken ct = default)
        {
            ReadStarted.TrySetResult(true);
            return ReadOverride?.Invoke(id) ?? Task.FromResult<TerminalSessionDto?>(Current);
        }
        public Task<TerminalSessionDto?> GetSessionAsync(string id, CancellationToken ct = default) => GetGatewaySessionAsync(id, ct);
        public Task<TerminalActionResponse> CloseAsync(string id, string? reason = null, CancellationToken ct = default)
        {
            CloseCalls.Add(id);
            return Task.FromResult(new TerminalActionResponse("tracking", "closed", id));
        }
        public Task<TerminalActionResponse> RenewGatewayAttachmentAsync(string id, ulong generation, string lease, string browser, bool claimOwnership, CancellationToken ct = default) =>
            RenewException is null
                ? Task.FromResult(new TerminalActionResponse("tracking", "renewed", id) { AttachmentLeaseId = lease })
                : Task.FromException<TerminalActionResponse>(RenewException);
        public Task<TerminalActionResponse> SendInputAsync(string id, string data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ITerminalInputChannel> OpenInputChannelAsync(string id, Action<TerminalInputChannelStatus>? onStatus = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TerminalActionResponse> ResizeAsync(string id, int cols, int rows, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TerminalSessionDto>> GetSessionsAsync(string identity, CancellationToken ct = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<TerminalStreamMessage> StreamSessionAsync(string id, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var stream = Channel.CreateUnbounded<TerminalStreamMessage>();
            Streams[id] = stream;
            await foreach (var message in stream.Reader.ReadAllAsync(ct)) yield return message;
        }
    }
}
