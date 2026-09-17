using System.Collections.Concurrent;
using System.Threading.Channels;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.API.Gateway;

/// <summary>
/// Transient gateway-edge projection for agent-authenticated WTS inventory and
/// exact-target preparation. This is not a Remote Support lifecycle authority
/// and does not write V2 lifecycle/audit tables.
/// </summary>
public interface IRemoteSupportV2PreparationRegistry
{
    RemoteSupportTargetInventoryProjection? GetInventory(ClientKey client);

    RemoteSupportV2CapabilitySnapshot? GetCapabilities(ClientKey client);

    Task RequestInventoryRefreshAsync(ClientKey client, CancellationToken cancellationToken);

    Task<RemoteSupportPreparedTargetResult> PrepareAsync(
        ClientKey client,
        RemoteSupportOperatorBinding operatorBinding,
        RemoteSupportTargetDescriptor target,
        CancellationToken cancellationToken);

    Task<RemoteSupportPreparedTargetResult> PrepareMediaAsync(
        RemoteSupportSessionKey session,
        RemoteSupportOperatorBinding operatorBinding,
        RemoteSupportTargetDescriptor target,
        CancellationToken cancellationToken);

    RemoteSupportV2PreparationRegistration Register(
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        string protocolVersion,
        IReadOnlyList<string> capabilities);

    bool TryReceiveInventory(ClientKey client, RemoteSupportV2InventorySnapshot snapshot);

    bool TryCompletePreparation(ClientKey client, RemoteSupportV2PreparedTarget preparedTarget);
}

public sealed record RemoteSupportTargetInventoryProjection(
    RemoteSupportTargetInventorySnapshot Snapshot,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsFresh(DateTimeOffset now) => now < ExpiresAtUtc;
}

public sealed class RemoteSupportV2InventoryUnavailableException(ClientKey client)
    : InvalidOperationException($"No active Remote Support V2 preparation gateway is available for tenant {client.TenantId}, agent {client.AgentId:D}.");

public sealed class RemoteSupportV2InventoryStaleException(ClientKey client)
    : InvalidOperationException($"Remote Support V2 inventory is stale for tenant {client.TenantId}, agent {client.AgentId:D}.");

public sealed class RemoteSupportV2PreparationRegistry(TimeProvider timeProvider) : IRemoteSupportV2PreparationRegistry
{
    private const int MaximumCapabilityCount = 32;
    private const int MaximumCapabilityLength = 96;
    private static readonly TimeSpan ProjectionLifetime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CapabilityLifetime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PreparationLifetime = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<ClientKey, PreparationTransportSession> _sessions = new();
    private readonly ConcurrentDictionary<ClientKey, RemoteSupportTargetInventoryProjection> _inventory = new();
    private readonly ConcurrentDictionary<ClientKey, RemoteSupportV2CapabilitySnapshot> _capabilities = new();

    public RemoteSupportTargetInventoryProjection? GetInventory(ClientKey client) =>
        _inventory.TryGetValue(client, out var projection) ? projection : null;

    public RemoteSupportV2CapabilitySnapshot? GetCapabilities(ClientKey client) =>
        _capabilities.TryGetValue(client, out var projection) ? projection : null;

    public Task RequestInventoryRefreshAsync(ClientKey client, CancellationToken cancellationToken) =>
        GetSession(client).RequestInventoryRefreshAsync(cancellationToken);

