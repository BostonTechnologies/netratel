using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Shared.Contracts.RemoteDesktop;
using NetRatel.Shared.Contracts.RemoteSupport;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteSupport;

internal sealed class RemoteSupportInteractiveWebRtcManager : IDisposable
{
    private readonly ConcurrentDictionary<string, RemoteSupportInteractiveWebRtcSession> _sessions = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly RemoteSupportProviderMetadata _providerMetadata;
    private readonly Action<RemoteSupportSasPipeRequest>? _sendSasRequest;

    public RemoteSupportInteractiveWebRtcManager(RemoteSupportProviderMetadata? providerMetadata = null, Action<RemoteSupportSasPipeRequest>? sendSasRequest = null)
    {
        _providerMetadata = providerMetadata ?? RemoteSupportProviderMetadata.InteractiveUserHelper;
        _sendSasRequest = sendSasRequest;
    }

    public async Task HandleOfferAsync(
        RemoteSupportPipeSignal signal,
        StreamWriter writer,
        object writerSync,
        CancellationToken ct)
    {
        await CloseSessionAsync(signal.SessionId, "Replacing remote support session.").ConfigureAwait(false);

        var offer = ParseOfferPayload(signal.PayloadJson);
        var session = new RemoteSupportInteractiveWebRtcSession(signal.SessionId, offer.IceServers, offer.MediaProfile, _providerMetadata, offer.TargetWindowsSessionId, offer.TargetUserSidHash, payload =>
            WriteSignal(writer, writerSync, payload), _sendSasRequest);
        if (!_sessions.TryAdd(signal.SessionId, session))
        {
            session.Dispose();
            return;
        }

        try
        {
            await session.ApplyOfferAsync(offer.OfferJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportWebRTC] Offer handling failed session={signal.SessionId}: {ex}");
            WriteSignal(
                writer,
                writerSync,
                new RemoteSupportPipeSignal(
                    signal.SessionId,
                    RemoteSupportSignalTypes.Error,
                    JsonSerializer.Serialize(new
                    {
                        code = "helper_webrtc_offer_failed",
                        message = ex.Message
                    }, _json)));
            await CloseSessionAsync(signal.SessionId, "Offer handling failed.").ConfigureAwait(false);
        }
    }

    public void HandleIce(RemoteSupportPipeSignal signal)
    {
        if (_sessions.TryGetValue(signal.SessionId, out var session))
        {
            session.AddIceCandidate(signal.PayloadJson);
        }
    }

    private RemoteSupportPipeOffer ParseOfferPayload(string payloadJson)
    {
        try
        {
            var offer = JsonSerializer.Deserialize<RemoteSupportPipeOffer>(payloadJson, _json);
            if (offer is not null && !string.IsNullOrWhiteSpace(offer.OfferJson))
            {
                return offer;
            }
        }
        catch
        {
        }

        return new RemoteSupportPipeOffer(payloadJson, null);
    }

    public Task CloseSessionAsync(string sessionId, string reason)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            LogManager.WriteLog($"[RemoteSupportWebRTC] Closing helper peer session={sessionId} reason={reason}");
            session.MarkClosed(reason);
            session.Dispose();
        }

        return Task.CompletedTask;
    }

    private void WriteSignal(StreamWriter writer, object writerSync, RemoteSupportPipeSignal signal)
    {
        var line = JsonSerializer.Serialize(
            new RemoteDesktopPipeMessage(
                RemoteSupportPipeKinds.Signal,
                JsonSerializer.Serialize(signal, _json)),
            _json);

        lock (writerSync)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }

    public void Dispose()
    {
        foreach (var sessionId in _sessions.Keys.ToList())
        {
            _ = CloseSessionAsync(sessionId, "Remote support helper disposed.");
        }
    }
}

internal sealed class SecureAttentionNotSupportedException : InvalidOperationException
{
    public SecureAttentionNotSupportedException()
        : base("secure_attention_required_not_supported")
    {
    }
}

internal sealed class RemoteSupportInteractiveWebRtcSession : IDisposable
{
    private readonly string _sessionId;
    private readonly Action<RemoteSupportPipeSignal> _sendSignal;
    private readonly RemoteSupportProviderMetadata _providerMetadata;
    private readonly int? _targetWindowsSessionId;
    private readonly string? _targetUserSidHash;
    private readonly IRemoteSupportCaptureProvider _captureProvider;
    private readonly Action<RemoteSupportSasPipeRequest>? _sendSasRequest;
    private readonly RemoteSupportDesktopContextCoordinator _desktopContextCoordinator = new();
    private readonly InteractiveInputDispatcher? _inputDispatcher;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly RTCPeerConnection _peerConnection;
    private readonly VideoEncoderEndPoint _videoSource;
    private readonly CancellationTokenSource _videoCancellation = new();
    private readonly AutoResetEvent _captureWake = new(false);
    private readonly object _feedbackSync = new();
    private readonly object _captureProviderSync = new();
    private Thread? _videoThread;
    private bool _videoStarted;
    private bool _disposed;
    private readonly object _profileLock = new();
    private RemoteSupportMediaProfile _profile = RemoteSupportMediaProfile.Default;
    private int? _lockedProfileMaxWidth;
    private int? _lockedProfileMaxHeight;
    private long _inputReceived;
    private long _inputAttempted;
    private long _inputInjected;
    private long _inputFailures;
    private long _mouseReceived;
    private long _keyboardReceived;
    private long _mouseInjected;
    private long _keyboardInjected;
    private long _inputRejected;
    private long _mouseMoveReceived;
    private long _mouseMoveSent;
    private long _mouseMoveCoalesced;
    private long _mouseClickSent;
    private long _qualityProfileChanges;
    private DateTimeOffset _lastInputStatsUtc = DateTimeOffset.MinValue;
    private int _lastCaptureSourceWidth;
    private int _lastCaptureSourceHeight;
    private ulong _captureFrameSequence;
    private ulong _encoderFrameSequence;
    private ulong _captureSameFrameCount;
    private string? _lastFrameHash;
    private string? _lastInputEventId;
    private string? _lastInputKind;
    private string? _lastKeyCategory;
    private DateTimeOffset? _lastBrowserSentAt;
    private DateTimeOffset? _lastDataChannelSentAt;
    private DateTimeOffset? _lastInputReceivedAt;
    private DateTimeOffset? _lastInputInjectedAt;
    private DateTimeOffset? _lastSendInputAttemptedAt;
    private int? _lastSendInputResultCount;
    private int? _lastSendInputWin32Error;
    private string? _pendingFeedbackInputEventId;
    private DateTimeOffset? _pendingFeedbackInputInjectedAt;
    private DateTimeOffset? _firstFrameAfterInputAt;
    private DateTimeOffset? _firstChangedFrameAfterInputAt;
    private string? _lastFeedbackStatus;
    private string? _lastKeyframeStatus;
    private RTCDataChannel? _controlChannel;
    private string? _peerInstanceId;
    private string? _dataChannelId;
    private string? _dataChannelLabel;
    private DateTimeOffset? _lastAcknowledgementAt;
    private const int MaxConsecutiveCaptureFailures = 20;

