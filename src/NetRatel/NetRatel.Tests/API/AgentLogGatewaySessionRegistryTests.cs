using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using NetRatel.API.Gateway;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentLogGatewaySessionRegistryTests
{
    [Fact]
    public void ExactReconnect_LateDisposeAndOldIngressCannotAffectReplacement()
    {
        var client = new ClientKey(44, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        using var old = registry.Register(client, connection, 5, CreateHello());
        old.TryAppend(new AgentLogBatch { Records = { CreateRecord(1, "old") } }).Should().BeTrue();
        using var current = registry.Register(client, connection, 5, CreateHello());

        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        old.RegistrationId.Should().NotBe(current.RegistrationId);
        old.Dispose();
        old.Dispose();
        current.IsCurrent.Should().BeTrue();
        old.TryAppend(new AgentLogBatch { Records = { CreateRecord(2, "stale") } }).Should().BeFalse();
        old.GetSources().Should().BeEmpty();
        current.TryAppend(new AgentLogBatch { Records = { CreateRecord(1, "current") } }).Should().BeTrue();
        registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null))
            .Records.Should().ContainSingle(record => record.Message == "current");
    }

    [Fact]
    public void ProvisionalRegistration_IsInvisibleUntilActivated()
    {
        var client = new ClientKey(44, Guid.NewGuid());
        var registry = new AgentLogGatewaySessionRegistry();
        using var registration = registry.Register(client, Guid.NewGuid(), 5, CreateHello(), provisional: true);
        registration.IsCurrent.Should().BeTrue();
        registry.GetSources(client).Should().BeEmpty();
        registration.TryAppend(new AgentLogBatch { Records = { CreateRecord(1, "provisional") } }).Should().BeFalse();
        registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null)).Records.Should().BeEmpty();
        registration.TryActivate().Should().BeTrue();
        registry.GetSources(client).Should().NotBeEmpty();
    }

    [Fact]
    public void AppendingIdenticalMessages_PreservesBothSequenceIdentities_AndPublishesTheBatch()
    {
        var client = new ClientKey(43, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        GatewayLogBatchEvent? accepted = null;
        registry.LogBatchAccepted += batch => accepted = batch;
        using var registration = registry.Register(client, connectionId, 5, CreateHello());

        var appended = registration.TryAppend(new AgentLogBatch
        {
            SessionId = connectionId.ToString("D"),
            Records =
            {
                CreateRecord(1, "identical message"),
                CreateRecord(2, "identical message")
            }
        });

        appended.Should().BeTrue();
        var page = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null));
        page.Records.Should().HaveCount(2);
        page.Records.Select(record => record.Sequence).Should().Equal(1, 2);
        accepted.Should().NotBeNull();
        accepted!.Records.Should().HaveCount(2);
    }

    [Fact]
    public void StaleConnectionEpoch_CannotAppendRecords()
    {
        var client = new ClientKey(44, Guid.NewGuid());
        var registry = new AgentLogGatewaySessionRegistry();
        using var registration = registry.Register(client, Guid.NewGuid(), 7, CreateHello());
        using var current = registry.Register(client, Guid.NewGuid(), 8, CreateHello());

        var accepted = registration.TryAppend(new AgentLogBatch
        {
            Records = { CreateRecord(1, "stale") }
        });

        accepted.Should().BeFalse();
        registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null)).Records.Should().BeEmpty();
    }

    [Fact]
    public void Register_RejectsStaleFences_AndRetainsTheCurrentLogSession()
    {
        var client = new ClientKey(44, Guid.NewGuid());
        var registry = new AgentLogGatewaySessionRegistry();
        var currentConnection = Guid.NewGuid();
        using var registration = registry.Register(client, currentConnection, 5, CreateHello());

        var staleEpoch = () => registry.Register(client, Guid.NewGuid(), 4, CreateHello());
        staleEpoch.Should().Throw<AgentGatewayRegistrationFencedException>();
        var ambiguousFence = () => registry.Register(client, Guid.NewGuid(), 5, CreateHello());
        ambiguousFence.Should().Throw<AgentGatewayRegistrationFencedException>();

        registration.TryAppend(new AgentLogBatch { Records = { CreateRecord(1, "current") } }).Should().BeTrue();
        registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null)).Records.Should().ContainSingle();
    }

    [Fact]
    public void SlowConsumerRetention_IsBoundedAndSignalsResync()
    {
        var client = new ClientKey(45, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        using var registration = registry.Register(client, connectionId, 2, CreateHello());

        for (var sequence = 1; sequence <= 2_100; sequence++)
        {
            registration.TryAppend(new AgentLogBatch
            {
                Records = { CreateRecord((ulong)sequence, $"message-{sequence}") }
            }).Should().BeTrue();
        }

        var page = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 2_000, null, null, null, null, null));
        page.Records.Should().HaveCount(100); // Browser queries remain independently page-bounded.
        page.DroppedRecordCount.Should().BeGreaterThan(0);
        page.ResyncRequired.Should().BeTrue();
        page.Records[0].Sequence.Should().BeGreaterThan(100);
    }

    [Fact]
    public void OversizedStructuredProperties_AreDroppedBeforeTheyCanExceedTheTransientBudget()
    {
        var client = new ClientKey(46, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        using var registration = registry.Register(client, connectionId, 1, CreateHello());
        var record = CreateRecord(1, "safe message");
        record.StructuredProperties["payload"] = new string('x', 1_025);

        registration.TryAppend(new AgentLogBatch { Records = { record } }).Should().BeTrue();

        var page = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null));
        page.Records.Should().BeEmpty();
        page.DroppedRecordCount.Should().Be(1);
        page.ResyncRequired.Should().BeTrue();
    }

    [Fact]
    public void SuccessfulBoundedResync_ClearsOnlyTheGapFlag_AndRetainsTheDropEvidence()
    {
        var client = new ClientKey(49, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        using var registration = registry.Register(client, connectionId, 1, CreateHello());
        var record = CreateRecord(1, "oversized structured property");
        record.StructuredProperties["payload"] = new string('x', 1_025);
        registration.TryAppend(new AgentLogBatch { Records = { record } }).Should().BeTrue();

        registry.TryCompleteResync(client, "netratel-runtime").Should().BeTrue();

        var page = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null));
        page.DroppedRecordCount.Should().Be(1);
        page.ResyncRequired.Should().BeFalse();
    }

    [Fact]
    public void OpaqueHistoryCursor_LoadsTheEarlierChronologicalPage()
    {
        var client = new ClientKey(47, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        using var registration = registry.Register(client, connectionId, 1, CreateHello());
        for (var sequence = 1; sequence <= 150; sequence++)
        {
            registration.TryAppend(new AgentLogBatch { Records = { CreateRecord((ulong)sequence, $"history-{sequence}") } }).Should().BeTrue();
        }

        var newest = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null));
        newest.Records.Select(record => record.Sequence).Should().StartWith(51UL);
        newest.PreviousCursor.Should().NotBeNullOrWhiteSpace();
        newest.PreviousCursor.Should().StartWith("before:");

        var earlier = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", newest.PreviousCursor, 100, null, null, null, null, null));
        earlier.Records.Select(record => record.Sequence).Should().Equal(Enumerable.Range(1, 50).Select(sequence => (ulong)sequence));
        earlier.HasMore.Should().BeFalse();
        earlier.PreviousCursor.Should().BeNull();

        var following = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", $"after:{newest.Records.First().Cursor}", 25, null, null, null, null, null));
        following.NextCursor.Should().StartWith("after:");

        var followingPage = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", following.NextCursor, 25, null, null, null, null, null));
        followingPage.Records.Select(record => record.Sequence).Should().Equal(Enumerable.Range(77, 25).Select(sequence => (ulong)sequence));
    }

    [Fact]
    public void Query_AppliesTheTypedCategoryFilter()
    {
        var client = new ClientKey(48, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        using var registration = registry.Register(client, connectionId, 1, CreateHello());
        var batch = new AgentLogBatch();
        batch.Records.Add(CreateRecord(1, "client", "Client"));
        batch.Records.Add(CreateRecord(2, "gateway", "Gateway"));
        registration.TryAppend(batch).Should().BeTrue();

        var page = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null, Categories: ["Gateway"]));

        page.Records.Should().ContainSingle().Which.Category.Should().Be("Gateway");
    }

    [Fact]
    public void CredentialBearingLogContent_IsRedactedBeforeItEntersTheTransientGatewayCache()
    {
        var client = new ClientKey(50, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        using var registration = registry.Register(client, connectionId, 1, CreateHello());
        var record = CreateRecord(1, "Authorization: Bearer bearer-value password=password-value");
        record.StructuredProperties["accessToken"] = "access-token-value";
        record.StructuredProperties["details"] = "refresh_token=refresh-token-value";

        registration.TryAppend(new AgentLogBatch { Records = { record } }).Should().BeTrue();

        var cached = registry.Query(client, new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null)).Records.Should().ContainSingle().Which;
        cached.Message.Should().Contain("[REDACTED]").And.NotContain("bearer-value").And.NotContain("password-value");
        cached.StructuredProperties!["accessToken"].Should().Be("[REDACTED]");
        cached.StructuredProperties["details"].Should().Contain("[REDACTED]").And.NotContain("refresh-token-value");
    }

    private static AgentLogHello CreateHello() => new()
    {
        Capabilities = { "log-gateway" },
        Sources =
        {
            new LogSourceDescriptor
            {
                SourceId = "netratel-runtime",
                Kind = "runtime",
                DisplayName = "NetRatel Client Logs",
                Platform = "linux",
                Available = true,
                SupportsLive = true,
                SupportsHistory = true,
                SupportsPaging = true
            }
        }
    };

    private static AgentLogRecord CreateRecord(ulong sequence, string message, string category = "Client") => new()
    {
        Cursor = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RecordSequence = sequence,
        TimestampUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Severity = "Information",
        SourceId = "netratel-runtime",
        Category = category,
        Message = message
    };
}
