using FluentAssertions;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Requests;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ExternalServiceIngestRequestMatcherTests
{
    [Fact]
    public void ContainsExternalServiceTaskId_Finds_TaskId_In_Nested_Meta()
    {
        var request = CreateRequest("""
        {
          "meta": {
            "requestTaskId": "019e20bb13717583bef964579cbcf339"
          },
          "input": {}
        }
        """);

        ExternalServiceIngestRequestMatcher.ContainsExternalServiceTaskId(request, "019e20bb13717583bef964579cbcf339")
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task FindExistingAsync_Returns_Existing_ExternalService_Request_For_Duplicate_Task()
    {
        var existing = CreateRequest("""{"meta":{"requestTaskId":"task-1"}}""", id: 12);
        var service = new StubRequestService([existing]);

        var found = await ExternalServiceIngestRequestMatcher.FindExistingAsync(
            service,
            new NetRatelIngestRequest { RequestTaskId = "task-1" },
            CancellationToken.None);

        found.Should().Be(existing);
        service.CreatedCount.Should().Be(0);
    }

    [Fact]
    public void ContainsExternalServiceTaskId_Does_Not_Match_Different_Task()
    {
        var request = CreateRequest("""{"meta":{"requestTaskId":"task-1"}}""");

        ExternalServiceIngestRequestMatcher.ContainsExternalServiceTaskId(request, "task-2")
            .Should()
            .BeFalse();
    }

    private static RequestInfo CreateRequest(string? jobInputs, int id = 10)
        => new(
            id,
            "external-service.api",
            "client-1",
            "2",
            null,
            "Failed",
            "Target client not found.",
            null,
            jobInputs,
            Array.Empty<string>(),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow);

    private sealed class StubRequestService(IReadOnlyList<RequestInfo> requests) : IRequestService
    {
        public int CreatedCount { get; private set; }

        public Task<IReadOnlyList<RequestInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult(requests);
        public Task<RequestInfo?> GetAsync(int requestId, CancellationToken ct = default) => Task.FromResult(requests.FirstOrDefault(x => x.Id == requestId));
        public Task<RequestInfo> CreateAsync(CreateRequestCommand command, CancellationToken ct = default)
        {
            CreatedCount++;
            throw new NotSupportedException();
        }

        public Task<RequestInfo?> UpdateAsync(UpdateRequestCommand command, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