    public RemoteSupportInteractiveWebRtcSession(
        string sessionId,
        IReadOnlyList<RemoteSupportIceServerDto>? iceServers,
        RemoteSupportMediaProfileDto? mediaProfile,
        RemoteSupportProviderMetadata providerMetadata,
        int? targetWindowsSessionId,
        string? targetUserSidHash,
        Action<RemoteSupportPipeSignal> sendSignal,
        Action<RemoteSupportSasPipeRequest>? sendSasRequest = null)
    {
        _sessionId = sessionId;
        _sendSignal = sendSignal;
        _sendSasRequest = sendSasRequest;
        _providerMetadata = providerMetadata;
        _targetWindowsSessionId = targetWindowsSessionId;
        _targetUserSidHash = targetUserSidHash;
        if (!_providerMetadata.IsConsoleProvider && OperatingSystem.IsWindows())
        {
            _inputDispatcher = new InteractiveInputDispatcher(
                _sessionId,
                _targetWindowsSessionId,
                _targetUserSidHash,
                _desktopContextCoordinator,
                ApplyWindowsInputOnCurrentThread,
                SendInteractiveDesktopStatus);
        }
        _captureProvider = RemoteSupportCaptureProviderFactory.Create(sessionId, providerMetadata, _desktopContextCoordinator);
        _profile = RemoteSupportMediaProfile.FromDto(mediaProfile);
        UpdateTransportState(
            peerConnected: false,
            dataChannelOpen: false,
            firstFrameDelivered: false,
            renderedFrames: 0,
            captureSupported: false);
        UpdateInputProviderState();

        _peerConnection = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = BuildIceServers(iceServers),
            X_ICEIncludeAllInterfaceAddresses = true,
            X_GatherTimeoutMs = 5000
        });
        LogManager.WriteLog($"[RemoteSupportWebRTC] ICE servers configured session={_sessionId} count={iceServers?.Count ?? 0} initialProfile={_profile.Name} max={_profile.MaxWidth}x{_profile.MaxHeight} fps={_profile.Fps}");

        _videoSource = new VideoEncoderEndPoint();
        var videoTrack = new MediaStreamTrack(_videoSource.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
        _peerConnection.addTrack(videoTrack);

        _videoSource.OnVideoSourceEncodedSample += _peerConnection.SendVideo;
        _peerConnection.OnVideoFormatsNegotiated += formats =>
        {
            if (formats.Count > 0)
            {
                var format = formats.First();
                _videoSource.SetVideoSourceFormat(format);
                LogManager.WriteLog($"[RemoteSupportWebRTC] Video format negotiated session={_sessionId} codec={format.Codec}");
            }
        };

        _peerConnection.onicecandidate += candidate =>
        {
            try
            {
                if (candidate is not null)
                {
                    _sendSignal(new RemoteSupportPipeSignal(_sessionId, RemoteSupportSignalTypes.Ice, candidate.toJSON()));
                    LogManager.WriteLog($"[RemoteSupportWebRTC] Local ICE sent session={_sessionId}.");
                }
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteSupportWebRTC] Local ICE send failed session={_sessionId}: {ex.Message}");
            }
        };

        _peerConnection.onconnectionstatechange += async state =>
        {
            LogManager.WriteLog($"[RemoteSupportWebRTC] Peer state session={_sessionId} state={state}");
            UpdateTransportState(peerConnected: state == RTCPeerConnectionState.connected, peerLastState: state.ToString());
            if (state == RTCPeerConnectionState.connected)
            {
                await StartVideoAsync().ConfigureAwait(false);
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                await StopVideoAsync().ConfigureAwait(false);
            }
        };

        _peerConnection.oniceconnectionstatechange += state =>
            LogManager.WriteLog($"[RemoteSupportWebRTC] ICE state session={_sessionId} state={state}");

        _peerConnection.onicegatheringstatechange += state =>
            LogManager.WriteLog($"[RemoteSupportWebRTC] ICE gathering state session={_sessionId} state={state}");

        _peerConnection.ondatachannel += channel =>
        {
            _controlChannel = channel;
            _dataChannelLabel = channel.label;
            LogManager.WriteLog($"[RemoteSupportWebRTC] Data channel received session={_sessionId} label={channel.label}");
            channel.onopen += () =>
            {
                LogManager.WriteLog($"[RemoteSupportWebRTC] Data channel open session={_sessionId} label={channel.label}");
                UpdateTransportState(dataChannelOpen: true);
                UpdateInputProviderState();
                SendInputStatsStatus();
            };
            channel.onclose += () =>
            {
                if (ReferenceEquals(_controlChannel, channel))
                {
                    _controlChannel = null;
                }
                LogManager.WriteLog($"[RemoteSupportWebRTC] Data channel closed session={_sessionId} label={channel.label}");
                UpdateTransportState(dataChannelOpen: false);
                UpdateInputProviderState(inputError: "control data channel closed");
            };
            channel.onmessage += (_, _, data) =>
            {
                var text = Encoding.UTF8.GetString(data);
                HandleDataChannelMessage(text);
            };
        };
    }

    public async Task ApplyOfferAsync(string offerJson, CancellationToken ct)
    {
        var offer = ParseDescription(offerJson, RTCSdpType.offer);
        var result = _peerConnection.setRemoteDescription(offer);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Failed to set browser offer: {result}.");
        }

        var answer = _peerConnection.createAnswer(null);
        await _peerConnection.setLocalDescription(answer).ConfigureAwait(false);
        await WaitForIceGatheringAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        var localDescription = _peerConnection.localDescription;
        var localSdp = localDescription?.sdp?.ToString();
        var answerSdp = string.IsNullOrWhiteSpace(localSdp) ? answer.sdp : localSdp;
        var answerJson = JsonSerializer.Serialize(new { type = "answer", sdp = answerSdp }, _json);
        _sendSignal(new RemoteSupportPipeSignal(_sessionId, RemoteSupportSignalTypes.Answer, answerJson));
        LogManager.WriteLog($"[RemoteSupportWebRTC] Answer sent session={_sessionId} iceGathering={_peerConnection.iceGatheringState} sdpBytes={answerSdp?.Length ?? 0} candidates={CountSdpCandidates(answerSdp)}");

        if (_peerConnection.connectionState == RTCPeerConnectionState.connected)
        {
            await StartVideoAsync().ConfigureAwait(false);
        }
    }

    public void AddIceCandidate(string candidateJson)
    {
        try
        {
            var candidate = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson, _json);
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.candidate))
            {
                return;
            }

            _peerConnection.addIceCandidate(candidate);
            LogManager.WriteLog($"[RemoteSupportWebRTC] Remote ICE added session={_sessionId}.");
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportWebRTC] Remote ICE failed session={_sessionId}: {ex.Message}");
        }
    }

    public void MarkClosed(string reason)
    {
        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.RemoteSupportSessionId = _sessionId;
            state.RemoteSupportStateIsCurrentSession = false;
            state.RemoteSupportIsLiveSession = false;
            state.RemoteSupportIsLastClosedSession = true;
            state.RemoteSupportClosedAt = DateTimeOffset.UtcNow;
            state.RemoteSupportCloseReason = reason;
            state.RemoteSupportPeerConnected = false;
            state.RemoteSupportDataChannelOpen = false;
            state.RemoteSupportInputSupported = false;
            state.RemoteSupportStateUpdatedUtc = DateTimeOffset.UtcNow;
            state.RemoteSupportStateSource = "helper_close";
        });
    }

    private void HandleDataChannelMessage(string text)
    {
        string? inputEventId = null;
        string? inputCategory = null;
        var injectionAttempted = false;
        var helperReceivedAt = DateTimeOffset.UtcNow;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var type = doc.RootElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            if (type.Equals("quality_profile", StringComparison.OrdinalIgnoreCase))
            {
                ApplyQualityProfile(text);
                return;
            }

            if (type.Equals("refresh_video", StringComparison.OrdinalIgnoreCase))
            {
                RequestCaptureRefresh("manual_refresh", resetProvider: true);
                SendVisualFeedbackStatus("post_input_frame_forced", "Refreshing login screen video.");
                return;
            }

            if (type.Equals("sas", StringComparison.OrdinalIgnoreCase))
            {
                HandleSasMessage(doc.RootElement);
                return;
            }

            var input = JsonSerializer.Deserialize<RemoteDesktopInputRequest>(text, _json);
            if (input is null)
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var isKeyboard = input.Type is RemoteDesktopInputTypes.KeyDown or RemoteDesktopInputTypes.KeyUp;
            inputEventId = string.IsNullOrWhiteSpace(input.InputEventId)
                ? Guid.NewGuid().ToString("N")
                : input.InputEventId.Trim();
            var inputKind = isKeyboard ? "keyboard" : "mouse";
            var keyCategory = isKeyboard ? CategorizeKey(input) : null;
            inputCategory = (isKeyboard ? keyCategory : CategorizeMouse(input.Type)) ?? "other";
            _peerInstanceId = input.PeerInstanceId ?? _peerInstanceId;
            _dataChannelId = input.DataChannelId ?? _dataChannelId;
            LogRemoteInput("remote_input_helper_received", inputEventId, inputCategory, helperReceivedAt, input, null);

            var received = Interlocked.Increment(ref _inputReceived);
            if (isKeyboard)
            {
                Interlocked.Increment(ref _keyboardReceived);
            }
            else
            {
                Interlocked.Increment(ref _mouseReceived);
                if (input.Type == RemoteDesktopInputTypes.MouseMove)
                {
                    Interlocked.Increment(ref _mouseMoveReceived);
                    if (input.MouseMoveCoalescedCount is >= 0)
                    {
                        Interlocked.Exchange(ref _mouseMoveCoalesced, input.MouseMoveCoalescedCount.Value);
                    }
                }
            }

            _lastInputEventId = inputEventId;
            _lastInputKind = inputKind;
            _lastKeyCategory = keyCategory;
            _lastBrowserSentAt = input.BrowserSentAt;
            _lastDataChannelSentAt = input.DataChannelSentAt;
            _lastInputReceivedAt = helperReceivedAt;
            LogRemoteInput("remote_input_payload_parsed", inputEventId, inputCategory, helperReceivedAt, input, null);
            ValidateInputTarget(input);
            LogRemoteInput("remote_input_target_validated", inputEventId, inputCategory, DateTimeOffset.UtcNow, input, null);
            UpdateInputProviderState(inputReceived: true);
            Interlocked.Increment(ref _inputAttempted);
            injectionAttempted = true;
#pragma warning disable CA1416
            var sendInputResult = ApplyWindowsInput(input);
#pragma warning restore CA1416
            _lastSendInputAttemptedAt = sendInputResult.AttemptedAt;
            _lastSendInputResultCount = sendInputResult.ResultCount;
            _lastSendInputWin32Error = sendInputResult.Win32Error;
            LogRemoteInput("remote_input_injection_attempted", inputEventId, inputCategory, sendInputResult.AttemptedAt, input, sendInputResult);

            if (sendInputResult.Succeeded)
            {
                var injected = Interlocked.Increment(ref _inputInjected);
                _lastInputInjectedAt = DateTimeOffset.UtcNow;
                if (isKeyboard)
                {
                    Interlocked.Increment(ref _keyboardInjected);
                }
                else
                {
                    Interlocked.Increment(ref _mouseInjected);
                    if (input.Type == RemoteDesktopInputTypes.MouseMove)
                    {
                        Interlocked.Increment(ref _mouseMoveSent);
                    }
                    else
                    {
                        Interlocked.Increment(ref _mouseClickSent);
                    }
                }

                UpdateInputProviderState(inputInjected: true);
                LogRemoteInput("remote_input_injection_succeeded", inputEventId, inputCategory, _lastInputInjectedAt.Value, input, sendInputResult);
                LogInputStatsIfNeeded(received, injected);
                if (_providerMetadata.IsConsoleProvider)
                {
                    SchedulePostInputRefresh(inputEventId, _lastInputInjectedAt.Value);
                }
            }
            else
            {
                Interlocked.Increment(ref _inputFailures);
                Interlocked.Increment(ref _inputRejected);
                UpdateInputProviderState(inputError: sendInputResult.Status);
                LogRemoteInput("remote_input_injection_failed", inputEventId, inputCategory, DateTimeOffset.UtcNow, input, sendInputResult);
            }

            SendInputAcknowledgement(inputEventId, inputCategory, helperReceivedAt, sendInputResult, injectionAttempted: true);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _inputFailures);
            Interlocked.Increment(ref _inputRejected);
            UpdateInputProviderState(inputError: ex.Message);
            if (ex is SecureAttentionNotSupportedException)
            {
                SendInputStatus("secure_attention_required_not_supported", "Secure Attention Sequence is required but is not supported by this preview build.");
            }

            var status = ex.Message.Contains("interactive_input_target_session_mismatch", StringComparison.Ordinal)
                ? "interactive_input_target_session_mismatch"
                : "input_processing_failed";
            if (!string.IsNullOrWhiteSpace(inputEventId))
            {
                Interlocked.Increment(ref _inputFailures);
                Interlocked.Increment(ref _inputRejected);
                UpdateInputProviderState(inputError: status);
                SendInputAcknowledgement(inputEventId, inputCategory ?? "other", helperReceivedAt,
                    new SendInputAttemptResult(DateTimeOffset.UtcNow, 0, Marshal.GetLastWin32Error(), false, status),
                    injectionAttempted);
            }

            LogManager.WriteLog($"[RemoteSupportInput] remote_input_injection_failed session={_sessionId} targetSession={_targetWindowsSessionId?.ToString() ?? "none"} helperPid={Environment.ProcessId} helperSession={Process.GetCurrentProcess().SessionId} inputEventId={inputEventId ?? "missing"} category={inputCategory ?? "other"} status={status} win32={Marshal.GetLastWin32Error()}");
        }
    }

    private void HandleSasMessage(JsonElement root)
    {
        if (!OperatingSystem.IsWindows())
        {
            SendSasStatus(RemoteSupportStatusCodes.SasFailedBeforeInvoke, "Ctrl+Alt+Del is only available from Windows console providers.");
            return;
        }

        var action = root.TryGetProperty("action", out var actionElement)
            ? actionElement.GetString()
            : null;
        if (!string.Equals(action, "send_ctrl_alt_del", StringComparison.OrdinalIgnoreCase))
        {
            SendSasStatus(RemoteSupportStatusCodes.SasFailedBeforeInvoke, "Unsupported secure-attention action.");
            return;
        }

        if (!_providerMetadata.IsConsoleProvider)
        {
            SendSasStatus("secure_attention_required_not_supported", "Ctrl+Alt+Del is only available for console lock/logon Remote Support sessions.");
            return;
        }

        uint? activeConsoleSessionId = null;
        bool providerMatchesActiveConsole = false;
        int processSessionId = Process.GetCurrentProcess().SessionId;
        try
        {
            var active = WTSGetActiveConsoleSessionId();
            activeConsoleSessionId = active == uint.MaxValue ? null : active;
            providerMatchesActiveConsole = activeConsoleSessionId.HasValue && processSessionId == unchecked((int)activeConsoleSessionId.Value);
        }
        catch
        {
        }

        if (!providerMatchesActiveConsole)
        {
            SendSasStatus(RemoteSupportStatusCodes.SasFailedBeforeInvoke, "Console provider no longer matches the active console session.");
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            if (_sendSasRequest is null)
            {
#pragma warning disable CA1416
                var probe = new RemoteSupportSasController().ProbeProviderLocal(activeConsoleSessionId, processSessionId, providerMatchesActiveConsole);
#pragma warning restore CA1416
                SendSasStatus(RemoteSupportStatusCodes.SasFailedBeforeInvoke, "Service-owned SAS path is unavailable; provider-local SAS is diagnostics-only.", probe);
                return;
            }

            _sendSasRequest(new RemoteSupportSasPipeRequest(
                _sessionId,
                requestId,
                action!,
                _providerMetadata.Provider,
                processSessionId,
                activeConsoleSessionId,
                providerMatchesActiveConsole,
                _providerMetadata.DesktopState,
                _lastFrameHash));
            SendSasStatus(RemoteSupportStatusCodes.SasInvoked, "Ctrl+Alt+Del request sent to service-owned SAS controller.");
        }
        catch (Exception ex)
        {
            SendSasStatus(RemoteSupportStatusCodes.SasFailedBeforeInvoke, ex.Message);
        }
    }

    private void SendSasStatus(string statusCode, string message, RemoteSupportSasProbe? probe = null)
    {
        _sendSignal(new RemoteSupportPipeSignal(
            _sessionId,
            RemoteSupportSignalTypes.Ready,
            JsonSerializer.Serialize(new
            {
                role = "agent",
                platform = "windows",
                code = statusCode,
                statusCode,
                message,
                provider = _providerMetadata.Provider,
                desktopState = _providerMetadata.DesktopState,
                inputProviderName = _providerMetadata.Provider,
                remoteSupportSasProvider = probe?.Backend ?? "service_owned_send_sas",
                remoteSupportSasApiAvailable = probe?.ApiAvailable,
                remoteSupportSasPolicyAllowsServices = probe?.Policy.AllowsServices,
                remoteSupportSasTargetSessionId = probe?.TargetSessionId,
                softwareSasGenerationRawValue = probe?.Policy.RawValue,
                softwareSasGenerationInterpretedValue = probe?.Policy.InterpretedValue,
                softwareSasAllowsServices = probe?.Policy.AllowsServices,
                softwareSasAllowsEaseOfAccess = probe?.Policy.AllowsEaseOfAccess,
                softwareSasPolicySource = probe?.Policy.Source,
                remoteSupportOsCaption = probe?.OsCaption,
                remoteSupportOsVersion = probe?.OsVersion,
                remoteSupportOsBuild = probe?.OsBuild,
                remoteSupportOsIsServer = probe?.OsIsServer
            }, _json)));
    }

    private void ApplyQualityProfile(string text)
    {
        var requested = JsonSerializer.Deserialize<RemoteSupportQualityProfileMessage>(text, _json);
        if (requested is null)
        {
            return;
        }

        var profile = RemoteSupportMediaProfile.FromRequest(requested);
        var bitrateApplied = TryApplyTargetBitrate(profile.TargetKbps);
        lock (_profileLock)
        {
            if (_videoStarted)
            {
                profile = profile with
                {
                    MaxWidth = _lockedProfileMaxWidth ?? _profile.MaxWidth,
                    MaxHeight = _lockedProfileMaxHeight ?? _profile.MaxHeight,
                    GeometryStatus = "resolution_pending_reconnect"
                };
            }

            _profile = profile with { BitrateStatus = bitrateApplied ? "applied" : "not_applied" };
        }

        Interlocked.Increment(ref _qualityProfileChanges);
        LogManager.WriteLog(
            $"[RemoteSupportWebRTC] Quality profile applied session={_sessionId} profile={profile.Name} max={profile.MaxWidth}x{profile.MaxHeight} fps={profile.Fps} targetKbps={profile.TargetKbps} bitrate={_profile.BitrateStatus} geometry={_profile.GeometryStatus}");
        SendReadyStatus("Quality profile applied.");
    }

    private void LogInputStatsIfNeeded(long received, long injected)
    {
        var now = DateTimeOffset.UtcNow;
        if (received <= 5 ||
            received % 100 == 0 ||
            now - _lastInputStatsUtc > TimeSpan.FromSeconds(10))
        {
            _lastInputStatsUtc = now;
            LogManager.WriteLog(
                $"[RemoteSupportWebRTC] Input stats session={_sessionId} received={received} injected={injected} mouseReceived={Interlocked.Read(ref _mouseReceived)} mouseInjected={Interlocked.Read(ref _mouseInjected)} keyboardReceived={Interlocked.Read(ref _keyboardReceived)} keyboardInjected={Interlocked.Read(ref _keyboardInjected)} qualityChanges={Interlocked.Read(ref _qualityProfileChanges)} failures={Interlocked.Read(ref _inputFailures)} rejected={Interlocked.Read(ref _inputRejected)}");
            SendInputStatsStatus();
        }
    }

    private Task StartVideoAsync()
    {
        if (_videoStarted)
        {
            return Task.CompletedTask;
        }

        _videoStarted = true;
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Remote support desktop capture is Windows-only.");
        }

