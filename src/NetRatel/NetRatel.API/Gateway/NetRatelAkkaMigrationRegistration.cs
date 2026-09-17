using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetRatel.API.Realtime.Shadow;
using NetRatel.API.Realtime.Operations;
using NetRatel.API.Realtime;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Terminal;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.Akka.Observability;
using NetRatel.Application.Commands;

namespace NetRatel.API.Gateway;

public static class NetRatelAkkaMigrationRegistration
{
    public static IServiceCollection AddNetRatelAkkaMigration(
        this IServiceCollection services,
        IConfiguration configuration) =>
        AddNetRatelAkkaMigration(services, configuration, environment: null);

    public static IServiceCollection AddNetRatelAkkaMigration(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(NetRatelAkkaMigrationOptions.SectionName);
        var optionsBuilder = services.AddOptions<NetRatelAkkaMigrationOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<NetRatelAkkaMigrationOptions>,
            NetRatelAkkaMigrationOptionsValidator>());

        var options = section.Get<NetRatelAkkaMigrationOptions>() ?? new NetRatelAkkaMigrationOptions();
        services.AddSingleton(options);
        NetRatelAkkaTelemetry.ConfigureAuthorityPaths(
            presenceActive: options.IsPresenceAuthorityActive,
            telemetryActive: options.IsTelemetryAuthorityActive,
            fileBrowseActive: options.IsFileBrowseAuthorityActive,
            commandsActive: options.IsCommandAuthorityActive,
            jobsActive: options.IsJobAuthorityActive,
            remoteSupportActive: options.IsRemoteSupportAuthorityActive,
            terminalActive: options.IsTerminalAuthorityActive,
            signalRActive: options.IsSignalRAuthorityActive,
            environment: environment?.EnvironmentName ?? "unknown");
        services.AddHealthChecks().AddCheck<AkkaAuthorityModeHealthCheck>(
            "akka-authority-mode",
            tags: ["akka-migration", "authority"]);
        var signalRLocalCanaryActive = options.Enabled && options.SignalRShadowEnabled &&
            options.SignalRShadowLocalCanaryEnabled && environment?.IsDevelopment() == true;
        var operationsLogHubActive = options.IsLogAuthorityActive;

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SignalRShadowSubscriptionRegistry>();
        services.AddHealthChecks().AddCheck<SignalRShadowHealthCheck>(
            "akka-signalr-shadow",
        tags: options.IsSignalRAuthorityActive ? ["akka-authority"] : ["akka-shadow", "local-canary"]);

        if (!signalRLocalCanaryActive)
        {
            services.TryAddSingleton<IShadowFanoutSink>(NullShadowFanoutSink.Instance);
        }

        if (!options.Enabled || !options.TerminalShadowEnabled)
        {
            services.TryAddSingleton<ITerminalShadowObservationSink>(
                NullTerminalShadowObservationSink.Instance);
        }

        if (!options.Enabled)
        {
            services.TryAddScoped<IAkkaJobAuthorityService, UnavailableAkkaJobAuthorityService>();
            services.TryAddSingleton<IAgentCommandAuthorityDispatcher, UnavailableAgentCommandAuthorityDispatcher>();
            return services;
        }

        if (options.PresenceEnabled || options.TelemetryShadowEnabled || options.CommandShadowEnabled ||
            options.JobShadowEnabled || options.TerminalShadowEnabled || options.IsRemoteSupportV2LifecycleAuthorityActive)
        {
            services.AddNetRatelMigrationActors(options);
        }

        if (options.GatewayEnabled)
        {
            services.AddGrpc(grpc =>
            {
                grpc.EnableDetailedErrors = false;
                grpc.MaxReceiveMessageSize = options.MaxInboundMessageBytes;
                grpc.MaxSendMessageSize = options.MaxOutboundMessageBytes;
            });
        }

