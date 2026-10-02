using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.RemoteSupport;
using NetRatel.Application.Services;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts.RemoteSupport;
using NetRatel.Tests.Akka;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(NetRatel.Tests.Akka.NetRatelAkkaTelemetryCollection.Name)]
public sealed class RealtimeRegistrationAndScopeAuditTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public async Task Runtime_StartsActorsAndOneHostedFanoutBridgeForItsSinkAndSnapshotInterfaces()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<ICommandPersistenceStore>(new TestCommandPersistenceStore());
                services.AddSingleton<IJobObservationStore>(new StartupJobPersistenceStore());
                services.AddSingleton<IRemoteSupportLifecycleStore>(new StartupRemoteSupportLifecycleStore());
                services.AddSingleton<IClientServicesStore>(new MemoryServicesStore());
                services.AddSingleton<IClientConnectionEpochStore>(new MemoryConnectionEpochStore());
                // Monitoring stores own scoped operations and stay dormant in
                // this actor-registration fixture; no database operation runs.
                NetRatel.Infrastructure.Persistence.MonitoringPersistenceRegistration.AddNetRatelMonitoringPersistence(services);
                services.AddNetRatelAkkaRuntime(configuration);
            })
            .Build();

        await host.StartAsync();
        try
        {
            var provider = host.Services;
            var bridge = provider.GetRequiredService<RealtimeFanoutBridge>();
            var hostedServices = provider.GetServices<IHostedService>().ToArray();

            provider.GetRequiredService<IRealtimeFanoutSink>().Should().BeSameAs(bridge);
            provider.GetRequiredService<IRealtimeFanoutSnapshotSource>().Should().BeSameAs(bridge);
            hostedServices.Should().ContainSingle(service => ReferenceEquals(service, bridge));
            hostedServices.Distinct().Should().HaveCount(hostedServices.Length);
            hostedServices.Should().HaveCountGreaterThanOrEqualTo(6);
            hostedServices.Should().ContainSingle(service => service.GetType().Name == "OperationsLogFanoutBridge");
            hostedServices.Should().ContainSingle(service => service.GetType().Name == "GatewayTerminalBrowserAttachmentExpiryService");
            hostedServices.Should().ContainSingle(service => service.GetType().Name == "ProductionMcpTerminalExpiryService");
            hostedServices.Should().ContainSingle(service => service.GetType().Name == "TelemetryInteractiveDemandHostedService");
            provider.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();

            var presence = await provider.GetRequiredService<IClientPresenceRouter>()
                .ProbeAsync(CancellationToken.None);
            presence.Mode.Should().Be("akka");

            var telemetry = await provider.GetRequiredService<IClientTelemetryRouter>()
                .ProbeAsync(CancellationToken.None);
            telemetry.Authority.Should().Be("akka");

            var servicesState = await provider.GetRequiredService<IClientServicesRouter>()
                .GetSnapshotAsync(new ClientKey(1, Guid.NewGuid()), CancellationToken.None);
            servicesState.LastCompleteInventory.Should().BeNull();

            var commands = await provider.GetRequiredService<IClientCommandRouter>()
                .ProbeAsync(CancellationToken.None);
            commands.Authority.Should().Be("akka");

            var jobs = await provider.GetRequiredService<IJobRuntimeRouter>()
                .ProbeAsync(CancellationToken.None);
            jobs.Authority.Should().Be("akka");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void SignalRHub_IsMappedAtTheStableAuthorityPath()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.Services.AddSignalR();
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("RealtimeAccess", policy => policy.RequireAuthenticatedUser()));

        using var app = builder.Build();
        app.MapSignalRAuthorityEndpoints();

        Routes(app).Should().Contain(RealtimeEndpointRegistrationExtensions.AuthorityHubPath);
    }

    [Fact]
    public void ForbiddenSurfacesAndProductionUiRemainOutsideTheFanout()
    {
        var webSources = Directory.EnumerateFiles(
                Path.Combine(RepoRoot, "src", "NetRatel", "NetRatel.Web"),
                "*",
                SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) ||
                           path.EndsWith(".razor", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        webSources.Should().OnlyContain(source =>
            !source.Contains("RealtimeHub", StringComparison.Ordinal) &&
            !source.Contains("/hubs/akka-shadow", StringComparison.Ordinal) &&
            !source.Contains("SignalRShadow", StringComparison.Ordinal));

        Directory.Exists(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Shared/module_bindings"))
            .Should().BeFalse();
        File.Exists(Path.Combine(RepoRoot, "NetRatel.Server/StdbModule.csproj"))
            .Should().BeFalse();
        Read("src/NetRatel/NetRatel.AgentGateway.Contracts/Protos/agent_gateway.proto").Should().NotContain("SignalRShadow");
        Read("src/NetRatel/NetRatel.Akka/NetRatel.Akka.csproj").Should().Contain("Akka.Cluster.Hosting");
        Read("src/NetRatel/NetRatel.Shared/NetRatel.Shared.csproj").Should().NotContain("SpacetimeDB");
    }

    private static IEnumerable<string?> Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(static dataSource => dataSource.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(static endpoint => endpoint.RoutePattern.RawText);

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private sealed class StartupJobPersistenceStore : IJobObservationStore
    {
        public Task<JobObservationWriteResult> RecordAsync(
            IJobObservation observation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This registration fixture does not record jobs.");

        public Task<IReadOnlyList<PersistedJobObservation>> ReplayAsync(
            ulong jobRunId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersistedJobObservation>>([]);

        public Task<JobObservationDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new JobObservationDiagnostics(
                0,
                0,
                0,
                0,
                0,
                null,
                null,
                "akka",
                "akka"));

        public void RecordRecoverySucceeded()
        {
        }
    }

    private sealed class StartupRemoteSupportLifecycleStore : IRemoteSupportLifecycleStore
    {
        public Task<RemoteSupportLifecycleOpenResult> OpenAsync(
            RemoteSupportOpenSessionCommand command,
            Guid remoteSupportSessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This registration fixture does not open sessions.");

        public Task<RemoteSupportSessionSnapshot?> LoadAsync(
            RemoteSupportSessionKey session,
            CancellationToken cancellationToken) =>
            Task.FromResult<RemoteSupportSessionSnapshot?>(null);

        public Task<RemoteSupportLifecycleTransitionResult> TransitionAsync(
            RemoteSupportLifecycleTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This registration fixture does not transition sessions.");

        public Task<IReadOnlyList<RemoteSupportAuditEvent>> ReadAuditAsync(
            RemoteSupportSessionKey session,
            long afterAuditSequence,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteSupportAuditEvent>>([]);
    }
}
