using System.Reflection;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

[Collection(ServiceLinkRealPeerCollection.Name)]
public sealed class PhysicalDiskIncidentTests
{
    [Fact(Timeout = 300_000)]
    [Trait("Category", "hosted")]
    [Trait("Category", "PhysicalDiskIncidentProof")]
    public async Task Real_full_Client_disk_breach_survives_actual_worker_process_crashes_rotation_and_unlink_without_duplicates()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(300));
        await using var fixture = await PhysicalIncidentFixture.CreateAsync(budget.Token);
        await PhysicalDiskIncidentAcceptance.RunAsync(fixture, budget.Token);
    }
}
