using AwesomeAssertions;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Services;

public sealed class ClientServicesRefreshLifecycleTests
{
    [Fact]
    public void PendingRequestsCoalesce_TimeoutIsDurable_AndLateEvidenceCannotRewriteIt()
    {
        var now = DateTimeOffset.UtcNow;
        var client = new ClientKey(19, Guid.NewGuid());
        var empty = ClientServicesState.Empty(client);
        var request = Request(now);
        var admitted = ClientServicesRefreshLifecycle.Update(empty, request, now);
        ClientServicesRefreshLifecycle.IsValidServerTransition(empty, admitted, now).Should().BeTrue();
        ClientServicesRefreshLifecycle.Update(admitted, Request(now.AddSeconds(16)), now.AddSeconds(16))
            .RefreshRequests.Should().Equal(request);
        var expired = ClientServicesRefreshLifecycle.Expire(admitted, request.DeadlineUtc);
        var timeout = expired.RefreshRequests!.Single();
        timeout.Outcome.Should().Be(ClientServicesRefreshOutcome.TimedOut);
        ClientServicesRefreshLifecycle.IsValidServerTransition(admitted, expired, request.DeadlineUtc.AddSeconds(1)).Should().BeTrue();
        var newAdmission = ClientServicesRefreshLifecycle.Update(expired, Request(request.DeadlineUtc), request.DeadlineUtc);
        newAdmission.RefreshRequests.Should().HaveCount(2);
        var chunk = new ClientServicesChunk(client, request.ConnectionId, 1, 1, Guid.NewGuid(), 0, true,
            ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Complete, now, request.DeadlineUtc, 0, [], 200,
            RefreshRequestId: request.RequestId, RegistrationId: request.RegistrationId);
        ClientServicesRefreshLifecycle.Observe(newAdmission, chunk, request.DeadlineUtc.AddSeconds(1))
            .RefreshRequests!.First().Should().Be(timeout);
    }

    [Fact]
    public void ServerOnlyTransitionsCannotInventCollectionCompletionOrEraseHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var state = ClientServicesState.Empty(new ClientKey(19, Guid.NewGuid()));
        var request = Request(now);
        state = ClientServicesRefreshLifecycle.Update(state, request, now);
        var forged = state with { RefreshRequests = [request with { Outcome = ClientServicesRefreshOutcome.Complete,
            CollectionId = Guid.NewGuid(), CompletedAtUtc = now }] };
        ClientServicesRefreshLifecycle.IsValidServerTransition(state, forged, now).Should().BeFalse();
        ClientServicesRefreshLifecycle.IsValidServerTransition(state, state with { RefreshRequests = [] }, now).Should().BeFalse();
        var offline = ClientServicesRefreshLifecycle.Update(state, request with { Outcome = ClientServicesRefreshOutcome.Offline,
            CompletedAtUtc = now.AddSeconds(1), ErrorCode = "refresh-connection-ended" }, now.AddSeconds(1));
        ClientServicesRefreshLifecycle.IsValidServerTransition(state, offline, now.AddSeconds(2)).Should().BeTrue();
    }

    private static ClientServicesRefreshRequestDto Request(DateTimeOffset now) => new(Guid.NewGuid(), Guid.NewGuid(), 1,
        Guid.NewGuid(), now, now + ClientServicesLimits.RefreshTimeout, true);
}
