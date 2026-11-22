using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.ComputerUse;
using NetRatel.Shared.Contracts.RemoteDesktop;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteDesktop;

[SupportedOSPlatform("windows")]
internal sealed class RemoteDesktopUserHelperPipeHost : IDisposable
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private const int PipeBufferBytes = 16 * 1024;
    private const int StreamBufferBytes = 4096;
    private const int MaximumPendingHandshakes = 8;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly string HelperVersion = RemoteDesktopInteractiveHelper.GetHelperVersion();
    private readonly SemaphoreSlim _handshakes = new(MaximumPendingHandshakes);
    private readonly CancellationTokenSource _cts = new();
    private readonly InteractiveHelperRegistry _registry = new(HelperVersion);
    private Task? _listenerTask;
    private bool _disposed;

    public event Action<RemoteDesktopPipeMessage>? MessageReceived;
    public event Action<ConnectedUserHelper, RemoteDesktopPipeMessage>? HelperMessageReceived;
    public event Action<ConnectedUserHelper>? HelperDisconnected;

    public void Start()
    {
        if (_listenerTask is not null)
        {
            return;
        }

        LogManager.WriteLog("[RemoteDesktop] Helper pipe feature enabled.");
        LogManager.WriteLog($"[RemoteDesktop] Helper pipe listener starting name={RemoteDesktopUserHelperConstants.PipeName} path={RemoteDesktopUserHelperConstants.FullPipePath}");
        LogManager.WriteLog("[RemoteDesktop] Helper pipe security mode=LocalSystem FullControl; Interactive Users ReadWrite.");
        RemoteDesktopDiagnosticState.Update(state =>
        {
            state.PipeHostStarted = true;
            state.LatestStage = "pipe_host_starting";
            state.LatestError = null;
        });
        _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
        LogManager.WriteLog("[RemoteDesktop] Helper pipe listener task started.");
    }

    public ConnectedUserHelper? GetConnectedHelper()
    {
        var activeConsoleSessionId = WTSGetActiveConsoleSessionId();
        return activeConsoleSessionId == uint.MaxValue
            ? null
            : GetConnectedHelper(unchecked((int)activeConsoleSessionId));
    }

    public ConnectedUserHelper? GetConnectedHelper(int windowsSessionId) =>
        _registry.Get(windowsSessionId);

    public IReadOnlyCollection<ConnectedUserHelper> GetConnectedHelpers() =>
        _registry.Snapshot();

    public async Task<ConnectedUserHelper?> WaitForHelperAsync(TimeSpan timeout, CancellationToken ct)
    {
        var activeConsoleSessionId = WTSGetActiveConsoleSessionId();
        return activeConsoleSessionId == uint.MaxValue
            ? null
            : await WaitForHelperAsync(unchecked((int)activeConsoleSessionId), timeout, ct).ConfigureAwait(false);
    }

    public async Task<ConnectedUserHelper?> WaitForHelperAsync(int windowsSessionId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var helper = GetConnectedHelper(windowsSessionId);
            if (helper is not null)
            {
                return helper;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                LogManager.WriteLog($"[RemoteDesktop] Helper pipe creating name={RemoteDesktopUserHelperConstants.PipeName} path={RemoteDesktopUserHelperConstants.FullPipePath}");
                var pipe = CreatePipe();
                LogManager.WriteLog($"[RemoteDesktop] Helper pipe listener ready name={RemoteDesktopUserHelperConstants.PipeName} path={RemoteDesktopUserHelperConstants.FullPipePath}");
                RemoteDesktopDiagnosticState.Update(state =>
                {
                    state.PipeHostStarted = true;
                    state.LatestStage = "pipe_host_ready";
                    state.LatestError = null;
                });
                LogManager.WriteLog($"[RemoteDesktop] Helper pipe waiting for connection path={RemoteDesktopUserHelperConstants.FullPipePath}");
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var peer = ComputerUsePipeSecurity.GetVerifiedHelper(pipe);
                    if (peer is not null && _handshakes.Wait(0))
                    {
                        _ = HandleConnectionAsync(pipe, peer, ct);
                        continue;
                    }
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteDesktop] Helper pipe listener fatal exception: {ex}");
                RemoteDesktopDiagnosticState.Update(state =>
                {
                    state.LatestStage = "pipe_host_error";
                    state.LatestError = ex.Message;
                });
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, VerifiedRemoteSupportHelper peer, CancellationToken ct)
    {
        await using var ownedPipe = pipe;
        var reader = new BoundedHelperPipeReader(pipe);
        ConnectedUserHelper? helper = null;

        try
        {
            try
            {
                var line = await reader.ReadAsync(BoundedHelperPipeReader.MaximumHelloBytes, HandshakeTimeout, ct).ConfigureAwait(false);
                var message = DeserializePipeMessage(line);
                if (message?.Kind != "hello" || string.IsNullOrWhiteSpace(message.PayloadJson) ||
                    JsonSerializer.Deserialize<RemoteDesktopHelperHello>(message.PayloadJson, _json) is not { } hello ||
                    !RemoteSupportHelperProcessValidation.MatchesPeer(hello, peer, HelperVersion))
                    return;
            }
            finally
            {
                _handshakes.Release();
            }

            // Every route (desktop capture, signaling, and computer use) now
            // comes from the same OS-authenticated process and Windows session.
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: StreamBufferBytes, leaveOpen: true) { AutoFlush = true };
            helper = new ConnectedUserHelper(peer.SessionId, peer.ProcessId, HelperVersion,
                peer.UserName, DateTimeOffset.UtcNow, writer, pipe, computerUseAuthenticated: true);
            if (!TryRegisterHelper(helper, out var replaced, out var rejectionReason))
            {
                LogManager.WriteLog($"[RemoteDesktop] Helper registration rejected pid={helper.ProcessId} sessionId={helper.SessionId} reason={rejectionReason}");
                helper = null;
                return;
            }

            replaced?.Disconnect("replaced_by_newer_session_helper");

            LogManager.WriteLog($"[RemoteDesktop] Helper registered pid={helper.ProcessId} sessionId={helper.SessionId} username={helper.UserName} version={helper.Version} connectedAt={helper.ConnectedAtUtc:O} helperCount={_registry.Count}");
            RemoteDesktopDiagnosticState.Update(state =>
            {
                state.HelperConnected = true;
                state.HelperPid = helper.ProcessId;
                state.HelperSessionId = helper.SessionId;
                state.HelperUser = helper.UserName;
                state.HelperVersion = helper.Version;
                state.HelperConnectedUtc = helper.ConnectedAtUtc;
                state.LatestStage = "helper_registered";
                state.LatestError = null;
            });

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadAsync(BoundedHelperPipeReader.MaximumMessageBytes, IdleTimeout, ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                var message = DeserializePipeMessage(line);
                if (message is null)
                {
                    continue;
                }

                helper.LastHeartbeatUtc = DateTimeOffset.UtcNow;
                if (string.Equals(message.Kind, "heartbeat", StringComparison.OrdinalIgnoreCase))
                {
                    LogManager.WriteLog($"[RemoteDesktop] Helper heartbeat pid={helper.ProcessId} sessionId={helper.SessionId} lastHeartbeat={helper.LastHeartbeatUtc:O}");
                    continue;
                }

                LogPipeMessageReceived(message, line.Length);
                try
                {
                    MessageReceived?.Invoke(message);
                    HelperMessageReceived?.Invoke(helper, message);
                }
                catch (Exception ex)
                {
                    LogManager.WriteLog($"[RemoteDesktopTrace] stage=service_pipe_message_dispatch_failed kind={message.Kind} streamId={message.StreamId ?? string.Empty} bytes={message.PayloadJson?.Length ?? 0} error={ex}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or
            InvalidDataException or JsonException or DecoderFallbackException or ObjectDisposedException)
        {
            // Closed, expired, or invalid connections are removed below.
        }
        finally
        {
            if (helper is not null)
            {
                _registry.Remove(helper);

                LogManager.WriteLog($"[RemoteDesktop] Helper disconnected pid={helper.ProcessId} sessionId={helper.SessionId} helperCount={_registry.Count}");
                RemoteDesktopDiagnosticState.Update(state =>
                {
                    state.HelperConnected = false;
                    state.HelperPid = null;
                    state.HelperSessionId = null;
                    state.HelperUser = null;
                    state.HelperVersion = null;
                    state.HelperConnectedUtc = null;
                    state.LatestStage = "helper_disconnected";
                });
                HelperDisconnected?.Invoke(helper);
            }
        }
    }

    private bool TryRegisterHelper(
        ConnectedUserHelper candidate,
        out ConnectedUserHelper? replaced,
        out string? rejectionReason)
    {
        replaced = null;
        rejectionReason = null;
        return _registry.TryRegister(candidate, out replaced, out rejectionReason);
    }

    private void LogPipeMessageReceived(RemoteDesktopPipeMessage message, int lineBytes)
    {
        var payloadBytes = message.PayloadJson?.Length ?? 0;
        if (string.Equals(message.Kind, RemoteDesktopStreamDirections.Frame, StringComparison.OrdinalIgnoreCase))
        {
            var metadata = TryReadFrameMetadata(message.PayloadJson);
            if (ShouldTraceFrame(metadata.Sequence))
            {
                LogManager.WriteLog($"[RemoteDesktopTrace] stage=service_pipe_message_received kind={message.Kind} streamId={message.StreamId ?? string.Empty} seq={metadata.Sequence} bytes={payloadBytes} lineBytes={lineBytes} width={metadata.Width} height={metadata.Height}");
            }

            return;
        }

        LogManager.WriteLog($"[RemoteDesktopTrace] stage=service_pipe_message_received kind={message.Kind} streamId={message.StreamId ?? string.Empty} bytes={payloadBytes} lineBytes={lineBytes}");
    }

    private (ulong? Sequence, int? Width, int? Height) TryReadFrameMetadata(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return (null, null, null);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<RemoteDesktopFramePayload>(payloadJson, _json);
            return (payload?.Sequence, payload?.Width, payload?.Height);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static bool ShouldTraceFrame(ulong? sequence) =>
        sequence is null || sequence <= 5 || sequence % 50 == 0;

    private RemoteDesktopPipeMessage? DeserializePipeMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RemoteDesktopPipeMessage>(line, _json);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Ignoring malformed helper pipe message: {ex.Message}");
            return null;
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            RemoteDesktopUserHelperConstants.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            PipeBufferBytes,
            PipeBufferBytes,
            security);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        foreach (var helper in _registry.Snapshot())
        {
            helper.Disconnect("pipe_host_disposed");
        }

        _registry.Clear();
        _cts.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}

internal sealed class ConnectedUserHelper
{
    public ConnectedUserHelper(
        int sessionId,
        int processId,
        string version,
        string userName,
        DateTimeOffset connectedAtUtc,
        StreamWriter writer,
        NamedPipeServerStream? pipe = null,
        bool computerUseAuthenticated = false)
    {
        SessionId = sessionId;
        ProcessId = processId;
        Version = version;
        UserName = userName;
        ConnectedAtUtc = connectedAtUtc;
        LastHeartbeatUtc = connectedAtUtc;
        Writer = writer;
        Pipe = pipe;
        ComputerUseAuthenticated = computerUseAuthenticated;
    }

    public int SessionId { get; }
    public Guid RouteId { get; } = Guid.NewGuid();
    public int ProcessId { get; }
    public string Version { get; }
    public string UserName { get; }
    public DateTimeOffset ConnectedAtUtc { get; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
    public StreamWriter Writer { get; }
    public NamedPipeServerStream? Pipe { get; }
    public bool ComputerUseAuthenticated { get; }
    public object WriterSync { get; } = new();

    public void Send(RemoteDesktopPipeMessage message, JsonSerializerOptions json)
    {
        lock (WriterSync)
        {
            Writer.WriteLine(JsonSerializer.Serialize(message, json));
            Writer.Flush();
        }
    }

    public void Disconnect(string reason)
    {
        try
        {
            LogManager.WriteLog($"[RemoteDesktop] Disconnecting helper pid={ProcessId} sessionId={SessionId} reason={reason}");
            Pipe?.Dispose();
        }
        catch
        {
        }
    }
}
