using System.Collections.Concurrent;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using NetRatel.Akka.Hosting;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Application.RemoteSupport;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.API.Gateway;

/// <summary>
/// Per-replica registry for admitted V2 gRPC connections. It owns only live
/// local edge actors/channels; the sharded session actor remains the authority.
/// </summary>
public interface IRemoteSupportV2AgentEdgeRegistry
{
    RemoteSupportV2AgentEdgeConnection Register(ClientKey client, Guid connectionId, ulong connectionEpoch);
}

public sealed class RemoteSupportV2AgentEdgeRegistry(
    ActorSystem actorSystem,
    IRequiredActor<RemoteSupportSessionAuthorityRegion> authorityRegion,
    NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions options)
    : IRemoteSupportV2AgentEdgeRegistry
{
    private readonly ConcurrentDictionary<ClientKey, Connection> _connections = [];

    public RemoteSupportV2AgentEdgeConnection Register(ClientKey client, Guid connectionId, ulong connectionEpoch)
    {
        var connection = new Connection(client, connectionId, connectionEpoch, actorSystem, authorityRegion, options.AskTimeout);
        if (!_connections.TryAdd(client, connection))
        {
            connection.Dispose();
            throw new InvalidOperationException("A remote-support V2 edge is already registered for this agent on this replica.");
        }

        return new RemoteSupportV2AgentEdgeConnection(
            connection.Reader,
            connection.RegisterAsync,
            connection.RouteAsync,
            connection.RouteEvidenceAsync,
            connection.RenewAsync,
            () =>
            {
                if (_connections.TryRemove(new KeyValuePair<ClientKey, Connection>(client, connection)))
                {
                    connection.Dispose();
                }
            });
    }

    private sealed class Connection
    {
        private readonly ClientKey _client;
        private readonly Guid _connectionId;
        private readonly ulong _connectionEpoch;
        private readonly IRequiredActor<RemoteSupportSessionAuthorityRegion> _authorityRegion;
        private readonly TimeSpan _askTimeout;
        private readonly Channel<RemoteSupportAgentRouteEnvelope> _outbound =
            Channel.CreateBounded<RemoteSupportAgentRouteEnvelope>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        private readonly IActorRef _edge;
        private readonly ConcurrentDictionary<RemoteSupportSessionKey, Route> _routes = [];
        private int _disposed;

        public Connection(
            ClientKey client,
            Guid connectionId,
            ulong connectionEpoch,
            ActorSystem actorSystem,
            IRequiredActor<RemoteSupportSessionAuthorityRegion> authorityRegion,
            TimeSpan askTimeout)
        {
            _client = client;
            _connectionId = connectionId;
            _connectionEpoch = connectionEpoch;
            _authorityRegion = authorityRegion;
            _askTimeout = askTimeout;
            _edge = actorSystem.ActorOf(RemoteSupportAgentEdgeActor.Props(_outbound), $"remote-support-v2-agent-edge-{Guid.NewGuid():N}");
        }

        public ChannelReader<RemoteSupportAgentRouteEnvelope> Reader => _outbound.Reader;

        public async Task<bool> RegisterAsync(RemoteSupportSessionKey session, long generation, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0 || session.TenantId != _client.TenantId || session.AgentId != _client.AgentId ||
                generation <= 0)
            {
                return false;
            }

            var route = _routes.AddOrUpdate(
                session,
                _ => new Route(Guid.NewGuid(), generation),
                (_, current) => generation > current.Generation ? new Route(Guid.NewGuid(), generation) : current);
            if (generation < route.Generation)
            {
                return false;
            }

            var authority = await _authorityRegion.GetAsync(cancellationToken).ConfigureAwait(false);
            var result = await authority.Ask<RemoteSupportAgentEdgeRegistration>(
                    new RegisterRemoteSupportAgentEdge(
                        session,
                        _client.AgentId,
                        _connectionId,
                        _connectionEpoch,
                        route.EdgeRouteId,
                        route.Generation,
                        _edge),
                    _askTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Accepted;
        }

        public async Task RenewAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            foreach (var entry in _routes)
            {
                await RegisterAsync(entry.Key, entry.Value.Generation, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<bool> RouteAsync(
            RemoteSupportV2NegotiationEnvelope envelope,
            Guid edgeRouteId,
            long routeGeneration,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0 || envelope.Session.TenantId != _client.TenantId ||
                envelope.Session.AgentId != _client.AgentId || !RemoteSupportV2ContractValidator.TryValidate(envelope, out _) ||
                !_routes.TryGetValue(envelope.Session, out var route) || route.EdgeRouteId != edgeRouteId || route.Generation != routeGeneration)
            {
                return false;
            }

            var authority = await _authorityRegion.GetAsync(cancellationToken).ConfigureAwait(false);
            var result = await authority.Ask<RemoteSupportNegotiationIngressResult>(
                    new ReceiveRemoteSupportNegotiationByKey(new RemoteSupportNegotiationIngress(
                        envelope,
                        AgentEdgeRouteId: edgeRouteId,
                        AgentRouteGeneration: routeGeneration)),
                    _askTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Accepted;
        }

        public async Task<bool> RouteEvidenceAsync(
            RemoteSupportV2TransitionEvidence evidence,
            Guid edgeRouteId,
            long routeGeneration,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0 || evidence.Session.TenantId != _client.TenantId ||
                evidence.Session.AgentId != _client.AgentId || !RemoteSupportV2ContractValidator.TryValidate(evidence, out _) ||
                !_routes.TryGetValue(evidence.Session, out var route) || route.EdgeRouteId != edgeRouteId || route.Generation != routeGeneration)
            {
                return false;
            }

            var authority = await _authorityRegion.GetAsync(cancellationToken).ConfigureAwait(false);
            return await authority.Ask<bool>(
                    new ReceiveRemoteSupportTransitionEvidence(evidence, edgeRouteId, routeGeneration, _connectionEpoch),
                    _askTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var entry in _routes)
            {
                _authorityRegion.ActorRef.Tell(new UnregisterRemoteSupportAgentEdge(
                    entry.Key,
                    entry.Value.EdgeRouteId,
                    _connectionEpoch));
            }

            _routes.Clear();
            _edge.Tell(new StopRemoteSupportAgentEdge());
            _outbound.Writer.TryComplete();
        }

        private sealed record Route(Guid EdgeRouteId, long Generation);
    }
}

public sealed class RemoteSupportV2AgentEdgeConnection(
    ChannelReader<RemoteSupportAgentRouteEnvelope> reader,
    Func<RemoteSupportSessionKey, long, CancellationToken, Task<bool>> register,
    Func<RemoteSupportV2NegotiationEnvelope, Guid, long, CancellationToken, Task<bool>> route,
    Func<RemoteSupportV2TransitionEvidence, Guid, long, CancellationToken, Task<bool>> routeEvidence,
    Func<CancellationToken, Task> renew,
    Action unregister) : IDisposable
{
    public ChannelReader<RemoteSupportAgentRouteEnvelope> Reader { get; } = reader;

    public RemoteSupportV2AgentEdgeConnection(
        ChannelReader<RemoteSupportAgentRouteEnvelope> reader,
        Func<RemoteSupportSessionKey, long, CancellationToken, Task<bool>> register,
        Func<CancellationToken, Task> renew,
        Action unregister)
        : this(reader, register, static (_, _, _, _) => Task.FromResult(false), static (_, _, _, _) => Task.FromResult(false), renew, unregister)
    {
    }

    public Task<bool> RegisterAsync(RemoteSupportSessionKey session, long generation, CancellationToken cancellationToken) =>
        register(session, generation, cancellationToken);

    public Task<bool> RouteAsync(RemoteSupportV2NegotiationEnvelope envelope, Guid edgeRouteId, long routeGeneration, CancellationToken cancellationToken) =>
        route(envelope, edgeRouteId, routeGeneration, cancellationToken);

    public Task<bool> RouteEvidenceAsync(RemoteSupportV2TransitionEvidence evidence, Guid edgeRouteId, long routeGeneration, CancellationToken cancellationToken) =>
        routeEvidence(evidence, edgeRouteId, routeGeneration, cancellationToken);

    public Task RenewAsync(CancellationToken cancellationToken) => renew(cancellationToken);

    public void Dispose() => unregister();
}
