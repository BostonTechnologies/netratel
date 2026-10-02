using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Grpc.AspNetCore.Server;
using NetRatel.API.Realtime;
using NetRatel.API.Realtime.Operations;
using NetRatel.API.Services;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Terminal;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.Akka.Observability;
using NetRatel.Application.Commands;
using NetRatel.Application.Services;

namespace NetRatel.API.Gateway;

public static class NetRatelAkkaRuntimeRegistration
{
    public static IServiceCollection AddNetRatelAkkaRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<NetRatelAkkaOptions>()
            .Bind(configuration.GetSection(NetRatelAkkaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<NetRatelAkkaOptions>,
            NetRatelAkkaOptionsValidator>());
        services.TryAddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<NetRatelAkkaOptions>>().Value);

        services.TryAddSingleton(TimeProvider.System);
        services.AddNetRatelAkkaActors(
            configuration[$"{NetRatelAkkaOptions.SectionName}:ActorSystemName"] ?? "NetRatel");
        services.AddGrpc();
        services.AddOptions<GrpcServiceOptions>()
            .Configure<NetRatelAkkaOptions>((grpc, options) =>
        {
            grpc.EnableDetailedErrors = false;
            grpc.MaxReceiveMessageSize = options.MaxInboundMessageBytes;
            grpc.MaxSendMessageSize = options.MaxOutboundMessageBytes;
        });

