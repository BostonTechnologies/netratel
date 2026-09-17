using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Shared.Contracts.RemoteSupport;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteSupport;

internal sealed class RemoteSupportSessionManager : IDisposable
{
    private readonly Action<RemoteSupportPipeSignal> _gatewaySignalSink;
    private readonly ConcurrentDictionary<string, GatewayRemoteSupportSessionState> _gatewaySessions = new(StringComparer.Ordinal);
    private readonly RemoteDesktopUserHelperPipeHost? _helperPipeHost;
    private RemoteSupportConsoleProviderPipeHost? _consoleProviderPipeHost;
    private readonly ConcurrentDictionary<string, ulong> _sequences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _announced = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _processedSignals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionProviders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteSupportSessionRoute> _sessionRoutes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteSupportV2PreparedMediaRoute> _preparedMediaRoutes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _targetPreparations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _desktopRecoveryRepairs = new(StringComparer.Ordinal);
    private readonly RemoteSupportHandoverOptions _handoverOptions;
    private RemoteSupportProviderTransitionCoordinator? _transitionCoordinator;
    private RemoteSupportSasController? _sasController;
    private RemoteSupportInteractiveHelperRepairService? _helperRepairService;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly string _serviceVersion = GetCurrentVersion();
    private readonly RemoteSupportAssistTargetPreflight _assistTargetPreflight;
    private readonly IRemoteSupportWindowsSessionSource _windowsSessionSource;
    private readonly IRemoteSupportTransitionEvidenceSink? _transitionEvidenceSink;
    private readonly ConcurrentDictionary<string, string> _transitionEvidenceStates = new(StringComparer.Ordinal);
    private readonly Timer? _transitionEvidenceTimer;
    private bool _disposed;

    /// <summary>
    /// Owns the Windows helper/provider side of an Akka gateway support stream.
    /// The gateway is the sole signalling transport.
    /// </summary>
    internal RemoteSupportSessionManager(
        RemoteDesktopUserHelperPipeHost? helperPipeHost,
        Action<RemoteSupportPipeSignal> gatewaySignalSink,
        RemoteSupportHandoverOptions? handoverOptions = null,
        IRemoteSupportWindowsSessionSource? windowsSessionSource = null,
        IRemoteSupportTransitionEvidenceSink? transitionEvidenceSink = null)
    {
        ArgumentNullException.ThrowIfNull(gatewaySignalSink);
        _helperPipeHost = helperPipeHost;
        _gatewaySignalSink = gatewaySignalSink;
        _handoverOptions = handoverOptions ?? RemoteSupportHandoverOptions.Disabled;
        _windowsSessionSource = windowsSessionSource ?? new LocalRemoteSupportWindowsSessionSource();
        _transitionEvidenceSink = transitionEvidenceSink;
        _assistTargetPreflight = new RemoteSupportAssistTargetPreflight(_windowsSessionSource);
        _transitionEvidenceTimer = transitionEvidenceSink is null
            ? null
            : new Timer(_ => ObserveWindowsTransitionEvidence(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        InitializeProviderRuntime();
        LogManager.WriteLog("[RemoteSupport] Gateway provider runtime initialized.");
    }

    private void InitializeProviderRuntime()
    {
        if (OperatingSystem.IsWindows() && _helperPipeHost is not null)
        {
            _helperPipeHost.HelperMessageReceived += HandleHelperPipeMessage;
            _helperPipeHost.HelperDisconnected += HandleHelperDisconnected;
        }

        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            _sasController = new RemoteSupportSasController();
            _consoleProviderPipeHost = new RemoteSupportConsoleProviderPipeHost();
            _consoleProviderPipeHost.MessageReceived += HandleConsoleProviderPipeMessage;
            _consoleProviderPipeHost.ProviderDisconnected += HandleConsoleProviderDisconnected;
            _consoleProviderPipeHost.Start();
            _helperRepairService = new RemoteSupportInteractiveHelperRepairService(_helperPipeHost, _consoleProviderPipeHost, _serviceVersion);
            _ = Task.Run(RunStartupHelperRepairPreflightAsync);
#pragma warning restore CA1416
        }

        if (_handoverOptions.Enabled && _handoverOptions.CoordinatorEnabled)
        {
            _transitionCoordinator = new RemoteSupportProviderTransitionCoordinator(GetProviderDecision, SendHandoverNotification);
            LogManager.WriteLog("[RemoteSupport] Handover coordinator enabled.");
        }
        else
        {
            LogManager.WriteLog("[RemoteSupport] Handover disabled; provider changes require reconnect.");
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.HandoverSupported = false;
                state.HandoverInProgress = false;
                state.HandoverGeneration = 1;
                state.HandoverFailureReason = "disabled";
            });
        }
    }

    private async Task RunStartupHelperRepairPreflightAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
