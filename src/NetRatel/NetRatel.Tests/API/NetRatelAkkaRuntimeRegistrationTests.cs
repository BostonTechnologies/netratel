using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.API.Services;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.RemoteSupport;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(NetRatel.Tests.Akka.NetRatelAkkaTelemetryCollection.Name)]
public sealed class NetRatelAkkaRuntimeRegistrationTests
{
    private static readonly string[] RetiredBooleanKeys =
    [
        "NetRatelAkkaMigration:Enabled",
        "NetRatelAkkaMigration:PresenceEnabled",
        "NetRatelAkkaMigration:GatewayEnabled",
        "NetRatelAkkaMigration:ClientUpdatesEnabled",
        "NetRatelAkkaMigration:ControlGatewayEnabled",
        "NetRatelAkkaMigration:FileGatewayEnabled",
        "NetRatelAkkaMigration:LogGatewayEnabled",
        "NetRatelAkkaMigration:RemoteSupportGatewayEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2InventoryEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2LifecycleAuthorityEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2ReplicaSafeEdgeEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2MediaEnabled",
        "NetRatelAkkaMigration:RemoteSupportLegacyGatewayRollbackEnabled",
        "NetRatelAkkaMigration:PrimaryCardGatewayReadsEnabled",
        "NetRatelAkkaMigration:PrimaryCardGatewayActionsEnabled",
        "NetRatelAkkaMigration:TerminalGatewayEnabled",
        "NetRatelAkkaMigration:TerminalGatewayPrimaryCardEnabled",
        "NetRatelAkkaMigration:PresenceReadModelEnabled",
        "NetRatelAkkaMigration:TelemetryShadowEnabled",
        "NetRatelAkkaMigration:CommandShadowEnabled",
        "NetRatelAkkaMigration:CommandPersistenceEnabled",
        "NetRatelAkkaMigration:JobShadowEnabled",
        "NetRatelAkkaMigration:TerminalShadowEnabled",
        "NetRatelAkkaMigration:SignalRShadowEnabled",
        "NetRatelAkkaMigration:SignalRShadowLocalCanaryEnabled",
        "NetRatelAkkaMigration:PresenceAuthorityEnabled",
        "NetRatelAkkaMigration:PingAuthorityEnabled",
        "NetRatelAkkaMigration:TelemetryAuthorityEnabled",
        "NetRatelAkkaMigration:FileBrowseAuthorityEnabled",
        "NetRatelAkkaMigration:LogAuthorityEnabled",
        "NetRatelAkkaMigration:RemoteSupportAuthorityEnabled",
        "NetRatelAkkaMigration:CommandAuthorityEnabled",
        "NetRatelAkkaMigration:JobAuthorityEnabled",
        "NetRatelAkkaMigration:TerminalAuthorityEnabled",
        "NetRatelAkkaMigration:SignalRAuthorityEnabled",
        "NetRatelAkkaMigration:RemoteSupportShadowEnabled",
        "LegacyQueueWorker:Enabled"
    ];

    [Fact]
    public void AkkaOptions_ValidateOnlyNormalLocalOrClusterTopology()
    {
        var validator = new NetRatelAkkaOptionsValidator();

        validator.Validate(null, new NetRatelAkkaOptions()).Succeeded.Should().BeTrue();
        validator.Validate(null, new NetRatelAkkaOptions { Cluster = null! }).FailureMessage
            .Should().Contain("NetRatelAkka:Cluster");

        var clustered = new NetRatelAkkaOptions
        {
            Cluster = new NetRatelAkkaClusterOptions
            {
                Port = 2551,
                SeedNodes = ["akka.tcp://NetRatel@127.0.0.1:2552"]
            }
        };
        validator.Validate(null, clustered).Succeeded.Should().BeTrue();

        foreach (var seed in new[]
                 {
                     "akka.tcp://127.0.0.1:2552",
                     "akka.tcp://OtherSystem@127.0.0.1:2552",
                     "akka.tcp://NetRatel@127.0.0.1:2552/actor",
                     "akka.tcp://NetRatel@127.0.0.1:2552?role=remote"
                 })
        {
            clustered.Cluster.SeedNodes = [seed];
            validator.Validate(null, clustered).Failed.Should().BeTrue(seed);
        }
    }