    public async Task<RemoteSupportPreparedTargetResult> PrepareAsync(
        ClientKey client,
        RemoteSupportOperatorBinding operatorBinding,
        RemoteSupportTargetDescriptor target,
        CancellationToken cancellationToken)
    {
        return await PrepareAsync(client, operatorBinding, target, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<RemoteSupportPreparedTargetResult> PrepareMediaAsync(
        RemoteSupportSessionKey session,
        RemoteSupportOperatorBinding operatorBinding,
        RemoteSupportTargetDescriptor target,
        CancellationToken cancellationToken) =>
        PrepareAsync(new ClientKey(session.TenantId, session.AgentId), operatorBinding, target, session, cancellationToken);

    private async Task<RemoteSupportPreparedTargetResult> PrepareAsync(
        ClientKey client,
        RemoteSupportOperatorBinding operatorBinding,
        RemoteSupportTargetDescriptor target,
        RemoteSupportSessionKey? remoteSupportSession,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!_inventory.TryGetValue(client, out var projection) || !projection.IsFresh(now))
        {
            throw new RemoteSupportV2InventoryStaleException(client);
        }

        if (string.Equals(target.Kind, RemoteSupportV2TargetKinds.InteractiveUser, StringComparison.OrdinalIgnoreCase) &&
            target.InventorySequence != projection.Snapshot.InventorySequence)
        {
            throw new ArgumentException("The interactive target must use the current agent inventory sequence.", nameof(target));
        }

        var transport = GetSession(client);
        var command = new RemoteSupportPrepareTargetCommand(
            RemoteSupportV2ContractVersions.Current,
            client.TenantId,
            client.AgentId,
            Guid.NewGuid(),
            operatorBinding,
            target,
            Guid.NewGuid(),
            now,
            now.Add(PreparationLifetime),
            remoteSupportSession);
        if (!RemoteSupportV2PreparationValidator.TryValidate(command, out var error))
        {
            throw new ArgumentException(error!.Message, nameof(target));
        }

        return await transport.PrepareAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public RemoteSupportV2PreparationRegistration Register(
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        string protocolVersion,
        IReadOnlyList<string> capabilities)
    {
        var now = timeProvider.GetUtcNow();
        var advertised = (capabilities ?? [])
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability.Trim())
            .Where(capability => capability.Length <= MaximumCapabilityLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(capability => capability, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCapabilityCount)
            .ToArray();
        _capabilities[client] = new RemoteSupportV2CapabilitySnapshot(
            client.TenantId, client.AgentId, connectionId, connectionEpoch, advertised, now, now.Add(CapabilityLifetime));
        var session = new PreparationTransportSession(client, connectionId, connectionEpoch, protocolVersion);
        // The agent's inventory sequence is scoped to its preparation stream.
        // A fresh presence session starts its writer at sequence one, so the
        // prior stream's projection must never reject that first snapshot as
        // out of order after a token refresh or reconnect.
        _inventory.TryRemove(client, out _);
        _sessions.AddOrUpdate(client, session, (_, existing) =>
        {
            existing.Complete("preparation_gateway_replaced");
            return session;
        });

        return new RemoteSupportV2PreparationRegistration(session.Reader, () =>
        {
            if (((ICollection<KeyValuePair<ClientKey, PreparationTransportSession>>)_sessions)
                .Remove(new KeyValuePair<ClientKey, PreparationTransportSession>(client, session)))
            {
                if (_capabilities.TryGetValue(client, out var projection) &&
                    projection.ConnectionId == connectionId && projection.ConnectionEpoch == connectionEpoch)
                {
                    _capabilities.TryRemove(new KeyValuePair<ClientKey, RemoteSupportV2CapabilitySnapshot>(client, projection));
                }
                session.Complete("preparation_gateway_disconnected");
            }
        });
    }

    public RemoteSupportV2PreparationRegistration Register(
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        string protocolVersion) => Register(client, connectionId, connectionEpoch, protocolVersion, []);

    public bool TryReceiveInventory(ClientKey client, RemoteSupportV2InventorySnapshot snapshot)
    {
        if (!TryMapInventory(client, snapshot, out var mapped) ||
            !RemoteSupportV2PreparationValidator.TryValidate(mapped!, out _))
        {
            return false;
        }

        var receivedAt = timeProvider.GetUtcNow();
        while (true)
        {
            if (_inventory.TryGetValue(client, out var existing) && mapped!.InventorySequence <= existing.Snapshot.InventorySequence)
            {
                return false;
            }

            var projection = new RemoteSupportTargetInventoryProjection(mapped!, receivedAt, receivedAt.Add(ProjectionLifetime));
            if (existing is null)
            {
                if (_inventory.TryAdd(client, projection))
                {
                    RenewCapabilityLease(client, receivedAt);
                    return true;
                }
            }
            else if (_inventory.TryUpdate(client, projection, existing))
            {
                RenewCapabilityLease(client, receivedAt);
                return true;
            }
        }
    }

    private void RenewCapabilityLease(ClientKey client, DateTimeOffset observedAtUtc)
    {
        while (_capabilities.TryGetValue(client, out var existing))
        {
            var renewed = existing with
            {
                ObservedAtUtc = observedAtUtc,
                ExpiresAtUtc = observedAtUtc.Add(CapabilityLifetime)
            };
            if (_capabilities.TryUpdate(client, renewed, existing))
            {
                return;
            }
        }
    }

    public bool TryCompletePreparation(ClientKey client, RemoteSupportV2PreparedTarget preparedTarget)
    {
        if (!_sessions.TryGetValue(client, out var session) || !TryMapPrepared(client, preparedTarget, out var mapped) ||
            !RemoteSupportV2PreparationValidator.TryValidate(mapped!, out _))
        {
            return false;
        }

        return session.TryComplete(mapped!);
    }

    private PreparationTransportSession GetSession(ClientKey client) =>
        _sessions.TryGetValue(client, out var session)
            ? session
            : throw new RemoteSupportV2InventoryUnavailableException(client);

    public static bool TryMapInventory(
        ClientKey client,
        RemoteSupportV2InventorySnapshot snapshot,
        out RemoteSupportTargetInventorySnapshot? mapped)
    {
        try
        {
            mapped = new RemoteSupportTargetInventorySnapshot(
                checked((int)snapshot.ContractVersion),
                client.TenantId,
                client.AgentId,
                snapshot.InventorySequence,
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.ObservedUnixMs),
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.ExpiresUnixMs),
                snapshot.Entries.Select(entry => new RemoteSupportTargetInventoryEntry(
                    entry.WindowsSessionId,
                    entry.State,
                    string.IsNullOrWhiteSpace(entry.UserSidHash) ? null : entry.UserSidHash,
                    entry.IsConsoleSession,
                    entry.IsConnected,
                    entry.IsLocked,
                    entry.IsWinlogon,
                    entry.HelperConnected,
                    entry.HelperVersionMatches,
                    string.IsNullOrWhiteSpace(entry.HelperVersion) ? null : entry.HelperVersion,
                    string.IsNullOrWhiteSpace(entry.DisplayLabel) ? null : entry.DisplayLabel)).ToArray());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            mapped = null;
            return false;
        }
    }

