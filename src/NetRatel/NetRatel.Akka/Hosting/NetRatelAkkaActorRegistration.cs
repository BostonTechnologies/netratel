using Akka.Actor;
using Akka.Cluster.Hosting;
using Akka.Cluster.Sharding;
using Akka.Hosting;
using Akka.Remote.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Akka.Commands;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Jobs;
using NetRatel.Akka.Presence;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Akka.Telemetry;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.RemoteSupport;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Akka.Hosting;

/// <summary>Marker used for type-safe access to the local client region.</summary>
public sealed class ClientPresenceRegion;

/// <summary>Marker used for type-safe access to the local gateway presence read model.</summary>
public sealed class ClientPresenceReadModelRegion;

/// <summary>Marker used for type-safe access to the local telemetry region.</summary>
public sealed class ClientTelemetryRegion;

/// <summary>Marker used for type-safe access to the local command region.</summary>
public sealed class ClientCommandRegion;

/// <summary>Marker used for type-safe access to the local job region.</summary>
public sealed class JobRuntimeRegion;

/// <summary>Marker used for type-safe access to the V2 remote-support authority router.</summary>
public sealed class RemoteSupportSessionAuthorityRegion;

public static class NetRatelAkkaActorRegistration
{
    public static IServiceCollection AddNetRatelAkkaActors(
        this IServiceCollection services,
        string actorSystemName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSystemName);

        services.AddAkka(actorSystemName, (akka, serviceProvider) =>
        {
            var options = serviceProvider.GetRequiredService<NetRatelAkkaOptions>();
            var commandPersistence = serviceProvider.GetRequiredService<ICommandPersistenceStore>();
            var jobObservations = serviceProvider.GetRequiredService<IJobObservationStore>();
            var remoteSupportLifecycle = serviceProvider.GetRequiredService<IRemoteSupportLifecycleStore>();
            akka
                .WithActorSystemLivenessCheck();

            if (options.Cluster.IsClustered)
            {
                var cluster = options.Cluster;
                akka.WithRemoting(cluster.HostName, cluster.Port)
                    .WithClustering(new ClusterOptions
                    {
                        Roles = [cluster.Role],
                        SeedNodes = cluster.SeedNodes
                    });
            }

            akka
                .WithActors((system, registry, _) =>
                {
                    var presenceReadModel = system.ActorOf(
                        PresenceReadModelActor.Props(),
                        "client-presence-read-model");
                    registry.Register<ClientPresenceReadModelRegion>(presenceReadModel);
                    var presenceRegion = system.ActorOf(
                        ClientPresenceRouterActor.Props(options, presenceReadModel),
                        "client-presence");
                    registry.Register<ClientPresenceRegion>(presenceRegion);

                    var telemetryRegion = system.ActorOf(
                        ClientTelemetryRouterActor.Props(),
                        "client-telemetry");
                    registry.Register<ClientTelemetryRegion>(telemetryRegion);

                    var commandRegion = system.ActorOf(
                        ClientCommandRouterActor.Props(commandPersistence),
                        "client-commands");
                    registry.Register<ClientCommandRegion>(commandRegion);

                    var jobRegion = system.ActorOf(
                        JobCoordinatorActor.Props(jobObservations),
                        "job-runtime");
                    registry.Register<JobRuntimeRegion>(jobRegion);

                    var remoteSupportShardRegion = options.Cluster.IsClustered
                        ? ClusterSharding.Get(system).Start(
                            RemoteSupportSessionMessageExtractor.RegionName,
                            RemoteSupportSessionActor.Props(remoteSupportLifecycle),
                            ClusterShardingSettings.Create(system),
                            new RemoteSupportSessionMessageExtractor())
                        : null;
                    var remoteSupportRegion = system.ActorOf(
                        remoteSupportShardRegion is null
                            ? RemoteSupportSessionRouterActor.Props(remoteSupportLifecycle)
                            : RemoteSupportSessionRouterActor.Props(remoteSupportLifecycle, remoteSupportShardRegion),
                        "remote-support-v2-lifecycle");
                    registry.Register<RemoteSupportSessionAuthorityRegion>(remoteSupportRegion);
                });
        });

