using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Branding;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class SharedWireContractForwardingTests
{
    [Theory]
    [InlineData(typeof(EffectiveDeploymentBranding))]
    [InlineData(typeof(IntegrationCredentialSummary))]
    public void Previously_published_assembly_qualified_contracts_still_resolve(Type contract)
    {
        var resolved = Type.GetType($"{contract.FullName}, NetRatel.Infrastructure", throwOnError: true);

        Assert.Same(contract, resolved);
    }
}
