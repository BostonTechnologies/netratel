using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Client.Service.ComputerUse;

/// <summary>One controller per interactive helper, with monotonic commands and one-use observed-state authority.</summary>
internal sealed class ComputerUseInteractiveHost : IAsyncDisposable
{
    private readonly bool _trustedService;
    private readonly Func<bool> _manualSupportActive;
    private readonly IComputerUseDesktop _desktop;
    private readonly Func<string, ComputerUseOptions, IComputerUseProvider> _createProvider;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _expiryTask;
    private readonly Dictionary<Guid, (string Fingerprint, ComputerUseResult Result)> _completed = [];
    private readonly Dictionary<Guid, ComputerUseCommand> _endedSessions = [];
    private readonly Dictionary<Guid, RegisteredOperation> _registered = [];
    private long _generation;
    private IComputerUseProvider? _provider;
    private ComputerUseCommand? _lease;
    private ComputerUseCommand? _running;
    private CancellationTokenSource? _operationCancellation;
    private long _highestRevision;
    private string? _processFingerprint;
    private ComputerUseObservation? _observation;
    private uint _observedInput;
    private bool _paused;
    private bool _pauseRequested;
    private bool _disposed;

    internal ComputerUseInteractiveHost(
        bool trustedService,
        Func<bool> manualSupportActive,
        IComputerUseDesktop desktop,
        Func<string, ComputerUseOptions, IComputerUseProvider> createProvider,
        TimeProvider? timeProvider = null)
    {
        _trustedService = trustedService;
        _manualSupportActive = manualSupportActive;
        _desktop = desktop;
        _createProvider = createProvider;
        _clock = timeProvider ?? TimeProvider.System;
        _expiryTask = ExpireLeasesAsync(_lifetime.Token);
    }