        services.AddSingleton<IClientPresenceRouter>(serviceProvider =>
            new AkkaClientPresenceRouter(
                serviceProvider.GetRequiredService<IRequiredActor<ClientPresenceRegion>>(),
                serviceProvider.GetRequiredService<NetRatelAkkaOptions>().AskTimeout));
        services.AddSingleton<IClientPresenceReadModel>(serviceProvider =>
            new AkkaClientPresenceReadModel(
                serviceProvider.GetRequiredService<IRequiredActor<ClientPresenceReadModelRegion>>(),
                serviceProvider.GetRequiredService<NetRatelAkkaOptions>().AskTimeout));
        services.AddSingleton<IClientTelemetryRouter>(serviceProvider =>
            new AkkaClientTelemetryRouter(
                serviceProvider.GetRequiredService<IRequiredActor<ClientTelemetryRegion>>(),
                serviceProvider.GetRequiredService<NetRatelAkkaOptions>().AskTimeout));
        services.AddSingleton<IClientCommandRouter>(serviceProvider =>
            new AkkaClientCommandRouter(
                serviceProvider.GetRequiredService<IRequiredActor<ClientCommandRegion>>(),
                serviceProvider.GetRequiredService<NetRatelAkkaOptions>().AskTimeout));
        services.AddSingleton<IJobRuntimeRouter>(serviceProvider =>
            new AkkaJobRuntimeRouter(
                serviceProvider.GetRequiredService<IRequiredActor<JobRuntimeRegion>>(),
                serviceProvider.GetRequiredService<NetRatelAkkaOptions>().AskTimeout));
        services.AddSingleton<IRemoteSupportLifecycleRouter>(serviceProvider =>
            new AkkaRemoteSupportLifecycleRouter(
                serviceProvider.GetRequiredService<IRequiredActor<RemoteSupportSessionAuthorityRegion>>(),
                serviceProvider.GetRequiredService<NetRatelAkkaOptions>().AskTimeout));

        return services;
    }
}

internal sealed class AkkaRemoteSupportLifecycleRouter : IRemoteSupportLifecycleRouter
{
    private readonly IRequiredActor<RemoteSupportSessionAuthorityRegion> _region;
    private readonly TimeSpan _askTimeout;

    public AkkaRemoteSupportLifecycleRouter(
        IRequiredActor<RemoteSupportSessionAuthorityRegion> region,
        TimeSpan askTimeout)
    {
        _region = region;
        _askTimeout = askTimeout;
    }

