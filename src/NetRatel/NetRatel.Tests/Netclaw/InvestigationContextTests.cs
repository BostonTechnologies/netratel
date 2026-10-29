using System.Text.Json;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed class InvestigationContextTests
{
    [Fact]
    public void General_chat_payload_has_only_tenant_scope_and_preserves_the_question_and_byte_budget()
    {
        var row = new AiInvestigationRecord { TenantId = 17, Title = "Private conversation title" };
        const string question = "Draft a script to review notifications; do not save or execute it.";
        var context = InvestigationContextComposer.Compose(row, "Tenant operations", [], null, Guid.NewGuid(), question, "session", DateTimeOffset.UtcNow);
        Assert.Equal(InvestigationContextComposer.SerializedPayloadBytes("session", context.Request), context.PayloadBytes);
        Assert.EndsWith(question, context.Request);
        Assert.Contains("request human approval", context.Request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorized for this tenant", context.Request);
        Assert.DoesNotContain("Private conversation title", context.Request);
        Assert.DoesNotContain("selectedAgentIds", context.Request);
        Assert.DoesNotContain("Saved tenant default guidance", context.Request);
        using var metadata = JsonDocument.Parse(context.MetadataJson);
        Assert.Equal(17, metadata.RootElement.GetProperty("tenantId").GetInt32());
        Assert.Equal("chat", metadata.RootElement.GetProperty("conversationKind").GetString());
        Assert.False(metadata.RootElement.TryGetProperty("systems", out _));
        Assert.False(metadata.RootElement.TryGetProperty("alert", out _));
        Assert.Throws<ArgumentException>(() => InvestigationContextComposer.Compose(row, "Tenant", [], null,
            Guid.NewGuid(), new string('界', 8000), "session", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void General_chat_rejects_mixed_investigation_context_and_saved_prompt_leakage()
    {
        var prompt = new AiPromptSnapshot(Guid.NewGuid(), 1, "Investigation only", null, "Private investigation guidance", true);
        var rows = new[]
        {
            new AiInvestigationRecord { TenantId = 17, OccurrenceId = Guid.NewGuid() },
            new AiInvestigationRecord { TenantId = 17, FlowRunId = Guid.NewGuid() },
            new AiInvestigationRecord { TenantId = 17, SourceActionKey = "flow-action" },
            new AiInvestigationRecord { TenantId = 17, Automated = true },
            new AiInvestigationRecord { TenantId = 17, PromptSnapshotsJson = JsonSerializer.Serialize(new[] { prompt }) },
            new AiInvestigationRecord { TenantId = 17, SelectedPromptReferencesJson = JsonSerializer.Serialize(new[] { new AiPromptReference(prompt.PromptId, 1) }) }
        };
        foreach (var row in rows)
            Assert.Throws<ArgumentException>(() => InvestigationContextComposer.Compose(row, "Tenant", [], null,
                Guid.NewGuid(), "Question", "session", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => InvestigationContextComposer.Compose(new AiInvestigationRecord { TenantId = 17 }, "Tenant",
            [new InvestigationClientDto(Guid.NewGuid(), "Unselected worker", null, [], null)], null,
            Guid.NewGuid(), "Question", "session", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("Windows", "x64")]
    [InlineData("Linux", "arm64")]
    public void Canonical_cached_identity_survives_GUID_directory_name_and_offline_presence(string os, string architecture)
    {
        var id = Guid.NewGuid(); var observed = DateTimeOffset.Parse("2026-10-13T09:00:00Z");
        var agent = new Agent { Id = id, TenantId = 17, Name = id.ToString(), FriendlyName = "Production worker",
            Tags = ["production", "database"], LastSeenUtc = observed,
            DeviceInfoJson = JsonSerializer.Serialize(new { hostName = "worker-01", os, architecture,
                reportedAddress = "10.20.30.40", ipAddresses = new[] { "10.20.30.40", "fd00::17", "invalid" }, secret = "must-never-be-serialized" }) };
        var owner = new ClientConnectionOwnerRecord { TenantId = 17, AgentId = id, ConnectionEpoch = 2,
            AcceptanceGuardAtUtc = observed, LastReceivedAtUtc = observed, Active = false,
            MetadataJson = "{\"AgentVersion\":\"0.1.1-beta.fixture\",\"Capabilities\":[]}" };
        var client = InvestigationClientProjection.Create(agent, "Tenant operations", owner, observed.AddMinutes(1));
        Assert.Equal("worker-01", client.HostName); Assert.Equal(os, client.OperatingSystem); Assert.Equal(architecture, client.Architecture);
        Assert.Equal("10.20.30.40", client.PrimaryIpAddress); Assert.Equal(["10.20.30.40", "fd00::17"], client.IpAddresses);
        Assert.Equal("offline", client.Status); Assert.Equal(observed, client.ObservedAtUtc);
        Assert.Equal("Production worker", client.FriendlyName); Assert.Equal(agent.Tags, client.Tags);
        Assert.True(InvestigationClientProjection.Matches(client, "fd00::17"));
        var row = new AiInvestigationRecord { TenantId = 17, Title = "Investigate", AgentIds = [id] };
        var context = InvestigationContextComposer.Compose(row, "Tenant operations", [client], null, Guid.NewGuid(), "Find evidence", "session", observed);
        var arguments = JsonSerializer.SerializeToUtf8Bytes(new[] { "session", context.Request });
        Assert.Equal(context.PayloadBytes, arguments.Length);
        using var payload = JsonDocument.Parse(arguments);
        Assert.Equal(context.Request, payload.RootElement[1].GetString());
        Assert.Contains("Tenant operations", context.Request); Assert.Contains("worker-01", context.Request);
        Assert.Contains("10.20.30.40", context.Request); Assert.DoesNotContain("must-never-be-serialized", context.Request);
        using var metadata = JsonDocument.Parse(context.MetadataJson);
        Assert.Equal(id, metadata.RootElement.GetProperty("selectedAgentIds")[0].GetGuid());
    }

    [Fact]
    public void Missing_hostname_is_unknown_with_immutable_reference_and_unobserved_status()
    {
        var id = Guid.NewGuid(); var agent = new Agent { Id = id, TenantId = 17, Name = id.ToString() };
        var client = InvestigationClientProjection.Create(agent, "Tenant", null, DateTimeOffset.UtcNow);
        Assert.False(client.HostNameKnown); Assert.Equal("Unknown hostname", client.HostName);
        Assert.Equal(id, client.AgentId); Assert.Null(client.OperatingSystem); Assert.Null(client.PrimaryIpAddress);
        Assert.Equal("unknown", client.Status); Assert.Null(client.ObservedAtUtc);
    }

    [Fact]
    public void Saved_revisions_accompany_later_turns_while_client_observations_refresh_and_scope_is_preserved()
    {
        var id = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var saved = new[] { new AiPromptSnapshot(Guid.NewGuid(), 3, "Tenant guide", null, "Use the permitted configured MCP connection.", true),
            new AiPromptSnapshot(Guid.NewGuid(), 2, "Database evidence", "Read only", "Check database evidence.") };
        var row = new AiInvestigationRecord { TenantId = 17, AgentIds = [id], PromptSnapshotsJson = JsonSerializer.Serialize(saved) };
        var first = new InvestigationClientDto(id, "worker", null, [], at, "Linux", "x64", "10.0.0.17", ["10.0.0.17"], "offline", "1", at);
        var before = InvestigationContextComposer.Compose(row, "Tenant", [first], null, Guid.NewGuid(), "First question", "session", at);
        var after = InvestigationContextComposer.Compose(row, "Tenant", [first with { PrimaryIpAddress = "10.0.0.18", ObservedAtUtc = at.AddMinutes(1) }], null,
            Guid.NewGuid(), "Follow-up", "session", at.AddMinutes(1));
        Assert.Contains("Tenant guide", before.Request); Assert.Contains("Database evidence", after.Request);
        Assert.True(after.Request.IndexOf("Tenant guide", StringComparison.Ordinal) < after.Request.IndexOf("Database evidence", StringComparison.Ordinal));
        Assert.Contains("10.0.0.17", before.Request); Assert.Contains("10.0.0.18", after.Request);
        Assert.Equal(saved, InvestigationContextComposer.Prompts(row));
        Assert.Contains("request human approval", after.Request); Assert.Contains(id.ToString(), after.Request);
    }

    [Fact]
    public void Twenty_clients_fit_and_UTF8_overflow_rejects_without_truncating_guidance_or_question()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var row = new AiInvestigationRecord { TenantId = 17, AgentIds = ids };
        var clients = ids.Select((id, index) => new InvestigationClientDto(id, $"worker-{index}", "Friendly worker", ["production"], null,
            "Windows", "x64", $"10.0.0.{index + 1}", [$"10.0.0.{index + 1}"], "unknown")).ToArray();
        var context = InvestigationContextComposer.Compose(row, "Tenant", clients, null, Guid.NewGuid(), "Investigate", "session", DateTimeOffset.UtcNow);
        Assert.InRange(context.PayloadBytes, 1, InvestigationContextComposer.MaximumPayloadBytes);
        Assert.All(ids, id => Assert.Contains(id.ToString(), context.Request));
        var question = new string('界', 8000);
        var error = Assert.Throws<ArgumentException>(() => InvestigationContextComposer.Compose(row, "Tenant", clients, null,
            Guid.NewGuid(), question, "session", DateTimeOffset.UtcNow));
        Assert.Contains("28,000", error.Message); Assert.Equal(8000, question.Length);
        Assert.Equal([new AiPromptReference(ids[1], 1), new AiPromptReference(ids[0], 2)],
            InvestigationStore.NormalizePrompts([new(ids[1], 1), new(ids[0], 2), new(ids[0], 2)]));
        Assert.Throws<ArgumentException>(() => InvestigationStore.NormalizePrompts([new(ids[0], 1), new(ids[0], 2)]));
    }
}
