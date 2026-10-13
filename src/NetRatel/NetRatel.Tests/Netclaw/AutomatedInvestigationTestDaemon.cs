using System.Text.Json;
using System.Threading.Channels;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Netclaw;

namespace NetRatel.Tests.Netclaw;

/// <summary>Deterministic native session contract: sending is admission, only an emitted completion completes a turn.</summary>
internal sealed class AutomatedInvestigationTestDaemon : IAiAssistantChatClientFactory, INetclawPairingService
{
    public Channel<(string SessionId, string Text)> Sent { get; } = Channel.CreateUnbounded<(string SessionId, string Text)>();
    public Channel<bool> TurnClosed { get; } = Channel.CreateUnbounded<bool>();
    private int sends;
    public int Sends => Volatile.Read(ref sends);
    private Func<JsonElement, Task>? output;
    private string? sessionId;
    private string? lastRequest;
    public IAiAssistantChatClient Create(NetclawRuntimeProfile profile) => new Client(this);
    public Task<string> ExchangeCodeAsync(NetclawPairingTarget target, string pairingCode, CancellationToken cancellationToken = default) =>
        Task.FromResult("synthetic-automated-context-device-token");
    public Task EmitAsync(string type, string? text = null) => EmitAsync(JsonSerializer.SerializeToElement(new { type, sessionId, text }));
    public Task EmitAsync(JsonElement item) => output?.Invoke(item) ?? throw new InvalidOperationException("No session is connected.");

    private sealed class Client(AutomatedInvestigationTestDaemon daemon) : IAiAssistantChatClient
    {
        public bool IsConnected { get; private set; }
        private bool sent;
        public async Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken cancellationToken)
        {
            IsConnected = true; daemon.output = output; daemon.sessionId = sessionId ?? Guid.NewGuid().ToString("N");
            if (sessionId is not null)
                await output(JsonSerializer.SerializeToElement(new { type = "session_joined", sessionId,
                    recentMessages = daemon.lastRequest is null ? Array.Empty<object>() : new object[] { new { role = "user", content = daemon.lastRequest } } }));
            return new(daemon.sessionId, sessionId is null);
        }
        public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken)
        {
            sent = true; daemon.lastRequest = text; Interlocked.Increment(ref daemon.sends);
            daemon.Sent.Writer.TryWrite((sessionId, text)); return Task.CompletedTask;
        }
        public Task RespondAsync(string sessionId, string callId, string key, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            if (sent) daemon.TurnClosed.Writer.TryWrite(true);
            return ValueTask.CompletedTask;
        }
    }
}
