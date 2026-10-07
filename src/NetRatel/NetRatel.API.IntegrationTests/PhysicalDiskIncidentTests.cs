using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

[Collection(ServiceLinkRealPeerCollection.Name)]
public sealed class PhysicalDiskIncidentTests(ITestOutputHelper output)
{
    [Fact(Timeout = 300_000)]
    [Trait("Category", "hosted")]
    [Trait("Category", "PhysicalDiskIncidentProof")]
    public async Task Real_full_Client_disk_breach_survives_actual_worker_process_crashes_rotation_and_unlink_without_duplicates()
    {
        var elapsed = Stopwatch.StartNew();
        void Progress(string stage) => output.WriteLine("physical stage={0} elapsedMilliseconds={1}", stage, elapsed.ElapsedMilliseconds);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(300));
        await using var fixture = await PhysicalIncidentFixture.CreateAsync(budget.Token, Progress);
        await PhysicalDiskIncidentAcceptance.RunAsync(fixture, budget.Token, Progress);
    }
}
