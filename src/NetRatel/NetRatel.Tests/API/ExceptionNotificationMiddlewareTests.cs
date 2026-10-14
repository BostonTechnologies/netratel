using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Middleware;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Events;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ExceptionNotificationMiddlewareTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Repeated_attempt_fault_retains_one_occurrence_across_time_and_recorder_instances()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var first = await CaptureAsync("7", "agent-one", Guid.NewGuid(), "database unavailable");
        var payload = (ExceptionPayload)first.Payload!;
        await using (var db = new OrchestratorDbContext(options))
        {
            await new OutboxEventRecorder(db, NullLogger<OutboxEventRecorder>.Instance).RecordAsync(first);
            var message = Assert.Single(db.OutboxMessages);
            message.Status = OutboxStatuses.Published;
            await db.SaveChangesAsync();
        }
        // Retry trace changes, and the fault remains unresolved across a UTC day boundary.
        var retry = first with
        {
            OccurredUtc = first.OccurredUtc.AddDays(1), CorrelationId = "retry-correlation",
            Payload = payload with
            {
                CorrelationId = "retry-correlation",
                Occurrence = payload.Occurrence! with { LastSeenUtc = first.OccurredUtc.AddDays(1), LastCorrelationId = "retry-correlation", LastTraceId = "retry-trace" }
            }
        };
        await using (var db = new OrchestratorDbContext(options))
        {
            await new OutboxEventRecorder(db, NullLogger<OutboxEventRecorder>.Instance).RecordAsync(retry);
            var row = Assert.Single(db.OutboxMessages);
            Assert.Equal(OutboxStatuses.Published, row.Status);
            var occurrence = JsonSerializer.Deserialize<ExceptionPayload>(row.PayloadJson, JsonOptions)!.Occurrence!;
            Assert.Equal(2, occurrence.Count);
            Assert.Equal(first.OccurredUtc, occurrence.FirstSeenUtc);
            Assert.Equal(retry.OccurredUtc, occurrence.LastSeenUtc);
            Assert.Equal(first.CorrelationId, occurrence.FirstCorrelationId);
            Assert.Equal("retry-correlation", occurrence.LastCorrelationId);
            Assert.Equal("retry-trace", occurrence.LastTraceId);
        }
    }

    [Fact]
    public async Task Fault_identity_separates_tenants_agents_attempts_and_underlying_errors_and_leaves_other_routes_unchanged()
    {
        var attempt = Guid.NewGuid();
        var original = await CaptureAsync("7", "agent-one", attempt, "database unavailable");
        var originalPayload = (ExceptionPayload)original.Payload!;
        Assert.Equal(typeof(IOException).FullName, originalPayload.InnerExceptionType);
        Assert.Contains("database unavailable", originalPayload.InnerError);
        var bounded = (ExceptionPayload)(await CaptureAsync("7", "agent-one", attempt, new string('x', 6000))).Payload!;
        Assert.Equal(4000, bounded.InnerError!.Length);
        var identity = originalPayload.Occurrence!.ErrorIdentity;
        Assert.Equal(identity, ((ExceptionPayload)(await CaptureAsync("7", "agent-one", attempt, "database unavailable")).Payload!).Occurrence!.ErrorIdentity);
        foreach (var distinct in new[]
        {
            await CaptureAsync("8", "agent-one", attempt, "database unavailable"),
            await CaptureAsync("7", "agent-two", attempt, "database unavailable"),
            await CaptureAsync("7", "agent-one", Guid.NewGuid(), "database unavailable"),
            await CaptureAsync("7", "agent-one", attempt, "permission denied")
        }) Assert.NotEqual(identity, ((ExceptionPayload)distinct.Payload!).Occurrence!.ErrorIdentity);
        var unrelated = await CaptureAsync("7", "agent-one", attempt, "database unavailable", "/api/v1/jobs/1");
        Assert.Null(((ExceptionPayload)unrelated.Payload!).Occurrence);
    }

    private static async Task<DomainEvent> CaptureAsync(string tenant, string agent, Guid attemptId, string innerError, string? path = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path ?? $"/api/v2/agent-updates/attempts/{attemptId}/result";
        http.Request.RouteValues["attemptId"] = attemptId;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenant), new Claim("agent_id", agent)], "fixture"));
        var recorder = new CapturingRecorder();
        var middleware = new ExceptionNotificationMiddleware(_ => throw new InvalidOperationException("Report failed", new IOException(innerError)),
            NullLogger<ExceptionNotificationMiddleware>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(http, recorder, new Correlation()));
        Assert.Equal(tenant, recorder.Event!.TenantId);
        Assert.Equal(agent, recorder.Event.EntityId);
        return recorder.Event;
    }

    private sealed class CapturingRecorder : IEventRecorder
    {
        public DomainEvent? Event { get; private set; }
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) { Event = domainEvent; return Task.CompletedTask; }
    }

    private sealed class Correlation : ICorrelationContext
    {
        public string? Current { get; } = Guid.NewGuid().ToString();
        public string GetOrCreate() => Current!;
    }
}