        services.Configure<TelemetryInteractiveOptions>(
            configuration.GetSection(TelemetryInteractiveOptions.SectionName));
        services.TryAddSingleton<TelemetryInteractiveDemandRegistry>();
        services.TryAddSingleton<ITelemetryInteractiveDemandRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<TelemetryInteractiveDemandRegistry>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TelemetryInteractiveDemandHostedService>());
        services.TryAddSingleton<GatewayTelemetryLiveRegistry>();
        services.TryAddSingleton<IGatewayTelemetryLiveRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<GatewayTelemetryLiveRegistry>());
        services.TryAddSingleton<AgentTelemetryGatewaySessionRegistry>();
        services.TryAddSingleton<IAgentTelemetryGatewaySessionRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTelemetryGatewaySessionRegistry>());
        services.TryAddSingleton<IClientServiceWatchPolicySource, EmptyClientServiceWatchPolicySource>();
        services.TryAddSingleton<ClientServicesCoordinator>();

        services.TryAddSingleton<AgentControlSessionRegistry>();
        services.TryAddSingleton<IAgentControlSessionRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentControlSessionRegistry>());
        services.TryAddSingleton<AgentFileGatewaySessionRegistry>();
        services.TryAddSingleton<IAgentFileGatewaySessionRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentFileGatewaySessionRegistry>());
        services.TryAddSingleton<AgentLogGatewaySessionRegistry>();
        services.TryAddSingleton<IAgentLogGatewaySessionRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentLogGatewaySessionRegistry>());
        services.TryAddSingleton<IAgentLogGatewayQueryDispatcher, AgentLogGatewayQueryDispatcher>();

        services.TryAddSingleton<AgentCommandGatewaySessionRegistry>();
        services.TryAddSingleton<IAgentCommandGatewaySessionRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentCommandGatewaySessionRegistry>());
        services.TryAddSingleton<IAgentCommandAuthorityDispatcher, AgentCommandAuthorityDispatcher>();
        services.TryAddScoped<IAkkaJobAuthorityService, AkkaJobAuthorityService>();
        services.TryAddSingleton<AgentJobGatewaySessionRegistry>();
        services.TryAddSingleton<IAgentJobGatewaySessionRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentJobGatewaySessionRegistry>());

        services.TryAddSingleton<RemoteSupportV2PreparationRegistry>();
        services.TryAddSingleton<IRemoteSupportV2PreparationRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<RemoteSupportV2PreparationRegistry>());
        services.TryAddSingleton<RemoteSupportV2AgentEdgeRegistry>();
        services.TryAddSingleton<IRemoteSupportV2AgentEdgeRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<RemoteSupportV2AgentEdgeRegistry>());

        services.TryAddSingleton<TerminalTimingRecorder>();
        services.TryAddSingleton<AgentTerminalSessionRegistry>();
        services.TryAddSingleton<IAgentTerminalSessionRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTerminalSessionRegistry>());
        services.TryAddSingleton<GatewayTerminalBrowserAttachmentLeaseRegistry>();
        services.AddHostedService<GatewayTerminalBrowserAttachmentExpiryService>();
        services.AddHostedService<ProductionMcpTerminalExpiryService>();

        services.TryAddScoped<IOperationsLogTenantAuthorizer, OperationsLogTenantAccessAuthorizer>();
        services.TryAddSingleton<OperationsLogSubscriptionRegistry>();
        services.AddSignalR(signalR =>
        {
            signalR.EnableDetailedErrors = false;
            signalR.MaximumReceiveMessageSize = 8 * 1024;
            signalR.StreamBufferCapacity = 4;
            signalR.MaximumParallelInvocationsPerClient = 1;
        });
        services.TryAddScoped<IRealtimeTenantAuthorizer, RealtimeTenantAccessAuthorizer>();
        services.TryAddSingleton<RealtimeSubscriptionRegistry>();
        services.TryAddSingleton<RealtimeFanoutBridge>();
        services.TryAddSingleton<IRealtimeFanoutSink>(serviceProvider =>
            serviceProvider.GetRequiredService<RealtimeFanoutBridge>());
        services.TryAddSingleton<IRealtimeFanoutSnapshotSource>(serviceProvider =>
            serviceProvider.GetRequiredService<RealtimeFanoutBridge>());
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<RealtimeFanoutBridge>());
        services.TryAddSingleton<OperationsLogFanoutBridge>();
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<OperationsLogFanoutBridge>());

        services.AddHealthChecks()
            .AddCheck<AgentGatewayHealthCheck>("akka-presence", tags: ["ready", "akka"])
            .AddCheck<OidcSigningKeyHealthCheck>("agent-auth-signing", tags: ["ready", "auth"])
            .AddCheck<TelemetryRuntimeHealthCheck>("akka-telemetry", tags: ["ready", "akka"])
            .AddCheck<CommandRuntimeHealthCheck>("akka-command", tags: ["ready", "akka"])
            .AddCheck<CommandLifecyclePersistenceHealthCheck>("akka-command-persistence", tags: ["ready", "akka", "persistence"])
            .AddCheck<JobRuntimeHealthCheck>("akka-job", tags: ["ready", "akka", "persistence"])
            .AddCheck<SignalRFanoutHealthCheck>("akka-signalr", tags: ["ready", "akka"]);

        return services;
    }

    public static IEndpointRouteBuilder MapAgentGatewayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGrpcService<AgentGatewayService>().RequireAuthorization("AgentGatewayAccess");
        endpoints.MapGrpcService<AgentTelemetryGatewayV2Service>().RequireAuthorization("AgentGatewayAccess");
        endpoints.MapGrpcService<AgentCommandGatewayService>().RequireAuthorization("AgentGatewayAccess");
        endpoints.MapGrpcService<AgentJobGatewayService>().RequireAuthorization("AgentGatewayAccess");
        endpoints.MapGrpcService<AgentControlGatewayService>().RequireAuthorization("AgentGatewayAccess");
        endpoints.MapGrpcService<AgentFileGatewayService>().RequireAuthorization("AgentGatewayAccess");
        endpoints.MapGrpcService<AgentLogGatewayService>().RequireAuthorization("AgentGatewayAccess");
        // The service carries the existing V2 lifecycle, preparation, and edge
        // RPCs together with compatible protocol frames used by existing agents.
        endpoints.MapGrpcService<AgentRemoteSupportGatewayService>().RequireAuthorization("AgentGatewayAccess");
        endpoints.MapGrpcService<AgentRemoteSupportPreparationGatewayService>().RequireAuthorization("AgentGatewayAccess");
        endpoints.MapGrpcService<AgentTerminalGatewayService>().RequireAuthorization("AgentGatewayAccess");

        return endpoints;
    }
}

internal sealed class TelemetryInteractiveDemandHostedService(
    TelemetryInteractiveDemandRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => registry.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => registry.StopAsync(cancellationToken);
}
