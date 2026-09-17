using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class TenantEndpointSourceTests
{
    [Fact]
    public void TenantEndpoints_Do_Not_Depend_On_The_Retired_Spacetime_Client_Runtime()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "NetRatel", "NetRatel.API", "Endpoints", "Tenants", "TenantEndpoints.cs"));

        source.Should().NotContain("SpacetimeDbService");
        source.Should().NotContain("RepublishAllForTenantAsync");
        source.Should().NotContain("ClientUpdateBroadcaster");
    }

    [Fact]
    public void UpdateTenantEndpoint_Does_Not_Delete_Spacetime_Tenant()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "NetRatel", "NetRatel.API", "Endpoints", "Tenants", "TenantEndpoints.cs"));
        var updateStart = source.IndexOf("group.MapPut(\"/{id:int}\"", StringComparison.Ordinal);
        var deleteStart = source.IndexOf("group.MapDelete(\"/{id:int}\"", StringComparison.Ordinal);

        updateStart.Should().BeGreaterThanOrEqualTo(0);
        deleteStart.Should().BeGreaterThan(updateStart);

        var updateBlock = source[updateStart..deleteStart];
        updateBlock.Should().NotContain("DeleteTenant(id)");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetRatel.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
