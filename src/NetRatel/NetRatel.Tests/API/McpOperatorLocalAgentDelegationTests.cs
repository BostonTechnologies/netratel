using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Middleware;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorLocalAgentDelegationTests
{
    [Fact]
    public void Production_local_agent_uses_only_the_authenticated_allowed_client_identity()
    {
        var context = new DefaultHttpContext
        {
            User = Principal("operator-subject", "local-cli", ["Operator"], ["netratel.mcp.execute"])
        };
        var options = new McpOperatorLocalAgentOptions
        {
            Enabled = true,
            ServicePrincipal = "netratel-local-agent",
            AllowedClientIds = ["local-cli"]
        };
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var created = McpOperatorLocalAgentDelegation.TryCreate(
            context,
            new HostEnvironment { EnvironmentName = Environments.Production },
            options,
            "netratel_commands",
            "execute",
            7,
            agentId,
            out var delegation);

        created.Should().BeTrue();
        delegation.Should().NotBeNull();
        delegation!.Identity.Subject.Should().Be("operator-subject");
        delegation.Identity.ClientId.Should().Be("local-cli");
        delegation.Identity.Scopes.Should().Contain("netratel.mcp.execute");
        delegation.ServicePrincipal.Should().Be("netratel-local-agent");
        delegation.Resource.Should().Be("netratel-local-agent");
        delegation.Instance.Should().Be("prod");
        delegation.Tool.Should().Be("netratel_commands");
        delegation.Operation.Should().Be("execute");
        delegation.TenantId.Should().Be(7);
        delegation.AgentId.Should().Be(agentId);
    }

    [Fact]
    public void Unknown_client_or_nonproduction_environment_cannot_create_a_local_operator_identity()
    {
        var context = new DefaultHttpContext
        {
            User = Principal("operator-subject", "untrusted-client", [], ["netratel.mcp.execute"])
        };
        var options = new McpOperatorLocalAgentOptions
        {
            Enabled = true,
            AllowedClientIds = ["local-cli"]
        };

        McpOperatorLocalAgentDelegation.TryCreate(
            context,
            new HostEnvironment { EnvironmentName = Environments.Production },
            options,
            "netratel_commands",
            "execute",
            7,
            Guid.NewGuid(),
            out _).Should().BeFalse();

        context.User = Principal("operator-subject", "local-cli", [], ["netratel.mcp.execute"]);
        McpOperatorLocalAgentDelegation.TryCreate(
            context,
            new HostEnvironment { EnvironmentName = Environments.Development },
            options,
            "netratel_commands",
            "execute",
            7,
            Guid.NewGuid(),
            out _).Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(string subject, string clientId, IReadOnlyList<string> roles, IReadOnlyList<string> scopes)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("client_id", clientId)
        };
        claims.AddRange(roles.Select(role => new Claim("roles", role)));
        claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "M2M"));
    }

    private sealed class HostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "NetRatel.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
