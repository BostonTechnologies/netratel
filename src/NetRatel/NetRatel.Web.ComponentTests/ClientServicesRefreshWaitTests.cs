using AwesomeAssertions;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Services.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientServicesRefreshWaitTests
{
    [Fact]
    public void CorrelatedWaitIgnoresUnrelatedInventory_AndUsesOnlyItsPersistedTerminalOutcome()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new ClientServicesRefreshRequestDto(Guid.NewGuid(), Guid.NewGuid(), 2, Guid.NewGuid(),
            now, now + ClientServicesLimits.RefreshTimeout, true);
        var model = new ClientServicesReadModelDto(3, Guid.NewGuid(), null, null, [], [], 0, true, true, now,
            SupportsRefreshCorrelation: true, RefreshRequests: [request]);
        var wait = new ClientServicesRefreshWait(model, now, request.RequestId, true);
        var unrelated = model with { LastCompleteInventory = new(Guid.NewGuid(), 2, 50, now, now, []),
            LatestInventoryAttempt = new(Guid.NewGuid(), ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Complete,
                2, 50, now, now, 0, IsFinal: true, RefreshRequestId: Guid.NewGuid()) };
        wait.Outcome(unrelated, now.AddSeconds(1)).Should().BeNull();
        var terminal = request with { Outcome = ClientServicesRefreshOutcome.Partial, CollectionId = Guid.NewGuid(), CompletedAtUtc = now.AddSeconds(2) };
        wait.Outcome(unrelated with { RefreshRequests = [terminal] }, now.AddSeconds(2))
            .Should().Contain(request.RequestId.ToString("D")).And.Contain("Partial");
    }

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
