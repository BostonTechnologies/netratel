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
using NetRatel.Akka.Terminals;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.RemoteSupport;
using NetRatel.Application.Telemetry;
using NetRatel.Application.Terminals;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Akka.Hosting;

/// <summary>Marker used for type-safe access to the local client region.</summary>
public sealed class ClientPresenceRegion;

/// <summary>Marker used for type-safe access to the local gateway presence read model.</summary>
public sealed class ClientPresenceReadModelRegion;

/// <summary>Marker used for type-safe access to the local telemetry region.</summary>
public sealed class ClientTelemetryRegion;

/// <summary>Marker used for type-safe access to the local command shadow region.</summary>
public sealed class ClientCommandRegion;

/// <summary>Marker used for type-safe access to the local job shadow region.</summary>
public sealed class JobShadowRegion;

/// <summary>Marker used for type-safe access to the local terminal shadow region.</summary>
public sealed class ClientTerminalRegion;

/// <summary>Marker used for type-safe access to the V2 remote-support authority router.</summary>
public sealed class RemoteSupportSessionAuthorityRegion;

public static class ClientPresenceActorRegistration
{
    public static IServiceCollection AddNetRatelMigrationActors(
        this IServiceCollection services,
        NetRatelAkkaMigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddAkka(options.ActorSystemName, (akka, serviceProvider) =>
        {
            var commandPersistence = options.CommandPersistenceEnabled
                ? serviceProvider.GetRequiredService<ICommandPersistenceStore>()
                : null;
            var jobShadowPersistence = options.JobShadowEnabled
                ? serviceProvider.GetRequiredService<IJobShadowPersistenceStore>()
                : null;
            var remoteSupportLifecycle = options.IsRemoteSupportV2LifecycleAuthorityActive
                ? serviceProvider.GetRequiredService<IRemoteSupportLifecycleStore>()
                : null;
            akka
                .WithActorSystemLivenessCheck();

            if (options.IsRemoteSupportV2ReplicaSafeEdgeActive)
            {
                var cluster = options.RemoteSupportV2Cluster;
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
                    if (options.PresenceEnabled)
                    {
                        var presenceReadModel = system.ActorOf(
                            PresenceReadModelActor.Props(),
                            "client-presence-read-model");
                        registry.Register<ClientPresenceReadModelRegion>(presenceReadModel);
                        var presenceRegion = system.ActorOf(
                            ClientPresenceRouterActor.Props(options, presenceReadModel),
                            "client-presence");
                        registry.Register<ClientPresenceRegion>(presenceRegion);
                    }

                    if (options.TelemetryShadowEnabled)
                    {
                        var telemetryRegion = system.ActorOf(
                            ClientTelemetryRouterActor.Props(),
                            "client-telemetry");
                        registry.Register<ClientTelemetryRegion>(telemetryRegion);
                    }

                    if (options.CommandShadowEnabled)
                    {
                        var commandRegion = system.ActorOf(
                            commandPersistence is null
                                ? ClientCommandRouterActor.Props()
                                : ClientCommandRouterActor.Props(commandPersistence),
                            "client-commands");
                        registry.Register<ClientCommandRegion>(commandRegion);
                    }

                    if (options.JobShadowEnabled)
                    {
                        var jobRegion = system.ActorOf(
                            JobCoordinatorActor.Props(jobShadowPersistence!),
                            "job-shadow");
                        registry.Register<JobShadowRegion>(jobRegion);
                    }

                    if (options.TerminalShadowEnabled)
                    {
                        var terminalRegion = system.ActorOf(
                            ClientTerminalRouterActor.Props(),
                            "terminal-shadow");
                        registry.Register<ClientTerminalRegion>(terminalRegion);
                    }

                    if (options.IsRemoteSupportV2LifecycleAuthorityActive)
                    {
                        var shardRegion = options.IsRemoteSupportV2ReplicaSafeEdgeActive
                            ? ClusterSharding.Get(system).Start(
                                RemoteSupportSessionMessageExtractor.RegionName,
                                RemoteSupportSessionActor.Props(remoteSupportLifecycle!),
                                ClusterShardingSettings.Create(system),
                                new RemoteSupportSessionMessageExtractor())
                            : null;
                        var remoteSupportRegion = system.ActorOf(
                            shardRegion is null
                                ? RemoteSupportSessionRouterActor.Props(remoteSupportLifecycle!)
                                : RemoteSupportSessionRouterActor.Props(remoteSupportLifecycle!, shardRegion),
                            "remote-support-v2-lifecycle");
                        registry.Register<RemoteSupportSessionAuthorityRegion>(remoteSupportRegion);
                    }

                });
        });