    public async Task<RemoteSupportSessionSnapshot> OpenAsync(
        RemoteSupportOpenSessionCommand command,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<RemoteSupportSessionSnapshot>(
                new OpenRemoteSupportSession(command, _askTimeout), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteSupportSessionSnapshot?> GetAsync(
        RemoteSupportSessionKey session,
        RemoteSupportOperatorBinding operatorBinding,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<RemoteSupportSessionSnapshot?>(
                new GetRemoteSupportSessionByKey(session, operatorBinding), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteSupportLifecycleTransitionResult> ControlAsync(
        RemoteSupportControlCommand command,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<RemoteSupportLifecycleTransitionResult>(
                new ControlRemoteSupportSessionByKey(command), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteSupportSessionResume?> ResumeAsync(
        RemoteSupportResumeRequest request,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<RemoteSupportSessionResume?>(
                new ResumeRemoteSupportSessionByKey(request), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteSupportLifecycleTransitionResult> AdvanceAsync(
        AdvanceRemoteSupportSessionLifecycle command,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<RemoteSupportLifecycleTransitionResult>(
                new AdvanceRemoteSupportSessionByKey(command), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteSupportLifecycleTransitionResult> PrepareMediaAsync(
        PrepareRemoteSupportMedia command,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<RemoteSupportLifecycleTransitionResult>(
                new PrepareRemoteSupportMediaByKey(command), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteSupportNegotiationIngressResult> NegotiateAsync(
        RemoteSupportNegotiationIngress command,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<RemoteSupportNegotiationIngressResult>(
                new ReceiveRemoteSupportNegotiationByKey(command), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteSupportBrowserEdgeSubscription?> SubscribeAsync(
        RemoteSupportSessionKey session,
        RemoteSupportOperatorBinding operatorBinding,
        long afterAuditSequence,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        var registration = await region.Ask<RemoteSupportBrowserEdgeLocalRegistration?>(
                new SubscribeRemoteSupportBrowserEdge(session, operatorBinding, afterAuditSequence, _askTimeout),
                _askTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (registration is null)
        {
            return null;
        }

        return new RemoteSupportBrowserEdgeSubscription(
            registration.Reader,
            () =>
            {
                region.Tell(new UnsubscribeRemoteSupportBrowserEdge(
                    registration.Session,
                    registration.EdgeRouteId,
                    registration.Edge));
                return ValueTask.CompletedTask;
            });
    }

    public async Task<RemoteSupportBrowserNegotiationSubscription?> SubscribeNegotiationAsync(
        RemoteSupportSessionKey session,
        RemoteSupportOperatorBinding operatorBinding,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        var registration = await region.Ask<RemoteSupportBrowserNegotiationEdgeLocalRegistration?>(
                new SubscribeRemoteSupportBrowserNegotiationEdge(session, operatorBinding, _askTimeout),
                _askTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (registration is null)
        {
            return null;
        }

        return new RemoteSupportBrowserNegotiationSubscription(
            registration.Reader,
            () =>
            {
                region.Tell(new UnsubscribeRemoteSupportBrowserNegotiationEdge(
                    registration.Session,
                    registration.EdgeRouteId,
                    registration.Edge));
                return ValueTask.CompletedTask;
            });
    }
}

internal sealed class AkkaJobRuntimeRouter : IJobRuntimeRouter
{
    private readonly IRequiredActor<JobRuntimeRegion> _region;
    private readonly TimeSpan _askTimeout;

    public AkkaJobRuntimeRouter(
        IRequiredActor<JobRuntimeRegion> region,
        TimeSpan askTimeout)
    {
        _region = region;
        _askTimeout = askTimeout;
    }

    public async Task<JobMessageResult> RecordAsync(
        RecordJobObservation message,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<JobMessageResult>(message, _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JobRunView> GetStateAsync(
        ulong jobRunId,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<JobRunView>(new GetJobRunProjection(jobRunId), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JobRuntimeStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<JobRuntimeStatus>(new ProbeJobRuntime(), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class AkkaClientCommandRouter : IClientCommandRouter
{
    private readonly IRequiredActor<ClientCommandRegion> _region;
    private readonly TimeSpan _askTimeout;

    public AkkaClientCommandRouter(
        IRequiredActor<ClientCommandRegion> region,
        TimeSpan askTimeout)
    {
        _region = region;
        _askTimeout = askTimeout;
    }

    public async Task<CommandMessageResult> RecordAsync(
        RecordCommandLifecycleEvent message,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<CommandMessageResult>(message, _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CommandState> GetStateAsync(
        CommandKey command,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<CommandState>(new GetCommandState(command), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClientCommandRouteStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<ClientCommandRouteStatus>(new ProbeClientCommandRoute(), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class AkkaClientTelemetryRouter : IClientTelemetryRouter
{
    private readonly IRequiredActor<ClientTelemetryRegion> _region;
    private readonly TimeSpan _askTimeout;

    public AkkaClientTelemetryRouter(
        IRequiredActor<ClientTelemetryRegion> region,
        TimeSpan askTimeout)
    {
        _region = region;
        _askTimeout = askTimeout;
    }

    public async Task<TelemetryMessageResult> RecordAsync(
        RecordTelemetrySnapshot message,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<TelemetryMessageResult>(message, _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClientTelemetryState> GetSnapshotAsync(
        ClientKey client,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<ClientTelemetryState>(new GetClientTelemetry(client), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClientTelemetryReadModelSnapshot> GetReadModelAsync(CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<ClientTelemetryReadModelSnapshot>(
                new GetClientTelemetryReadModel(),
                _askTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClientTelemetryRouteStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<ClientTelemetryRouteStatus>(new ProbeClientTelemetryRoute(), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class AkkaClientPresenceRouter : IClientPresenceRouter
{
    private readonly IRequiredActor<ClientPresenceRegion> _region;
    private readonly TimeSpan _askTimeout;

    public AkkaClientPresenceRouter(
        IRequiredActor<ClientPresenceRegion> region,
        TimeSpan askTimeout)
    {
        _region = region;
        _askTimeout = askTimeout;
    }

    public async Task<GatewayPresenceSessionStarted> StartSessionAsync(
        StartGatewayPresenceSession message,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<GatewayPresenceSessionStarted>(message, _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PresenceMessageResult> RecordHeartbeatAsync(
        RecordGatewayHeartbeat message,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<PresenceMessageResult>(message, _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PresenceMessageResult> EndSessionAsync(
        EndGatewayPresenceSession message,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<PresenceMessageResult>(message, _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClientPresenceSnapshot> GetSnapshotAsync(
        ClientKey client,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<ClientPresenceSnapshot>(new GetClientPresence(client), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<ClientPresenceRouteStatus>(new ProbeClientPresenceRoute(), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class AkkaClientPresenceReadModel : IClientPresenceReadModel
{
    private readonly IRequiredActor<ClientPresenceReadModelRegion> _region;
    private readonly TimeSpan _askTimeout;

    public AkkaClientPresenceReadModel(
        IRequiredActor<ClientPresenceReadModelRegion> region,
        TimeSpan askTimeout)
    {
        _region = region;
        _askTimeout = askTimeout;
    }

    public async Task<ClientPresenceReadModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<ClientPresenceReadModelSnapshot>(
                new GetClientPresenceReadModel(),
                _askTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