    public static bool TryMapPrepared(
        ClientKey client,
        RemoteSupportV2PreparedTarget preparedTarget,
        out RemoteSupportPreparedTargetResult? mapped)
    {
        if (!TryParseGuid(preparedTarget.RequestId, out var requestId) || !TryParseGuid(preparedTarget.RouteNonce, out var routeNonce))
        {
            mapped = null;
            return false;
        }

        try
        {
            var helperRoute = preparedTarget.HasHelperRoute ? preparedTarget.HelperRoute : null;
            mapped = new RemoteSupportPreparedTargetResult(
                checked((int)preparedTarget.ContractVersion),
                client.TenantId,
                client.AgentId,
                requestId,
                routeNonce,
                ToTarget(preparedTarget.Target),
                preparedTarget.InventorySequence,
                preparedTarget.TargetValid,
                preparedTarget.ProviderReady,
                preparedTarget.Code,
                preparedTarget.Message,
                helperRoute is not null && TryParseGuid(helperRoute.HelperRouteId, out var helperRouteId)
                    ? new RemoteSupportHelperRoute(
                        helperRouteId,
                        helperRoute.WindowsSessionId,
                        helperRoute.UserSidHash,
                        helperRoute.HelperVersion)
                    : null,
                DateTimeOffset.FromUnixTimeMilliseconds(preparedTarget.ObservedUnixMs),
                preparedTarget.HasSession && TryMapSession(preparedTarget.Session, client, out var session) ? session : null);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            mapped = null;
            return false;
        }
    }

    private static RemoteSupportTargetDescriptor ToTarget(RemoteSupportV2Target? target) =>
        new(
            target?.Kind ?? string.Empty,
            target?.HasWindowsSessionId == true ? target.WindowsSessionId : null,
            string.IsNullOrWhiteSpace(target?.UserSidHash) ? null : target.UserSidHash,
            target?.HasInventorySequence == true ? target.InventorySequence : null);

    private static bool TryParseGuid(string? value, out Guid parsed) =>
        Guid.TryParse(value, out parsed) && parsed != Guid.Empty;

    private static bool TryMapSession(RemoteSupportV2SessionKey? value, ClientKey client, out RemoteSupportSessionKey? session)
    {
        session = null;
        if (value is null || value.TenantId != client.TenantId || !TryParseGuid(value.AgentId, out var agentId) ||
            agentId != client.AgentId || !TryParseGuid(value.RemoteSupportSessionId, out var remoteSupportSessionId))
        {
            return false;
        }

        session = new RemoteSupportSessionKey(client.TenantId, client.AgentId, remoteSupportSessionId);
        return true;
    }
}

public sealed class RemoteSupportV2PreparationRegistration(ChannelReader<GatewayRemoteSupportPreparationFrame> reader, Action unregister) : IDisposable
{
    private int _disposed;
    public ChannelReader<GatewayRemoteSupportPreparationFrame> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unregister();
        }
    }
}