#pragma warning disable CA1416
            var task = new RemoteDesktopUserHelperTask();
            task.EnsureLauncherAndRunKey();
            task.TryRegisterScheduledTask();

            var helper = _helperPipeHost?.GetConnectedHelper();
            if (helper is not null &&
                _helperRepairService is not null &&
                !IsHelperVersionCompatible(helper.Version, _serviceVersion))
            {
                LogManager.WriteLog($"[RemoteSupportHelperRepair] Startup mismatch detected helperVersion={helper.Version} serviceVersion={_serviceVersion} pid={helper.ProcessId} session={helper.SessionId}");
                var result = await _helperRepairService.TryRepairAsync("startup", helper, CancellationToken.None).ConfigureAwait(false);
                LogManager.WriteLog($"[RemoteSupportHelperRepair] Startup preflight result={result.StatusCode} message={result.Message}");
            }
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportHelperRepair] Startup preflight failed; continuing agent startup. error={ex}");
        }
    }

    private Task PrepareGatewayTargetForOfferAsync(string sessionId, string openRequestPayload) =>
        PrepareTargetForOfferAsync(sessionId, openRequestPayload);

    private async Task PrepareTargetForOfferAsync(string sessionId, string? openRequestPayload)
    {
        try
        {
            var request = string.IsNullOrWhiteSpace(openRequestPayload)
                ? new OpenRemoteSupportRequest()
                : JsonSerializer.Deserialize<OpenRemoteSupportRequest>(openRequestPayload, _json) ?? new OpenRemoteSupportRequest();
            if (!RemoteSupportTargetResolver.TryResolve(request, out var target, out var targetError) || target is null)
            {
                SendSignal(sessionId, RemoteSupportSignalTypes.Error, JsonSerializer.Serialize(new
                {
                    code = RemoteSupportStatusCodes.TargetSessionStale,
                    statusCode = RemoteSupportStatusCodes.TargetSessionStale,
                    message = targetError ?? "Remote Support target could not be resolved.",
                    reconnectRequired = true
                }, _json));
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                SendSignal(sessionId, RemoteSupportSignalTypes.Error, JsonSerializer.Serialize(new
                {
                    code = "native_webrtc_unsupported_os",
                    statusCode = "native_webrtc_unsupported_os",
                    message = "Native remote support media is currently Windows-only.",
                    reconnectRequired = false
                }, _json));
                return;
            }

            SendTargetPreparationStatus(sessionId, request, RemoteSupportStatusCodes.TargetValidating, "Validating the selected Windows target.");
            var decision = GetProviderDecision();
            switch (target)
            {
                case InteractiveSessionTarget interactive:
                    {
                        if (!ValidateAssistUserTarget(
                                request,
                                decision,
                                out var failurePayload,
                                out var helperReady,
                                out var helperNeedsPreparation))
                        {
                            SendSignal(sessionId, RemoteSupportSignalTypes.Error, failurePayload);
                            return;
                        }

                        if (!helperReady && helperNeedsPreparation)
                        {
                            SendTargetPreparationStatus(
                                sessionId,
                                request,
                                RemoteSupportStatusCodes.TargetHelperLaunching,
                                $"Preparing the helper for Windows session {interactive.WindowsSessionId}.");
                            await PrepareInteractiveHelperAsync(
                                sessionId,
                                decision,
                                interactive.WindowsSessionId).ConfigureAwait(false);
                            decision = GetProviderDecision();
                            _ = ValidateAssistUserTarget(
                                request,
                                decision,
                                out failurePayload,
                                out helperReady,
                                out _);
                        }

                        if (!helperReady)
                        {
                            SendSignal(
                                sessionId,
                                RemoteSupportSignalTypes.Error,
                                string.IsNullOrWhiteSpace(failurePayload)
                                    ? BuildTargetFailurePayload(
                                        RemoteSupportStatusCodes.TargetHelperUnavailable,
                                        "The selected Windows session helper is unavailable.",
                                        decision,
                                        request)
                                    : failurePayload);
                            return;
                        }

                        SendTargetPreparationStatus(sessionId, request, RemoteSupportStatusCodes.TargetHelperReady, "Selected user-session helper is ready.", true);
                        break;
                    }

                case ConsoleLoginTarget:
                    {
                        _consoleProviderPipeHost?.ResetConnectedProviderIfVersionMismatch(_serviceVersion);
                        var provider = await EnsureConsoleProviderAsync(sessionId, decision).ConfigureAwait(false);
                        if (provider is null)
                        {
                            SendSignal(sessionId, RemoteSupportSignalTypes.Error, BuildConsoleProviderUnavailablePayload(decision));
                            return;
                        }

                        SendTargetPreparationStatus(sessionId, request, RemoteSupportStatusCodes.TargetHelperReady, "Console login provider is ready.", true);
                        break;
                    }

                case LegacyAutomaticTarget:
                    SendTargetPreparationStatus(sessionId, request, RemoteSupportStatusCodes.TargetHelperReady, "Legacy automatic provider selection is ready.", true);
                    break;
            }

            if (!IsSessionStillOpen(sessionId))
            {
                return;
            }

            SendTargetPreparationStatus(
                sessionId,
                request,
                RemoteSupportStatusCodes.ReadyForOffer,
                "Target validated and provider ready for the browser offer.",
                true);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Target preparation failed session={sessionId}: {ex}");
            SendSignal(sessionId, RemoteSupportSignalTypes.Error, JsonSerializer.Serialize(new
            {
                code = RemoteSupportStatusCodes.TargetHelperUnavailable,
                statusCode = RemoteSupportStatusCodes.TargetHelperUnavailable,
                message = ex.Message,
                reconnectRequired = true
            }, _json));
        }
    }

    private void SendTargetPreparationStatus(
        string sessionId,
        OpenRemoteSupportRequest request,
        string statusCode,
        string message,
        bool? mediaSupported = null)
    {
        var provider = IsLoginTargetMode(request.TargetMode)
            ? RemoteSupportProviderKinds.ConsoleSecureDesktopHelper
            : IsAssistUserTargetMode(request.TargetMode)
                ? RemoteSupportProviderKinds.InteractiveUserHelper
                : GetProviderDecision().Provider;
        SendSignal(sessionId, RemoteSupportSignalTypes.Ready, JsonSerializer.Serialize(new
        {
            code = statusCode,
            statusCode,
            message,
            provider,
            targetMode = request.TargetMode ?? "auto",
            targetWindowsSessionId = request.TargetWindowsSessionId,
            targetUserSidHash = request.TargetUserSidHash,
            inventorySequence = request.InventorySequence,
            mediaSupported,
            reconnectRequired = false,
            providerGeneration = 1
        }, _json));
    }

    private void AnnounceGatewayReady(string sessionId)
    {
        if (!_announced.TryAdd(sessionId, 0))
        {
            return;
        }

        _transitionCoordinator?.RegisterSession(sessionId);
        var providerDecision = GetProviderDecision();
        var generation = ResolveSessionGeneration(sessionId);
        SendSignal(
            sessionId,
            RemoteSupportSignalTypes.Ready,
            JsonSerializer.Serialize(new
            {
                role = "agent",
                platform = OperatingSystem.IsWindows() ? "windows" : "unsupported",
                media = providerDecision.MediaSupported ? "webrtc_helper_ready" : "prelogin_diagnostics_only",
                version = typeof(RemoteSupportSessionManager).Assembly.GetName().Version?.ToString(),
                provider = providerDecision.Provider,
                desktopState = providerDecision.DesktopState,
                supportLevel = providerDecision.SupportLevel,
                statusCode = providerDecision.StatusCode,
                message = providerDecision.Message,
                inputDesktopName = providerDecision.InputDesktopName,
                captureAvailable = providerDecision.CaptureAvailable,
                inputAvailable = providerDecision.InputAvailable,
                mediaSupported = providerDecision.MediaSupported,
                reconnectRequired = providerDecision.ReconnectRequired,
                providerGeneration = generation,
                handoverSupported = _handoverOptions.Enabled && _handoverOptions.CoordinatorEnabled,
                handoverState = RemoteSupportHandoverStates.Stable
            }, _json));
        LogManager.WriteLog($"[RemoteSupport] gateway provider ready session={sessionId}");
    }

    internal void OpenGatewaySession(string sessionId, string openRequestPayload)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A gateway remote-support session id is required.", nameof(sessionId));
        }

        var session = _gatewaySessions.AddOrUpdate(
            sessionId,
            _ => new GatewayRemoteSupportSessionState(openRequestPayload),
            (_, existing) => existing.Reopen(openRequestPayload));

        if (!OperatingSystem.IsWindows())
        {
            SendSignal(sessionId, RemoteSupportSignalTypes.Reject, JsonSerializer.Serialize(new
            {
                code = "native_webrtc_unsupported_os",
                message = "Native remote support media is currently Windows-only.",
                reconnectRequired = false
            }, _json));
            session.Close();
            return;
        }

        _announced.TryRemove(sessionId, out _);
        AnnounceGatewayReady(sessionId);
        _targetPreparations.GetOrAdd(sessionId, _ => Task.Run(() => PrepareGatewayTargetForOfferAsync(sessionId, session.OpenRequestPayload)));
    }

    internal void ProcessGatewaySignal(RemoteSupportPipeSignal signal, ulong sequence)
    {
        if (string.Equals(signal.SignalType, "open", StringComparison.OrdinalIgnoreCase))
        {
            OpenGatewaySession(signal.SessionId, signal.PayloadJson);
            return;
        }

        if (!_gatewaySessions.ContainsKey(signal.SessionId))
        {
            SendSignal(signal.SessionId, RemoteSupportSignalTypes.Error, JsonSerializer.Serialize(new
            {
                code = "remote_support_session_not_open",
                message = "The gateway did not establish this remote support session."
            }, _json));
            return;
        }

        if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Offer, StringComparison.OrdinalIgnoreCase) &&
            _gatewaySessions.TryGetValue(signal.SessionId, out var session))
        {
            session.NoteOffer(sequence);
        }

        ProcessIncomingSignal(new RemoteSupportIncomingSignal(
            signal.SessionId,
            signal.SignalType,
            signal.PayloadJson,
            sequence,
            unchecked((long)sequence)));
    }

    /// <summary>
    /// Opens an RS2-4 session only after the agent has retained an immutable
    /// preparation result. This path deliberately bypasses legacy target
    /// resolution: an offer may reach only the prepared helper pipe route.
    /// </summary>
    internal bool OpenPreparedV2Session(
        string sessionId,
        RemoteSupportPreparedTargetResult prepared,
        long negotiationGeneration,
        RemoteSupportSessionIceConfiguration? iceConfiguration)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || negotiationGeneration <= 0 ||
            iceConfiguration is null || iceConfiguration.Generation != negotiationGeneration ||
            iceConfiguration.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            !RemoteSupportV2PreparationValidator.TryValidate(prepared, out _) ||
            !prepared.TargetValid || !prepared.ProviderReady || prepared.HelperRoute is null)
        {
            return false;
        }

        var isConsole = RemoteSupportV2TargetKinds.IsConsoleLogin(prepared.Target.Kind);
        ConnectedUserHelper? helper = null;
        ConnectedConsoleProvider? consoleProvider = null;
        if (isConsole)
        {
            if (!TryValidatePreparedConsoleProvider(prepared, out consoleProvider))
            {
                return false;
            }
        }
        else if (!TryValidatePreparedHelper(prepared, out helper))
        {
            return false;
        }

        var provider = isConsole
            ? RemoteSupportProviderKinds.ConsoleSecureDesktopHelper
            : RemoteSupportProviderKinds.InteractiveUserHelper;
        var targetWindowsSessionId = isConsole ? consoleProvider!.SessionId : prepared.Target.WindowsSessionId;
        var request = new OpenRemoteSupportRequest(
            TargetMode: isConsole ? "console_login" : "assist_user",
            TargetWindowsSessionId: isConsole ? null : prepared.Target.WindowsSessionId,
            TargetUserSidHash: isConsole ? null : prepared.Target.UserSidHash,
            InventorySequence: prepared.Target.InventorySequence,
            IceServers: iceConfiguration.Servers);
        var requestPayload = JsonSerializer.Serialize(request, _json);
        _gatewaySessions.AddOrUpdate(
            sessionId,
            _ => new GatewayRemoteSupportSessionState(requestPayload),
            (_, existing) => existing.Reopen(requestPayload));
        _sessionProviders[sessionId] = provider;
        _sessionRoutes[sessionId] = new RemoteSupportSessionRoute(
            provider,
            checked((int)negotiationGeneration),
            "v2_prepared_route",
            null,
            provider,
            null,
            targetWindowsSessionId);
        _preparedMediaRoutes[sessionId] = new RemoteSupportV2PreparedMediaRoute(
            prepared,
            negotiationGeneration,
            prepared.HelperRoute.HelperRouteId,
            provider,
            isConsole ? consoleProvider!.ProcessId : null);
        return true;
    }

    internal bool ProcessPreparedV2Signal(RemoteSupportPipeSignal signal, ulong sequence, long negotiationGeneration)
    {
        if (!_preparedMediaRoutes.TryGetValue(signal.SessionId, out var route) || route.NegotiationGeneration != negotiationGeneration ||
            !IsSessionStillOpen(signal.SessionId))
        {
            return false;
        }

        var isConsole = string.Equals(route.Provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal);
        ConnectedUserHelper? helper = null;
        ConnectedConsoleProvider? consoleProvider = null;
        if (isConsole)
        {
            if (!TryValidatePreparedConsoleProvider(route.PreparedTarget, out consoleProvider) ||
                consoleProvider!.RouteId != route.HelperRouteId || consoleProvider.ProcessId != route.ProviderProcessId)
            {
                return false;
            }
        }
        else if (!TryValidatePreparedHelper(route.PreparedTarget, out helper) || helper!.RouteId != route.HelperRouteId)
        {
            return false;
        }

        if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Offer, StringComparison.OrdinalIgnoreCase))
        {
            _gatewaySessions[signal.SessionId].NoteOffer(sequence);
            var offerPayload = JsonSerializer.Serialize(new RemoteSupportPipeOffer(
                signal.PayloadJson,
                ReadIceServers(_gatewaySessions[signal.SessionId].OpenRequestPayload),
                null,
                checked((int)negotiationGeneration),
                "v2_prepared_route",
                null,
                route.Provider,
                null,
                route.PreparedTarget.Target.WindowsSessionId,
                route.PreparedTarget.Target.UserSidHash), _json);
            var offer = new RemoteSupportPipeSignal(signal.SessionId, signal.SignalType, offerPayload);
            return isConsole
                ? SendExactConsoleProviderMessage(consoleProvider!, RemoteSupportPipeKinds.Offer, offer)
                : SendExactHelperMessage(helper!, RemoteSupportPipeKinds.Offer, offer);
        }

        if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Ice, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(signal.SignalType, RemoteSupportV2NegotiationSignalTypes.EndOfCandidates, StringComparison.OrdinalIgnoreCase))
        {
            return isConsole
                ? SendExactConsoleProviderMessage(consoleProvider!, RemoteSupportPipeKinds.Ice, signal)
                : SendExactHelperMessage(helper!, RemoteSupportPipeKinds.Ice, signal);
        }

        return false;
    }

    internal void CloseGatewaySession(string sessionId, string reason)
    {
        if (_gatewaySessions.TryGetValue(sessionId, out var session))
        {
            session.Close();
        }

        _ = SendProviderMessage(
            ResolveSessionProvider(sessionId),
            RemoteSupportPipeKinds.Close,
            new RemoteSupportPipeSignal(sessionId, RemoteSupportSignalTypes.Close, JsonSerializer.Serialize(new { message = reason }, _json)));
        _sessionProviders.TryRemove(sessionId, out _);
        _sessionRoutes.TryRemove(sessionId, out _);
        _preparedMediaRoutes.TryRemove(sessionId, out _);
        _targetPreparations.TryRemove(sessionId, out _);
        _desktopRecoveryRepairs.TryRemove(sessionId, out _);
        _transitionCoordinator?.RemoveSession(sessionId);
    }

    private void ProcessIncomingSignal(RemoteSupportIncomingSignal signal)
    {
        var key = $"{signal.SessionId}:{signal.SignalType}:{signal.Sequence}:{signal.SortKey}";
        if (!_processedSignals.TryAdd(key, 0))
        {
            return;
        }

        LogManager.WriteLog($"[RemoteSupport] Signal received session={signal.SessionId} type={signal.SignalType} seq={signal.Sequence} bytes={signal.PayloadJson?.Length ?? 0}");
        if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Offer, StringComparison.OrdinalIgnoreCase))
        {
            LogManager.WriteLog($"[RemoteSupport] remoteSupportAgentSawOfferAt={DateTimeOffset.UtcNow:O} session={signal.SessionId} seq={signal.Sequence}");
        }
        var metadata = ReadSignalMetadata(signal.PayloadJson);
        var rawPayloadJson = GetSignalPayloadJson(signal.PayloadJson, metadata);

        if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Offer, StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() => HandleOfferSignalAsync(signal));
            return;
        }

        if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Ice, StringComparison.OrdinalIgnoreCase))
        {
            _ = SendProviderMessage(
                ResolveSessionProvider(signal.SessionId, metadata.ProviderGeneration),
                RemoteSupportPipeKinds.Ice,
                new RemoteSupportPipeSignal(signal.SessionId, signal.SignalType, rawPayloadJson));
            return;
        }

        if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Close, StringComparison.OrdinalIgnoreCase))
        {
            _ = SendProviderMessage(
                ResolveSessionProvider(signal.SessionId, metadata.ProviderGeneration),
                RemoteSupportPipeKinds.Close,
                new RemoteSupportPipeSignal(signal.SessionId, signal.SignalType, rawPayloadJson));
            _sessionProviders.TryRemove(signal.SessionId, out _);
            _sessionRoutes.TryRemove(signal.SessionId, out _);
            _transitionCoordinator?.RemoveSession(signal.SessionId);
        }
    }

    private async Task HandleOfferSignalAsync(RemoteSupportIncomingSignal signal)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                SendSignal(
                    signal.SessionId,
                    RemoteSupportSignalTypes.Error,
                    JsonSerializer.Serialize(new
                    {
                        code = "native_webrtc_unsupported_os",
                        message = "Native remote support media is currently Windows-only."
                    }, _json));
                return;
            }

            _transitionCoordinator?.RegisterSession(signal.SessionId);
            var providerDecision = GetProviderDecision();
            var metadata = ReadSignalMetadata(signal.PayloadJson);
            var openRequest = ReadOpenRequest(signal.SessionId);
            var targetMode = ReadTargetMode(signal.SessionId);
            var requestedProvider = !string.IsNullOrWhiteSpace(metadata.ToProvider)
                ? metadata.ToProvider
                : null;
            var targetProvider = requestedProvider
                ?? (IsAssistUserTargetMode(targetMode)
                    ? RemoteSupportProviderKinds.InteractiveUserHelper
                    : IsLoginTargetMode(targetMode)
                        ? RemoteSupportProviderKinds.ConsoleSecureDesktopHelper
                        : providerDecision.CanUseInteractiveUserHelper
                    ? RemoteSupportProviderKinds.InteractiveUserHelper
                    : providerDecision.CanUseConsoleSecureDesktopHelper
                        ? RemoteSupportProviderKinds.ConsoleSecureDesktopHelper
                        : providerDecision.Provider);
            var providerGeneration = _handoverOptions.ProviderGenerationEnabled
                ? metadata.ProviderGeneration <= 0
                    ? ResolveSessionGeneration(signal.SessionId)
                    : metadata.ProviderGeneration
                : 1;
            _sessionProviders[signal.SessionId] = targetProvider;
            _sessionRoutes[signal.SessionId] = new RemoteSupportSessionRoute(
                targetProvider,
                providerGeneration,
                metadata.Reason,
                metadata.FromProvider,
                targetProvider,
                metadata.HandoverReason,
                openRequest?.TargetWindowsSessionId);
            _transitionCoordinator?.NoteOffer(signal.SessionId, targetProvider, providerGeneration, metadata.HandoverReason);

            if (string.Equals(targetProvider, RemoteSupportProviderKinds.InteractiveUserHelper, StringComparison.Ordinal))
            {
                var assistTargetRequested = IsAssistUserTargetMode(targetMode);
                var assistTargetHelperReady = false;
                var assistTargetNeedsHelperPreparation = false;
                var targetFailurePayload = string.Empty;
                if (assistTargetRequested &&
                    !ValidateAssistUserTarget(
                        openRequest,
                        providerDecision,
                        out targetFailurePayload,
                        out assistTargetHelperReady,
                        out assistTargetNeedsHelperPreparation))
                {
                    SendSignal(
                        signal.SessionId,
                        RemoteSupportSignalTypes.Error,
                        WrapProviderSignalPayload(
                            signal.SessionId,
                            RemoteSupportSignalTypes.Error,
                            targetFailurePayload,
                            targetProvider));
                    return;
                }

                if (assistTargetRequested && assistTargetNeedsHelperPreparation)
                {
                    SendSignal(
                        signal.SessionId,
                        RemoteSupportSignalTypes.Ready,
                        WrapProviderSignalPayload(
                            signal.SessionId,
                            RemoteSupportSignalTypes.Ready,
                            BuildTargetHelperStatusPayload(
                                openRequest,
                                providerDecision,
                                "target_helper_missing",
                                "Preparing the helper for the selected Windows session."),
                            targetProvider));
                }

                if ((!assistTargetRequested && !providerDecision.CanUseInteractiveUserHelper) ||
                    (assistTargetRequested && !assistTargetHelperReady))
                {
                    var prepareResult = await PrepareInteractiveHelperAsync(
                        signal.SessionId,
                        providerDecision,
                        assistTargetRequested ? openRequest?.TargetWindowsSessionId : null).ConfigureAwait(false);
                    providerDecision = GetProviderDecision();
                    if (assistTargetRequested)
                    {
                        _ = ValidateAssistUserTarget(
                            openRequest,
                            providerDecision,
                            out targetFailurePayload,
                            out assistTargetHelperReady,
                            out assistTargetNeedsHelperPreparation);
                    }

                    var providerReady = assistTargetRequested
                        ? assistTargetHelperReady
                        : providerDecision.CanUseInteractiveUserHelper;
                    if ((prepareResult?.VersionRestored == true || assistTargetRequested) &&
                        providerReady &&
                        IsSessionStillOpen(signal.SessionId) &&
                        IsOfferStillCurrent(signal))
                    {
                        if (SendProviderMessage(
                            RemoteSupportProviderKinds.InteractiveUserHelper,
                            RemoteSupportPipeKinds.Offer,
                            new RemoteSupportPipeSignal(signal.SessionId, signal.SignalType, BuildHelperOfferPayload(signal, metadata, targetProvider))))
                        {
                            return;
                        }
                    }

                    SendSignal(
                        signal.SessionId,
                        RemoteSupportSignalTypes.Error,
                        WrapProviderSignalPayload(
                            signal.SessionId,
                            RemoteSupportSignalTypes.Error,
                            assistTargetRequested && !string.IsNullOrWhiteSpace(targetFailurePayload)
                                ? targetFailurePayload
                                : BuildProviderUnavailablePayload(providerDecision),
                            targetProvider));
                    _transitionCoordinator?.Trigger(signal.SessionId, "target_provider_unavailable");
                    return;
                }

                if (!SendProviderMessage(
                    RemoteSupportProviderKinds.InteractiveUserHelper,
                    RemoteSupportPipeKinds.Offer,
                    new RemoteSupportPipeSignal(signal.SessionId, signal.SignalType, BuildHelperOfferPayload(signal, metadata, targetProvider))))
                {
                    SendSignal(
                        signal.SessionId,
                        RemoteSupportSignalTypes.Error,
                        WrapProviderSignalPayload(
                            signal.SessionId,
                            RemoteSupportSignalTypes.Error,
                            BuildProviderUnavailablePayload(GetProviderDecision()),
                            targetProvider));
                }

                return;
            }

            if (string.Equals(targetProvider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal))
            {
                var loginTargetRequested = IsLoginTargetMode(targetMode);
                if (!providerDecision.CanUseConsoleSecureDesktopHelper && !loginTargetRequested)
                {
                    SendSignal(
                        signal.SessionId,
                        RemoteSupportSignalTypes.Error,
                        WrapProviderSignalPayload(
                            signal.SessionId,
                            RemoteSupportSignalTypes.Error,
                            BuildConsoleProviderUnavailablePayload(providerDecision),
                            targetProvider));
                    _transitionCoordinator?.Trigger(signal.SessionId, "target_provider_unavailable");
                    return;
                }

                _consoleProviderPipeHost?.ResetConnectedProviderIfVersionMismatch(_serviceVersion);
                var provider = await EnsureConsoleProviderAsync(signal.SessionId, providerDecision).ConfigureAwait(false);
                if (provider is null)
                {
                    SendSignal(
                        signal.SessionId,
                        RemoteSupportSignalTypes.Error,
                        WrapProviderSignalPayload(
                            signal.SessionId,
                            RemoteSupportSignalTypes.Error,
                            BuildConsoleProviderUnavailablePayload(providerDecision),
                            targetProvider));
                    return;
                }

                if (!SendProviderMessage(
                    RemoteSupportProviderKinds.ConsoleSecureDesktopHelper,
                    RemoteSupportPipeKinds.Offer,
                    new RemoteSupportPipeSignal(signal.SessionId, signal.SignalType, BuildHelperOfferPayload(signal, metadata, targetProvider))))
                {
                    SendSignal(
                        signal.SessionId,
                        RemoteSupportSignalTypes.Error,
                        WrapProviderSignalPayload(
                            signal.SessionId,
                            RemoteSupportSignalTypes.Error,
                            BuildConsoleProviderUnavailablePayload(providerDecision),
                            targetProvider));
                }
                else
                {
                    ScheduleConsoleProviderAnswerWatchdog(signal.SessionId);
                }

                return;
            }

            SendSignal(
                signal.SessionId,
                RemoteSupportSignalTypes.Error,
                WrapProviderSignalPayload(
                    signal.SessionId,
                    RemoteSupportSignalTypes.Error,
                    BuildProviderUnavailablePayload(providerDecision),
                    targetProvider));
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Offer handling failed session={signal.SessionId}: {ex}");
            SendSignal(
                signal.SessionId,
                RemoteSupportSignalTypes.Error,
                WrapProviderSignalPayload(signal.SessionId, RemoteSupportSignalTypes.Error, JsonSerializer.Serialize(new
                {
                    code = "remote_support_offer_handling_failed",
                    message = ex.Message
                }, _json), ResolveSessionProvider(signal.SessionId)));
        }
    }

    private async Task<RemoteSupportHelperRepairResult?> PrepareInteractiveHelperAsync(
        string sessionId,
        RemoteSupportProviderDecision providerDecision,
        int? targetWindowsSessionId = null)
    {
        if (!OperatingSystem.IsWindows() || _helperRepairService is null)
        {
            return null;
        }

        try
        {
#pragma warning disable CA1416
            var helper = targetWindowsSessionId.HasValue
                ? _helperPipeHost?.GetConnectedHelper(targetWindowsSessionId.Value)
                : _helperPipeHost?.GetConnectedHelper();
            if (helper is null)
            {
                SendHelperRepairStatus(
                    sessionId,
                    RemoteSupportStatusCodes.InteractiveHelperMissing,
                    "Preparing user remote support helper.",
                    null,
                    null,
                    providerDecision.HelperSessionId);
                var launch = targetWindowsSessionId.HasValue
                    ? await _helperRepairService.TryLaunchAsync(sessionId, targetWindowsSessionId.Value, CancellationToken.None).ConfigureAwait(false)
                    : await _helperRepairService.TryLaunchAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                SendHelperRepairStatus(sessionId, launch.StatusCode, launch.Message, launch.ConnectedVersion, launch.StalePid, launch.StaleSessionId, launch);
                return launch;
            }

            if (!targetWindowsSessionId.HasValue && !providerDecision.HelperMatchesActiveConsole)
            {
                SendHelperRepairStatus(
                    sessionId,
                    "helper_connected_non_console_session",
                    "Connected interactive helper is not in the active console session; refusing to attach to non-console/RDP helper.",
                    helper.Version,
                    helper.ProcessId,
                    helper.SessionId);
                return new RemoteSupportHelperRepairResult(
                    RemoteSupportStatusCodes.InteractiveHelperRepairRequiresLogoff,
                    "Connected helper is not in the active console session.",
                    false,
                    false,
                    false,
                    false,
                    true,
                    StalePid: helper.ProcessId,
                    StaleSessionId: helper.SessionId,
                    ConnectedVersion: helper.Version,
                    ServiceVersion: _serviceVersion);
            }

            if (!IsHelperVersionCompatible(helper.Version, _serviceVersion))
            {
                SendHelperRepairStatus(
                    sessionId,
                    RemoteSupportStatusCodes.InteractiveHelperVersionMismatchDetected,
                    "Updating remote support helper.",
                    helper.Version,
                    helper.ProcessId,
                    helper.SessionId);
                var repair = targetWindowsSessionId.HasValue
                    ? await _helperRepairService.TryRepairAsync(sessionId, targetWindowsSessionId.Value, helper, CancellationToken.None).ConfigureAwait(false)
                    : await _helperRepairService.TryRepairAsync(sessionId, helper, CancellationToken.None).ConfigureAwait(false);
                SendHelperRepairStatus(sessionId, repair.StatusCode, repair.Message, repair.ConnectedVersion, repair.StalePid, repair.StaleSessionId, repair);
                return repair;
            }
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportHelperRepair] Prepare failed session={sessionId}: {ex}");
            SendHelperRepairStatus(
                sessionId,
                RemoteSupportStatusCodes.InteractiveHelperRepairFailed,
                $"Interactive helper preparation failed: {ex.Message}",
                providerDecision.HelperVersion,
                null,
                providerDecision.HelperSessionId);
        }

        return null;
    }

    private async Task<ConnectedConsoleProvider?> EnsureConsoleProviderAsync(string sessionId, RemoteSupportProviderDecision decision)
    {
        if (!OperatingSystem.IsWindows() || _consoleProviderPipeHost is null)
        {
            return null;
        }

#pragma warning disable CA1416
        _consoleProviderPipeHost.ResetConnectedProviderIfVersionMismatch(_serviceVersion);
        var existing = _consoleProviderPipeHost.GetConnectedProvider();
        if (existing is not null && IsHelperVersionCompatible(existing.Version, _serviceVersion))
        {
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderVersionMatchesService = true;
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleSecureDesktopMediaPreviewAvailable;
                state.ConsoleProviderLastError = null;
            });
            return existing;
        }

        SendSignal(
            sessionId,
            RemoteSupportSignalTypes.Ready,
            JsonSerializer.Serialize(new
            {
                role = "agent",
                platform = "windows",
                media = "console_secure_desktop_media_preview_starting",
                provider = decision.Provider,
                desktopState = decision.DesktopState,
                supportLevel = decision.SupportLevel,
                statusCode = RemoteSupportStatusCodes.ConsoleSecureDesktopHelperStarting,
                message = "Starting console secure-desktop media preview provider.",
                reconnectRequired = decision.ReconnectRequired,
                inputDesktopName = decision.InputDesktopName
            }, _json));

        var provider = await _consoleProviderPipeHost
            .EnsureProviderAsync(TimeSpan.FromSeconds(8), CancellationToken.None)
            .ConfigureAwait(false);
        if (provider is not null && IsHelperVersionCompatible(provider.Version, _serviceVersion))
        {
            LogManager.WriteLog($"[RemoteSupport] Console provider ready session={sessionId} pid={provider.ProcessId} version={provider.Version}");
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderHelperPid = provider.ProcessId;
                state.ConsoleProviderHelperSessionId = provider.SessionId;
                state.ConsoleProviderHelperVersion = provider.Version;
                state.ConsoleProviderHelperConnected = true;
                state.ConsoleProviderVersionMatchesService = true;
                state.ConsoleProviderHelloReceived = true;
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderConnectedButOfferNotForwarded;
                state.ConsoleProviderLastError = null;
            });
            return provider;
        }

        if (provider is not null)
        {
            LogManager.WriteLog($"[RemoteSupport] Console provider version mismatch session={sessionId} providerVersion={provider.Version} serviceVersion={_serviceVersion}");
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderHelperPid = provider.ProcessId;
                state.ConsoleProviderHelperSessionId = provider.SessionId;
                state.ConsoleProviderHelperVersion = provider.Version;
                state.ConsoleProviderHelperConnected = true;
                state.ConsoleProviderVersionMatchesService = false;
                state.ConsoleProviderHelloReceived = true;
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleSecureDesktopHelperUnavailable;
                state.ConsoleProviderLastError = "Console provider version does not match service version.";
            });
        }

        return null;
