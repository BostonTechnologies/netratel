using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.Terminal;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientTerminalSessionDialogLifecycleTests
{
    private static readonly Guid AgentId = Guid.Parse("10a6b281-cb92-4c42-b3b5-49c4d7a57560");

    [Fact]
    public void Sending_telemetry_is_not_a_transport_health_signal()
    {
        TerminalInputChannelStatus.Sending(frames: 3, bytes: 18).State.Should().Be("sending");
    }

    [Fact]
    public async Task Recoverable_close_failure_preserves_gateway_handle_for_reattach()
    {
        var storage = new SessionStorageJsRuntime(CreateHandleJson("session-1"));
        var terminals = new CloseResultTerminalService(
            new HttpRequestException("terminal transport reconnecting", null, HttpStatusCode.ServiceUnavailable));
        var dialog = CreateDialog(storage, terminals);

        await CloseSessionIfActiveAsync(dialog, "operator closed");

        terminals.CloseCalls.Should().Be(1);
        storage.SetItemCalls.Should().Be(0, "a failed close must leave the browser recovery handle intact");
        storage.StoredPayload.Should().Contain("session-1");
    }

    [Fact]
    public async Task Accepted_close_removes_gateway_handle_only_after_server_admission()
    {
        var storage = new SessionStorageJsRuntime(CreateHandleJson("session-1"));
        var terminals = new CloseResultTerminalService();
        var dialog = CreateDialog(storage, terminals);

        await CloseSessionIfActiveAsync(dialog, "operator closed");

        terminals.CloseCalls.Should().Be(1);
        storage.SetItemCalls.Should().Be(1);
        storage.StoredPayload.Should().NotContain("session-1");
    }

    [Fact]
    public async Task Late_open_cleanup_failure_also_preserves_gateway_handle_for_reconciliation()
    {
        var storage = new SessionStorageJsRuntime(CreateHandleJson("session-1"));
        var terminals = new CloseResultTerminalService(
            new HttpRequestException("terminal transport reconnecting", null, HttpStatusCode.ServiceUnavailable));
        var dialog = CreateDialog(storage, terminals);

        await CloseOpenedSessionAfterDialogCloseAsync(dialog, "session-1");

        terminals.CloseCalls.Should().Be(1);
        storage.SetItemCalls.Should().Be(0);
        storage.StoredPayload.Should().Contain("session-1");
    }

    [Fact]
    public async Task BrowserHeartbeat_OnlyRenewsTheCurrentNonClosingGenerationAndLeaseFence()
    {
        var storage = new SessionStorageJsRuntime(CreateHandleJson("session-1"));
        var terminals = new CloseResultTerminalService();
        var dialog = CreateDialog(storage, terminals, closedByUser: false);

        (await dialog.OnGatewayTerminalAttachmentHeartbeat(
            "session-1", "8", "lease-7", "browser-7", claimOwnership: true)).ShouldContinue.Should().BeFalse();
        var renewed = await dialog.OnGatewayTerminalAttachmentHeartbeat(
            "session-1", "7", "lease-7", "browser-7", claimOwnership: true);
        renewed.ShouldContinue.Should().BeTrue();
        renewed.OwnershipConfirmed.Should().BeTrue();

        SetPrivateField(dialog, "_session", new TerminalSessionDto(
            "session-1",
            AgentId.ToString("N"),
            "bash",
            "closing",
            HasWebSocket: true,
            1,
            1,
            CloseReason: null)
        {
            Generation = 7,
            AttachmentLeaseId = "lease-7"
        });
        (await dialog.OnGatewayTerminalAttachmentHeartbeat(
            "session-1", "7", "lease-7", "browser-7", claimOwnership: false)).ShouldContinue.Should().BeFalse();

        terminals.RenewCalls.Should().ContainSingle().Which.Should().Be(("session-1", 7UL, "lease-7", "browser-7", true));
    }

    [Fact]
    public void ServerStatusPoll_DoesNotLetAnOlderCircuitReclaimARotatedBrowserLease()
    {
        var dialog = CreateDialog(new SessionStorageJsRuntime(CreateHandleJson("session-1")), new CloseResultTerminalService(), closedByUser: false);
        SetPrivateField(dialog, "_attachmentHeartbeatId", 1L);
        SetPrivateField(dialog, "_attachmentHeartbeatSessionId", "session-1");
        SetPrivateField(dialog, "_attachmentHeartbeatGeneration", 7UL);
        SetPrivateField(dialog, "_attachmentHeartbeatLeaseId", "lease-7");

        var current = GetPrivateField(dialog, "_session") as TerminalSessionDto
            ?? throw new InvalidOperationException("Expected a current terminal session.");
        var merge = typeof(ClientTerminalSessionDialog).GetMethod(
            "MergeSessionMetadata",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ClientTerminalSessionDialog), "MergeSessionMetadata");
        merge.Invoke(dialog, [current with { AttachmentLeaseId = "lease-8" }]);

        (GetPrivateField(dialog, "_attachmentHeartbeatOwnershipLostSessionId") as string).Should().Be("session-1");
        ((ulong?)GetPrivateField(dialog, "_attachmentHeartbeatOwnershipLostGeneration")).Should().Be(7UL);
        GetPrivateField(dialog, "_attachmentHeartbeatId").Should().BeNull();
    }

    private static ClientTerminalSessionDialog CreateDialog(
        SessionStorageJsRuntime storage,
        ITerminalService terminals,
        bool closedByUser = true)
    {
        var dialog = new ClientTerminalSessionDialog();

        SetProperty(dialog, "TenantId", 7);
        SetProperty(dialog, "AgentId", AgentId);
        SetProperty(dialog, "Shell", "bash");
        SetProperty(dialog, "Term", terminals);
        SetProperty<ILogger<ClientTerminalSessionDialog>>(dialog, "Logger", NullLogger<ClientTerminalSessionDialog>.Instance);
        SetProperty<IJSRuntime>(dialog, "JS", storage);
        SetPrivateField(dialog, "_closedByUser", closedByUser);
        SetPrivateField(dialog, "_session", new TerminalSessionDto(
            "session-1",
            AgentId.ToString("N"),
            "bash",
            "opened",
            HasWebSocket: true,
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CloseReason: null,
            Backend: "agent-pty",
            Cols: 80,
            Rows: 24,
            Capabilities: ["akka-gateway"],
            Transport: TerminalTransportKind.AkkaGateway)
        {
            Generation = 7,
            AttachmentLeaseId = "lease-7"
        });
        return dialog;
    }

    private static async Task CloseSessionIfActiveAsync(ClientTerminalSessionDialog dialog, string reason)
    {
        var method = typeof(ClientTerminalSessionDialog).GetMethod(
            "CloseSessionIfActiveAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ClientTerminalSessionDialog), "CloseSessionIfActiveAsync");

        var task = (Task)(method.Invoke(dialog, [reason, CancellationToken.None])
            ?? throw new InvalidOperationException("Terminal close operation did not return a task."));
        await task;
    }

    private static async Task CloseOpenedSessionAfterDialogCloseAsync(ClientTerminalSessionDialog dialog, string sessionId)
    {
        var method = typeof(ClientTerminalSessionDialog).GetMethod(
            "CloseOpenedSessionAfterDialogCloseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ClientTerminalSessionDialog), "CloseOpenedSessionAfterDialogCloseAsync");

        var task = (Task)(method.Invoke(dialog, [sessionId])
            ?? throw new InvalidOperationException("Late-open terminal cleanup did not return a task."));
        await task;
    }

    private static void SetProperty<T>(ClientTerminalSessionDialog dialog, string name, T value)
    {
        var property = typeof(ClientTerminalSessionDialog).GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(ClientTerminalSessionDialog), name);
        property.SetValue(dialog, value);
    }

    private static void SetPrivateField(ClientTerminalSessionDialog dialog, string name, object? value)
    {
        var field = typeof(ClientTerminalSessionDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(ClientTerminalSessionDialog), name);
        field.SetValue(dialog, value);
    }

    private static object? GetPrivateField(ClientTerminalSessionDialog dialog, string name)
    {
        var field = typeof(ClientTerminalSessionDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(ClientTerminalSessionDialog), name);
        return field.GetValue(dialog);
    }

    private static string CreateHandleJson(string sessionId) =>
        $$"""
        [{"sessionId":"{{sessionId}}","tenantId":7,"agentId":"{{AgentId:D}}","route":"gateway-v2","shell":"bash","cols":80,"rows":24,"createdUnix":1}]
        """;

    private sealed class CloseResultTerminalService(Exception? closeException = null) : ITerminalService
    {
        public int CloseCalls { get; private set; }
        public List<(string SessionId, ulong Generation, string LeaseId, string BrowserId, bool ClaimOwnership)> RenewCalls { get; } = [];

        public Task EnsureSubscribedAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TerminalOpenResponse> OpenSessionAsync(string clientIdentityHex, OpenTerminalRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TerminalOpenResponse> OpenGatewaySessionAsync(int tenantId, Guid agentId, OpenTerminalRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TerminalActionResponse> CloseAsync(string sessionId, string? reason = null, CancellationToken ct = default)
        {
            CloseCalls++;
            return closeException is null
                ? Task.FromResult(new TerminalActionResponse("tracking", "Close requested.", sessionId))
                : Task.FromException<TerminalActionResponse>(closeException);
        }

        public Task<TerminalActionResponse> RenewGatewayAttachmentAsync(
            string sessionId,
            ulong generation,
            string attachmentLeaseId,
            string browserAttachmentId,
            bool claimOwnership,
            CancellationToken ct = default)
        {
            RenewCalls.Add((sessionId, generation, attachmentLeaseId, browserAttachmentId, claimOwnership));
            return Task.FromResult(new TerminalActionResponse("tracking", "Browser attachment renewed.", sessionId)
            {
                AttachmentLeaseId = attachmentLeaseId
            });
        }

        public Task<TerminalActionResponse> SendInputAsync(string sessionId, string data, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ITerminalInputChannel> OpenInputChannelAsync(
            string sessionId,
            Action<TerminalInputChannelStatus>? onStatus = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TerminalActionResponse> ResizeAsync(string sessionId, int cols, int rows, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<TerminalStreamMessage> StreamSessionAsync(
            string sessionId,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<TerminalSessionDto>> GetSessionsAsync(string clientIdentityHex, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TerminalSessionDto?> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

    }

    private sealed class SessionStorageJsRuntime(string storedPayload) : IJSRuntime
    {
        public string? StoredPayload { get; private set; } = storedPayload;
        public int SetItemCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (string.Equals(identifier, "sessionStorage.getItem", StringComparison.Ordinal))
            {
                return ValueTask.FromResult((TValue)(object?)StoredPayload!);
            }

            if (string.Equals(identifier, "sessionStorage.setItem", StringComparison.Ordinal))
            {
                SetItemCalls++;
                StoredPayload = args is { Length: > 1 } ? args[1] as string : null;
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
