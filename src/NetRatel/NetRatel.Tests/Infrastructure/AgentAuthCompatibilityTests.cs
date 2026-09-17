using FluentAssertions;
using NetRatel.Application.Agents;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class AgentAuthCompatibilityTests
{
    [Fact]
    public void Defaults_Use_NetRatel_Names_And_Legacy_Values_Are_OptIn()
    {
        var options = new AgentAuthOptions
        {
            LegacyAudiences = ["spacetimedb", "netratel-agent"],
            LegacySigningKeyIds = ["spacetime-es256", "netratel-agent-es256"]
        };

        options.Audience.Should().Be("netratel-agent");
        options.SigningKeyId.Should().Be("netratel-agent-es256");
        options.GetAcceptedAudiences().Should().Equal("netratel-agent", "spacetimedb");
        options.GetAcceptedSigningKeyIds().Should().Equal("netratel-agent-es256", "spacetime-es256");
    }
}
