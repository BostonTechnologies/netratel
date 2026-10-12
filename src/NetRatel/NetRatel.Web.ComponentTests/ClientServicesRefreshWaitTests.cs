using AwesomeAssertions;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Services.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientServicesRefreshWaitTests
{
    [Fact]
    public void DeadlineAndConnectionOutcomes_AreBounded_WithoutInventingRequestCorrelation()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new ClientServicesReadModelDto(3, Guid.NewGuid(), null,
            new(Guid.NewGuid(), ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Partial, 1, 10, now, now, 0),
            [], [], 0, true, true, now);
        var wait = new ClientServicesRefreshWait(model, now);
        wait.Outcome(model, wait.Deadline.AddTicks(-1)).Should().BeNull();
        wait.Outcome(model, wait.Deadline).Should().Contain("Timed out");
        wait.Outcome(model with { Connected = false }, now).Should().Contain("offline");
        wait.Outcome(model with { SupportsServices = false }, now).Should().Contain("unsupported");
        // Replay and a different older collection cannot satisfy even the subsequent-observation wait.
        wait.Outcome(model with { LatestAttempt = model.LatestAttempt! with { CollectionId = Guid.NewGuid(), IsFinal = true, Sequence = 9 } }, now).Should().BeNull();
        wait.Outcome(model with { LatestAttempt = model.LatestAttempt! with { IsFinal = true, Sequence = 11 } }, now).Should().Contain("Subsequent inventory collection finished: Partial");
    }
}
