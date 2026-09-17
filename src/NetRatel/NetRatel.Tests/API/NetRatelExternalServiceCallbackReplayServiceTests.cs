using System.Text.Json;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Notifications;
using Xunit;

namespace NetRatel.Tests.API;

public class NetRatelExternalServiceCallbackReplayServiceTests
{
    [Fact]
    public async Task ReplayAsync_ReplaysStoredCallbackPayload()
    {
        var client = new RecordingCallbackClient();
        var sut = new NetRatelExternalServiceCallbackReplayService(client);
        var taskId = Guid.NewGuid().ToString("N");
        var notification = new NetRatelNotificationDto
        {
            Id = Guid.NewGuid(),
            EventType = "DomainEvent.Orchestration.ExternalServiceCallbackFailed",
            CorrelationId = "corr-replay",
            PayloadJson = JsonSerializer.Serialize(new
            {
                requestTaskId = taskId,
                requestId = "req-1",
                executionId = "run-1",
                netratelRequestId = "netratel-req-1",
                netratelRunId = "netratel-run-1",
                status = "failed",
                message = "callback failed",
                resultJson = "{\"ok\":false}",
                errorJson = "{\"error\":\"boom\"}",
                worklogSummary = "NetRatel callback replay",
                startedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                completedAtUtc = DateTimeOffset.UtcNow
            })
        };

        await sut.ReplayAsync(notification);

        var replay = Assert.Single(client.Requests);
        Assert.Equal(taskId, replay.Request.RequestTaskId);
        Assert.Equal("req-1", replay.Request.RequestId);
        Assert.Equal("run-1", replay.Request.ExecutionId);
        Assert.Equal("netratel-req-1", replay.Request.NetRatelRequestId);
        Assert.Equal("netratel-run-1", replay.Request.NetRatelRunId);
        Assert.Equal("failed", replay.Request.Status);
        Assert.Equal("{\"ok\":false}", replay.Request.ResultJson);
        Assert.Equal("{\"error\":\"boom\"}", replay.Request.ErrorJson);
        Assert.Equal("NetRatel callback replay", replay.Request.WorklogSummary);
        Assert.Equal("corr-replay", replay.CorrelationId);
    }

    [Fact]
    public async Task ReplayAsync_Throws_WhenPayloadIsIncomplete()
    {
        var client = new RecordingCallbackClient();
        var sut = new NetRatelExternalServiceCallbackReplayService(client);
        var notification = new NetRatelNotificationDto
        {
            Id = Guid.NewGuid(),
            EventType = "DomainEvent.Orchestration.ExternalServiceCallbackRejected",
            CorrelationId = "corr-bad",
            PayloadJson = JsonSerializer.Serialize(new
            {
                requestTaskId = "",
                status = "running"
            })
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReplayAsync(notification));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void CanReplay_ReturnsTrue_OnlyForExternalServiceCallbackFailureEvents()
    {
        var sut = new NetRatelExternalServiceCallbackReplayService(new RecordingCallbackClient());

        Assert.True(sut.CanReplay(new NetRatelNotificationDto { EventType = "DomainEvent.Orchestration.ExternalServiceCallbackFailed" }));
        Assert.True(sut.CanReplay(new NetRatelNotificationDto { EventType = "DomainEvent.Orchestration.ExternalServiceCallbackRejected" }));
        Assert.False(sut.CanReplay(new NetRatelNotificationDto { EventType = "DomainEvent.Agent.Enrolled" }));
    }

    private sealed class RecordingCallbackClient : INetRatelExternalServiceCallbackClient
    {
        public List<(NetRatelExternalServiceCallbackRequest Request, string CorrelationId)> Requests { get; } = new();

        public Task SendStatusAsync(NetRatelExternalServiceCallbackRequest request, string correlationId, CancellationToken ct = default)
        {
            Requests.Add((request, correlationId));
            return Task.CompletedTask;
        }
    }
}