        if (options.IsTelemetryAuthorityActive)
        {
            services.Configure<TelemetryInteractiveOptions>(configuration.GetSection(TelemetryInteractiveOptions.SectionName));
            services.TryAddSingleton<TelemetryInteractiveDemandRegistry>();
            services.TryAddSingleton<ITelemetryInteractiveDemandRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<TelemetryInteractiveDemandRegistry>());
            // TryAddEnumerable requires a concrete implementation type. Registering
            // a factory as IHostedService is rejected at startup, and registering the
            // registry type directly would create a second registry instance. The
            // adapter preserves the one demand-policy state used by the gateway.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TelemetryInteractiveDemandHostedService>());
            services.TryAddSingleton<GatewayTelemetryLiveRegistry>();
            services.TryAddSingleton<IGatewayTelemetryLiveRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<GatewayTelemetryLiveRegistry>());
            services.TryAddSingleton<AgentTelemetryGatewaySessionRegistry>();
            services.TryAddSingleton<IAgentTelemetryGatewaySessionRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentTelemetryGatewaySessionRegistry>());
        }

        if (options.ControlGatewayEnabled)
        {
            services.TryAddSingleton<AgentControlSessionRegistry>();
            services.TryAddSingleton<IAgentControlSessionRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentControlSessionRegistry>());
        }

        if (options.FileGatewayEnabled)
        {
            services.TryAddSingleton<AgentFileGatewaySessionRegistry>();
            services.TryAddSingleton<IAgentFileGatewaySessionRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentFileGatewaySessionRegistry>());
        }

        if (options.LogGatewayEnabled)
        {
            services.TryAddSingleton<AgentLogGatewaySessionRegistry>();
            services.TryAddSingleton<IAgentLogGatewaySessionRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentLogGatewaySessionRegistry>());
            services.TryAddSingleton<IAgentLogGatewayQueryDispatcher, AgentLogGatewayQueryDispatcher>();
            var adminGroupId = configuration["Authorization:Oidc:AdminGroupId"]
                               ?? configuration["Authorization:Azure:AdminGroupId"]
                               ?? configuration["AzureAd:AdminGroupId"]
                               ?? configuration["Jwt:AdminGroupId"];
            services.TryAddSingleton<IOperationsLogTenantAuthorizer>(_ => new OperationsLogTenantAuthorizer(adminGroupId));
        }

        if (operationsLogHubActive)
        {
            services.TryAddSingleton<OperationsLogSubscriptionRegistry>();
        }

        if (options.CommandShadowEnabled)
        {
            services.TryAddSingleton<AgentCommandGatewaySessionRegistry>();
            services.TryAddSingleton<IAgentCommandGatewaySessionRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentCommandGatewaySessionRegistry>());
            services.TryAddSingleton<IAgentCommandAuthorityDispatcher, AgentCommandAuthorityDispatcher>();
        }
        else
        {
            services.TryAddSingleton<IAgentCommandAuthorityDispatcher, UnavailableAgentCommandAuthorityDispatcher>();
        }

        if (options.JobShadowEnabled)
        {
            services.TryAddSingleton<AgentJobGatewaySessionRegistry>();
            services.TryAddSingleton<IAgentJobGatewaySessionRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentJobGatewaySessionRegistry>());
        }

        if (options.IsJobAuthorityActive)
        {
            services.TryAddScoped<IAkkaJobAuthorityService, AkkaJobAuthorityService>();
        }
        else
        {
            services.TryAddScoped<IAkkaJobAuthorityService, UnavailableAkkaJobAuthorityService>();
        }

        if (options.IsLegacyRemoteSupportGatewayActive)
        {
            services.TryAddSingleton<GatewayRemoteSupportSessionRegistry>();
            services.TryAddSingleton<IGatewayRemoteSupportSessionRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<GatewayRemoteSupportSessionRegistry>());
        }

        if (options.IsRemoteSupportV2InventoryActive)
        {
            services.TryAddSingleton<RemoteSupportV2PreparationRegistry>();
            services.TryAddSingleton<IRemoteSupportV2PreparationRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<RemoteSupportV2PreparationRegistry>());
        }

        if (options.IsRemoteSupportV2ReplicaSafeEdgeActive)
        {
            services.TryAddSingleton<RemoteSupportV2AgentEdgeRegistry>();
            services.TryAddSingleton<IRemoteSupportV2AgentEdgeRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<RemoteSupportV2AgentEdgeRegistry>());
        }

        if (options.TerminalGatewayEnabled)
        {
            services.TryAddSingleton<TerminalTimingRecorder>();
            services.TryAddSingleton<AgentTerminalSessionRegistry>();
            services.TryAddSingleton<IAgentTerminalSessionRegistry>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentTerminalSessionRegistry>());
        }

        if (signalRLocalCanaryActive || operationsLogHubActive)
        {
            services.AddSignalR(signalR =>
            {
                signalR.EnableDetailedErrors = false;
                signalR.MaximumReceiveMessageSize = 8 * 1024;
                signalR.StreamBufferCapacity = 4;
                signalR.MaximumParallelInvocationsPerClient = 1;
            });
            if (signalRLocalCanaryActive)
            {
                var adminGroupId = configuration["Authorization:Oidc:AdminGroupId"]
                                   ?? configuration["Authorization:Azure:AdminGroupId"]
                                   ?? configuration["AzureAd:AdminGroupId"]
                                   ?? configuration["Jwt:AdminGroupId"];
                services.AddSingleton<IShadowTenantAuthorizer>(_ => new ShadowTenantClaimAuthorizer(adminGroupId));
                services.AddSingleton<SignalRShadowFanoutBridge>();
                services.AddSingleton<IShadowFanoutSink>(serviceProvider =>
                    serviceProvider.GetRequiredService<SignalRShadowFanoutBridge>());
                services.AddSingleton<IShadowFanoutSnapshotSource>(serviceProvider =>
                    serviceProvider.GetRequiredService<SignalRShadowFanoutBridge>());
                services.AddSingleton<IHostedService>(serviceProvider =>
                    serviceProvider.GetRequiredService<SignalRShadowFanoutBridge>());
            }

            if (operationsLogHubActive)
            {
                services.AddSingleton<OperationsLogFanoutBridge>();
                services.AddSingleton<IHostedService>(serviceProvider =>
                    serviceProvider.GetRequiredService<OperationsLogFanoutBridge>());
            }
        }

        if (options.PresenceEnabled)
        {
            services.AddHealthChecks().AddCheck<AgentGatewayHealthCheck>(
                "akka-presence-shadow",
                tags: options.IsPresenceAuthorityActive
                    ? ["ready", "akka-authority", "dev-canary"]
                    : ["ready", "akka-shadow"]);
        }

        if (options.TelemetryShadowEnabled)
        {
            services.AddHealthChecks().AddCheck<TelemetryShadowHealthCheck>(
                "akka-telemetry-shadow",
                tags: ["akka-shadow"]);
        }

        if (options.CommandShadowEnabled)
        {
            services.AddHealthChecks().AddCheck<CommandShadowHealthCheck>(
                "akka-command-shadow",
                tags: ["akka-shadow"]);
        }

        if (options.CommandPersistenceEnabled)
        {
            services.AddHealthChecks().AddCheck<CommandPersistenceHealthCheck>(
                "akka-command-persistence-shadow",
                tags: ["akka-shadow", "persistence"]);
        }

        if (options.JobShadowEnabled)
        {
            services.AddSingleton<JobShadowObservationQueue>();
            services.AddSingleton<IJobShadowObservationSink>(serviceProvider =>
                serviceProvider.GetRequiredService<JobShadowObservationQueue>());
            services.AddSingleton<IHostedService>(serviceProvider =>
                serviceProvider.GetRequiredService<JobShadowObservationQueue>());
            services.AddHealthChecks().AddCheck<JobShadowHealthCheck>(
                "akka-job-shadow",
                tags: ["akka-shadow", "persistence"]);
        }

        if (options.TerminalShadowEnabled)
        {
            services.AddSingleton<TerminalShadowObservationQueue>();
            services.AddSingleton<ITerminalShadowObservationSink>(serviceProvider =>
                serviceProvider.GetRequiredService<TerminalShadowObservationQueue>());
            services.AddSingleton<IHostedService>(serviceProvider =>
                serviceProvider.GetRequiredService<TerminalShadowObservationQueue>());
            services.AddHealthChecks().AddCheck<TerminalShadowHealthCheck>(
                "akka-terminal-shadow",
                tags: ["akka-shadow"]);
        }

        return services;
    }

    public static IEndpointRouteBuilder MapAgentGatewayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<NetRatelAkkaMigrationOptions>();
        if (!options.Enabled || !options.GatewayEnabled)
        {
            return endpoints;
        }

        if (options.PresenceEnabled)
        {
            endpoints.MapGrpcService<AgentGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.TelemetryShadowEnabled)
        {
            endpoints.MapGrpcService<AgentTelemetryGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.IsTelemetryAuthorityActive)
        {
            endpoints.MapGrpcService<AgentTelemetryGatewayV2Service>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.CommandShadowEnabled)
        {
            endpoints.MapGrpcService<AgentCommandShadowGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.IsCommandAuthorityActive)
        {
            endpoints.MapGrpcService<AgentCommandGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.IsJobAuthorityActive)
        {
            endpoints.MapGrpcService<AgentJobGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.ControlGatewayEnabled)
        {
            endpoints.MapGrpcService<AgentControlGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.IsFileBrowseAuthorityActive)
        {
            endpoints.MapGrpcService<AgentFileGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.IsLogAuthorityActive)
        {
            endpoints.MapGrpcService<AgentLogGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.IsLegacyRemoteSupportGatewayActive || options.IsRemoteSupportV2ReplicaSafeEdgeActive)
        {
            endpoints.MapGrpcService<AgentRemoteSupportGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.IsRemoteSupportV2InventoryActive)
        {
            endpoints.MapGrpcService<AgentRemoteSupportPreparationGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        if (options.IsTerminalAuthorityActive)
        {
            endpoints.MapGrpcService<AgentTerminalGatewayService>()
                .RequireAuthorization("AgentGatewayAccess");
        }

        return endpoints;
    }
}

internal sealed class TelemetryInteractiveDemandHostedService(
    TelemetryInteractiveDemandRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => registry.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => registry.StopAsync(cancellationToken);
}
