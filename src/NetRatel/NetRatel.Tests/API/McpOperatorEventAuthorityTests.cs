using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorEventAuthorityTests
{
    private const string RejectedCallback = "DomainEvent.Orchestration.ExternalServiceCallbackRejected";

    [Fact]
    public async Task Exact_filters_find_older_published_callback_failures_with_stable_pages_and_redacted_metadata()
    {
        await using var db = Database();
        var occurred = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        for (var index = 0; index < 25; index++)
            db.OutboxMessages.Add(Message(Guid.NewGuid(), "DomainEvent.Unrelated", "other", "Published", occurred.AddDays(1)));
        var first = Message(Guid.Parse("00000000-0000-0000-0000-000000000001"), RejectedCallback, "owned-correlation", "Published", occurred);
        var second = Message(Guid.Parse("00000000-0000-0000-0000-000000000002"), RejectedCallback, "owned-correlation", "Published", occurred);
        db.OutboxMessages.AddRange(first, second,
            Message(Guid.NewGuid(), RejectedCallback, "other-correlation", "Published", occurred),
            Message(Guid.NewGuid(), RejectedCallback, "owned-correlation", "Disabled", occurred));
        await db.SaveChangesAsync();
        var authority = Authority(db);
        var query = new McpOperatorEventQuery(RejectedCallback, "owned-correlation", "Published");

        var pageOne = await authority.ListAsync(1, 1, query, CancellationToken.None);
        var pageTwo = await authority.ListAsync(2, 1, query, CancellationToken.None);
        var exhausted = await authority.ListAsync(3, 1, query, CancellationToken.None);

        Assert.Equal(second.Id, Assert.Single(pageOne).Id);
        Assert.Equal(first.Id, Assert.Single(pageTwo).Id);
        Assert.Empty(exhausted);
        var summary = Assert.Single(pageOne);
        Assert.Equal("Published", summary.Status);
        Assert.Equal("external-service-callback", summary.RetryTarget);
        Assert.True(summary.RetrySupported);
        Assert.Null(summary.RetryBlockedReason);
        var readback = await authority.GetAsync(second.Id, CancellationToken.None);
        Assert.Equal(summary, readback);
        var json = JsonSerializer.Serialize(new { pageOne, readback });
        Assert.DoesNotContain("protected-sentinel", json);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.example", json);
    }

    [Fact]
    public async Task Disabled_callback_reports_retry_unavailable_and_does_not_dispatch()
    {
        await using var db = Database();
        var message = Message(Guid.NewGuid(), RejectedCallback, "owned-correlation", "Disabled", DateTimeOffset.UtcNow);
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        var authority = Authority(db);

        var summary = await authority.GetAsync(message.Id, CancellationToken.None);
        Assert.NotNull(summary);
        Assert.False(summary.RetrySupported);
        Assert.Equal("event_disabled", summary.RetryBlockedReason);
        Assert.Equal("external-service-callback", summary.RetryTarget);
        Assert.Equal(new McpOperatorEventMutationOutcome(false, "event_retry_not_allowed"), await authority.RetryAsync(message.Id, CancellationToken.None));
        Assert.Equal("Disabled", (await db.OutboxMessages.SingleAsync()).Status);
    }

    private static OrchestratorDbContext Database() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static McpOperatorEventAuthority Authority(OrchestratorDbContext db) => new(
        new NetRatelNotificationService(db, new NetRatelNotificationDisplaySanitizer(new Names())), new CallbackReplay());

    private static OutboxMessage Message(Guid id, string type, string correlation, string status, DateTimeOffset occurred) => new()
    {
        Id = id, Type = type, Source = "fixture", CorrelationId = correlation, Status = status,
        OccurredUtc = occurred, Severity = "Warning", Message = "protected-sentinel",
        PayloadJson = "{\"credential\":\"protected-sentinel\",\"callback\":\"https://secret.example/callback\"}",
        LastError = "protected-sentinel"
    };

    private sealed class Names : IClientDisplayNameResolver
    {
        public IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> identities) => new Dictionary<string, string>();
    }

    private sealed class CallbackReplay : INetRatelExternalServiceCallbackReplayService
    {
        public bool CanReplay(NetRatelNotificationDto notification) => notification.EventType == RejectedCallback;
        public Task ReplayAsync(NetRatelNotificationDto notification, CancellationToken ct = default) =>
            throw new InvalidOperationException("These read-only and disabled-event tests must not dispatch a callback.");
    }
}
