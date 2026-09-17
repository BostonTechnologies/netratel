using FluentAssertions;
using NetRatel.API.Services.AgentDirectory;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentDirectoryPresentationTests
{
    [Fact]
    public void Persistent_agent_directory_matches_host_operating_system_tenant_and_agent_id_when_offline()
    {
        var agentId = Guid.Parse("ad6e6ef2-b9a4-4d99-94d3-fae1d6b8e4de");
        var agent = AgentDirectoryPresentation.Create(
            7,
            agentId,
            null,
            true,
            """{"hostName":"oidc-01","os":"Linux","architecture":"x64"}""",
            "Example Organization");

        agent.DisplayName.Should().Be("oidc-01");
        agent.HostName.Should().Be("oidc-01");
        agent.OperatingSystem.Should().Be("Linux");
        agent.TenantId.Should().Be(7);
        agent.AgentId.Should().Be(agentId);
        agent.Matches("oidc").Should().BeTrue();
        agent.Matches("linux").Should().BeTrue();
        agent.Matches("example").Should().BeTrue();
        agent.Matches(agentId.ToString("D")).Should().BeTrue();
    }

    [Fact]
    public void Repeated_host_labels_remain_distinct_agent_directory_records()
    {
        const string device = """{"hostName":"docker04","os":"Linux","architecture":"x64"}""";
        var first = AgentDirectoryPresentation.Create(7, Guid.Parse("4f2ac2ba-0967-4f22-9c8d-8dfa1f22430b"), null, true, device, "STO");
        var second = AgentDirectoryPresentation.Create(7, Guid.Parse("4e410661-cf0f-4418-ad9a-1d5ef5e7e89b"), null, false, device, "STO");

        first.HostName.Should().Be(second.HostName);
        first.AgentId.Should().NotBe(second.AgentId);
        new[] { first, second }.Select(agent => (agent.TenantId, agent.AgentId)).Distinct().Should().HaveCount(2);
    }
}