#pragma warning restore CA1416
    }

    private void HandleHelperPipeMessage(ConnectedUserHelper helper, RemoteDesktopPipeMessage message)
    {
        if (!string.Equals(message.Kind, RemoteSupportPipeKinds.Signal, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(message.PayloadJson))
        {
            return;
        }

        try
        {
            var signal = JsonSerializer.Deserialize<RemoteSupportPipeSignal>(message.PayloadJson, _json);
            if (signal is null ||
                string.IsNullOrWhiteSpace(signal.SessionId) ||
                string.IsNullOrWhiteSpace(signal.SignalType))
            {
                return;
            }

            if (_sessionRoutes.TryGetValue(signal.SessionId, out var route) &&
                route.TargetWindowsSessionId.HasValue &&
                route.TargetWindowsSessionId.Value != helper.SessionId)
            {
                LogManager.WriteLog($"[RemoteSupport] Ignoring helper signal from wrong Windows session remoteSession={signal.SessionId} expectedSession={route.TargetWindowsSessionId} actualSession={helper.SessionId} type={signal.SignalType}");
                return;
            }

            if (_preparedMediaRoutes.TryGetValue(signal.SessionId, out var preparedRoute) &&
                helper.RouteId != preparedRoute.HelperRouteId)
            {
                LogManager.WriteLog($"[RemoteSupport] Ignoring helper signal from a replaced V2 route remoteSession={signal.SessionId} type={signal.SignalType}");
                return;
            }

            LogManager.WriteLog($"[RemoteSupport] Helper signal received session={signal.SessionId} type={signal.SignalType} bytes={signal.PayloadJson?.Length ?? 0}");
            _transitionCoordinator?.NoteProviderSignal(
                signal.SessionId,
                RemoteSupportProviderKinds.InteractiveUserHelper,
                signal.SignalType,
                ResolveSessionGeneration(signal.SessionId));
            SendSignal(
                signal.SessionId,
                signal.SignalType,
                WrapProviderSignalPayload(
                    signal.SessionId,
                    signal.SignalType,
                    signal.PayloadJson ?? string.Empty,
                    RemoteSupportProviderKinds.InteractiveUserHelper));
            if (string.Equals(
                    ReadStatusCode(signal.PayloadJson),
                    RemoteSupportStatusCodes.InteractiveDesktopRecoveryExhausted,
                    StringComparison.OrdinalIgnoreCase))
            {
                _desktopRecoveryRepairs.GetOrAdd(
                    signal.SessionId,
                    _ => Task.Run(() => RepairExhaustedInteractiveDesktopAsync(signal.SessionId, helper)));
                ReportTransitionEvidence(new RemoteSupportTransitionEvidenceFact(
                    signal.SessionId,
                    RemoteSupportV2TransitionEvidenceKinds.DesktopRecoveryExhausted,
                    helper.SessionId,
                    ProviderKind: RemoteSupportProviderKinds.InteractiveUserHelper,
                    HelperRouteId: preparedRoute?.HelperRouteId));
            }
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Helper signal parse failed: {ex.Message}");
        }
    }

    private async Task RepairExhaustedInteractiveDesktopAsync(string sessionId, ConnectedUserHelper helper)
    {
        if (!OperatingSystem.IsWindows() || _helperRepairService is null)
        {
            return;
        }

        if (!_sessionRoutes.TryGetValue(sessionId, out var route) ||
            route.TargetWindowsSessionId != helper.SessionId ||
            !string.Equals(route.Provider, RemoteSupportProviderKinds.InteractiveUserHelper, StringComparison.Ordinal) ||
            !IsSessionStillOpen(sessionId))
        {
            LogManager.WriteLog($"[RemoteSupportDesktopRecovery] Repair ignored for stale route session={sessionId} helperSession={helper.SessionId}");
            return;
        }

        SendInteractiveDesktopRecoveryStatus(
            sessionId,
            RemoteSupportStatusCodes.InteractiveHelperRestartStarted,
            "Restarting the exact-session interactive helper after desktop recovery was exhausted.",
            helper,
            reconnectRequired: false);

        try
        {
#pragma warning disable CA1416
            var repair = await _helperRepairService.TryRepairAsync(
                sessionId,
                helper.SessionId,
                helper,
                CancellationToken.None).ConfigureAwait(false);
#pragma warning restore CA1416
            if (repair.VersionRestored)
            {
                SendInteractiveDesktopRecoveryStatus(
                    sessionId,
                    RemoteSupportStatusCodes.InteractiveHelperRestartedReconnectRequired,
                    "The exact-session helper restarted; opening a fresh generation-1 Remote Support session.",
                    helper,
                    reconnectRequired: true,
                    repair);
                return;
            }

            SendInteractiveDesktopRecoveryStatus(
                sessionId,
                repair.StatusCode,
                repair.Message,
                helper,
                reconnectRequired: repair.ReconnectRequired,
                repair);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportDesktopRecovery] Exact-session helper restart failed session={sessionId}: {ex}");
            SendInteractiveDesktopRecoveryStatus(
                sessionId,
                RemoteSupportStatusCodes.InteractiveHelperRepairFailed,
                $"Exact-session helper restart failed: {ex.Message}",
                helper,
                reconnectRequired: false);
        }
    }

    private void SendInteractiveDesktopRecoveryStatus(
        string sessionId,
        string statusCode,
        string message,
        ConnectedUserHelper helper,
        bool reconnectRequired,
        RemoteSupportHelperRepairResult? repair = null)
    {
        SendSignal(
            sessionId,
            RemoteSupportSignalTypes.Ready,
            WrapProviderSignalPayload(
                sessionId,
                RemoteSupportSignalTypes.Ready,
                JsonSerializer.Serialize(new
                {
                    role = "agent",
                    platform = "windows",
                    code = statusCode,
                    statusCode,
                    message,
                    provider = RemoteSupportProviderKinds.InteractiveUserHelper,
                    inputProviderName = RemoteSupportProviderKinds.InteractiveUserHelper,
                    inputProviderReady = false,
                    targetWindowsSessionId = helper.SessionId,
                    helperProcessId = helper.ProcessId,
                    helperProcessSessionId = helper.SessionId,
                    interactiveHelperRepairSupported = true,
                    interactiveHelperRepairInProgress = statusCode == RemoteSupportStatusCodes.InteractiveHelperRestartStarted,
                    interactiveHelperRepairLastResult = statusCode,
                    interactiveHelperRepairLastError = repair?.Error,
                    interactiveHelperConnectedVersion = repair?.ConnectedVersion ?? helper.Version,
                    interactiveHelperServiceVersion = _serviceVersion,
                    reconnectRequired
                }, _json),
                RemoteSupportProviderKinds.InteractiveUserHelper));
    }

    private static string? ReadStatusCode(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return GetString(doc.RootElement, "statusCode") ?? GetString(doc.RootElement, "code");
        }
        catch (JsonException)
        {
            LogManager.WriteLog("[RemoteSupport] Provider status payload was not JSON.");
            return null;
        }
    }

    private void HandleHelperDisconnected(ConnectedUserHelper helper)
    {
        LogManager.WriteLog($"[RemoteSupport] Helper disconnected pid={helper.ProcessId} sessionId={helper.SessionId}");
        foreach (var item in _sessionProviders.Where(x => string.Equals(x.Value, RemoteSupportProviderKinds.InteractiveUserHelper, StringComparison.Ordinal)).ToArray())
        {
            _transitionCoordinator?.Trigger(item.Key, "provider_pipe_disconnected");
            _preparedMediaRoutes.TryGetValue(item.Key, out var route);
            ReportTransitionEvidence(new RemoteSupportTransitionEvidenceFact(
                item.Key,
                RemoteSupportV2TransitionEvidenceKinds.HelperDisconnected,
                helper.SessionId,
                ProviderKind: RemoteSupportProviderKinds.InteractiveUserHelper,
                HelperRouteId: route?.HelperRouteId));
        }
    }

    private void ObserveWindowsTransitionEvidence()
    {
        if (_disposed || _transitionEvidenceSink is null || _preparedMediaRoutes.IsEmpty)
        {
            return;
        }

        IReadOnlyList<WindowsSessionInventoryItem> inventory;
        try
        {
            inventory = _windowsSessionSource.Capture();
        }
        catch
        {
            return;
        }

        foreach (var item in _preparedMediaRoutes)
        {
            var route = item.Value;
            var target = route.PreparedTarget.Target;
            var entry = target.WindowsSessionId is { } targetSession
                ? inventory.SingleOrDefault(candidate => candidate.WindowsSessionId == targetSession)
                : inventory.SingleOrDefault(candidate => candidate.IsConsoleSession);
            if (entry is null)
            {
                ReportTransitionEvidence(new RemoteSupportTransitionEvidenceFact(item.Key, RemoteSupportV2TransitionEvidenceKinds.SessionLoggedOff,
                    target.WindowsSessionId, target.UserSidHash, ProviderKind: route.Provider, HelperRouteId: route.HelperRouteId));
                continue;
            }

            var kind = entry.IsConnected
                ? entry.IsLocked || entry.IsWinlogon
                    ? RemoteSupportV2TransitionEvidenceKinds.WorkstationLocked
                    : RemoteSupportV2TargetKinds.IsConsoleLogin(target.Kind)
                        ? RemoteSupportV2TransitionEvidenceKinds.SignedInDesktopObserved
                        : string.Empty
                : RemoteSupportV2TransitionEvidenceKinds.SessionDisconnected;
            if (kind.Length == 0)
            {
                continue;
            }

            ReportTransitionEvidence(new RemoteSupportTransitionEvidenceFact(item.Key, kind, entry.WindowsSessionId, entry.UserSidHash,
                inventory.SingleOrDefault(candidate => candidate.IsConsoleSession)?.WindowsSessionId, entry.State,
                entry.IsWinlogon ? "winlogon" : entry.IsLocked ? "locked" : "default", route.Provider, route.HelperRouteId));
        }
    }

    private void ReportTransitionEvidence(RemoteSupportTransitionEvidenceFact evidence)
    {
        if (_transitionEvidenceSink is null)
        {
            return;
        }

        var signature = string.Join('|', evidence.Kind, evidence.WindowsSessionId, evidence.ActiveConsoleSessionId,
            evidence.WindowsSessionState, evidence.DesktopKind, evidence.HelperRouteId);
        if (!_transitionEvidenceStates.TryAdd(evidence.SessionId, signature) &&
            _transitionEvidenceStates.TryGetValue(evidence.SessionId, out var existing) && existing == signature)
        {
            return;
        }

        _transitionEvidenceStates[evidence.SessionId] = signature;
        _transitionEvidenceSink.Report(evidence);
    }

    private void HandleConsoleProviderPipeMessage(RemoteDesktopPipeMessage message)
    {
        if (string.Equals(message.Kind, RemoteSupportPipeKinds.SasRequest, StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() => HandleConsoleProviderSasRequestAsync(message));
            return;
        }

        HandleProviderPipeMessage(message, "Console provider");
    }

    private async Task HandleConsoleProviderSasRequestAsync(RemoteDesktopPipeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.PayloadJson))
        {
            return;
        }

        RemoteSupportSasPipeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<RemoteSupportSasPipeRequest>(message.PayloadJson, _json);
            if (request is null || string.IsNullOrWhiteSpace(request.SessionId))
            {
                return;
            }

            LogManager.WriteLog($"[RemoteSupportSAS] Request received session={request.SessionId} request={request.RequestId} provider={request.Provider} targetSession={request.ActiveConsoleSessionId}");
            RemoteSupportSasResult result;
            if (!OperatingSystem.IsWindows() || _sasController is null)
            {
                var probe = new RemoteSupportSasProbe(
                    "service_owned_send_sas",
                    false,
                    new RemoteSupportSasPolicyInfo(null, "unsupported_os", false, false, "unsupported_os"),
                    request.ActiveConsoleSessionId,
                    request.ProviderProcessSessionId,
                    request.ProviderMatchesActiveConsole,
                    null,
                    null,
                    null,
                    false);
                result = new RemoteSupportSasResult(
                    RemoteSupportStatusCodes.SasFailedBeforeInvoke,
                    "Ctrl+Alt+Del is only available from the Windows service.",
                    false,
                    false,
                    probe,
                    DateTimeOffset.UtcNow);
            }
            else
            {
#pragma warning disable CA1416
                result = await _sasController.InvokeServiceOwnedAsync(
                    request.SessionId,
                    request.ActiveConsoleSessionId,
                    request.ProviderProcessSessionId,
                    request.ProviderMatchesActiveConsole,
                    request.DesktopState,
                    request.CaptureFrameHash,
                    CancellationToken.None).ConfigureAwait(false);
#pragma warning restore CA1416
            }

            SendSignal(
                request.SessionId,
                RemoteSupportSignalTypes.Ready,
                WrapProviderSignalPayload(
                    request.SessionId,
                    RemoteSupportSignalTypes.Ready,
                    BuildSasStatusPayload(request, result),
                    RemoteSupportProviderKinds.ConsoleSecureDesktopHelper));
            SendConsoleProviderSasResponse(request, result);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportSAS] Request handling failed: {ex}");
            if (request is not null)
            {
                var probe = new RemoteSupportSasProbe(
                    "service_owned_send_sas",
                    false,
                    new RemoteSupportSasPolicyInfo(null, "request_failed", false, false, "request_failed"),
                    request.ActiveConsoleSessionId,
                    request.ProviderProcessSessionId,
                    request.ProviderMatchesActiveConsole,
                    null,
                    null,
                    null,
                    false,
                    HResult: ex.HResult,
                    Error: ex.Message);
                var result = new RemoteSupportSasResult(
                    RemoteSupportStatusCodes.SasFailedBeforeInvoke,
                    ex.Message,
                    false,
                    false,
                    probe,
                    DateTimeOffset.UtcNow,
                    HResult: ex.HResult,
                    Error: ex.Message);
                SendSignal(
                    request.SessionId,
                    RemoteSupportSignalTypes.Ready,
                    WrapProviderSignalPayload(
                        request.SessionId,
                        RemoteSupportSignalTypes.Ready,
                        BuildSasStatusPayload(request, result),
                        RemoteSupportProviderKinds.ConsoleSecureDesktopHelper));
                SendConsoleProviderSasResponse(request, result);
            }
        }
    }

    private void SendConsoleProviderSasResponse(RemoteSupportSasPipeRequest request, RemoteSupportSasResult result)
    {
        if (!OperatingSystem.IsWindows() || _consoleProviderPipeHost is null)
        {
            return;
        }

#pragma warning disable CA1416
        var provider = _consoleProviderPipeHost.GetConnectedProvider();
#pragma warning restore CA1416
        try
        {
            provider?.Send(
                new RemoteDesktopPipeMessage(
                    RemoteSupportPipeKinds.SasResponse,
                    JsonSerializer.Serialize(new RemoteSupportSasPipeResponse(
                        request.SessionId,
                        request.RequestId,
                        result.StatusCode,
                        result.Message), _json)),
                _json);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportSAS] Response send failed session={request.SessionId}: {ex.Message}");
        }
    }

    private void HandleConsoleProviderDisconnected(ConnectedConsoleProvider provider)
    {
        LogManager.WriteLog($"[RemoteSupport] Console provider disconnected pid={provider.ProcessId} sessionId={provider.SessionId}");
        foreach (var item in _sessionProviders.Where(x => string.Equals(x.Value, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal)).ToArray())
        {
            _sessionProviders.TryRemove(item.Key, out _);
            _sessionRoutes.TryRemove(item.Key, out _);
            _transitionCoordinator?.Trigger(item.Key, "provider_pipe_disconnected");
        }
    }

    private void ScheduleConsoleProviderAnswerWatchdog(string sessionId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                if (!_sessionProviders.TryGetValue(sessionId, out var provider) ||
                    !string.Equals(provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal))
                {
                    return;
                }

                var state = RemoteSupportDiagnosticState.Read();
                if (!string.Equals(state.ConsoleProviderLastStage, RemoteSupportStatusCodes.ConsoleProviderOfferForwardedButNoAnswer, StringComparison.Ordinal))
                {
                    return;
                }

                RemoteSupportDiagnosticState.UpdateConsoleProvider(s =>
                {
                    s.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderOfferForwardedButNoAnswer;
                    s.ConsoleProviderLastError = "Console provider did not return an answer after the browser offer was forwarded.";
                });
                SendSignal(
                        sessionId,
                        RemoteSupportSignalTypes.Error,
                        WrapProviderSignalPayload(
                            sessionId,
                            RemoteSupportSignalTypes.Error,
                            BuildConsoleProviderUnavailablePayload(GetProviderDecision()),
                            RemoteSupportProviderKinds.ConsoleSecureDesktopHelper));
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteSupport] Console provider answer watchdog failed session={sessionId}: {ex.Message}");
            }
        });
    }

    private void HandleProviderPipeMessage(RemoteDesktopPipeMessage message, string source)
    {
        if (!string.Equals(message.Kind, RemoteSupportPipeKinds.Signal, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(message.PayloadJson))
        {
            return;
        }

        try
        {
            var signal = JsonSerializer.Deserialize<RemoteSupportPipeSignal>(message.PayloadJson, _json);
            if (signal is null ||
                string.IsNullOrWhiteSpace(signal.SessionId) ||
                string.IsNullOrWhiteSpace(signal.SignalType))
            {
                return;
            }

            LogManager.WriteLog($"[RemoteSupport] {source} signal received session={signal.SessionId} type={signal.SignalType} bytes={signal.PayloadJson?.Length ?? 0}");
            if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Answer, StringComparison.OrdinalIgnoreCase))
            {
                LogManager.WriteLog($"[RemoteSupport] remoteSupportAnswerSentAt={DateTimeOffset.UtcNow:O} session={signal.SessionId} provider={source}");
            }
            var sourceProvider = string.Equals(source, "Console provider", StringComparison.OrdinalIgnoreCase)
                ? RemoteSupportProviderKinds.ConsoleSecureDesktopHelper
                : ResolveSessionProvider(signal.SessionId);
            if (string.Equals(source, "Console provider", StringComparison.OrdinalIgnoreCase))
            {
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Answer, StringComparison.OrdinalIgnoreCase))
                    {
                        state.ConsoleProviderLastAnswerReceived = DateTimeOffset.UtcNow;
                        state.ConsoleProviderLastStage = "console_provider_answer_received";
                        state.ConsoleProviderLastError = null;
                    }
                    else if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Error, StringComparison.OrdinalIgnoreCase))
                    {
                        state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderWebRtcFailed;
                        state.ConsoleProviderLastError = ExtractProviderMessage(signal.PayloadJson) ?? "Console provider returned an error.";
                    }
                });
            }

            if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Error, StringComparison.OrdinalIgnoreCase) &&
                IsDesktopContextFailure(signal.PayloadJson))
            {
                _transitionCoordinator?.Trigger(signal.SessionId, "provider_capture_failed");
            }

            _transitionCoordinator?.NoteProviderSignal(
                signal.SessionId,
                sourceProvider,
                signal.SignalType,
                ResolveSessionGeneration(signal.SessionId));
            SendSignal(
                signal.SessionId,
                signal.SignalType,
                WrapProviderSignalPayload(signal.SessionId, signal.SignalType, signal.PayloadJson ?? string.Empty, sourceProvider));
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] {source} signal parse failed: {ex.Message}");
        }
    }

    private bool SendHelperMessage(string kind, RemoteSupportPipeSignal signal)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var targetWindowsSessionId = _sessionRoutes.TryGetValue(signal.SessionId, out var route)
            ? route.TargetWindowsSessionId
            : ReadOpenRequest(signal.SessionId)?.TargetWindowsSessionId;
        var helper = targetWindowsSessionId.HasValue
            ? _helperPipeHost?.GetConnectedHelper(targetWindowsSessionId.Value)
            : _helperPipeHost?.GetConnectedHelper();
        if (helper is null)
        {
            LogManager.WriteLog($"[RemoteSupport] Helper unavailable for signal session={signal.SessionId} type={signal.SignalType} targetWindowsSessionId={targetWindowsSessionId?.ToString() ?? "active_console"}");
            return false;
        }

        if (!IsHelperVersionCompatible(helper.Version, _serviceVersion))
        {
            LogManager.WriteLog($"[RemoteSupport] Helper version mismatch session={signal.SessionId} type={signal.SignalType} helperPid={helper.ProcessId} helperVersion={helper.Version} serviceVersion={_serviceVersion}");
            return false;
        }

        try
        {
            helper.Send(
                new RemoteDesktopPipeMessage(kind, JsonSerializer.Serialize(signal, _json)),
                _json);
            LogManager.WriteLog($"[RemoteSupport] Helper message sent kind={kind} session={signal.SessionId} type={signal.SignalType} helperPid={helper.ProcessId} bytes={signal.PayloadJson?.Length ?? 0}");
            return true;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Helper message failed kind={kind} session={signal.SessionId}: {ex.Message}");
            return false;
        }
    }

    private bool TryValidatePreparedHelper(RemoteSupportPreparedTargetResult prepared, out ConnectedUserHelper? helper)
    {
        helper = null;
        if (!OperatingSystem.IsWindows() || !RemoteSupportV2PreparationValidator.TryValidate(prepared, out _) ||
            !prepared.TargetValid || !prepared.ProviderReady || prepared.HelperRoute is null ||
            prepared.Target.WindowsSessionId != prepared.HelperRoute.WindowsSessionId ||
            !string.Equals(prepared.Target.UserSidHash, prepared.HelperRoute.UserSidHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        helper = _helperPipeHost?.GetConnectedHelper(prepared.Target.WindowsSessionId!.Value);
        return helper is not null && helper.SessionId == prepared.HelperRoute.WindowsSessionId &&
            helper.RouteId == prepared.HelperRoute.HelperRouteId && IsHelperVersionCompatible(helper.Version, prepared.HelperRoute.HelperVersion);
    }

    internal async Task<RemoteSupportConsoleProviderReadiness?> PrepareV2ConsoleProviderAsync(
        RemoteSupportPrepareTargetCommand command,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !RemoteSupportV2TargetKinds.IsConsoleLogin(command.Target.Kind) ||
            _consoleProviderPipeHost is null)
        {
            return null;
        }

#pragma warning disable CA1416
        _consoleProviderPipeHost.ResetConnectedProviderIfVersionMismatch(_serviceVersion);
        var provider = _consoleProviderPipeHost.GetConnectedProvider() ??
            await _consoleProviderPipeHost.EnsureProviderAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1416
        if (provider is null || provider.ProcessId <= 0 || provider.SessionId <= 0 ||
            provider.ActiveConsoleSessionId is null || provider.ActiveConsoleSessionId == uint.MaxValue ||
            provider.SessionId != unchecked((int)provider.ActiveConsoleSessionId.Value) ||
            !provider.HelperLaunchedInTargetSession ||
            !string.Equals(provider.Provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal) ||
            !IsHelperVersionCompatible(provider.Version, _serviceVersion))
        {
            return null;
        }

        return new RemoteSupportConsoleProviderReadiness(provider.RouteId, provider.SessionId, provider.ProcessId, provider.Version);
    }

    /// <summary>
    /// Executes an actor-authorized V2 machine control against the exact
    /// prepared provider. Normal input never enters this path.
    /// </summary>
    internal async Task<string> ExecuteV2PrivilegedControlAsync(
        string sessionId,
        RemoteSupportPreparedTargetResult prepared,
        RemoteSupportV2PrivilegedControl control,
        CancellationToken cancellationToken)
    {
        if (control.CommandId == Guid.Empty || control.Target != prepared.Target ||
            control.HelperRouteId != prepared.HelperRoute?.HelperRouteId)
        {
            return "control_route_invalid";
        }

        if (!OperatingSystem.IsWindows())
        {
            return "control_platform_unsupported";
        }

        if (string.Equals(control.ControlType, RemoteSupportV2ControlTypes.RequestSas, StringComparison.Ordinal))
        {
            if (!TryValidatePreparedConsoleProvider(prepared, out var provider) || _sasController is null)
            {
                return "sas_target_invalid";
            }

            var result = await _sasController.InvokeServiceOwnedAsync(
                sessionId,
                provider!.ActiveConsoleSessionId,
                provider.SessionId,
                provider.HelperLaunchedInTargetSession,
                provider.DesktopState,
                null,
                cancellationToken).ConfigureAwait(false);
            return result.StatusCode;
        }

        if (string.Equals(control.ControlType, RemoteSupportV2ControlTypes.RepairHelper, StringComparison.Ordinal))
        {
            if (!TryValidatePreparedHelper(prepared, out var helper) || _helperRepairService is null)
            {
                return "helper_repair_target_invalid";
            }

            var result = await _helperRepairService.TryRepairAsync(
                control.CommandId.ToString("N"),
                helper!.SessionId,
                helper,
                cancellationToken).ConfigureAwait(false);
            return result.StatusCode;
        }

        return "control_type_unsupported";
    }

    private bool TryValidatePreparedConsoleProvider(
        RemoteSupportPreparedTargetResult prepared,
        out ConnectedConsoleProvider? provider)
    {
        provider = null;
        if (!OperatingSystem.IsWindows() || !RemoteSupportV2PreparationValidator.TryValidate(prepared, out _) ||
            !RemoteSupportV2TargetKinds.IsConsoleLogin(prepared.Target.Kind) ||
            !prepared.TargetValid || !prepared.ProviderReady || prepared.HelperRoute is null)
        {
            return false;
        }

        provider = _consoleProviderPipeHost?.GetConnectedProvider();
        return provider is not null && provider.ProcessId > 0 && provider.SessionId > 0 &&
            provider.RouteId == prepared.HelperRoute.HelperRouteId &&
            provider.SessionId == prepared.HelperRoute.WindowsSessionId &&
            provider.ActiveConsoleSessionId is { } activeConsole && activeConsole != uint.MaxValue &&
            provider.SessionId == unchecked((int)activeConsole) && provider.HelperLaunchedInTargetSession &&
            string.Equals(provider.Provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal) &&
            IsHelperVersionCompatible(provider.Version, prepared.HelperRoute.HelperVersion);
    }

    private bool SendExactHelperMessage(ConnectedUserHelper helper, string kind, RemoteSupportPipeSignal signal)
    {
        try
        {
            helper.Send(new RemoteDesktopPipeMessage(kind, JsonSerializer.Serialize(signal, _json)), _json);
            return true;
        }
        catch (Exception exception)
        {
            LogManager.WriteLog($"[RemoteSupport] Exact helper route write failed session={signal.SessionId} type={signal.SignalType}: {exception.Message}");
            return false;
        }
    }

    private bool SendExactConsoleProviderMessage(ConnectedConsoleProvider provider, string kind, RemoteSupportPipeSignal signal)
    {
        try
        {
            provider.Send(new RemoteDesktopPipeMessage(kind, JsonSerializer.Serialize(signal, _json)), _json);
            return true;
        }
        catch (Exception exception)
        {
            LogManager.WriteLog($"[RemoteSupport] Exact console provider route write failed session={signal.SessionId} type={signal.SignalType}: {exception.Message}");
            return false;
        }
    }

    private bool SendProviderMessage(string provider, string kind, RemoteSupportPipeSignal signal)
    {
        if (string.Equals(provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal))
        {
            return SendConsoleProviderMessage(kind, signal);
        }

        return SendHelperMessage(kind, signal);
    }

    private string ResolveSessionProvider(string sessionId) =>
        _sessionProviders.TryGetValue(sessionId, out var provider)
            ? provider
            : RemoteSupportProviderKinds.InteractiveUserHelper;

    private string ResolveSessionProvider(string sessionId, int providerGeneration)
    {
        if (_sessionRoutes.TryGetValue(sessionId, out var route) &&
            (providerGeneration <= 0 || route.ProviderGeneration == providerGeneration))
        {
            return route.Provider;
        }

        return ResolveSessionProvider(sessionId);
    }

    private int ResolveSessionGeneration(string sessionId) =>
        _sessionRoutes.TryGetValue(sessionId, out var route)
            ? route.ProviderGeneration
            : _handoverOptions.ProviderGenerationEnabled
                ? _transitionCoordinator?.GetGeneration(sessionId) ?? 1
                : 1;

    private bool SendConsoleProviderMessage(string kind, RemoteSupportPipeSignal signal)
    {
        if (!OperatingSystem.IsWindows() || _consoleProviderPipeHost is null)
        {
            return false;
        }

#pragma warning disable CA1416
        var provider = _consoleProviderPipeHost.GetConnectedProvider();
#pragma warning restore CA1416
        if (provider is null)
        {
            LogManager.WriteLog($"[RemoteSupport] Console provider unavailable for signal session={signal.SessionId} type={signal.SignalType}");
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderConnectedButOfferNotForwarded;
                state.ConsoleProviderLastError = "Console provider is unavailable while forwarding signal.";
            });
            return false;
        }

        if (!IsHelperVersionCompatible(provider.Version, _serviceVersion))
        {
            LogManager.WriteLog($"[RemoteSupport] Console provider version mismatch session={signal.SessionId} type={signal.SignalType} providerPid={provider.ProcessId} providerVersion={provider.Version} serviceVersion={_serviceVersion}");
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderVersionMatchesService = false;
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleSecureDesktopHelperUnavailable;
                state.ConsoleProviderLastError = "Console provider version does not match service version.";
            });
            return false;
        }

        try
        {
            provider.Send(
                new RemoteDesktopPipeMessage(kind, JsonSerializer.Serialize(signal, _json)),
                _json);
            LogManager.WriteLog($"[RemoteSupport] Console provider message sent kind={kind} session={signal.SessionId} type={signal.SignalType} providerPid={provider.ProcessId} bytes={signal.PayloadJson?.Length ?? 0}");
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderHelperPid = provider.ProcessId;
                state.ConsoleProviderHelperSessionId = provider.SessionId;
                state.ConsoleProviderHelperVersion = provider.Version;
                state.ConsoleProviderHelperConnected = true;
                state.ConsoleProviderVersionMatchesService = true;
                state.ConsoleProviderHelloReceived = true;
                state.ConsoleProviderLastError = null;
                if (string.Equals(kind, RemoteSupportPipeKinds.Offer, StringComparison.OrdinalIgnoreCase))
                {
                    state.ConsoleProviderLastOfferForwarded = DateTimeOffset.UtcNow;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderOfferForwardedButNoAnswer;
                }
                else if (string.Equals(kind, RemoteSupportPipeKinds.Ice, StringComparison.OrdinalIgnoreCase))
                {
                    state.ConsoleProviderLastIceForwarded = DateTimeOffset.UtcNow;
                }
                else if (string.Equals(kind, RemoteSupportPipeKinds.Close, StringComparison.OrdinalIgnoreCase))
                {
                    state.ConsoleProviderLastCloseForwarded = DateTimeOffset.UtcNow;
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Console provider message failed kind={kind} session={signal.SessionId}: {ex.Message}");
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderLastStage = string.Equals(kind, RemoteSupportPipeKinds.Offer, StringComparison.OrdinalIgnoreCase)
                    ? RemoteSupportStatusCodes.ConsoleProviderConnectedButOfferNotForwarded
                    : state.ConsoleProviderLastStage;
                state.ConsoleProviderLastError = ex.Message;
            });
            return false;
        }
    }

    private RemoteSupportProviderDecision GetProviderDecision()
    {
        ConnectedUserHelper? helper = null;
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            helper = _helperPipeHost?.GetConnectedHelper();
#pragma warning restore CA1416
        }

        var decision = RemoteSupportProviderDiagnostics.Evaluate(
            helper,
            _serviceVersion,
            helper is not null && IsHelperVersionCompatible(helper.Version, _serviceVersion));
        if (OperatingSystem.IsWindows() &&
            string.Equals(decision.Provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal) &&
            _consoleProviderPipeHost is not null)
        {
#pragma warning disable CA1416
            var consoleProvider = _consoleProviderPipeHost.GetConnectedProvider();
#pragma warning restore CA1416
            if (consoleProvider is not null && IsHelperVersionCompatible(consoleProvider.Version, _serviceVersion))
            {
                decision = decision with
                {
                    SupportLevel = RemoteSupportSupportLevels.MediaPreview,
                    StatusCode = RemoteSupportStatusCodes.ConsoleSecureDesktopMediaPreviewAvailable,
                    Message = "Console secure-desktop media preview provider is connected.",
                    MediaSupported = true
                };
            }
            else
            {
                decision = decision with
                {
                    SupportLevel = RemoteSupportSupportLevels.MediaPreview,
                    MediaSupported = false
                };
            }
        }

        RemoteSupportDiagnosticState.Write(decision);
        LogManager.WriteLog($"[RemoteSupport] Provider decision provider={decision.Provider} desktopState={decision.DesktopState} supportLevel={decision.SupportLevel} mediaSupported={decision.MediaSupported} status={decision.StatusCode} helperConnected={decision.UserHelperConnected}");
        return decision;
    }

    private string BuildProviderUnavailablePayload(RemoteSupportProviderDecision decision)
    {
        var code = decision.StatusCode;
        if (string.Equals(decision.Provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal) &&
            !decision.MediaSupported &&
            !IsConsoleProviderStage(code))
        {
            code = string.Equals(decision.DesktopState, RemoteSupportDesktopStates.NoUser, StringComparison.Ordinal)
                ? RemoteSupportStatusCodes.WindowsLogonDesktopDetected
                : decision.StatusCode;
        }

        return JsonSerializer.Serialize(new
        {
            code,
            message = decision.Message,
            provider = decision.Provider,
            desktopState = decision.DesktopState,
            supportLevel = decision.SupportLevel,
            inputDesktopName = decision.InputDesktopName,
            captureAvailable = decision.CaptureAvailable,
            inputAvailable = decision.InputAvailable,
            captureStatusCode = decision.CaptureAvailable
                ? RemoteSupportStatusCodes.SecureDesktopCaptureAvailable
                : RemoteSupportStatusCodes.SecureDesktopCaptureUnavailable,
            inputStatusCode = decision.InputAvailable
                ? RemoteSupportStatusCodes.SecureDesktopInputAvailable
                : RemoteSupportStatusCodes.SecureDesktopInputUnavailable,
            mediaSupported = decision.MediaSupported,
            reconnectRequired = decision.ReconnectRequired,
            diagnosticHint = decision.DiagnosticError
        }, _json);
    }

    private string BuildConsoleProviderUnavailablePayload(RemoteSupportProviderDecision decision)
    {
        var consoleState = RemoteSupportDiagnosticState.Read();
        var code = string.IsNullOrWhiteSpace(consoleState.ConsoleProviderLastStage)
            ? RemoteSupportStatusCodes.ConsoleProviderLaunchNotAttempted
            : consoleState.ConsoleProviderLastStage;
        if (!IsConsoleProviderStage(code))
        {
            code = consoleState.ConsoleProviderHelperLaunchAttempted
                ? RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello
                : RemoteSupportStatusCodes.ConsoleProviderLaunchNotAttempted;
        }

        var message = BuildConsoleProviderFailureMessage(code, consoleState);
        return JsonSerializer.Serialize(new
        {
            code,
            statusCode = code,
            message,
            provider = decision.Provider,
            desktopState = decision.DesktopState,
            supportLevel = decision.SupportLevel,
            inputDesktopName = decision.InputDesktopName,
            captureAvailable = decision.CaptureAvailable,
            inputAvailable = decision.InputAvailable,
            mediaSupported = false,
            reconnectRequired = decision.ReconnectRequired,
            diagnosticHint = consoleState.ConsoleProviderLastError ?? decision.DiagnosticError,
            consoleProviderPipeName = consoleState.ConsoleProviderPipeName,
            consoleProviderPipePath = consoleState.ConsoleProviderPipePath,
            consoleProviderPipeHostStarted = consoleState.ConsoleProviderPipeHostStarted,
            consoleProviderPipeHostReady = consoleState.ConsoleProviderPipeHostReady,
            consoleProviderHelperLaunchAttempted = consoleState.ConsoleProviderHelperLaunchAttempted,
            consoleProviderHelperLaunchCommand = consoleState.ConsoleProviderHelperLaunchCommand,
            consoleProviderHelperLaunchExitCode = consoleState.ConsoleProviderHelperLaunchExitCode,
            consoleProviderHelperLaunchError = consoleState.ConsoleProviderHelperLaunchError,
            consoleProviderHelperPid = consoleState.ConsoleProviderHelperPid,
            consoleProviderHelperSessionId = consoleState.ConsoleProviderHelperSessionId,
            consoleProviderHelperConnected = consoleState.ConsoleProviderHelperConnected,
            consoleProviderHelperVersion = consoleState.ConsoleProviderHelperVersion,
            consoleProviderVersionMatchesService = consoleState.ConsoleProviderVersionMatchesService,
            consoleProviderHelloReceived = consoleState.ConsoleProviderHelloReceived,
            consoleProviderLastStage = consoleState.ConsoleProviderLastStage,
            consoleProviderLastError = consoleState.ConsoleProviderLastError
        }, _json);
    }

    private string BuildSasStatusPayload(RemoteSupportSasPipeRequest request, RemoteSupportSasResult result)
    {
        var probe = result.Probe;
        return JsonSerializer.Serialize(new
        {
            role = "agent",
            platform = "windows",
            code = result.StatusCode,
            statusCode = result.StatusCode,
            message = result.Message,
            provider = request.Provider,
            desktopState = request.DesktopState,
            inputProviderName = request.Provider,
            remoteSupportSasSupported = probe.ApiAvailable && probe.Policy.AllowsServices,
            remoteSupportSasPolicyAllowsServices = probe.Policy.AllowsServices,
            remoteSupportSasApiAvailable = probe.ApiAvailable,
            remoteSupportSasProvider = probe.Backend,
            remoteSupportSasTargetSessionId = probe.TargetSessionId,
            remoteSupportSasLastAttemptAt = result.AttemptedAt,
            remoteSupportSasLastResult = result.StatusCode,
            remoteSupportSasLastError = result.Error,
            remoteSupportSasLastWin32Error = result.Win32Error,
            remoteSupportSasLastHresult = result.HResult,
            softwareSasGenerationRawValue = probe.Policy.RawValue,
            softwareSasGenerationInterpretedValue = probe.Policy.InterpretedValue,
            softwareSasAllowsServices = probe.Policy.AllowsServices,
            softwareSasAllowsEaseOfAccess = probe.Policy.AllowsEaseOfAccess,
            softwareSasPolicySource = probe.Policy.Source,
            remoteSupportOsCaption = probe.OsCaption,
            remoteSupportOsVersion = probe.OsVersion,
            remoteSupportOsBuild = probe.OsBuild,
            remoteSupportOsIsServer = probe.OsIsServer,
            sasInvoked = result.Invoked,
            sasEffectObserved = result.EffectObserved
        }, _json);
    }

    private static string BuildConsoleProviderFailureMessage(string code, RemoteSupportDiagnosticState.State state) =>
        code switch
        {
            RemoteSupportStatusCodes.ConsoleProviderPipeHostNotStarted => "Console provider pipe host has not started.",
            RemoteSupportStatusCodes.ConsoleProviderPipeNotReady => "Console provider pipe is not ready.",
            RemoteSupportStatusCodes.ConsoleProviderLaunchNotAttempted => "Console provider launch was not attempted.",
            RemoteSupportStatusCodes.ConsoleProviderLaunchFailed => $"Console provider launch failed: {state.ConsoleProviderHelperLaunchError ?? state.ConsoleProviderLastError ?? "unknown error"}.",
            RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello => "Console provider process started but did not send hello.",
            RemoteSupportStatusCodes.ConsoleProviderConnectedButOfferNotForwarded => "Console provider connected, but the browser offer was not forwarded.",
            RemoteSupportStatusCodes.ConsoleProviderOfferForwardedButNoAnswer => "Console provider received the browser offer but has not returned an answer.",
            RemoteSupportStatusCodes.ConsoleProviderWebRtcFailed => $"Console provider WebRTC failed: {state.ConsoleProviderLastError ?? "unknown error"}.",
            _ => state.ConsoleProviderLastError ?? "Console secure-desktop media preview provider did not connect."
        };

    private static bool IsConsoleProviderStage(string? code) =>
        string.Equals(code, RemoteSupportStatusCodes.ConsoleProviderPipeHostNotStarted, StringComparison.Ordinal) ||
        string.Equals(code, RemoteSupportStatusCodes.ConsoleProviderPipeNotReady, StringComparison.Ordinal) ||
        string.Equals(code, RemoteSupportStatusCodes.ConsoleProviderLaunchNotAttempted, StringComparison.Ordinal) ||
        string.Equals(code, RemoteSupportStatusCodes.ConsoleProviderLaunchFailed, StringComparison.Ordinal) ||
        string.Equals(code, RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello, StringComparison.Ordinal) ||
        string.Equals(code, RemoteSupportStatusCodes.ConsoleProviderConnectedButOfferNotForwarded, StringComparison.Ordinal) ||
        string.Equals(code, RemoteSupportStatusCodes.ConsoleProviderOfferForwardedButNoAnswer, StringComparison.Ordinal) ||
        string.Equals(code, RemoteSupportStatusCodes.ConsoleProviderWebRtcFailed, StringComparison.Ordinal);

    private static string? ExtractProviderMessage(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string BuildHelperUnavailablePayload()
    {
        if (!OperatingSystem.IsWindows())
        {
            return JsonSerializer.Serialize(new
            {
                code = "native_webrtc_unsupported_os",
                message = "Native remote support media is currently Windows-only."
            }, _json);
        }

        var helper = _helperPipeHost?.GetConnectedHelper();
        if (helper is not null && !IsHelperVersionCompatible(helper.Version, _serviceVersion))
        {
            return JsonSerializer.Serialize(new
            {
                code = "remote_support_helper_version_mismatch",
                message = "The interactive remote support helper is still running an older NetRatel.Client version. Sign out/in or restart the helper so it matches the service version.",
                helperVersion = helper.Version,
                serviceVersion = _serviceVersion
            }, _json);
        }

        return JsonSerializer.Serialize(new
        {
            code = "remote_support_helper_not_connected",
            message = "No interactive user helper is connected for native WebRTC media."
        }, _json);
    }

    private void SendHelperRepairStatus(
        string sessionId,
        string statusCode,
        string message,
        string? helperVersion,
        int? helperPid,
        int? helperSessionId,
        RemoteSupportHelperRepairResult? repair = null)
    {
        SendSignal(
            sessionId,
            RemoteSupportSignalTypes.Ready,
            WrapProviderSignalPayload(
                sessionId,
                RemoteSupportSignalTypes.Ready,
                JsonSerializer.Serialize(new
                {
                    role = "agent",
                    platform = "windows",
                    code = statusCode,
                    statusCode,
                    message,
                    provider = RemoteSupportProviderKinds.InteractiveUserHelper,
                    inputProviderName = RemoteSupportProviderKinds.InteractiveUserHelper,
                    helperVersion,
                    serviceVersion = _serviceVersion,
                    interactiveHelperRepairSupported = true,
                    interactiveHelperRepairInProgress = statusCode == RemoteSupportStatusCodes.HelperVersionMismatchDetected || statusCode == RemoteSupportStatusCodes.HelperRepairStarted,
                    interactiveHelperRepairLastResult = statusCode,
                    interactiveHelperRepairLastError = repair?.Error,
                    interactiveHelperConnectedVersion = repair?.ConnectedVersion ?? helperVersion,
                    interactiveHelperServiceVersion = _serviceVersion,
                    interactiveHelperStalePid = helperPid,
                    interactiveHelperStaleSessionId = helperSessionId,
                    interactiveHelperTerminateAttempted = repair?.TerminateAttempted,
                    interactiveHelperRelaunchAttempted = repair?.RelaunchAttempted,
                    interactiveHelperRelaunchResult = repair?.StatusCode,
                    reconnectRequired = repair?.ReconnectRequired
                }, _json),
                RemoteSupportProviderKinds.InteractiveUserHelper));
    }

    private bool IsSessionStillOpen(string sessionId)
    {
        return _gatewaySessions.TryGetValue(sessionId, out var gatewaySession) && !gatewaySession.IsClosed;
    }

    private bool IsOfferStillCurrent(RemoteSupportIncomingSignal signal)
    {
        return _gatewaySessions.TryGetValue(signal.SessionId, out var gatewaySession) &&
            !gatewaySession.IsClosed && gatewaySession.LastOfferSequence == signal.Sequence;
    }

    private string BuildHelperOfferPayload(
        RemoteSupportIncomingSignal signal,
        RemoteSupportSignalMetadata metadata,
        string targetProvider)
    {
        var sessionPayloadJson = _gatewaySessions.TryGetValue(signal.SessionId, out var gatewaySession)
            ? gatewaySession.OpenRequestPayload
            : null;
        var iceServers = ReadIceServers(sessionPayloadJson);
        var mediaProfile = ReadMediaProfile(sessionPayloadJson);
        var openRequest = ReadOpenRequest(signal.SessionId);
        var offerJson = GetSignalPayloadJson(signal.PayloadJson, metadata);
        var generation = _handoverOptions.ProviderGenerationEnabled
            ? metadata.ProviderGeneration <= 0
                ? ResolveSessionGeneration(signal.SessionId)
                : metadata.ProviderGeneration
            : 1;
        LogManager.WriteLog($"[RemoteSupport] Helper offer payload session={signal.SessionId} provider={targetProvider} generation={generation} iceServers={iceServers.Count} profile={mediaProfile?.Profile ?? "default"} max={mediaProfile?.MaxWidth}x{mediaProfile?.MaxHeight} fps={mediaProfile?.Fps}");
        return JsonSerializer.Serialize(
            new RemoteSupportPipeOffer(
                offerJson,
                iceServers,
                mediaProfile,
                generation,
                metadata.Reason,
                metadata.FromProvider,
                targetProvider,
                metadata.HandoverReason,
                openRequest?.TargetWindowsSessionId,
                openRequest?.TargetUserSidHash),
            _json);
    }

    private void SendHandoverNotification(RemoteSupportHandoverNotification notification)
    {
        LogManager.WriteLog($"[RemoteSupportHandover] Notify session={notification.SessionId} generation={notification.ProviderGeneration} state={notification.HandoverState} from={notification.PreviousProvider} to={notification.TargetProvider} reason={notification.HandoverReason}");
        if (string.Equals(notification.TargetProvider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal) &&
            string.Equals(notification.HandoverState, RemoteSupportHandoverStates.HandoverWaitingForTargetProvider, StringComparison.Ordinal))
        {
            _ = Task.Run(async () =>
            {
                var provider = await EnsureConsoleProviderAsync(notification.SessionId, notification.Decision).ConfigureAwait(false);
                if (provider is not null)
                {
                    _transitionCoordinator?.Trigger(notification.SessionId, "target_provider_ready");
                }
            });
        }

        SendSignal(
            notification.SessionId,
            RemoteSupportSignalTypes.Ready,
            WrapProviderSignalPayload(
                notification.SessionId,
                RemoteSupportSignalTypes.Ready,
                JsonSerializer.Serialize(new
                {
                    role = "agent",
                    platform = "windows",
                    code = RemoteSupportHandoverStates.DesktopContextSwitching,
                    statusCode = RemoteSupportHandoverStates.DesktopContextSwitching,
                    message = BuildHandoverMessage(notification),
                    media = "remote_support_provider_handover",
                    provider = notification.TargetProvider,
                    desktopState = notification.Decision.DesktopState,
                    supportLevel = notification.Decision.SupportLevel,
                    mediaSupported = notification.Decision.MediaSupported,
                    reconnectRequired = false,
                    handoverSupported = true,
                    handoverState = notification.HandoverState,
                    handoverReason = notification.HandoverReason,
                    providerGeneration = notification.ProviderGeneration,
                    fromProvider = notification.PreviousProvider,
                    toProvider = notification.TargetProvider,
                    desiredProvider = notification.TargetProvider,
                    activeProvider = notification.PreviousProvider,
                    activeConsoleSessionId = notification.Decision.ActiveConsoleSessionId,
                    activeHelperSessionId = notification.Decision.HelperSessionId,
                    helperMatchesActiveConsole = notification.Decision.HelperMatchesActiveConsole,
                    rdpOrNonConsoleHelperDetected = notification.Decision.RdpOrNonConsoleHelperDetected
                }, _json),
                notification.TargetProvider,
                notification.ProviderGeneration,
                "provider_handover",
                notification.PreviousProvider,
                notification.TargetProvider,
                notification.HandoverReason));
    }

    private static string BuildHandoverMessage(RemoteSupportHandoverNotification notification)
    {
        if (string.Equals(notification.HandoverState, RemoteSupportHandoverStates.HandoverWaitingForTargetProvider, StringComparison.Ordinal))
        {
            return string.Equals(notification.TargetProvider, RemoteSupportProviderKinds.InteractiveUserHelper, StringComparison.Ordinal)
                ? "Waiting for user helper."
                : "Starting console provider.";
        }

        return notification.HandoverReason switch
        {
            "console_to_interactive_after_login" => "Switching to user desktop.",
            "console_to_interactive_after_unlock" => "Switching after unlock.",
            "interactive_to_console_after_lock" => "Switching to login screen.",
            "interactive_to_console_after_logoff" => "Switching to login screen.",
            _ => "Reconnecting remote support media."
        };
    }

    private RemoteSupportSignalMetadata ReadSignalMetadata(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new RemoteSupportSignalMetadata(1, null, null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("providerGeneration", out var generationElement) &&
                root.TryGetProperty("payload", out var payloadElement))
            {
                return new RemoteSupportSignalMetadata(
                    generationElement.TryGetInt32(out var generation) ? Math.Max(1, generation) : 1,
                    GetString(root, "reason"),
                    GetString(root, "fromProvider"),
                    GetString(root, "toProvider"),
                    GetString(root, "handoverReason"),
                    payloadElement.Clone());
            }
        }
        catch (JsonException)
        {
            LogManager.WriteLog("[RemoteSupport] Provider signal metadata was not an envelope; relaying the original payload.");
        }

        return new RemoteSupportSignalMetadata(1, null, null, null, null, null);
    }

    private static string GetSignalPayloadJson(string? originalPayloadJson, RemoteSupportSignalMetadata metadata) =>
        metadata.Payload.HasValue
            ? metadata.Payload.Value.GetRawText()
            : originalPayloadJson ?? string.Empty;

    private string WrapProviderSignalPayload(
        string sessionId,
        string signalType,
        string payloadJson,
        string provider,
        int? providerGeneration = null,
        string? reason = null,
        string? fromProvider = null,
        string? toProvider = null,
        string? handoverReason = null)
    {
        if (!_handoverOptions.ProviderGenerationEnabled)
        {
            return payloadJson;
        }

        var generation = providerGeneration ?? ResolveSessionGeneration(sessionId);
        var route = _sessionRoutes.TryGetValue(sessionId, out var foundRoute) ? foundRoute : null;
        using var payloadDoc = ParsePayloadElement(payloadJson);
        return JsonSerializer.Serialize(
            new
            {
                providerGeneration = generation,
                reason = reason ?? route?.Reason,
                fromProvider = fromProvider ?? route?.FromProvider,
                toProvider = toProvider ?? route?.ToProvider ?? provider,
                handoverReason = handoverReason ?? route?.HandoverReason,
                provider,
                signalType,
                payload = payloadDoc.RootElement
            },
            _json);
    }

    private static JsonDocument ParsePayloadElement(string payloadJson)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                return JsonDocument.Parse(payloadJson);
            }
            catch (JsonException)
            {
                LogManager.WriteLog("[RemoteSupport] Provider signal payload was not JSON; wrapping an empty payload object.");
            }
        }

        return JsonDocument.Parse("{}");
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool IsDesktopContextFailure(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var code = GetString(doc.RootElement, "code") ?? GetString(doc.RootElement, "statusCode");
            return string.Equals(code, "desktop_capture_failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "desktop_context_changed_reconnect_required", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "desktop_context_switching", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlyList<RemoteSupportIceServerDto> ReadIceServers(string? sessionPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(sessionPayloadJson))
        {
            return Array.Empty<RemoteSupportIceServerDto>();
        }

        try
        {
            var request = JsonSerializer.Deserialize<OpenRemoteSupportRequest>(sessionPayloadJson, _json);
            return request?.IceServers?
                .Where(x => x.Urls.Count > 0)
                .ToArray() ?? Array.Empty<RemoteSupportIceServerDto>();
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Failed to parse session ICE payload: {ex.Message}");
            return Array.Empty<RemoteSupportIceServerDto>();
        }
    }

    private string ReadTargetMode(string sessionId)
    {
        var request = ReadOpenRequest(sessionId);
        return string.IsNullOrWhiteSpace(request?.TargetMode) ? "auto" : request.TargetMode!.Trim();
    }

    private OpenRemoteSupportRequest? ReadOpenRequest(string sessionId)
    {
        try
        {
            var payloadJson = _gatewaySessions.TryGetValue(sessionId, out var gatewaySession)
                ? gatewaySession.OpenRequestPayload
                : null;
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return null;
            }

            return JsonSerializer.Deserialize<OpenRemoteSupportRequest>(payloadJson, _json);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Failed to parse session open request session={sessionId}: {ex.Message}");
            return null;
        }
    }

    private bool ValidateAssistUserTarget(
        OpenRemoteSupportRequest? request,
        RemoteSupportProviderDecision providerDecision,
        out string failurePayloadJson,
        out bool helperReady,
        out bool helperNeedsPreparation)
    {
        failurePayloadJson = string.Empty;
        helperReady = false;
        helperNeedsPreparation = false;
        if (request?.TargetWindowsSessionId is null)
        {
            failurePayloadJson = BuildTargetFailurePayload(
                "target_session_not_found",
                "Selected Windows session was not supplied.",
                providerDecision,
                request);
            return false;
        }

        ConnectedUserHelper? helper = null;
        if (OperatingSystem.IsWindows() && _helperPipeHost is not null)
        {
#pragma warning disable CA1416
            helper = _helperPipeHost.GetConnectedHelper(request.TargetWindowsSessionId.Value);
#pragma warning restore CA1416
        }

        var result = _assistTargetPreflight.Validate(request, helper, _serviceVersion);
        helperReady = result.HelperReady;
        helperNeedsPreparation = result.HelperNeedsPreparation;
        if (result.IsTargetValid && result.HelperReady)
        {
            return true;
        }

        failurePayloadJson = BuildTargetFailurePayload(result.Code, result.Message, providerDecision, request);
        return result.IsTargetValid;
    }

    private string BuildTargetHelperStatusPayload(
        OpenRemoteSupportRequest? request,
        RemoteSupportProviderDecision decision,
        string code,
        string message) =>
        JsonSerializer.Serialize(new
        {
            code,
            statusCode = code,
            message,
            provider = RemoteSupportProviderKinds.InteractiveUserHelper,
            selectedProvider = RemoteSupportProviderKinds.InteractiveUserHelper,
            targetProvider = RemoteSupportProviderKinds.InteractiveUserHelper,
            targetMode = request?.TargetMode ?? "assist_user",
            targetWindowsSessionId = request?.TargetWindowsSessionId,
            targetUserSidHash = request?.TargetUserSidHash,
            targetUsername = request?.TargetUsername,
            inventorySequence = request?.InventorySequence,
            desktopState = decision.DesktopState,
            reconnectRequired = false
        }, _json);

    private string BuildTargetFailurePayload(
        string code,
        string message,
        RemoteSupportProviderDecision decision,
        OpenRemoteSupportRequest? request) =>
        JsonSerializer.Serialize(new
        {
            code,
            statusCode = code,
            message,
            provider = RemoteSupportProviderKinds.InteractiveUserHelper,
            selectedProvider = RemoteSupportProviderKinds.InteractiveUserHelper,
            targetMode = request?.TargetMode ?? "assist_user",
            targetWindowsSessionId = request?.TargetWindowsSessionId,
            targetUserSidHash = request?.TargetUserSidHash,
            targetUsername = request?.TargetUsername,
            inventorySequence = request?.InventorySequence,
            desktopState = decision.DesktopState,
            reconnectRequired = true
        }, _json);

    private static bool IsAssistUserTargetMode(string? targetMode) =>
        string.Equals(targetMode, "assist_user", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(targetMode, "active_user", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoginTargetMode(string? targetMode) =>
        string.Equals(targetMode, "login", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(targetMode, "console_login", StringComparison.OrdinalIgnoreCase);

    private RemoteSupportMediaProfileDto? ReadMediaProfile(string? sessionPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(sessionPayloadJson))
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<OpenRemoteSupportRequest>(sessionPayloadJson, _json);
            return request?.MediaProfile;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Failed to parse session media profile: {ex.Message}");
            return null;
        }
    }

    private void SendSignal(string sessionId, string signalType, string payloadJson)
    {
        var sequence = _sequences.AddOrUpdate(sessionId, 1, static (_, current) => current + 1);
        try
        {
            _gatewaySignalSink(new RemoteSupportPipeSignal(sessionId, signalType, payloadJson));
            LogManager.WriteLog($"[RemoteSupport] Gateway signal sent session={sessionId} type={signalType} seq={sequence} bytes={payloadJson.Length}");
        }
        catch (InvalidOperationException)
        {
            // A full local gateway queue is a transport failure. Do not
            // suppress it or silently lose ordered WebRTC signalling.
            throw;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupport] Failed to send gateway signal session={sessionId} type={signalType}: {ex.Message}");
        }
    }

    private static bool IsHelperVersionCompatible(string? helperVersion, string serviceVersion)
    {
        var helperBase = NormalizeVersion(helperVersion);
        var serviceBase = NormalizeVersion(serviceVersion);
        return !string.IsNullOrWhiteSpace(helperBase) &&
            string.Equals(helperBase, serviceBase, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var trimmed = version.Trim();
        var plusIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        return plusIndex > 0 ? trimmed[..plusIndex] : trimmed;
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(RemoteSupportSessionManager).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (OperatingSystem.IsWindows() && _helperPipeHost is not null)
        {
            _helperPipeHost.HelperMessageReceived -= HandleHelperPipeMessage;
            _helperPipeHost.HelperDisconnected -= HandleHelperDisconnected;
        }

        if (OperatingSystem.IsWindows() && _consoleProviderPipeHost is not null)
        {
#pragma warning disable CA1416
            _consoleProviderPipeHost.MessageReceived -= HandleConsoleProviderPipeMessage;
            _consoleProviderPipeHost.ProviderDisconnected -= HandleConsoleProviderDisconnected;
            _consoleProviderPipeHost.Dispose();
#pragma warning restore CA1416
        }

        _transitionCoordinator?.Dispose();
        _transitionEvidenceTimer?.Dispose();
    }
}