internal sealed class PreparationTransportSession(
    ClientKey client,
    Guid connectionId,
    ulong connectionEpoch,
    string protocolVersion,
    Channel<GatewayRemoteSupportPreparationFrame>? outbound = null)
{
    private readonly Channel<GatewayRemoteSupportPreparationFrame> _outbound = outbound ?? Channel.CreateBounded<GatewayRemoteSupportPreparationFrame>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly ConcurrentDictionary<Guid, PendingPreparation> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ulong _sequence;

    public ChannelReader<GatewayRemoteSupportPreparationFrame> Reader => _outbound.Reader;

    public async Task<RemoteSupportPreparedTargetResult> PrepareAsync(RemoteSupportPrepareTargetCommand command, CancellationToken cancellationToken)
    {
        var pending = new PendingPreparation(command);
        if (!_pending.TryAdd(command.RequestId, pending))
        {
            throw new InvalidOperationException("The target preparation request could not be registered.");
        }

        try
        {
            await WriteAsync(new GatewayRemoteSupportPreparationFrame
            {
                PrepareTarget = new RemoteSupportV2PrepareTarget
                {
                    ContractVersion = checked((uint)command.ContractVersion),
                    RequestId = command.RequestId.ToString("D"),
                    Operator = new RemoteSupportV2OperatorBinding { OperatorId = command.Operator.OperatorId },
                    Target = ToProto(command.Target),
                    RouteNonce = command.RouteNonce.ToString("D"),
                    RequestedUnixMs = command.RequestedAtUtc.ToUnixTimeMilliseconds(),
                    ExpiresUnixMs = command.ExpiresAtUtc.ToUnixTimeMilliseconds(),
                    HasSession = command.Session is not null,
                    Session = command.Session is { } session ? ToProto(session) : null
                }
            }, cancellationToken).ConfigureAwait(false);
            return await pending.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(command.RequestId, out _);
        }
    }

    public bool TryComplete(RemoteSupportPreparedTargetResult result)
    {
        if (!_pending.TryGetValue(result.RequestId, out var pending) ||
            result.RouteNonce != pending.Command.RouteNonce || result.Target != pending.Command.Target ||
            result.Session != pending.Command.Session)
        {
            return false;
        }

        return pending.Result.TrySetResult(result);
    }

    public Task RequestInventoryRefreshAsync(CancellationToken cancellationToken) =>
        WriteAsync(new GatewayRemoteSupportPreparationFrame
        {
            InventoryRefresh = new RemoteSupportV2InventoryRefresh { RequestId = Guid.NewGuid().ToString("D") }
        }, cancellationToken);

    public void Complete(string reason)
    {
        _outbound.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            pending.Result.TrySetException(new InvalidOperationException($"Remote Support V2 preparation gateway closed: {reason}."));
        }
        _pending.Clear();
    }

    private async Task WriteAsync(GatewayRemoteSupportPreparationFrame frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            frame.ProtocolVersion = protocolVersion;
            frame.TenantId = client.TenantId;
            frame.ClientId = client.AgentId.ToString("D");
            frame.ConnectionEpoch = connectionEpoch;
            frame.ConnectionId = connectionId.ToString("D");
            // Refresh and preparation share the client's strict sequence fence.
            // Reserve a sequence in the same order that frames enter the channel.
            frame.Sequence = checked(++_sequence);
            await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static RemoteSupportV2Target ToProto(RemoteSupportTargetDescriptor target) => new()
    {
        Kind = target.Kind,
        WindowsSessionId = target.WindowsSessionId ?? 0,
        UserSidHash = target.UserSidHash ?? string.Empty,
        InventorySequence = target.InventorySequence ?? 0,
        HasWindowsSessionId = target.WindowsSessionId.HasValue,
        HasInventorySequence = target.InventorySequence.HasValue
    };

    private static RemoteSupportV2SessionKey ToProto(RemoteSupportSessionKey session) => new()
    {
        TenantId = session.TenantId,
        AgentId = session.AgentId.ToString("D"),
        RemoteSupportSessionId = session.RemoteSupportSessionId.ToString("D")
    };

    private sealed class PendingPreparation(RemoteSupportPrepareTargetCommand command)
    {
        public RemoteSupportPrepareTargetCommand Command { get; } = command;
        public TaskCompletionSource<RemoteSupportPreparedTargetResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
