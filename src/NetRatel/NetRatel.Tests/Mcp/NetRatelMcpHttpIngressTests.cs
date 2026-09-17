using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class NetRatelMcpHttpIngressTests
{
    [Fact]
    public void Public_activation_manifest_limits_ingress_to_mcp_and_protected_resource_paths()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var manifest = File.ReadAllText(Path.Combine(repositoryRoot, "docs/mcp-http/production-activation-manifest.yml"));

        manifest.Should().Contain("    - /mcp");
        manifest.Should().Contain("    - /.well-known/oauth-protected-resource/mcp");
        manifest.Should().Contain("  traefik_route: OWNER_DECISION_REQUIRED");
        manifest.Should().Contain("  production_ingress_enabled: false");
    }
}
