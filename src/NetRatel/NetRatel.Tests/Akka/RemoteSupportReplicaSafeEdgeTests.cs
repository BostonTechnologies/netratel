using FluentAssertions;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class RemoteSupportReplicaSafeEdgeTests
{
    [Fact]
    public void ReplicaSafeEdge_RequiresTheLifecycleAuthority()
    {
        var options = ReplicaSafeOptions();
        var result = new NetRatelAkkaMigrationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeTrue();

        options.RemoteSupportV2LifecycleAuthorityEnabled = false;
        result = new NetRatelAkkaMigrationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("RemoteSupportV2LifecycleAuthorityEnabled");
    }

    [Fact]
    public void MultiNodeReplicaSafeEdge_RequiresDeploymentManagedSeedNodes()
    {
        var options = ReplicaSafeOptions();
        options.RemoteSupportV2Cluster.AllowSingleNode = false;

        var result = new NetRatelAkkaMigrationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("SeedNodes");
    }

    [Fact]
    public void ReplicaSafeEdge_RequiresARealRenewalInterval()
    {
        var options = ReplicaSafeOptions();
        options.RemoteSupportV2AgentEdgeRenewalInterval = TimeSpan.Zero;

        var result = new NetRatelAkkaMigrationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("RemoteSupportV2AgentEdgeRenewalInterval");
    }

    [Fact]
    public void ShardExtractor_UsesOnlyTheStableLogicalSessionIdentity()
    {
        var session = new RemoteSupportSessionKey(7, Guid.NewGuid(), Guid.NewGuid());
        var envelope = new GetRemoteSupportSessionByKey(session, new RemoteSupportOperatorBinding("operator-a"));
        var extractor = new RemoteSupportSessionMessageExtractor();

        extractor.EntityId(envelope).Should().Be($"{session.TenantId}:{session.AgentId:N}:{session.RemoteSupportSessionId:N}");
        extractor.ShardId(envelope).Should().NotBeNullOrWhiteSpace();
        extractor.EntityMessage(envelope).Should().BeSameAs(envelope);
    }

    private static NetRatelAkkaMigrationOptions ReplicaSafeOptions() => new()
    {
        Enabled = true,
        PresenceEnabled = true,
        GatewayEnabled = true,
        RemoteSupportGatewayEnabled = true,
        PresenceAuthorityEnabled = true,
        RemoteSupportAuthorityEnabled = true,
        RemoteSupportV2LifecycleAuthorityEnabled = true,
        RemoteSupportV2ReplicaSafeEdgeEnabled = true
    };
}