        if (options.PresenceEnabled)
        {
            services.AddSingleton<IClientPresenceRouter>(serviceProvider =>
                new AkkaClientPresenceRouter(
                    serviceProvider.GetRequiredService<IRequiredActor<ClientPresenceRegion>>(),
                    options.AskTimeout));
            services.AddSingleton<IClientPresenceReadModel>(serviceProvider =>
                new AkkaClientPresenceReadModel(
                    serviceProvider.GetRequiredService<IRequiredActor<ClientPresenceReadModelRegion>>(),
                    options.AskTimeout));
        }

        if (options.TelemetryShadowEnabled)
        {
            services.AddSingleton<IClientTelemetryRouter>(serviceProvider =>
                new AkkaClientTelemetryRouter(
                    serviceProvider.GetRequiredService<IRequiredActor<ClientTelemetryRegion>>(),
                    options.AskTimeout));
        }

        if (options.CommandShadowEnabled)
        {
            services.AddSingleton<IClientCommandRouter>(serviceProvider =>
                new AkkaClientCommandRouter(
                    serviceProvider.GetRequiredService<IRequiredActor<ClientCommandRegion>>(),
                    options.AskTimeout));
        }

        if (options.JobShadowEnabled)
        {
            services.AddSingleton<IJobShadowRouter>(serviceProvider =>
                new AkkaJobShadowRouter(
                    serviceProvider.GetRequiredService<IRequiredActor<JobShadowRegion>>(),
                    options.AskTimeout));
        }

        if (options.TerminalShadowEnabled)
        {
            services.AddSingleton<ITerminalShadowRouter>(serviceProvider =>
                new AkkaTerminalShadowRouter(
                    serviceProvider.GetRequiredService<IRequiredActor<ClientTerminalRegion>>(),
                    options.AskTimeout));
        }

        if (options.IsRemoteSupportV2LifecycleAuthorityActive)
        {
            services.AddSingleton<IRemoteSupportLifecycleRouter>(serviceProvider =>
                new AkkaRemoteSupportLifecycleRouter(
                    serviceProvider.GetRequiredService<IRequiredActor<RemoteSupportSessionAuthorityRegion>>(),
                    options.AskTimeout));
        }

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

internal sealed class AkkaTerminalShadowRouter : ITerminalShadowRouter
{
    private readonly IRequiredActor<ClientTerminalRegion> _region;
    private readonly TimeSpan _askTimeout;

    public AkkaTerminalShadowRouter(
        IRequiredActor<ClientTerminalRegion> region,
        TimeSpan askTimeout)
    {
        _region = region;
        _askTimeout = askTimeout;
    }

    public async Task<TerminalShadowMessageResult> RecordAsync(
        RecordTerminalShadowEvent message,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<TerminalShadowMessageResult>(message, _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TerminalShadowState> GetStateAsync(
        TerminalShadowSessionKey session,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<TerminalShadowState>(new GetTerminalShadowState(session), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TerminalShadowRouteStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<TerminalShadowRouteStatus>(new ProbeTerminalShadowRoute(), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class AkkaJobShadowRouter : IJobShadowRouter
{
    private readonly IRequiredActor<JobShadowRegion> _region;
    private readonly TimeSpan _askTimeout;

    public AkkaJobShadowRouter(
        IRequiredActor<JobShadowRegion> region,
        TimeSpan askTimeout)
    {
        _region = region;
        _askTimeout = askTimeout;
    }

    public async Task<JobShadowMessageResult> RecordAsync(
        RecordJobShadowObservation message,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<JobShadowMessageResult>(message, _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JobRunShadowState> GetStateAsync(
        ulong jobRunId,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<JobRunShadowState>(new GetJobShadowState(jobRunId), _askTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JobShadowRouteStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<JobShadowRouteStatus>(new ProbeJobShadowRoute(), _askTimeout, cancellationToken)
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

    public async Task<CommandShadowState> GetStateAsync(
        CommandKey command,
        CancellationToken cancellationToken)
    {
        var region = await _region.GetAsync(cancellationToken).ConfigureAwait(false);
        return await region.Ask<CommandShadowState>(new GetCommandShadowState(command), _askTimeout, cancellationToken)
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