    internal static ComputerUseInteractiveHost Create(bool trustedService, Func<bool> manualSupportActive)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new(trustedService, manualSupportActive, new WindowsComputerUseDesktop(), static (provider, options) => provider switch
        {
            ComputerUseProviders.CuaDriver => new CuaDriverComputerUseProvider(options),
            ComputerUseProviders.NativeWindows when OperatingSystem.IsWindows() => new NativeWindowsComputerUseProvider(),
            _ => throw new ComputerUseRefusalException("unsupported_provider", "Provider is not available on this endpoint.")
        });
    }

    internal Task<ComputerUseResult> ExecuteAsync(ComputerUsePipeRequest request, CancellationToken cancellationToken = default) =>
        ExecuteRegisteredAsync(request, RegisterOperation(request.Command.OperationId), cancellationToken);

    internal RegisteredOperation RegisterOperation(Guid operationId)
    {
        lock (_sync)
        {
            var registration = new RegisteredOperation(operationId, _generation);
            if (_disposed) registration.RefusalCode = "helper_stopping";
            else if (_registered.ContainsKey(operationId)) registration.RefusalCode = "operation_in_progress";
            else if (_registered.Count >= 32) registration.RefusalCode = "operation_queue_full";
            else _registered.Add(operationId, registration);
            return registration;
        }
    }

    internal async Task<ComputerUseResult> ExecuteRegisteredAsync(ComputerUsePipeRequest request, RegisteredOperation registration, CancellationToken cancellationToken = default)
    {
        try
        {
            if (registration.RefusalCode is { } code) return ComputerUseResult.Refused(request.Command, code);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, registration.Cancellation.Token);
            return await ExecuteCoreAsync(request, registration.Generation, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
                if (_registered.TryGetValue(registration.OperationId, out var current) && ReferenceEquals(current, registration))
                    _registered.Remove(registration.OperationId);
            registration.Cancellation.Dispose();
        }
    }

    private async Task<ComputerUseResult> ExecuteCoreAsync(ComputerUsePipeRequest request, long generation, CancellationToken cancellationToken)
    {
        var command = request.Command;
        var actionDispatched = false;
        if (cancellationToken.IsCancellationRequested) return Outcome(command, ComputerUseStatuses.Cancelled, "cancelled_before_execution");
        if (!_trustedService) return ComputerUseResult.Refused(command, "untrusted_service_pipe");
        if (ComputerUseValidation.ValidateCommand(command) is { } invalid) return ComputerUseResult.Refused(command, invalid);
        if (!request.Options.Enabled)
        {
            await RevokeAllAsync().ConfigureAwait(false);
            return ComputerUseResult.Refused(command, "feature_disabled");
        }
        if (command.Provider == ComputerUseProviders.NativeWindows && !request.Options.EnableNativeProvider)
            return ComputerUseResult.Refused(command, "provider_disabled");
        var discovery = command.Operation is ComputerUseOperations.Capabilities or ComputerUseOperations.ListWindows;
        if (command.ExpiresAtUtc <= _clock.GetUtcNow() || command.ExpiresAtUtc > _clock.GetUtcNow().AddMinutes(15))
            return ComputerUseResult.Refused(command, "lease_expired");

        lock (_sync)
        {
            if (_disposed) return ComputerUseResult.Refused(command, "helper_stopping");
            if (generation != _generation) return ComputerUseResult.Refused(command, "dispatch_generation_revoked");
            foreach (var ended in new List<Guid>(_endedSessions.Keys))
                if (_endedSessions[ended].ExpiresAtUtc <= _clock.GetUtcNow()) _endedSessions.Remove(ended);
            if (_completed.TryGetValue(command.OperationId, out var cached))
                return cached.Fingerprint == CommandFingerprint(command) ? cached.Result : ComputerUseResult.Refused(command, "operation_replay_mismatch");
            if (!discovery && _endedSessions.TryGetValue(command.SessionId, out var endedSession))
                return command.Operation == ComputerUseOperations.Close && endedSession.Target == command.Target && endedSession.Provider == command.Provider
                    ? Outcome(command, ComputerUseStatuses.Completed, "session_closed")
                    : ComputerUseResult.Refused(command, "session_revoked");
            if (!discovery && _lease is { } lease)
            {
                if (lease.SessionId != command.SessionId) return ComputerUseResult.Refused(command, "controller_conflict");
                if (lease.Target != command.Target || lease.Provider != command.Provider || lease.ControlAllowed != command.ControlAllowed)
                    return ComputerUseResult.Refused(command, "session_binding_mismatch");
                if (command.SessionRevision <= _highestRevision) return ComputerUseResult.Refused(command, "stale_session_revision");
                _highestRevision = command.SessionRevision;
                if (command.Operation is ComputerUseOperations.Pause or ComputerUseOperations.Close)
                {
                    _pauseRequested = command.Operation == ComputerUseOperations.Pause;
                    _operationCancellation?.Cancel();
                }
            }
        }

        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Outcome(command, ComputerUseStatuses.Cancelled, "cancelled_before_execution"); }
        try
        {
            lock (_sync)
            {
                if (_disposed) return ComputerUseResult.Refused(command, "helper_stopping");
                if (generation != _generation) return ComputerUseResult.Refused(command, "dispatch_generation_revoked");
                if (cancellationToken.IsCancellationRequested) return Outcome(command, ComputerUseStatuses.Cancelled, "cancelled_before_execution");
                if (_completed.TryGetValue(command.OperationId, out var cached))
                    return cached.Fingerprint == CommandFingerprint(command) ? cached.Result : ComputerUseResult.Refused(command, "operation_replay_mismatch");
                if (!discovery && _endedSessions.TryGetValue(command.SessionId, out var endedSession))
                    return command.Operation == ComputerUseOperations.Close && endedSession.Target == command.Target && endedSession.Provider == command.Provider
                        ? Outcome(command, ComputerUseStatuses.Completed, "session_closed") : ComputerUseResult.Refused(command, "session_revoked");
                // Open can establish a lease while this request is waiting for the gate.
                // Recheck its binding and executed revision before any control transition.
                if (!discovery && _lease is { } lease)
                {
                    if (lease.SessionId != command.SessionId) return ComputerUseResult.Refused(command, "controller_conflict");
                    if (lease.Target != command.Target || lease.Provider != command.Provider || lease.ControlAllowed != command.ControlAllowed)
                        return ComputerUseResult.Refused(command, "session_binding_mismatch");
                    if (command.SessionRevision < _highestRevision || command.SessionRevision <= lease.SessionRevision)
                        return ComputerUseResult.Refused(command, "stale_session_revision");
                    _highestRevision = command.SessionRevision;
                }
            }
            if (_lease is { } current && current.ExpiresAtUtc <= _clock.GetUtcNow()) await EndLeaseAsync().ConfigureAwait(false);
            if (command.ExpiresAtUtc <= _clock.GetUtcNow()) return ComputerUseResult.Refused(command, "lease_expired");
            if (!discovery && command.Operation != ComputerUseOperations.Open && _lease is null)
                return ComputerUseResult.Refused(command, "session_not_open");
            if (!discovery && _lease is { } existing && existing.SessionId != command.SessionId)
                return ComputerUseResult.Refused(command, "controller_conflict");
            if (command.Operation == ComputerUseOperations.Close)
            {
                await EndLeaseAsync().ConfigureAwait(false);
                return Remember(command, Outcome(command, ComputerUseStatuses.Completed, "session_closed"));
            }
            if (command.Operation == ComputerUseOperations.Pause)
            {
                _paused = true;
                _pauseRequested = false;
                _observation = null;
                _lease = command;
                return Remember(command, Outcome(command, ComputerUseStatuses.Completed, "session_paused"));
            }
            if (_manualSupportActive())
            {
                await EndLeaseAsync().ConfigureAwait(false);
                return ComputerUseResult.Refused(command, "manual_support_active", "Manual Remote Support currently owns this desktop.");
            }
            _desktop.Validate(command.Target, !discovery);
            if (!discovery && _processFingerprint is not null && _processFingerprint != _desktop.Fingerprint(command.Target))
            {
                await EndLeaseAsync().ConfigureAwait(false);
                return ComputerUseResult.Refused(command, "target_identity_changed");
            }
            if (command.Operation == ComputerUseOperations.Resume)
            {
                _paused = false;
                _observation = null;
                _lease = command;
                return Remember(command, Outcome(command, ComputerUseStatuses.Completed, "session_resumed_observe_required"));
            }
            if (_paused) return ComputerUseResult.Refused(command, "session_paused");
            if (command.Operation == ComputerUseOperations.Open && _lease is not null)
                return ComputerUseResult.Refused(command, "session_already_open");
            if (command.Operation == ComputerUseOperations.Act)
            {
                if (_observation is null || command.ObservationId != _observation.ObservationId ||
                    _clock.GetUtcNow() - _observation.ObservedAtUtc > request.Options.ObservationLifetime)
                    return ComputerUseResult.Refused(command, "stale_observation", "Observe again before acting.");
                if (_observedInput != _desktop.LastInputTick)
                {
                    _observation = null;
                    return ComputerUseResult.Refused(command, "user_interference", "User input changed since the observation; observe again.");
                }
                _observation = null;
            }

            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            operationCancellation.CancelAfter(request.Options.OperationTimeout);
            var expiry = command.ExpiresAtUtc - _clock.GetUtcNow();
            if (expiry < request.Options.OperationTimeout) operationCancellation.CancelAfter(expiry);
            lock (_sync) { _running = command; _operationCancellation = operationCancellation; }
            var inputBefore = _desktop.LastInputTick;
            ComputerUseResult result;
            try
            {
                if (discovery)
                {
                    await using var provider = _createProvider(command.Provider, request.Options);
                    result = await provider.ExecuteAsync(command with { ControlAllowed = false }, operationCancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    _provider ??= _createProvider(command.Provider, request.Options);
                    _lease = command;
                    _highestRevision = Math.Max(_highestRevision, command.SessionRevision);
                    _processFingerprint ??= _desktop.Fingerprint(command.Target);
                    actionDispatched = command.Operation == ComputerUseOperations.Act;
                    result = await _provider.ExecuteAsync(command, operationCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                result = Outcome(command, ComputerUseStatuses.Cancelled, "operation_cancelled", command.Operation == ComputerUseOperations.Act);
            }
            catch (ComputerUseRefusalException refusal)
            {
                result = ComputerUseResult.Refused(command, refusal.Code, refusal.Message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ComputerUseDiagnostics.Record("provider_failed", exception);
                result = Outcome(command, ComputerUseStatuses.Failed, "provider_failed", command.Operation == ComputerUseOperations.Act);
            }
            finally { lock (_sync) { _running = null; _operationCancellation = null; } }

            if (!ComputerUseValidation.IsMatchingResult(command, result))
                result = Outcome(command, ComputerUseStatuses.Failed, "provider_invalid_result", command.Operation == ComputerUseOperations.Act);
            if (result.Observation is { } observation)
            {
                _desktop.Validate(command.Target, true);
                var inputAfter = _desktop.LastInputTick;
                if (inputAfter != inputBefore)
                {
                    _observation = null;
                    result = Outcome(command, actionDispatched ? ComputerUseStatuses.AppliedUnverified : ComputerUseStatuses.Refused,
                        "user_interference", actionDispatched);
                }
                else
                {
                    _observation = observation;
                    _observedInput = inputAfter;
                }
            }
            if (!discovery && (result.Status is ComputerUseStatuses.Cancelled or ComputerUseStatuses.Failed || command.Operation == ComputerUseOperations.Open && result.Status == ComputerUseStatuses.Refused))
            {
                if (_pauseRequested && _lease?.SessionId == command.SessionId)
                {
                    var provider = _provider;
                    _provider = null;
                    _observation = null;
                    _paused = true;
                    if (provider is not null) await provider.DisposeAsync().ConfigureAwait(false);
                }
                else await EndLeaseAsync().ConfigureAwait(false);
            }
            return Remember(command, result);
        }
        catch (ComputerUseRefusalException refusal)
        {
            if (!discovery) await EndLeaseAsync().ConfigureAwait(false);
            return actionDispatched
                ? Outcome(command, ComputerUseStatuses.Unknown, refusal.Code, true)
                : ComputerUseResult.Refused(command, refusal.Code, refusal.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ComputerUseDiagnostics.Record("helper_execution_failed", exception);
            if (!discovery) await EndLeaseAsync().ConfigureAwait(false);
            return Outcome(command, ComputerUseStatuses.Failed, "helper_execution_failed", actionDispatched);
        }
        finally { _gate.Release(); }
    }

    internal void Cancel(Guid operationId)
    {
        lock (_sync)
        {
            if (_registered.TryGetValue(operationId, out var registered)) registered.Cancellation.Cancel();
            if (_running?.OperationId == operationId) _operationCancellation?.Cancel();
        }
    }

    internal async Task RevokeAllAsync()
    {
        lock (_sync)
        {
            _generation++;
            foreach (var registered in _registered.Values) registered.Cancellation.Cancel();
            _operationCancellation?.Cancel();
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await EndLeaseAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task EndLeaseAsync()
    {
        var provider = _provider;
        _provider = null;
        lock (_sync)
        {
            if (_lease is { } lease)
                _endedSessions[lease.SessionId] = lease with { Action = null, ObservationId = null };
            _lease = null;
            _highestRevision = 0;
            _completed.Clear();
        }
        _processFingerprint = null;
        _observation = null;
        _paused = false;
        _pauseRequested = false;
        if (provider is not null) await provider.DisposeAsync().ConfigureAwait(false);
    }

    private ComputerUseResult Remember(ComputerUseCommand command, ComputerUseResult result)
    {
        lock (_sync)
        {
            if (_completed.Count >= 8) _completed.Clear();
            // Replay receipts retain no screenshots, window lists, accessibility state, or typed text.
            _completed[command.OperationId] = (CommandFingerprint(command), result with { Observation = null, Windows = null });
        }
        return result;
    }

    private async Task ExpireLeasesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                ComputerUseCommand? lease;
                lock (_sync) lease = _lease;
                if (lease is not null && lease.ExpiresAtUtc <= _clock.GetUtcNow())
                    await RevokeAllAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ComputerUseDiagnostics.Record("lease_expiry_monitor_stopped");
        }
    }

    private static ComputerUseResult Outcome(ComputerUseCommand command, string status, string code, bool mayHaveApplied = false) =>
        new(ComputerUseContract.Version, command.OperationId, command.SessionId, status, code, Provider: command.Provider, MayHaveApplied: mayHaveApplied);

    private static string CommandFingerprint(ComputerUseCommand command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, ComputerUseContract.JsonOptions)));

    internal sealed class RegisteredOperation(Guid operationId, long generation)
    {
        internal Guid OperationId { get; } = operationId;
        internal long Generation { get; } = generation;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal string? RefusalCode { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _expiryTask.ConfigureAwait(false);
        await RevokeAllAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        // Outstanding pipe tasks may still be entering the gate; leave its managed handle to GC.
    }
}