internal sealed record RemoteSupportHandoverOptions(
    bool Enabled,
    bool AutoReconnectEnabled,
    bool ProviderGenerationEnabled,
    bool CoordinatorEnabled)
{
    public static RemoteSupportHandoverOptions Disabled { get; } = new(false, false, false, false);
}

internal sealed record RemoteSupportSessionRoute(
    string Provider,
    int ProviderGeneration,
    string? Reason,
    string? FromProvider,
    string? ToProvider,
    string? HandoverReason,
    int? TargetWindowsSessionId)
{
    public bool IsBoundToWindowsSession(int windowsSessionId) =>
        TargetWindowsSessionId == windowsSessionId;
}

internal sealed record RemoteSupportIncomingSignal(
    string SessionId,
    string SignalType,
    string PayloadJson,
    ulong Sequence,
    long SortKey);

internal sealed record RemoteSupportV2PreparedMediaRoute(
    RemoteSupportPreparedTargetResult PreparedTarget,
    long NegotiationGeneration,
    Guid HelperRouteId,
    string Provider,
    int? ProviderProcessId);

internal sealed class GatewayRemoteSupportSessionState(string openRequestPayload)
{
    public string OpenRequestPayload { get; private set; } = openRequestPayload;
    public bool IsClosed { get; private set; }
    public ulong LastOfferSequence { get; private set; }

    public GatewayRemoteSupportSessionState Reopen(string openRequestPayload)
    {
        OpenRequestPayload = openRequestPayload;
        IsClosed = false;
        return this;
    }

    public void NoteOffer(ulong sequence) => LastOfferSequence = sequence;

    public void Close() => IsClosed = true;
}