    [Fact]
    public void RetiredMigrationKeys_AreInertWhenAbsentEnabledOrDisabled()
    {
        var absent = CaptureRuntimeRegistration(new Dictionary<string, string?>());
        var enabledConfiguration = RetiredBooleanKeys.ToDictionary(
            static key => key,
            static _ => (string?)"true",
            StringComparer.Ordinal);
        enabledConfiguration["NetRatelAkkaMigration:AuthorityMode"] = "Shadow";
        var enabled = CaptureRuntimeRegistration(enabledConfiguration);
        var disabledConfiguration = RetiredBooleanKeys.ToDictionary(
            static key => key,
            static _ => (string?)"false",
            StringComparer.Ordinal);
        disabledConfiguration["NetRatelAkkaMigration:AuthorityMode"] = "Authority";
        var disabled = CaptureRuntimeRegistration(disabledConfiguration);

        enabled.Should().BeEquivalentTo(absent);
        disabled.Should().BeEquivalentTo(absent);
        enabled.Options.ActorSystemName.Should().Be("NetRatel");
        enabled.Options.Port.Should().Be(0);
        enabled.RuntimeServices.Should().Contain(typeof(IClientPresenceRouter));
        enabled.RuntimeServices.Should().Contain(typeof(IClientPresenceReadModel));
        enabled.RuntimeServices.Should().Contain(typeof(IClientTelemetryRouter));
        enabled.RuntimeServices.Should().Contain(typeof(IClientCommandRouter));
        enabled.RuntimeServices.Should().Contain(typeof(IJobRuntimeRouter));
        enabled.RuntimeServices.Should().Contain(typeof(IRemoteSupportLifecycleRouter));
        enabled.RuntimeServices.Should().Contain(typeof(IAgentTerminalSessionRegistry));
        enabled.RuntimeServices.Should().Contain(typeof(IRealtimeFanoutSink));
        enabled.HostedServiceDescriptorCount.Should().BeGreaterThanOrEqualTo(6);
    }

    private static RuntimeRegistrationSnapshot CaptureRuntimeRegistration(
        IReadOnlyDictionary<string, string?> retiredConfiguration)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(retiredConfiguration)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetRatelAkkaRuntime(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NetRatelAkkaOptions>>().Value;
        var runtimeServices = services
            .Where(static descriptor => descriptor.ServiceType != typeof(IHostedService))
            .Select(static descriptor => descriptor.ServiceType)
            .Distinct()
            .Where(static serviceType => serviceType.Namespace is "NetRatel.Akka.Hosting" or "NetRatel.Application.Presence" or "NetRatel.Application.Telemetry" or "NetRatel.Application.Commands" or "NetRatel.Application.Jobs" or "NetRatel.Application.RemoteSupport" or "NetRatel.API.Gateway" or "NetRatel.API.Realtime")
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        var hostedCount = services.Count(static descriptor => descriptor.ServiceType == typeof(IHostedService));

        return new RuntimeRegistrationSnapshot(
            new OptionsSnapshot(
                options.ActorSystemName,
                options.GatewayGrpcPort,
                options.HeartbeatIntervalSeconds,
                options.MissedHeartbeatLimit,
                options.HeartbeatGraceSeconds,
                options.AskTimeoutSeconds,
                options.MaxInboundMessageBytes,
                options.MaxOutboundMessageBytes,
                options.MaxTelemetryScopesPerFrame,
                options.RemoteSupportAgentEdgeRenewalSeconds,
                options.Cluster.HostName,
                options.Cluster.Port,
                options.Cluster.Role,
                options.Cluster.SeedNodes.ToArray()),
            runtimeServices,
            hostedCount);
    }

    private sealed record RuntimeRegistrationSnapshot(
        OptionsSnapshot Options,
        Type[] RuntimeServices,
        int HostedServiceDescriptorCount);

    private sealed record OptionsSnapshot(
        string ActorSystemName,
        int GatewayGrpcPort,
        int HeartbeatIntervalSeconds,
        int MissedHeartbeatLimit,
        int HeartbeatGraceSeconds,
        int AskTimeoutSeconds,
        int MaxInboundMessageBytes,
        int MaxOutboundMessageBytes,
        int MaxTelemetryScopesPerFrame,
        int RemoteSupportAgentEdgeRenewalSeconds,
        string HostName,
        int Port,
        string Role,
        string[] SeedNodes);
}
