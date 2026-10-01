using System.Text.Json;
using NetRatel.Application.Fanout;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RealtimeEnvelopeContractTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void FanoutEnvelope_UsesStableWebJsonShapeAndNumericEnumOrdinals()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-07T10:00:00+00:00");
        var envelope = new RealtimeFanoutEnvelope(
            SchemaVersion: 1,
            Category: RealtimeFanoutCategory.Job,
            Target: new RealtimeFanoutTarget(
                TenantId: 7,
                Scope: RealtimeFanoutTargetScope.Job,
                JobId: 24),
            EventType: RealtimeFanoutEventType.Completed,
            Status: RealtimeFanoutStatus.Completed,
            Timestamp: timestamp,
            Sequence: 99,
            Revision: 12,
            CorrelationId: "job-run-42",
            Diagnostics: new RealtimeFanoutDiagnosticSummary(
                ObservedCount: 2,
                DroppedCount: 1,
                InvalidCount: 0,
                RetainedCount: 3,
                RepresentedBytes: 1_024,
                QueueDepth: 4,
                ActiveCount: 5,
                CompletedCount: 6),
            ResyncRequired: true,
            IsAuthoritative: false);

        var json = JsonSerializer.SerializeToElement(envelope, WebJsonOptions);

        AssertObjectProperties(
            json,
            "schemaVersion",
            "category",
            "target",
            "eventType",
            "status",
            "timestamp",
            "sequence",
            "revision",
            "correlationId",
            "diagnostics",
            "resyncRequired",
            "isAuthoritative",
            "isValid");
        AssertNumber(json, "schemaVersion", 1);
        AssertNumber(json, "category", 3);
        AssertNumber(json, "eventType", 1);
        AssertNumber(json, "status", 3);
        Assert.Equal(JsonValueKind.String, json.GetProperty("timestamp").ValueKind);
        Assert.Equal("2026-08-07T10:00:00+00:00", json.GetProperty("timestamp").GetString());
        AssertNumber(json, "sequence", 99);
        AssertNumber(json, "revision", 12);
        Assert.Equal("job-run-42", json.GetProperty("correlationId").GetString());
        Assert.Equal(JsonValueKind.True, json.GetProperty("resyncRequired").ValueKind);
        Assert.Equal(JsonValueKind.False, json.GetProperty("isAuthoritative").ValueKind);
        Assert.Equal(JsonValueKind.True, json.GetProperty("isValid").ValueKind);

        var target = json.GetProperty("target");
        AssertObjectProperties(target, "tenantId", "scope", "clientId", "sessionId", "requestId", "jobId", "commandId", "isValid");
        AssertNumber(target, "tenantId", 7);
        AssertNumber(target, "scope", 5);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("clientId").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("sessionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("requestId").ValueKind);
        AssertNumber(target, "jobId", 24);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("commandId").ValueKind);
        Assert.Equal(JsonValueKind.True, target.GetProperty("isValid").ValueKind);

        var diagnostics = json.GetProperty("diagnostics");
        AssertObjectProperties(
            diagnostics,
            "observedCount",
            "droppedCount",
            "invalidCount",
            "retainedCount",
            "representedBytes",
            "queueDepth",
            "activeCount",
            "completedCount",
            "isValid");
        AssertNumber(diagnostics, "observedCount", 2);
        AssertNumber(diagnostics, "droppedCount", 1);
        AssertNumber(diagnostics, "invalidCount", 0);
        AssertNumber(diagnostics, "retainedCount", 3);
        AssertNumber(diagnostics, "representedBytes", 1_024);
        AssertNumber(diagnostics, "queueDepth", 4);
        AssertNumber(diagnostics, "activeCount", 5);
        AssertNumber(diagnostics, "completedCount", 6);
        Assert.Equal(JsonValueKind.True, diagnostics.GetProperty("isValid").ValueKind);
    }

    private static void AssertObjectProperties(JsonElement element, params string[] expectedProperties)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        var actualProperties = element.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expected = expectedProperties.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actualProperties);
    }

    private static void AssertNumber(JsonElement element, string propertyName, long expected) =>
        Assert.Equal(expected, element.GetProperty(propertyName).GetInt64());
}
