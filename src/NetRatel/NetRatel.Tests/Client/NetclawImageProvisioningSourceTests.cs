using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class NetclawImageProvisioningSourceTests
{
    [Fact]
    public void PublicClientImage_DoesNotProvisionPrivateNetclawArchives()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "docker", "client", "Dockerfile.public"));

        dockerfile.Should().NotContain("NETCLAW_");
        dockerfile.Should().NotContain("netclaw");
        dockerfile.Should().NotContain("git clone");
    }
}