#pragma warning disable CA1416
        _videoThread = new Thread(() => DesktopCaptureLoop(_videoCancellation.Token))
        {
            IsBackground = true,
            Name = $"NetRatel.RemoteCapture.{_targetWindowsSessionId?.ToString() ?? "console"}"
        };
        _videoThread.Start();
#pragma warning restore CA1416
        var profile = GetProfile();
        LogManager.WriteLog($"[RemoteSupportWebRTC] Desktop video capture started session={_sessionId} max={profile.MaxWidth}x{profile.MaxHeight} fps={profile.Fps} targetKbps={profile.TargetKbps}");
        SendReadyStatus("Native WebRTC desktop media started.");
        return Task.CompletedTask;
    }

    private void UpdateTransportState(bool? peerConnected = null, bool? dataChannelOpen = null, bool? firstFrameDelivered = null, ulong? renderedFrames = null, bool? captureSupported = null, string? peerLastState = null, bool stateIsCurrentSession = true)
    {
        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.RemoteSupportTransportSupported = true;
            state.RemoteSupportSessionId = _sessionId;
            state.RemoteSupportStateIsCurrentSession = stateIsCurrentSession;
            state.RemoteSupportStateUpdatedUtc = DateTimeOffset.UtcNow;
            state.RemoteSupportStateSource = "helper_transport";
            state.RemoteSupportIsLiveSession = stateIsCurrentSession;
            state.RemoteSupportIsLastClosedSession = !stateIsCurrentSession;
            if (peerConnected.HasValue)
            {
                state.RemoteSupportPeerConnected = peerConnected.Value;
                if (peerConnected.Value)
                {
                    state.RemoteSupportPeerConnectedAt ??= DateTimeOffset.UtcNow;
                }
            }

            if (!string.IsNullOrWhiteSpace(peerLastState))
            {
                state.RemoteSupportPeerLastState = peerLastState;
            }

            if (dataChannelOpen.HasValue)
            {
                state.RemoteSupportDataChannelOpen = dataChannelOpen.Value;
                if (dataChannelOpen.Value)
                {
                    state.RemoteSupportDataChannelOpenAt ??= DateTimeOffset.UtcNow;
                }
            }

            if (firstFrameDelivered.HasValue)
            {
                state.RemoteSupportFirstFrameDelivered = firstFrameDelivered.Value;
                if (firstFrameDelivered.Value)
                {
                    state.RemoteSupportFirstFrameDeliveredAt ??= DateTimeOffset.UtcNow;
                }
            }

            if (renderedFrames.HasValue)
            {
                state.RemoteSupportRenderedFrames = renderedFrames.Value;
            }

            if (captureSupported.HasValue)
            {
                state.RemoteSupportCaptureSupported = captureSupported.Value;
            }

        });
    }

    private void UpdateInputProviderState(bool inputReceived = false, bool inputInjected = false, string? inputError = null)
    {
        var activeConsoleSessionId = Native.WTSGetActiveConsoleSessionId();
        var activeConsole = activeConsoleSessionId == uint.MaxValue ? (uint?)null : activeConsoleSessionId;
        var processSessionId = Process.GetCurrentProcess().SessionId;
        var matchesTarget = _providerMetadata.IsConsoleProvider
            ? activeConsole.HasValue && processSessionId == unchecked((int)activeConsole.Value)
            : _targetWindowsSessionId.HasValue && processSessionId == _targetWindowsSessionId.Value;
        var desktopContext = OperatingSystem.IsWindows() ? _inputDispatcher?.Snapshot : null;
        var desktopReady = _providerMetadata.IsConsoleProvider || desktopContext?.Ready == true;
#pragma warning disable CA1416
        var desktopName = Native.GetCurrentThreadDesktopName();
#pragma warning restore CA1416
        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.ActiveInputProvider = _providerMetadata.Provider;
            state.InputProviderName = _providerMetadata.Provider;
            state.InputProviderSessionId = processSessionId;
            state.InputProviderActiveConsoleSessionId = activeConsole;
            state.InputProviderMatchesTargetSession = matchesTarget;
            state.InputProviderDesktopName = desktopContext?.DesktopName ?? desktopName ?? _providerMetadata.InputDesktopName;
            state.InputProviderReady = matchesTarget &&
                _peerConnection.connectionState == RTCPeerConnectionState.connected &&
                _controlChannel is not null &&
                desktopReady;
            state.RemoteSupportInputSupported = state.InputProviderReady;
            state.RemoteSupportStateUpdatedUtc = DateTimeOffset.UtcNow;
            state.RemoteSupportStateSource = "helper_input";
            state.RemoteSupportIsLiveSession = true;
            state.RemoteSupportIsLastClosedSession = false;
            if (inputReceived)
            {
                state.InputProviderLastInputReceivedAt = DateTimeOffset.UtcNow;
            }

            if (inputInjected)
            {
                state.InputProviderLastInputInjectedAt = DateTimeOffset.UtcNow;
                state.InputProviderLastInputError = null;
            }

            if (!string.IsNullOrWhiteSpace(inputError))
            {
                state.InputProviderLastInputError = inputError;
            }

            state.InputProviderInjectedMouseCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _mouseInjected)));
            state.InputProviderInjectedKeyCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _keyboardInjected)));
            state.InputProviderRejectedCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _inputRejected)));
            state.InputProviderReceivedMouseCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _mouseReceived)));
            state.InputProviderReceivedKeyCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _keyboardReceived)));
            state.MouseMoveReceivedCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _mouseMoveReceived)));
            state.MouseMoveSentCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _mouseMoveSent)));
            state.MouseMoveCoalescedCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _mouseMoveCoalesced)));
            state.MouseClickSentCount = unchecked((ulong)Math.Max(0, Interlocked.Read(ref _mouseClickSent)));
            state.InputProviderLastInputEventId = _lastInputEventId;
            state.InputProviderLastInputKind = _lastInputKind;
            state.InputProviderLastKeyCategory = _lastKeyCategory;
            state.InputProviderLastBrowserSentAt = _lastBrowserSentAt;
            state.InputProviderLastDataChannelSentAt = _lastDataChannelSentAt;
            state.InputProviderLastReceivedAt = _lastInputReceivedAt;
            state.InputProviderLastSendInputAttemptedAt = _lastSendInputAttemptedAt;
            state.InputProviderLastSendInputResultCount = _lastSendInputResultCount;
            state.InputProviderLastSendInputWin32Error = _lastSendInputWin32Error;
        });
    }

    private async Task StopVideoAsync()
    {
        if (!_videoStarted)
        {
            return;
        }

        _videoStarted = false;
        try
        {
            _videoCancellation.Cancel();
            if (_videoThread is not null)
            {
                _captureWake.Set();
                _videoThread.Join(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
        }

        await _videoSource.CloseVideo().ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private void DesktopCaptureLoop(CancellationToken ct)
    {
        var profile = GetProfile();
        LockCaptureGeometry(profile);
        var frameDurationMs = Math.Max(1, 1000 / profile.Fps);
        var frames = 0UL;
        var droppedFrames = 0UL;
        var consecutiveFailures = 0;
        var lastStats = DateTimeOffset.UtcNow;
        var lastRecoveryStatus = DateTimeOffset.MinValue;
        var captureRecovering = false;
        var lastProfile = profile;

        LogManager.WriteLog($"[RemoteSupportWebRTC] Desktop capture loop starting session={_sessionId} provider={_providerMetadata.Provider} captureProvider={_captureProvider.Name} backend={_captureProvider.BackendName} profile={profile.Name} max={profile.MaxWidth}x{profile.MaxHeight} fps={profile.Fps}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = DateTimeOffset.UtcNow;
                try
                {
                    profile = GetProfile();
                    if (!profile.Equals(lastProfile))
                    {
                        frameDurationMs = Math.Max(1, 1000 / profile.Fps);
                        lastProfile = profile;
                        _desktopContextCoordinator.Invalidate("capture_profile_changed");
                        LogManager.WriteLog($"[RemoteSupportWebRTC] Desktop capture profile switched session={_sessionId} provider={_providerMetadata.Provider} captureProvider={_captureProvider.Name} profile={profile.Name} max={profile.MaxWidth}x{profile.MaxHeight} fps={profile.Fps} targetKbps={profile.TargetKbps}");
                    }

                    RemoteSupportCaptureFrame frame;
                    lock (_captureProviderSync)
                    {
                        frame = _captureProvider.CaptureFrame(profile.MaxWidth, profile.MaxHeight);
                    }
                    _lastCaptureSourceWidth = frame.SourceWidth;
                    _lastCaptureSourceHeight = frame.SourceHeight;
                    var frameUtc = DateTimeOffset.UtcNow;
                    var frameHash = ComputeFrameHash(frame.Bgr);
                    var frameChanged = !string.Equals(frameHash, _lastFrameHash, StringComparison.Ordinal);
                    _lastFrameHash = frameHash;
                    var captureSequence = ++_captureFrameSequence;
                    if (frameChanged)
                    {
                        _captureSameFrameCount = 0;
                    }
                    else
                    {
                        _captureSameFrameCount++;
                    }

                    _videoSource.ExternalVideoSourceRawSample((uint)frameDurationMs, frame.FrameWidth, frame.FrameHeight, frame.Bgr, VideoPixelFormatsEnum.Bgr);
                    frames++;
                    _encoderFrameSequence++;
                    UpdateFrameFreshnessState(frame, captureSequence, frameHash, frameChanged, frameUtc);
                    UpdateTransportState(firstFrameDelivered: true, renderedFrames: frames, captureSupported: true);
                    consecutiveFailures = 0;
                    if (captureRecovering)
                    {
                        captureRecovering = false;
                        _sendSignal(new RemoteSupportPipeSignal(
                            _sessionId,
                            RemoteSupportSignalTypes.Ready,
                            JsonSerializer.Serialize(new
                            {
                                code = "capture_ready",
                                statusCode = "capture_ready",
                                message = _providerMetadata.IsConsoleProvider
                                    ? "Console desktop capture recovered and delivered a frame."
                                    : "Interactive desktop capture recovered and delivered a frame.",
                                provider = _providerMetadata.Provider,
                                captureProvider = _captureProvider.Name,
                                backend = frame.BackendName,
                                firstFrameDelivered = true,
                                mediaSupported = true
                            }, _json)));
                    }

                    if (frames <= 3 || DateTimeOffset.UtcNow - lastStats > TimeSpan.FromSeconds(10))
                    {
                        LogManager.WriteLog($"[RemoteSupportWebRTC] Desktop frame stats session={_sessionId} provider={_providerMetadata.Provider} captureProvider={_captureProvider.Name} backend={frame.BackendName} profile={profile.Name} frames={frames} dropped={droppedFrames} bytes={frame.Bgr.Length} source={frame.SourceWidth}x{frame.SourceHeight} size={frame.FrameWidth}x{frame.FrameHeight} fps={profile.Fps} targetKbps={profile.TargetKbps} hash={frameHash} changed={frameChanged} sameFrameCount={_captureSameFrameCount} inputEventId={_pendingFeedbackInputEventId ?? _lastInputEventId ?? "<none>"} inputReceived={Interlocked.Read(ref _inputReceived)} inputInjected={Interlocked.Read(ref _inputInjected)} inputFailures={Interlocked.Read(ref _inputFailures)}");
                        lastStats = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    droppedFrames++;
                    consecutiveFailures++;
                    profile = GetProfile();
                    frameDurationMs = Math.Max(1, 1000 / profile.Fps);
                    var captureException = ex as RemoteSupportCaptureException;
                    if (captureException is not null && !captureException.DeterministicFailure)
                    {
                        captureRecovering = true;
                        if (DateTimeOffset.UtcNow - lastRecoveryStatus >= TimeSpan.FromSeconds(2) ||
                            captureException.AttemptCount <= 1)
                        {
                            lastRecoveryStatus = DateTimeOffset.UtcNow;
                            _sendSignal(new RemoteSupportPipeSignal(
                                _sessionId,
                                RemoteSupportSignalTypes.Ready,
                                JsonSerializer.Serialize(new
                                {
                                    code = captureException.CaptureState ?? "capture_initializing",
                                    statusCode = captureException.CaptureState ?? "capture_initializing",
                                    message = captureException.FirstFrameDelivered
                                        ? "Desktop changed; recovering desktop capture."
                                        : "Initializing desktop capture.",
                                    provider = _providerMetadata.Provider,
                                    captureProvider = captureException.ProviderName,
                                    backend = captureException.BackendName,
                                    captureState = captureException.CaptureState,
                                    captureAttemptCount = captureException.AttemptCount,
                                    captureElapsedMs = captureException.ElapsedMilliseconds,
                                    captureRetryAfterMs = captureException.RetryAfterMilliseconds,
                                    captureFirstFrameDelivered = captureException.FirstFrameDelivered,
                                    captureDesktopName = captureException.DesktopName,
                                    captureThreadDesktopBefore = captureException.ThreadDesktopBefore,
                                    captureThreadDesktopAfter = captureException.ThreadDesktopAfter,
                                    win32Error = captureException.Win32Error,
                                    hresult = captureException.HResultCode,
                                    backendResults = captureException.BackendResults,
                                    mediaSupported = true
                                }, _json)));
                        }

                        DelayCaptureLoop(
                            started,
                            Math.Max(frameDurationMs, captureException.RetryAfterMilliseconds),
                            _captureWake,
                            ct);
                        continue;
                    }

                    if (captureException?.DeterministicFailure == true)
                    {
                        consecutiveFailures = MaxConsecutiveCaptureFailures;
                    }

                    if (consecutiveFailures <= 3 || consecutiveFailures % 10 == 0)
                    {
                        LogManager.WriteLog($"[RemoteSupportWebRTC] Desktop capture frame dropped session={_sessionId} provider={_providerMetadata.Provider} captureProvider={_captureProvider.Name} backend={_captureProvider.BackendName} consecutiveFailures={consecutiveFailures} dropped={droppedFrames} nextMax={profile.MaxWidth}x{profile.MaxHeight} error={ex.Message}");
                    }

                    if (consecutiveFailures < MaxConsecutiveCaptureFailures)
                    {
                        DelayCaptureLoop(started, frameDurationMs, _captureWake, ct);
                        continue;
                    }

                    LogManager.WriteLog($"[RemoteSupportWebRTC] Desktop capture failed session={_sessionId} provider={_providerMetadata.Provider} captureProvider={captureException?.ProviderName ?? _captureProvider.Name} backend={captureException?.BackendName ?? _captureProvider.BackendName} win32Error={captureException?.Win32Error?.ToString() ?? "<none>"} consecutiveFailures={consecutiveFailures}: {ex}");
                    UpdateTransportState(firstFrameDelivered: false, captureSupported: false);
                    _sendSignal(new RemoteSupportPipeSignal(
                        _sessionId,
                        RemoteSupportSignalTypes.Error,
                        JsonSerializer.Serialize(new
                        {
                            code = captureException?.CaptureState == "interactive_desktop_recovery_exhausted"
                                ? "interactive_desktop_recovery_exhausted"
                                : "desktop_capture_failed",
                            statusCode = captureException?.CaptureState == "interactive_desktop_recovery_exhausted"
                                ? "interactive_desktop_recovery_exhausted"
                                : "desktop_capture_failed",
                            message = _providerMetadata.IsConsoleProvider
                                ? captureException?.DeterministicFailure == true
                                    ? "Peer connected, but no Winlogon capture backend succeeded."
                                    : $"Console secure-desktop media preview failed after {consecutiveFailures} consecutive frame attempts: {ex.Message}"
                                : $"Desktop capture failed after {consecutiveFailures} consecutive frame attempts: {ex.Message}",
                            provider = _providerMetadata.Provider,
                            captureProvider = captureException?.ProviderName ?? _captureProvider.Name,
                            backend = captureException?.BackendName ?? _captureProvider.BackendName,
                            win32Error = captureException?.Win32Error,
                            hresult = captureException?.HResultCode,
                            captureState = captureException?.CaptureState,
                            captureAttemptCount = captureException?.AttemptCount,
                            captureElapsedMs = captureException?.ElapsedMilliseconds,
                            captureFirstFrameDelivered = captureException?.FirstFrameDelivered ?? false,
                            backendResults = captureException?.BackendResults,
                            captureDesktopName = captureException?.DesktopName,
                            captureThreadDesktopBefore = captureException?.ThreadDesktopBefore,
                            captureThreadDesktopAfter = captureException?.ThreadDesktopAfter,
                            desktopState = _providerMetadata.DesktopState,
                            supportLevel = _providerMetadata.SupportLevel,
                            mediaSupported = false,
                            reconnectRequired = _providerMetadata.ReconnectRequired,
                            inputDesktopName = _providerMetadata.InputDesktopName,
                            diagnosticHint = ex.Message
                        }, _json)));
                    return;
                }

                DelayCaptureLoop(started, frameDurationMs, _captureWake, ct);
            }
        }
        finally
        {
            if (_captureProvider is IRemoteSupportThreadBoundCaptureProvider threadBound)
            {
                threadBound.ReleaseThreadContext();
            }
        }
    }

    private static void DelayCaptureLoop(DateTimeOffset started, int frameDurationMs, AutoResetEvent wake, CancellationToken ct)
    {
        var elapsed = DateTimeOffset.UtcNow - started;
        var delay = TimeSpan.FromMilliseconds(frameDurationMs) - elapsed;
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        WaitHandle.WaitAny(new[] { wake, ct.WaitHandle }, Math.Max(1, (int)delay.TotalMilliseconds));
    }

    private void SchedulePostInputRefresh(string inputEventId, DateTimeOffset injectedAt)
    {
        lock (_feedbackSync)
        {
            _pendingFeedbackInputEventId = inputEventId;
            _pendingFeedbackInputInjectedAt = injectedAt;
            _firstFrameAfterInputAt = null;
            _firstChangedFrameAfterInputAt = null;
            _lastFeedbackStatus = null;
        }

        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.CaptureLastInputEventId = inputEventId;
            state.CaptureLastInputInjectedUtc = injectedAt;
            state.CaptureFirstFrameAfterInputUtc = null;
            state.CaptureFirstChangedFrameAfterInputUtc = null;
            state.CaptureInputToChangedFrameMs = null;
            state.InputVisualFeedbackStatus = "post_input_capture_requested";
            state.RemoteSupportStateUpdatedUtc = DateTimeOffset.UtcNow;
            state.RemoteSupportStateSource = "helper_post_input_refresh";
        });

        LogManager.WriteLog($"[RemoteSupportWebRTC] Post-input refresh scheduled session={_sessionId} inputEventId={inputEventId}");
        _ = PulseCaptureAfterAsync(TimeSpan.Zero, _videoCancellation.Token);
        _ = PulseCaptureAfterAsync(TimeSpan.FromMilliseconds(50), _videoCancellation.Token);
        _ = PulseCaptureAfterAsync(TimeSpan.FromMilliseconds(150), _videoCancellation.Token);
        _ = PulseCaptureAfterAsync(TimeSpan.FromMilliseconds(300), _videoCancellation.Token);
        _ = CheckVisualFeedbackStaleAsync(inputEventId, injectedAt, _videoCancellation.Token);
    }

    private async Task PulseCaptureAfterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            ReleaseCaptureWake();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckVisualFeedbackStaleAsync(string inputEventId, DateTimeOffset injectedAt, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(650), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var stale = false;
        lock (_feedbackSync)
        {
            stale = string.Equals(_pendingFeedbackInputEventId, inputEventId, StringComparison.Ordinal) &&
                _pendingFeedbackInputInjectedAt == injectedAt &&
                !_firstChangedFrameAfterInputAt.HasValue;
        }

        if (!stale)
        {
            return;
        }

        SendVisualFeedbackStatus("input_visual_feedback_stale", "Refreshing login screen video.");
        RequestCaptureRefresh("input_visual_feedback_stale", resetProvider: true);
    }

    private void RequestCaptureRefresh(string reason, bool resetProvider)
    {
        if (resetProvider)
        {
            lock (_captureProviderSync)
            {
                _captureProvider.Refresh();
            }
        }

        var keyframeStatus = TryRequestKeyframe(reason) ? "requested" : "not_supported";
        _lastKeyframeStatus = keyframeStatus;
        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.EncoderKeyframeRequestedAt = DateTimeOffset.UtcNow;
            state.EncoderKeyframeStatus = keyframeStatus;
            state.InputVisualFeedbackStatus = reason;
            state.RemoteSupportStateUpdatedUtc = DateTimeOffset.UtcNow;
            state.RemoteSupportStateSource = "helper_capture_refresh";
        });

        LogManager.WriteLog($"[RemoteSupportWebRTC] Capture refresh requested session={_sessionId} reason={reason} resetProvider={resetProvider} keyframe={keyframeStatus}");
        ReleaseCaptureWake();
    }

    private void ReleaseCaptureWake()
    {
        _captureWake.Set();
    }

    private bool TryRequestKeyframe(string reason)
    {
        var method = _videoSource.GetType().GetMethods()
            .FirstOrDefault(x => x.GetParameters().Length == 0 &&
                (x.Name.Contains("KeyFrame", StringComparison.OrdinalIgnoreCase) ||
                 x.Name.Contains("Keyframe", StringComparison.OrdinalIgnoreCase) ||
                 x.Name.Contains("PictureLoss", StringComparison.OrdinalIgnoreCase)));
        if (method is null)
        {
            LogManager.WriteLog($"[RemoteSupportWebRTC] post_input_keyframe_not_supported session={_sessionId} reason={reason}");
            return false;
        }

        try
        {
            method.Invoke(_videoSource, null);
            LogManager.WriteLog($"[RemoteSupportWebRTC] post_input_keyframe_requested session={_sessionId} reason={reason} method={method.Name}");
            return true;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportWebRTC] post_input_keyframe_failed session={_sessionId} reason={reason} method={method.Name}: {ex.Message}");
            return false;
        }
    }

    private void UpdateFrameFreshnessState(RemoteSupportCaptureFrame frame, ulong captureSequence, string frameHash, bool frameChanged, DateTimeOffset frameUtc)
    {
        string? confirmedInputId = null;
        long? inputToChangedMs = null;
        lock (_feedbackSync)
        {
            if (_pendingFeedbackInputInjectedAt.HasValue &&
                frameUtc >= _pendingFeedbackInputInjectedAt.Value)
            {
                _firstFrameAfterInputAt ??= frameUtc;
                if (frameChanged)
                {
                    _firstChangedFrameAfterInputAt ??= frameUtc;
                    confirmedInputId = _pendingFeedbackInputEventId;
                    inputToChangedMs = (long)Math.Max(0, (frameUtc - _pendingFeedbackInputInjectedAt.Value).TotalMilliseconds);
                    _lastFeedbackStatus = "input_visual_feedback_confirmed";
                    _pendingFeedbackInputEventId = null;
                    _pendingFeedbackInputInjectedAt = null;
                }
            }
        }

        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.CaptureFrameSequence = captureSequence;
            state.CaptureFrameHash = frameHash;
            state.CaptureFrameChanged = frameChanged;
            state.CaptureSameFrameCount = _captureSameFrameCount;
            state.CaptureProviderBackendName = frame.BackendName;
            state.CaptureProviderLastSuccessfulFrameUtc = frameUtc;
            if (frameChanged)
            {
                state.CaptureLastChangedFrameUtc = frameUtc;
            }

            state.CaptureLastInputEventId = _pendingFeedbackInputEventId ?? _lastInputEventId;
            state.CaptureLastInputInjectedUtc = _lastInputInjectedAt;
            state.CaptureFirstFrameAfterInputUtc = _firstFrameAfterInputAt;
            state.CaptureFirstChangedFrameAfterInputUtc = _firstChangedFrameAfterInputAt;
            state.CaptureInputToChangedFrameMs = inputToChangedMs ?? state.CaptureInputToChangedFrameMs;
            state.EncoderFrameSequence = _encoderFrameSequence;
            state.EncoderLastFrameSentAt = frameUtc;
            state.InputVisualFeedbackStatus = _lastFeedbackStatus ?? state.InputVisualFeedbackStatus;
            state.RemoteSupportStateUpdatedUtc = DateTimeOffset.UtcNow;
            state.RemoteSupportStateSource = "helper_capture";
        });

        if (!string.IsNullOrWhiteSpace(confirmedInputId))
        {
            LogManager.WriteLog($"[RemoteSupportWebRTC] input_visual_feedback_confirmed session={_sessionId} inputEventId={confirmedInputId} ms={inputToChangedMs} frameHash={frameHash}");
            SendVisualFeedbackStatus("input_visual_feedback_confirmed", "Input visual feedback confirmed.");
        }
    }

    private void SendVisualFeedbackStatus(string code, string message)
    {
        _sendSignal(new RemoteSupportPipeSignal(
            _sessionId,
            RemoteSupportSignalTypes.Ready,
            JsonSerializer.Serialize(new
            {
                code,
                statusCode = code,
                message,
                provider = _providerMetadata.Provider,
                desktopState = _providerMetadata.DesktopState,
                supportLevel = _providerMetadata.SupportLevel,
                mediaSupported = true,
                captureFrameSequence = _captureFrameSequence,
                captureFrameHash = _lastFrameHash,
                captureSameFrameCount = _captureSameFrameCount,
                captureLastInputEventId = _lastInputEventId,
                captureInputToChangedFrameMs = GetCurrentInputToChangedMs(),
                encoderFrameSequence = _encoderFrameSequence,
                encoderKeyframeStatus = _lastKeyframeStatus,
                inputVisualFeedbackStatus = code
            }, _json)));
    }

    private long? GetCurrentInputToChangedMs()
    {
        lock (_feedbackSync)
        {
            if (_firstChangedFrameAfterInputAt.HasValue && _lastInputInjectedAt.HasValue)
            {
                return (long)Math.Max(0, (_firstChangedFrameAfterInputAt.Value - _lastInputInjectedAt.Value).TotalMilliseconds);
            }
        }

        return null;
    }

    private static string ComputeFrameHash(byte[] frame)
    {
        var hash = SHA256.HashData(frame);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static (int SourceWidth, int SourceHeight, int FrameWidth, int FrameHeight) GetCaptureGeometry(RemoteSupportMediaProfile profile)
    {
        var sourceWidth = Math.Max(1, GetSystemMetrics(0));
        var sourceHeight = Math.Max(1, GetSystemMetrics(1));
        var (frameWidth, frameHeight) = ScaleToFit(sourceWidth, sourceHeight, profile.MaxWidth, profile.MaxHeight);
        return (sourceWidth, sourceHeight, frameWidth, frameHeight);
    }

    private RemoteSupportMediaProfile GetProfile()
    {
        lock (_profileLock)
        {
            return _profile;
        }
    }

    private void SendReadyStatus(string message)
    {
        var profile = GetProfile();
        var processSessionId = Process.GetCurrentProcess().SessionId;
        var activeConsoleSessionId = Native.WTSGetActiveConsoleSessionId();
        var matchesTarget = _providerMetadata.IsConsoleProvider
            ? activeConsoleSessionId != uint.MaxValue && processSessionId == unchecked((int)activeConsoleSessionId)
            : _targetWindowsSessionId.HasValue && processSessionId == _targetWindowsSessionId.Value;
        var desktopContext = OperatingSystem.IsWindows() ? _inputDispatcher?.Snapshot : null;
        var desktopReady = _providerMetadata.IsConsoleProvider || desktopContext?.Ready == true;
        _sendSignal(new RemoteSupportPipeSignal(
            _sessionId,
            RemoteSupportSignalTypes.Ready,
            JsonSerializer.Serialize(new
            {
                role = "agent",
                platform = "windows",
                media = "webrtc_desktop_capture",
                message,
                provider = _providerMetadata.Provider,
                desktopState = _providerMetadata.DesktopState,
                supportLevel = _providerMetadata.SupportLevel,
                mediaSupported = true,
                reconnectRequired = _providerMetadata.ReconnectRequired,
                inputDesktopName = _providerMetadata.InputDesktopName,
                activeInputProvider = _providerMetadata.Provider,
                inputProviderName = _providerMetadata.Provider,
                targetWindowsSessionId = _targetWindowsSessionId,
                helperProcessId = Environment.ProcessId,
                helperProcessSessionId = processSessionId,
                inputProviderMatchesTargetSession = matchesTarget,
                inputProviderReady = matchesTarget &&
                    _peerConnection.connectionState == RTCPeerConnectionState.connected &&
                    _controlChannel is not null &&
                    desktopReady,
                inputProviderDesktopGeneration = desktopContext?.Generation,
                inputProviderDesktopName = desktopContext?.DesktopName ?? _providerMetadata.InputDesktopName,
                inputProviderDesktopStatus = desktopContext?.Status,
                inputProviderReceivedMouseCount = Interlocked.Read(ref _mouseReceived),
                inputProviderReceivedKeyCount = Interlocked.Read(ref _keyboardReceived),
                inputProviderInjectedMouseCount = Interlocked.Read(ref _mouseInjected),
                inputProviderInjectedKeyCount = Interlocked.Read(ref _keyboardInjected),
                inputProviderRejectedCount = Interlocked.Read(ref _inputRejected),
                mouseMoveReceivedCount = Interlocked.Read(ref _mouseMoveReceived),
                mouseMoveSentCount = Interlocked.Read(ref _mouseMoveSent),
                mouseMoveCoalescedCount = Interlocked.Read(ref _mouseMoveCoalesced),
                mouseClickSentCount = Interlocked.Read(ref _mouseClickSent),
                captureFrameHash = _lastFrameHash,
                captureSameFrameCount = _captureSameFrameCount,
                captureInputToChangedFrameMs = GetCurrentInputToChangedMs(),
                inputVisualFeedbackStatus = _lastFeedbackStatus,
                profile = profile.Name,
                maxWidth = profile.MaxWidth,
                maxHeight = profile.MaxHeight,
                fps = profile.Fps,
                targetKbps = profile.TargetKbps,
                bitrate = profile.BitrateStatus,
                geometry = profile.GeometryStatus
            }, _json)));
    }

    private void SendInputStatsStatus()
    {
        var activeConsoleSessionId = Native.WTSGetActiveConsoleSessionId();
        var processSessionId = Process.GetCurrentProcess().SessionId;
        var matchesTarget = _providerMetadata.IsConsoleProvider
            ? activeConsoleSessionId != uint.MaxValue && processSessionId == unchecked((int)activeConsoleSessionId)
            : _targetWindowsSessionId.HasValue && processSessionId == _targetWindowsSessionId.Value;
        var desktopContext = OperatingSystem.IsWindows() ? _inputDispatcher?.Snapshot : null;
        var desktopReady = _providerMetadata.IsConsoleProvider || desktopContext?.Ready == true;
#pragma warning disable CA1416
        var desktopName = Native.GetCurrentThreadDesktopName();
#pragma warning restore CA1416
        _sendSignal(new RemoteSupportPipeSignal(
            _sessionId,
            RemoteSupportSignalTypes.Ready,
            JsonSerializer.Serialize(new
            {
                code = "input_provider_stats",
                statusCode = "input_provider_stats",
                message = matchesTarget ? "Input ready." : "Input unavailable.",
                provider = _providerMetadata.Provider,
                desktopState = _providerMetadata.DesktopState,
                supportLevel = _providerMetadata.SupportLevel,
                mediaSupported = true,
                activeInputProvider = _providerMetadata.Provider,
                inputProviderName = _providerMetadata.Provider,
                inputProviderSessionId = processSessionId,
                targetWindowsSessionId = _targetWindowsSessionId,
                helperProcessId = Environment.ProcessId,
                helperProcessSessionId = processSessionId,
                peerInstanceId = _peerInstanceId,
                dataChannelId = _dataChannelId,
                dataChannelLabel = _dataChannelLabel,
                inputProviderActiveConsoleSessionId = activeConsoleSessionId == uint.MaxValue ? (uint?)null : activeConsoleSessionId,
                inputProviderMatchesTargetSession = matchesTarget,
                inputProviderDesktopName = desktopContext?.DesktopName ?? desktopName ?? _providerMetadata.InputDesktopName,
                inputProviderReady = matchesTarget &&
                    _peerConnection.connectionState == RTCPeerConnectionState.connected &&
                    _controlChannel is not null &&
                    desktopReady,
                inputProviderDesktopGeneration = desktopContext?.Generation,
                inputProviderDesktopStatus = desktopContext?.Status,
                inputProviderReceivedMouseCount = Interlocked.Read(ref _mouseReceived),
                inputProviderReceivedKeyCount = Interlocked.Read(ref _keyboardReceived),
                inputProviderInjectedMouseCount = Interlocked.Read(ref _mouseInjected),
                inputProviderInjectedKeyCount = Interlocked.Read(ref _keyboardInjected),
                inputProviderRejectedCount = Interlocked.Read(ref _inputRejected),
                mouseMoveReceivedCount = Interlocked.Read(ref _mouseMoveReceived),
                mouseMoveSentCount = Interlocked.Read(ref _mouseMoveSent),
                mouseMoveCoalescedCount = Interlocked.Read(ref _mouseMoveCoalesced),
                mouseClickSentCount = Interlocked.Read(ref _mouseClickSent),
                captureFrameHash = _lastFrameHash,
                captureSameFrameCount = _captureSameFrameCount,
                captureInputToChangedFrameMs = GetCurrentInputToChangedMs(),
                inputVisualFeedbackStatus = _lastFeedbackStatus
            }, _json)));
    }

    private void SendInputStatus(string code, string message)
    {
        _sendSignal(new RemoteSupportPipeSignal(
            _sessionId,
            RemoteSupportSignalTypes.Error,
            JsonSerializer.Serialize(new
            {
                code,
                statusCode = code,
                message,
                provider = _providerMetadata.Provider,
                desktopState = _providerMetadata.DesktopState,
                supportLevel = _providerMetadata.SupportLevel,
                mediaSupported = true,
                inputAvailable = false,
                activeInputProvider = _providerMetadata.Provider,
                inputProviderName = _providerMetadata.Provider,
                inputProviderReady = false,
                inputProviderReceivedMouseCount = Interlocked.Read(ref _mouseReceived),
                inputProviderReceivedKeyCount = Interlocked.Read(ref _keyboardReceived),
                inputProviderInjectedMouseCount = Interlocked.Read(ref _mouseInjected),
                inputProviderInjectedKeyCount = Interlocked.Read(ref _keyboardInjected),
                inputProviderRejectedCount = Interlocked.Read(ref _inputRejected),
                inputProviderLastInputError = message,
                reconnectRequired = false
            }, _json)));
    }

    private void SendInteractiveDesktopStatus(
        string code,
        RemoteSupportDesktopContextSnapshot snapshot,
        string reason)
    {
        var exhausted = string.Equals(code, "interactive_desktop_recovery_exhausted", StringComparison.Ordinal);
        var ready = string.Equals(code, "interactive_desktop_ready", StringComparison.Ordinal);
        _sendSignal(new RemoteSupportPipeSignal(
            _sessionId,
            exhausted ? RemoteSupportSignalTypes.Error : RemoteSupportSignalTypes.Ready,
            JsonSerializer.Serialize(new
            {
                code,
                statusCode = code,
                message = exhausted
                    ? "Interactive desktop recovery was exhausted; restarting the exact-session helper."
                    : ready
                        ? "Interactive desktop context recovered."
                        : "Recovering the interactive desktop context.",
                provider = _providerMetadata.Provider,
                desktopState = _providerMetadata.DesktopState,
                supportLevel = _providerMetadata.SupportLevel,
                mediaSupported = !exhausted,
                reconnectRequired = exhausted,
                inputAvailable = ready,
                inputProviderName = _providerMetadata.Provider,
                inputProviderReady = ready,
                targetWindowsSessionId = _targetWindowsSessionId,
                inputProviderDesktopGeneration = snapshot.Generation,
                inputProviderDesktopName = snapshot.DesktopName,
                inputProviderDesktopStatus = snapshot.Status,
                inputProviderDesktopWin32Error = snapshot.Win32Error,
                inputProviderDesktopWorkerThreadId = snapshot.WorkerThreadId,
                inputProviderDesktopLastBoundAt = snapshot.LastBoundAt,
                inputProviderDesktopLastProbeAt = snapshot.LastProbeAt,
                inputProviderDesktopInvalidationReason = snapshot.LastInvalidationReason ?? reason
            }, _json)));
        UpdateInputProviderState(inputError: exhausted || !ready ? reason : null);
    }

    private void LockCaptureGeometry(RemoteSupportMediaProfile profile)
    {
        lock (_profileLock)
        {
            _lockedProfileMaxWidth ??= profile.MaxWidth;
            _lockedProfileMaxHeight ??= profile.MaxHeight;
            if (profile.MaxWidth != _lockedProfileMaxWidth.Value ||
                profile.MaxHeight != _lockedProfileMaxHeight.Value)
            {
                _profile = profile with
                {
                    MaxWidth = _lockedProfileMaxWidth.Value,
                    MaxHeight = _lockedProfileMaxHeight.Value,
                    GeometryStatus = "resolution_pending_reconnect"
                };
            }
        }
    }

    private bool TryApplyTargetBitrate(int targetKbps)
    {
        var candidates = new[] { "TargetBitrate", "VideoBitrate", "Bitrate", "RcTargetBitrate" };
        var type = _videoSource.GetType();
        foreach (var name in candidates)
        {
            var property = type.GetProperty(name);
            if (property is null || !property.CanWrite)
            {
                continue;
            }

            try
            {
                if (property.PropertyType == typeof(int))
                {
                    property.SetValue(_videoSource, targetKbps);
                    return true;
                }

                if (property.PropertyType == typeof(uint))
                {
                    property.SetValue(_videoSource, (uint)targetKbps);
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] CaptureBgrFrame(int sourceWidth, int sourceHeight, int frameWidth, int frameHeight)
    {
        using var source = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(sourceWidth, sourceHeight), CopyPixelOperation.SourceCopy);
        }

        using var scaled = new Bitmap(frameWidth, frameHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.DrawImage(source, 0, 0, frameWidth, frameHeight);
        }

        var data = scaled.LockBits(
            new Rectangle(0, 0, frameWidth, frameHeight),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            var rowBytes = frameWidth * 3;
            var buffer = new byte[rowBytes * frameHeight];
            for (var y = 0; y < frameHeight; y++)
            {
                var sourcePtr = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(sourcePtr, buffer, y * rowBytes, rowBytes);
            }

            return buffer;
        }
        finally
        {
            scaled.UnlockBits(data);
        }
    }

    private static (int Width, int Height) ScaleToFit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        var scale = Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight);
        scale = Math.Min(1d, Math.Max(0.1d, scale));
        var width = Math.Max(2, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(2, (int)Math.Round(sourceHeight * scale));
        if (width % 2 != 0)
        {
            width--;
        }

        if (height % 2 != 0)
        {
            height--;
        }

        return (Math.Max(2, width), Math.Max(2, height));
    }

    private sealed record RemoteSupportQualityProfileMessage(
        string Type,
        string? Profile = null,
        int? MaxWidth = null,
        int? MaxHeight = null,
        int? Fps = null,
        int? TargetKbps = null);

    private sealed record RemoteSupportMediaProfile(
        string Name,
        int MaxWidth,
        int MaxHeight,
        int Fps,
        int TargetKbps,
        string BitrateStatus,
        string GeometryStatus)
    {
        public static readonly RemoteSupportMediaProfile Default = new("low", 1024, 576, 4, 900, "not_applied", "active");

        public static RemoteSupportMediaProfile FromRequest(RemoteSupportQualityProfileMessage request)
        {
            var name = string.IsNullOrWhiteSpace(request.Profile)
                ? "custom"
                : request.Profile.Trim().ToLowerInvariant();

            return new RemoteSupportMediaProfile(
                name,
                ClampEven(request.MaxWidth ?? Default.MaxWidth, 640, 1920),
                ClampEven(request.MaxHeight ?? Default.MaxHeight, 360, 1080),
                Math.Clamp(request.Fps ?? Default.Fps, 2, 12),
                Math.Clamp(request.TargetKbps ?? Default.TargetKbps, 500, 5000),
                "not_applied",
                "active");
        }

        public static RemoteSupportMediaProfile FromDto(RemoteSupportMediaProfileDto? dto)
        {
            if (dto is null)
            {
                return Default;
            }

            return new RemoteSupportMediaProfile(
                string.IsNullOrWhiteSpace(dto.Profile) ? "custom" : dto.Profile.Trim().ToLowerInvariant(),
                ClampEven(dto.MaxWidth, 640, 1920),
                ClampEven(dto.MaxHeight, 360, 1080),
                Math.Clamp(dto.Fps, 2, 12),
                Math.Clamp(dto.TargetKbps, 500, 5000),
                "not_applied",
                "active");
        }

        private static int ClampEven(int value, int min, int max)
        {
            var clamped = Math.Clamp(value, min, max);
            return clamped % 2 == 0 ? clamped : clamped - 1;
        }
    }

    private async Task WaitForIceGatheringAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_peerConnection.iceGatheringState == RTCIceGatheringState.complete)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RTCIceGatheringState state)
        {
            if (state == RTCIceGatheringState.complete)
            {
                tcs.TrySetResult();
            }
        }

        _peerConnection.onicegatheringstatechange += Handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                LogManager.WriteLog($"[RemoteSupportWebRTC] ICE gathering wait timed out session={_sessionId} state={_peerConnection.iceGatheringState}");
            }
        }
        finally
        {
            _peerConnection.onicegatheringstatechange -= Handler;
        }
    }

    private static int CountSdpCandidates(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            return 0;
        }

        return sdp.Split('\n').Count(line => line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase));
    }

    private static List<RTCIceServer> BuildIceServers(IReadOnlyList<RemoteSupportIceServerDto>? configured)
    {
        var servers = new List<RTCIceServer>();
        foreach (var server in configured ?? Array.Empty<RemoteSupportIceServerDto>())
        {
            foreach (var url in server.Urls.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                servers.Add(new RTCIceServer
                {
                    urls = url.Trim(),
                    username = string.IsNullOrWhiteSpace(server.Username) ? null : server.Username,
                    credential = string.IsNullOrWhiteSpace(server.Credential) ? null : server.Credential
                });
            }
        }

        return servers;
    }

    private static RTCSessionDescriptionInit ParseDescription(string json, RTCSdpType fallbackType)
    {
        using var doc = JsonDocument.Parse(json);
        var type = fallbackType;
        if (doc.RootElement.TryGetProperty("type", out var typeElement) &&
            string.Equals(typeElement.GetString(), "answer", StringComparison.OrdinalIgnoreCase))
        {
            type = RTCSdpType.answer;
        }

        var sdp = doc.RootElement.TryGetProperty("sdp", out var sdpElement)
            ? sdpElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(sdp))
        {
            throw new InvalidOperationException("WebRTC session description did not include SDP.");
        }

        return new RTCSessionDescriptionInit
        {
            type = type,
            sdp = sdp
        };
    }

    private static string TryGetPayloadTypeForLog(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? "<unknown>"
                : "<unknown>";
        }
        catch
        {
            return "<unparseable>";
        }
    }

    private sealed record SendInputAttemptResult(
        DateTimeOffset AttemptedAt,
        int ResultCount,
        int Win32Error,
        bool Succeeded,
        string Status,
        bool RetryAttempted = false,
        bool DesktopRebound = false,
        bool UipiPossible = false,
        long DesktopGeneration = 0,
        string? DesktopName = null,
        string? DesktopStatus = null);

    [SupportedOSPlatform("windows")]
    private sealed class InteractiveInputDispatcher : IDisposable
    {
        private readonly BlockingCollection<InputWorkItem> _queue = new();
        private readonly Func<RemoteDesktopInputRequest, SendInputAttemptResult> _inject;
        private readonly Thread _thread;
        private readonly string _sessionId;
        private readonly int? _targetWindowsSessionId;
        private readonly string? _targetUserSidHash;
        private readonly RemoteSupportDesktopContextCoordinator _coordinator;
        private readonly Action<string, RemoteSupportDesktopContextSnapshot, string> _statusChanged;
        private RemoteSupportDesktopContextSnapshot _snapshot = new(
            false, 0, null, 0, "desktop_context_not_started", null, null, null, 0);
        private DateTimeOffset? _recoveryStartedAt;
        private bool _recoveryExhausted;
        private string? _lastPublishedStatus;

        internal static readonly TimeSpan RecoveryProbeInterval = TimeSpan.FromMilliseconds(500);
        internal static readonly TimeSpan RecoveryDeadline = TimeSpan.FromSeconds(15);

        public InteractiveInputDispatcher(
            string sessionId,
            int? targetWindowsSessionId,
            string? targetUserSidHash,
            RemoteSupportDesktopContextCoordinator coordinator,
            Func<RemoteDesktopInputRequest, SendInputAttemptResult> inject,
            Action<string, RemoteSupportDesktopContextSnapshot, string> statusChanged)
        {
            _sessionId = sessionId;
            _targetWindowsSessionId = targetWindowsSessionId;
            _targetUserSidHash = targetUserSidHash;
            _coordinator = coordinator;
            _inject = inject;
            _statusChanged = statusChanged;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"NetRatel.RemoteInput.{targetWindowsSessionId?.ToString() ?? "unknown"}"
            };
            _thread.Start();
        }

        public RemoteSupportDesktopContextSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public SendInputAttemptResult Invoke(RemoteDesktopInputRequest input)
        {
            var completion = new TaskCompletionSource<SendInputAttemptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(new InputWorkItem(input, completion));
            }
            catch (InvalidOperationException)
            {
                return new SendInputAttemptResult(DateTimeOffset.UtcNow, 0, 0, false, "input_dispatcher_closed");
            }

            if (!completion.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                return new SendInputAttemptResult(DateTimeOffset.UtcNow, 0, 0, false, "input_dispatch_timeout");
            }

            return completion.Task.GetAwaiter().GetResult();
        }

        private void Run()
        {
            RemoteSupportInteractiveDesktopContext? context = null;
            try
            {
                var processSessionId = Process.GetCurrentProcess().SessionId;
                var windowStation = Native.GetProcessWindowStationName();
                var before = Native.GetCurrentThreadDesktopName();
                context = new RemoteSupportInteractiveDesktopContext(
                    new WindowsRemoteSupportDesktopContextNative(),
                    _coordinator,
                    requireInputProbe: true);
                PublishSnapshot(context.EnsureReady(forceRebind: true));
                var after = Snapshot.DesktopName ?? Native.GetCurrentThreadDesktopName();
                LogManager.WriteLog(
                    $"[RemoteSupportInput] {(Snapshot.Ready ? "remote_input_desktop_attached" : "remote_input_desktop_attach_failed")} session={_sessionId} targetSession={_targetWindowsSessionId?.ToString() ?? "none"} helperPid={Environment.ProcessId} helperSession={processSessionId} user={Environment.UserDomainName}\\{Environment.UserName} windowStation={windowStation ?? "unknown"} threadDesktopBefore={before ?? "unknown"} inputDesktop={Snapshot.DesktopName ?? "unknown"} threadDesktopAfter={after ?? "unknown"} targetSidHash={_targetUserSidHash ?? "none"} generation={Snapshot.Generation} win32={Snapshot.Win32Error}");
                if (!Snapshot.Ready)
                {
                    BeginRecovery("initial_desktop_bind_failed");
                }

                while (!_queue.IsCompleted)
                {
                    if (!_queue.TryTake(out var item, (int)RecoveryProbeInterval.TotalMilliseconds))
                    {
                        if (context.NeedsRefresh || _recoveryStartedAt.HasValue)
                        {
                            TryBackgroundRecovery(context, "desktop_context_probe");
                        }
                        continue;
                    }

                    try
                    {
                        var snapshot = context.EnsureReady();
                        PublishSnapshot(snapshot);
                        if (!snapshot.Ready)
                        {
                            BeginRecovery(snapshot.Status);
                            item.Completion.TrySetResult(CreateUnavailableResult(snapshot));
                            continue;
                        }

                        var result = WithDesktop(_inject(item.Input), snapshot);
                        if (!result.Succeeded && !result.UipiPossible)
                        {
                            context.Invalidate(result.Status);
                            BeginRecovery(result.Status);
                            var rebound = context.EnsureReady(forceRebind: true);
                            PublishSnapshot(rebound);
                            if (rebound.Ready)
                            {
                                var retry = WithDesktop(_inject(item.Input), rebound) with
                                {
                                    RetryAttempted = true,
                                    DesktopRebound = true
                                };
                                result = retry;
                                if (retry.Succeeded)
                                {
                                    CompleteRecovery(rebound, "input_retry_succeeded");
                                }
                                else
                                {
                                    context.Invalidate(retry.Status);
                                    BeginRecovery(retry.Status);
                                }
                            }
                            else
                            {
                                result = result with
                                {
                                    RetryAttempted = true,
                                    DesktopRebound = false,
                                    DesktopGeneration = rebound.Generation,
                                    DesktopName = rebound.DesktopName,
                                    DesktopStatus = rebound.Status
                                };
                            }
                        }

                        item.Completion.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        item.Completion.TrySetException(ex);
                    }
                }
            }
            finally
            {
                context?.Dispose();
            }
        }

        private void TryBackgroundRecovery(RemoteSupportInteractiveDesktopContext context, string reason)
        {
            if (_recoveryExhausted)
            {
                return;
            }

            BeginRecovery(reason);
            var snapshot = context.EnsureReady(forceRebind: true);
            PublishSnapshot(snapshot);
            if (snapshot.Ready)
            {
                CompleteRecovery(snapshot, reason);
                return;
            }

            if (_recoveryStartedAt.HasValue && DateTimeOffset.UtcNow - _recoveryStartedAt.Value >= RecoveryDeadline)
            {
                _recoveryExhausted = true;
                PublishStatus("interactive_desktop_recovery_exhausted", snapshot, reason);
            }
        }

        private void BeginRecovery(string reason)
        {
            _recoveryStartedAt ??= DateTimeOffset.UtcNow;
            PublishStatus("interactive_desktop_recovering", Snapshot, reason);
        }

        private void CompleteRecovery(RemoteSupportDesktopContextSnapshot snapshot, string reason)
        {
            var wasRecovering = _recoveryStartedAt.HasValue;
            _recoveryStartedAt = null;
            _recoveryExhausted = false;
            if (wasRecovering)
            {
                PublishStatus("interactive_desktop_ready", snapshot, reason);
            }
        }

        private void PublishSnapshot(RemoteSupportDesktopContextSnapshot snapshot) =>
            Volatile.Write(ref _snapshot, snapshot);

        private void PublishStatus(string code, RemoteSupportDesktopContextSnapshot snapshot, string reason)
        {
            if (string.Equals(_lastPublishedStatus, code, StringComparison.Ordinal))
            {
                return;
            }

            _lastPublishedStatus = code;
            _statusChanged(code, snapshot, reason);
        }

        private static SendInputAttemptResult WithDesktop(
            SendInputAttemptResult result,
            RemoteSupportDesktopContextSnapshot snapshot) => result with
            {
                DesktopGeneration = snapshot.Generation,
                DesktopName = snapshot.DesktopName,
                DesktopStatus = snapshot.Status
            };

        private static SendInputAttemptResult CreateUnavailableResult(RemoteSupportDesktopContextSnapshot snapshot) => new(
            DateTimeOffset.UtcNow,
            0,
            snapshot.Win32Error,
            false,
            snapshot.Status,
            DesktopGeneration: snapshot.Generation,
            DesktopName: snapshot.DesktopName,
            DesktopStatus: snapshot.Status);

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }

        private sealed record InputWorkItem(
            RemoteDesktopInputRequest Input,
            TaskCompletionSource<SendInputAttemptResult> Completion);
    }

    private void ValidateInputTarget(RemoteDesktopInputRequest input)
    {
        if (!string.IsNullOrWhiteSpace(input.RemoteSupportSessionId) &&
            !string.Equals(input.RemoteSupportSessionId, _sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("interactive_input_remote_support_session_mismatch");
        }

        var processSessionId = Process.GetCurrentProcess().SessionId;
        if (_providerMetadata.IsConsoleProvider)
        {
#pragma warning disable CA1416
            ValidateConsoleInputTarget();
#pragma warning restore CA1416
            return;
        }

        if (!_targetWindowsSessionId.HasValue ||
            processSessionId != _targetWindowsSessionId.Value ||
            (input.TargetWindowsSessionId.HasValue && input.TargetWindowsSessionId.Value != _targetWindowsSessionId.Value))
        {
            throw new InvalidOperationException("interactive_input_target_session_mismatch");
        }
    }

    private void SendInputAcknowledgement(
        string inputEventId,
        string category,
        DateTimeOffset helperReceivedAt,
        SendInputAttemptResult result,
        bool injectionAttempted)
    {
        var channel = _controlChannel;
        if (channel is null)
        {
            return;
        }

        var accepted = result.Succeeded;
        var acknowledgedAt = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            type = "input_ack",
            inputEventId,
            provider = _providerMetadata.Provider,
            remoteSupportSessionId = _sessionId,
            targetWindowsSessionId = _targetWindowsSessionId,
            helperProcessId = Environment.ProcessId,
            helperProcessSessionId = Process.GetCurrentProcess().SessionId,
            peerInstanceId = _peerInstanceId,
            dataChannelId = _dataChannelId,
            dataChannelLabel = _dataChannelLabel,
            category,
            helperDataChannelReceivedAt = helperReceivedAt,
            inputAcknowledgedAt = acknowledgedAt,
            received = true,
            parsed = true,
            injectionAttempted,
            injectionResultCount = result.ResultCount,
            win32Error = result.Win32Error,
            accepted,
            status = result.Status,
            retryAttempted = result.RetryAttempted,
            desktopRebound = result.DesktopRebound,
            uipiPossible = result.UipiPossible,
            desktopGeneration = result.DesktopGeneration,
            desktopName = result.DesktopName,
            desktopStatus = result.DesktopStatus
        }, _json);

        try
        {
            channel.send(payload);
            _lastAcknowledgementAt = acknowledgedAt;
            LogManager.WriteLog($"[RemoteSupportInput] remote_input_ack_sent session={_sessionId} targetSession={_targetWindowsSessionId?.ToString() ?? "none"} helperPid={Environment.ProcessId} helperSession={Process.GetCurrentProcess().SessionId} peer={_peerInstanceId ?? "unknown"} channel={_dataChannelId ?? _dataChannelLabel ?? "unknown"} inputEventId={inputEventId} category={category} accepted={accepted} result={result.ResultCount} win32={result.Win32Error} status={result.Status}");
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportInput] remote_input_ack_failed session={_sessionId} inputEventId={inputEventId} category={category} error={ex.Message}");
        }
    }

    private void LogRemoteInput(
        string stage,
        string inputEventId,
        string category,
        DateTimeOffset timestamp,
        RemoteDesktopInputRequest input,
        SendInputAttemptResult? result)
    {
        LogManager.WriteLog(
            $"[RemoteSupportInput] {stage} session={_sessionId} targetSession={_targetWindowsSessionId?.ToString() ?? "none"} helperPid={Environment.ProcessId} helperSession={Process.GetCurrentProcess().SessionId} provider={_providerMetadata.Provider} peer={input.PeerInstanceId ?? _peerInstanceId ?? "unknown"} channel={input.DataChannelId ?? input.DataChannelLabel ?? _dataChannelId ?? _dataChannelLabel ?? "unknown"} channelState={input.DataChannelState ?? "unknown"} inputEventId={inputEventId} category={category} at={timestamp:o} browserCreatedAt={input.BrowserEventCreatedAt?.ToString("o") ?? input.BrowserSentAt?.ToString("o") ?? "unknown"} browserSentAt={input.DataChannelSentAt?.ToString("o") ?? "unknown"} result={result?.ResultCount.ToString() ?? "na"} win32={result?.Win32Error.ToString() ?? "na"} status={result?.Status ?? "na"} retry={result?.RetryAttempted.ToString() ?? "na"} rebound={result?.DesktopRebound.ToString() ?? "na"} desktopGeneration={result?.DesktopGeneration.ToString() ?? "na"} desktop={result?.DesktopName ?? "unknown"}");
    }

    private static string CategorizeMouse(string type) => type switch
    {
        RemoteDesktopInputTypes.MouseMove => "mouse_move",
        RemoteDesktopInputTypes.MouseWheel => "mouse_wheel",
        RemoteDesktopInputTypes.MouseDown or RemoteDesktopInputTypes.MouseUp => "mouse_button",
        _ => "mouse"
    };

    private static string CategorizeKey(RemoteDesktopInputRequest input)
    {
        var key = input.Key;
        if (!string.IsNullOrEmpty(key) && key.Length == 1)
        {
            var ch = key[0];
            if (char.IsLetter(ch))
            {
                return "letter";
            }

            if (char.IsDigit(ch))
            {
                return "digit";
            }

            if (char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                return "punctuation";
            }
        }

        if (input.CtrlKey || input.ShiftKey || input.AltKey || input.MetaKey ||
            string.Equals(key, "Shift", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "Control", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "Alt", StringComparison.OrdinalIgnoreCase))
        {
            return "modifier";
        }

        if (!string.IsNullOrWhiteSpace(input.Code) &&
            input.Code.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase))
        {
            return "digit";
        }

        return key switch
        {
            "Enter" or "Escape" or "Backspace" or "Tab" or "ArrowLeft" or "ArrowUp" or "ArrowRight" or "ArrowDown" or
            "Delete" or "Home" or "End" or "PageUp" or "PageDown" or "Insert" => "navigation",
            " " or "Space" or "Spacebar" => "punctuation",
            _ => "navigation"
        };
    }

    [SupportedOSPlatform("windows")]
    private SendInputAttemptResult ApplyWindowsInput(RemoteDesktopInputRequest input)
    {
        return _inputDispatcher is null
            ? ApplyWindowsInputOnCurrentThread(input)
            : _inputDispatcher.Invoke(input);
    }

    [SupportedOSPlatform("windows")]
    private SendInputAttemptResult ApplyWindowsInputOnCurrentThread(RemoteDesktopInputRequest input)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var resultCount = 0;
        var win32Error = 0;
        var cursorSucceeded = true;
        var operation = input.Type;

        if (input.Type is RemoteDesktopInputTypes.MouseMove or
            RemoteDesktopInputTypes.MouseDown or
            RemoteDesktopInputTypes.MouseUp or
            RemoteDesktopInputTypes.MouseWheel)
        {
            var sourceWidth = _lastCaptureSourceWidth > 0 ? _lastCaptureSourceWidth : GetSystemMetrics(0);
            var sourceHeight = _lastCaptureSourceHeight > 0 ? _lastCaptureSourceHeight : GetSystemMetrics(1);
            var x = (int)Math.Round(Math.Clamp(input.X ?? 0, 0, 1) * Math.Max(1, sourceWidth - 1));
            var y = (int)Math.Round(Math.Clamp(input.Y ?? 0, 0, 1) * Math.Max(1, sourceHeight - 1));
            Marshal.SetLastPInvokeError(0);
            cursorSucceeded = SetCursorPos(x, y);
            if (!cursorSucceeded)
            {
                win32Error = Marshal.GetLastPInvokeError();
                return new SendInputAttemptResult(
                    attemptedAt,
                    0,
                    win32Error,
                    false,
                    "set_cursor_pos_failed");
            }

            if (input.Type == RemoteDesktopInputTypes.MouseMove)
            {
                resultCount = 1;
            }
        }

        var operationError = 0;
        switch (input.Type)
        {
            case RemoteDesktopInputTypes.MouseDown:
                resultCount = SendMouseButton(input.Button ?? 0, true, out operationError);
                break;
            case RemoteDesktopInputTypes.MouseUp:
                resultCount = SendMouseButton(input.Button ?? 0, false, out operationError);
                break;
            case RemoteDesktopInputTypes.MouseWheel:
                resultCount = SendMouse(MouseEventFlags.Wheel, -(input.DeltaY ?? 0), out operationError);
                break;
            case RemoteDesktopInputTypes.KeyDown:
                if (IsSecureAttentionAttempt(input))
                {
                    throw new SecureAttentionNotSupportedException();
                }

                if (MapVirtualKey(input.Key, input.Code) == 0)
                {
                    return new SendInputAttemptResult(attemptedAt, 0, 0, false, "key_mapping_failed");
                }

                resultCount = SendKey(input, true, out operationError);
                break;
            case RemoteDesktopInputTypes.KeyUp:
                if (IsSecureAttentionAttempt(input))
                {
                    throw new SecureAttentionNotSupportedException();
                }

                if (MapVirtualKey(input.Key, input.Code) == 0)
                {
                    return new SendInputAttemptResult(attemptedAt, 0, 0, false, "key_mapping_failed");
                }

                resultCount = SendKey(input, false, out operationError);
                break;
        }

        if (win32Error == 0 && operationError != 0)
        {
            win32Error = operationError;
        }

        var succeeded = input.Type == RemoteDesktopInputTypes.MouseMove
            ? cursorSucceeded
            : resultCount > 0;
        var uipiPossible = !succeeded && win32Error == 0 &&
            input.Type is RemoteDesktopInputTypes.MouseDown or RemoteDesktopInputTypes.MouseUp or
                RemoteDesktopInputTypes.MouseWheel or RemoteDesktopInputTypes.KeyDown or RemoteDesktopInputTypes.KeyUp;
        var status = succeeded
            ? "injected"
            : uipiPossible
                ? "send_input_uipi_possible"
                : input.Type == RemoteDesktopInputTypes.MouseMove
                    ? "set_cursor_pos_failed"
                    : "send_input_failed";
        return new SendInputAttemptResult(attemptedAt, resultCount, win32Error, succeeded, status, UipiPossible: uipiPossible);
    }

    private static bool IsSecureAttentionAttempt(RemoteDesktopInputRequest input) =>
        input.CtrlKey &&
        input.AltKey &&
        (string.Equals(input.Key, "Delete", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(input.Code, "Delete", StringComparison.OrdinalIgnoreCase));

    [SupportedOSPlatform("windows")]
    private void ValidateConsoleInputTarget()
    {
        var activeConsoleSessionId = Native.WTSGetActiveConsoleSessionId();
        if (activeConsoleSessionId == uint.MaxValue)
        {
            throw new InvalidOperationException("No active console session is available for console input.");
        }

        var processSessionId = Process.GetCurrentProcess().SessionId;
        if (processSessionId != unchecked((int)activeConsoleSessionId))
        {
            throw new InvalidOperationException($"Console input rejected because helper session {processSessionId} does not match active console session {activeConsoleSessionId}.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static int SendMouseButton(int button, bool down, out int win32Error)
    {
        var flags = button switch
        {
            1 => down ? MouseEventFlags.RightDown : MouseEventFlags.RightUp,
            2 => down ? MouseEventFlags.MiddleDown : MouseEventFlags.MiddleUp,
            _ => down ? MouseEventFlags.LeftDown : MouseEventFlags.LeftUp
        };
        return SendMouse(flags, 0, out win32Error);
    }

    [SupportedOSPlatform("windows")]
    private static int SendMouse(MouseEventFlags flags, int data, out int win32Error)
    {
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flags,
                    mouseData = data
                }
            }
        };
        Marshal.SetLastPInvokeError(0);
        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        win32Error = sent == 0 ? Marshal.GetLastPInvokeError() : 0;
        return unchecked((int)sent);
    }

    [SupportedOSPlatform("windows")]
    private static int SendKey(RemoteDesktopInputRequest input, bool down, out int win32Error)
    {
        win32Error = 0;
        var vk = MapVirtualKey(input.Key, input.Code);
        if (vk == 0)
        {
            return 0;
        }

        var sent = 0;
        var callError = 0;
        if (down)
        {
            sent += SendModifierKeys(input, true, out callError);
            PreserveFirstError(ref win32Error, callError);
            sent += SendVirtualKey(vk, true, out callError);
            PreserveFirstError(ref win32Error, callError);
        }
        else
        {
            sent += SendVirtualKey(vk, false, out callError);
            PreserveFirstError(ref win32Error, callError);
            sent += SendModifierKeys(input, false, out callError);
            PreserveFirstError(ref win32Error, callError);
        }

        return sent;
    }

    [SupportedOSPlatform("windows")]
    private static int SendModifierKeys(RemoteDesktopInputRequest input, bool down, out int win32Error)
    {
        win32Error = 0;
        var sent = 0;
        var callError = 0;
        if (input.CtrlKey)
        {
            sent += SendVirtualKey(0x11, down, out callError);
            PreserveFirstError(ref win32Error, callError);
        }

        if (input.ShiftKey)
        {
            sent += SendVirtualKey(0x10, down, out callError);
            PreserveFirstError(ref win32Error, callError);
        }

        if (input.AltKey)
        {
            sent += SendVirtualKey(0x12, down, out callError);
            PreserveFirstError(ref win32Error, callError);
        }

        return sent;
    }

    private static void PreserveFirstError(ref int currentError, int candidateError)
    {
        if (currentError == 0 && candidateError != 0)
        {
            currentError = candidateError;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int SendVirtualKey(ushort vk, bool down, out int win32Error)
    {
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = down ? 0u : 2u
                }
            }
        };
        Marshal.SetLastPInvokeError(0);
        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        win32Error = sent == 0 ? Marshal.GetLastPInvokeError() : 0;
        return unchecked((int)sent);
    }

    private static ushort MapVirtualKey(string? key, string? code)
    {
        if (string.IsNullOrEmpty(key))
        {
            return MapVirtualKeyCode(code);
        }

        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return ch;
            }
        }

        return key switch
        {
            "Enter" => 0x0D,
            "Escape" => 0x1B,
            "Backspace" => 0x08,
            "Tab" => 0x09,
            " " or "Space" or "Spacebar" => 0x20,
            "ArrowLeft" => 0x25,
            "ArrowUp" => 0x26,
            "ArrowRight" => 0x27,
            "ArrowDown" => 0x28,
            "Delete" => 0x2E,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "Insert" => 0x2D,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => MapVirtualKeyCode(code)
        };
    }

    private static ushort MapVirtualKeyCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return 0;
        }

        if (code.StartsWith("Key", StringComparison.OrdinalIgnoreCase) && code.Length == 4)
        {
            var ch = char.ToUpperInvariant(code[3]);
            if (ch is >= 'A' and <= 'Z')
            {
                return ch;
            }
        }

        if (code.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) && code.Length == 6)
        {
            var ch = code[5];
            if (ch is >= '0' and <= '9')
            {
                return ch;
            }
        }

        return code switch
        {
            "Minus" => 0xBD,
            "Equal" => 0xBB,
            "BracketLeft" => 0xDB,
            "BracketRight" => 0xDD,
            "Backslash" => 0xDC,
            "Semicolon" => 0xBA,
            "Quote" => 0xDE,
            "Comma" => 0xBC,
            "Period" => 0xBE,
            "Slash" => 0xBF,
            "Backquote" => 0xC0,
            "Numpad0" => 0x60,
            "Numpad1" => 0x61,
            "Numpad2" => 0x62,
            "Numpad3" => 0x63,
            "Numpad4" => 0x64,
            "Numpad5" => 0x65,
            "Numpad6" => 0x66,
            "Numpad7" => 0x67,
            "Numpad8" => 0x68,
            "Numpad9" => 0x69,
            "NumpadMultiply" => 0x6A,
            "NumpadAdd" => 0x6B,
            "NumpadSubtract" => 0x6D,
            "NumpadDecimal" => 0x6E,
            "NumpadDivide" => 0x6F,
            _ => 0
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UpdateTransportState(stateIsCurrentSession: false, peerLastState: "disposed");
        try
        {
            _peerConnection.Close("remote support helper disposed");
        }
        catch
        {
        }

        try
        {
            _videoCancellation.Cancel();
            _captureWake.Set();
            _videoThread?.Join(TimeSpan.FromSeconds(2));
            _videoSource.CloseVideo().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _videoCancellation.Dispose();
        _captureWake.Dispose();
        _videoSource.Dispose();
        _captureProvider.Dispose();
        if (OperatingSystem.IsWindows())
        {
            _inputDispatcher?.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public MouseEventFlags dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [Flags]
    private enum MouseEventFlags : uint
    {
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        MiddleDown = 0x0020,
        MiddleUp = 0x0040,
        Wheel = 0x0800
    }
}
