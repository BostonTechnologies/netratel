using FluentAssertions;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class RemoteSupportReplicaSafeEdgeTests
{
    [Fact]
    public void ClusteredRuntime_RequiresDeploymentManagedSeedNodes()
    {
        var options = new NetRatelAkkaOptions
        {
            Cluster = new NetRatelAkkaClusterOptions { Port = 2551 }
        };

        var result = new NetRatelAkkaOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("SeedNodes");
    }

    [Fact]
    public void ClusteredRuntime_RequiresSeedAddressesForTheConfiguredActorSystem()
    {
        var options = new NetRatelAkkaOptions
        {
            Cluster = new NetRatelAkkaClusterOptions
            {
                Port = 2551,
                SeedNodes = ["akka.tcp://OtherSystem@127.0.0.1:2552"]
            }
        };

        var result = new NetRatelAkkaOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("NetRatel");
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

    [Fact]
    public void NormalRuntime_ValidatesItsRenewalTuningRange()
    {
        var options = new NetRatelAkkaOptions { RemoteSupportAgentEdgeRenewalSeconds = 0 };
        var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options,
            new System.ComponentModel.DataAnnotations.ValidationContext(options),
            validationResults,
            validateAllProperties: true);

        valid.Should().BeFalse();
        validationResults.Should().Contain(result =>
            result.MemberNames.Contains(nameof(NetRatelAkkaOptions.RemoteSupportAgentEdgeRenewalSeconds)));
    }
}
