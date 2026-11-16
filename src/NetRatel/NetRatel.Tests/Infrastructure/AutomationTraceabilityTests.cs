using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutomationTrace;
using AutomationTrace.Contracts;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class AutomationTraceabilityTests
{
    [Fact]
    public void Native_run_identifiers_survive_browser_JSON_without_rounding_and_accept_legacy_numbers()
    {
        var value = new NetRatel.Shared.Contracts.Jobs.JobRunDto(639300000000000001, 67, "Demo", 7, "", "fixture",
            NetRatel.Shared.Contracts.Jobs.JobRunStatusDto.Failed, 6, 0, null, null, null, null)
            { ParentRunId = 639300000000000002 };
        var json = JsonSerializer.Serialize(value, AutomationTraceValidation.Json);
        using var wire = JsonDocument.Parse(json);
        Assert.Equal("639300000000000001", wire.RootElement.GetProperty("id").GetString());
        Assert.Equal("639300000000000002", wire.RootElement.GetProperty("parentRunId").GetString());
        Assert.Equal(value.Id, JsonSerializer.Deserialize<NetRatel.Shared.Contracts.Jobs.JobRunDto>(
            json.Replace("\"639300000000000001\"", "639300000000000001"), AutomationTraceValidation.Json)!.Id);
        var job = new NetRatel.Shared.Contracts.Jobs.JobDto(9007199254740993, "Demo job", "/", null, 7, "", 0, 0);
        var script = new NetRatel.Shared.Contracts.Scripts.ScriptDto(9007199254740995, "Demo script", "/", null, null, "bash", default, default);
        foreach (var (document, exact) in new[] {
            (JsonSerializer.SerializeToElement(job, AutomationTraceValidation.Json), "9007199254740993"),
            (JsonSerializer.SerializeToElement(script, AutomationTraceValidation.Json), "9007199254740995") })
            Assert.Equal(exact, document.GetProperty("id").GetString());
    }

    [Fact]
    public void Mirrored_v2_golden_preserves_exact_identifiers_and_every_semantic_context_field()
    {
        using var fixture = Fixture();
        var request = fixture.RootElement.GetProperty("create").Deserialize<AutomationIncidentCreate>(AutomationTraceValidation.Json)!;
        Assert.True(AutomationTraceValidation.Create(request));
        Assert.Equal(fixture.RootElement.GetProperty("fingerprint").GetString(), AutomationTraceValidation.Fingerprint(request));
        Assert.Equal(fixture.RootElement.GetProperty("canonicalCreateJson").GetString(), JsonSerializer.Serialize(request, AutomationTraceValidation.Json));
        Assert.Equal("130", request.Context.NativeRequestId); Assert.Equal("67", request.Context.Definition!.Id);
        Assert.Equal("639300000000000001", request.Context.ExecutionId);
        Assert.NotEqual(AutomationTraceValidation.Fingerprint(request), AutomationTraceValidation.Fingerprint(request with
        { Context = request.Context with { Origin = request.Context.Origin! with { RequestedFor = new("different-person", "Different person") } } }));
        var numeric = JsonNode.Parse(JsonSerializer.Serialize(request.Context, AutomationTraceValidation.Json))!.AsObject();
        numeric["executionId"] = 639300000000000001L;
        Assert.Throws<JsonException>(() => numeric.Deserialize<AutomationIncidentContext>(AutomationTraceValidation.Json));
    }

    [Fact]
    public void V2_preparation_freezes_exact_context_and_retains_v1_wire_key_and_guards()
    {
        var (draft, action, peer, capability) = PreparedInputs();
        var builder = new ReceiverPreparationBuilder(new RatelDeskReceiverFingerprint());
        var prepared = builder.Build(draft, action, peer, capability, RatelDeskReceiverFixture.Now, null);
        Assert.Equal(AutomationTraceContract.IncidentVersion, prepared.PresentationContract);
        Assert.Equal(RatelDeskReceiverKey.Prepare(draft, null), prepared.ReceiverIdempotencyKey);
        Assert.Equal(AutomationTraceValidation.Fingerprint(action.PresentationPayload!), prepared.ReceiverFingerprint);
        Assert.True(ReceiverPreparedBinding.Valid(prepared, action, draft, RatelDeskReceiverFixture.Now, new RatelDeskReceiverFingerprint()));
        var changed = action with { PresentationPayload = action.PresentationPayload! with { Context = action.PresentationPayload.Context with { SourceOutcome = "Unknown" } } };
        changed = changed with { SemanticFingerprint = FlowContractValidation.Fingerprint(changed) };
        Assert.False(ReceiverPreparedBinding.Valid(prepared, changed, draft, RatelDeskReceiverFixture.Now, new RatelDeskReceiverFingerprint()));
        Assert.Throws<InvalidOperationException>(() => builder.Build(draft, action, peer, capability with { AutomationPresentation = null }, RatelDeskReceiverFixture.Now, null));
    }

    [Theory]
    [InlineData("action")][InlineData("namespace")][InlineData("fingerprint")][InlineData("public-link")][InlineData("relation")]
    public void Receipt_verification_rejects_foreign_or_partial_presentation_evidence(string mutation)
    {
        var (draft, action, peer, capability) = PreparedInputs();
        var prepared = new ReceiverPreparationBuilder(new RatelDeskReceiverFingerprint()).Build(draft, action, peer, capability, RatelDeskReceiverFixture.Now, null);
        var accepted = Receipt(prepared, action.PresentationPayload!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(accepted, AutomationTraceValidation.Json);
        var verified = ReceiverWireValidation.Receipt(bytes, RatelDeskReceiverFixture.Location, prepared);
        Assert.Equal("INC-DEMO-001", verified.TrackingId); Assert.NotNull(verified.PresentationReceipt);
        var root = JsonNode.Parse(bytes)!.AsObject(); var receipt = root["integrationReceipt"]!.AsObject();
        if (mutation == "action") receipt["actionId"] = Guid.NewGuid().ToString("D");
        if (mutation == "namespace") receipt["sourceNamespaceId"] = Guid.NewGuid().ToString("D");
        if (mutation == "fingerprint") receipt["fingerprint"] = new string('0', 64);
        if (mutation == "public-link") receipt["publicLink"] = "https://foreign.example.test/incidents/incident-1";
        if (mutation == "relation") receipt["originRelation"]!["status"] = "unavailable";
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Receipt(Encoding.UTF8.GetBytes(root.ToJsonString()), RatelDeskReceiverFixture.Location, prepared));
    }

    [Fact]
    public void Later_failure_is_selected_before_limits_and_child_exit_code_stays_with_the_child()
    {
        var now = RatelDeskReceiverFixture.Now;
        var run = new JobRunInfo(639300000000000002, 67, 71, "", "service", JobRunState.Failed, 6, now, now, now.AddSeconds(1), null, null, null);
        var steps = Enumerable.Range(1, 6).Select(i => new JobStepRunInfo((ulong)i, run.Id, null,
            i == 6 ? JobStepRunState.Failed : JobStepRunState.Succeeded, i, "activity-" + i, null, now, now)).ToArray();
        var activities = Enumerable.Range(1, 6).Select(i => new JobTaskActivityInfo((ulong)i, "activity-" + i, run.Id, null, "", 71,
            "exec-library-script", i == 6 ? "Failed" : "Succeeded", i == 6 ? "Directory not found" : null, now, now,
            ResultJson: i == 6 ? "{\"stderr\":[\"No such file or directory\"],\"exitCode\":2}" : "{\"stdout\":[\"success\"]}",
            ExitCode: i == 6 ? 2 : 0, ScriptName: i == 6 ? "List requested directory" : "Earlier successful step")).ToArray();
        var result = AutomationIncidentContextBuilder.Evidence(new(run, steps, activities), "node-1", "Demo node", "demo-1.example.test", "Failed");
        var failure = Assert.Single(result.FailedSteps);
        Assert.Equal(6, failure.Ordinal); Assert.Equal("List requested directory", failure.Name); Assert.Equal(2, failure.ExitCode);
        Assert.Contains("No such file", failure.ErrorExcerpt); Assert.Equal("639300000000000002", result.ChildRunId);
        Assert.DoesNotContain(result.FailedSteps, x => x.Name == "Earlier successful step");
    }

    [Fact]
    public void Trusted_reference_projection_keeps_native_entities_and_distinct_actions_separate()
    {
        using var fixture = Fixture(); var context = fixture.RootElement.GetProperty("context").Deserialize<AutomationIncidentContext>(AutomationTraceValidation.Json)!;
        var value = new AutomationReferenceProjectionEvent(71, context.Origin!.SourceInstanceId, context.Origin.Organization.Id,
            context.Origin, 130, context.ExecutionId, "67", context.FlowRunId, context.ActionId, "incident-demo", "INC-DEMO-001", "https://desk.example.com/incidents/incident-demo");
        Assert.True(AutomationTraceValidation.Context(context));
        Assert.Equal("639300000000000001", context.ExecutionId);
        var rows = AutomationReferenceProjection.Rows(value);
        Assert.Equal(6, rows.Count); Assert.Equal(6, rows.Select(x => x.Id).Distinct().Count());
        Assert.Equal(rows.Select(x => x.Id), AutomationReferenceProjection.Rows(value).Select(x => x.Id));
        Assert.All(rows.Where(x => x.ResourceKind == "job-run"), row => { Assert.Equal("639300000000000001", row.ResourceId); Assert.Equal(67, row.JobDefinitionId); });
        Assert.All(rows.Where(x => x.ResourceKind == "request"), row => Assert.Equal("130", row.ResourceId));
        Assert.DoesNotContain(AutomationReferenceProjection.Rows(value with { ActionId = Guid.NewGuid().ToString("D") }), row => rows.Any(old => old.Id == row.Id));
        Assert.DoesNotContain("No such file", JsonSerializer.Serialize(rows));
    }

    private static (FlowIncidentActionDraft Draft, FlowIncidentActionRequest Action, RatelDeskSemanticPeer Peer, RatelDeskVerifiedCapability Capability) PreparedInputs()
    {
        using var fixture = Fixture(); var reference = fixture.RootElement.GetProperty("standard").Deserialize<IncidentPresentationReference>(AutomationTraceValidation.Json)!;
        var draft = RatelDeskReceiverFixture.Draft() with { IncidentPresentation = reference };
        var peer = RatelDeskReceiverFixture.Peer();
        var context = RatelDeskIncidentTemplateClient.Sample("monitoring") with { FlowRunId = draft.RunId.ToString("D"), ActionId = draft.ActionNodeId.ToString("D") };
        var payload = new AutomationIncidentCreate(AutomationTraceContract.IncidentVersion, new(peer.OrganizationId, peer.CustomerId, null, [], 1), reference, context);
        var action = RatelDeskReceiverFixture.Action() with { PresentationPayload = payload };
        action = action with { SemanticFingerprint = FlowContractValidation.Fingerprint(action) };
        var capability = ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Capability(peer), peer, RatelDeskReceiverFixture.Now) with
        { AutomationPresentation = new(AutomationTraceContract.IncidentVersion, AutomationTraceContract.ContextVersion, reference,
            RatelDeskReceiverFixture.Api + ReceiverWireValidation.PresentationCreatePath, RatelDeskReceiverFixture.Api + ReceiverWireValidation.TemplateCataloguePath,
            RatelDeskReceiverFixture.Api + ReceiverWireValidation.TemplatePreviewPath, "rateldesk.incident-templates.read", "https://desk.example.test") };
        return (draft, action, peer, capability);
    }
    private static object Receipt(RatelDeskReceiverPreparationV2 prepared, AutomationIncidentCreate payload) => new
    {
        id = "incident-1", trackingId = "INC-DEMO-001", organizationId = prepared.Peer.OrganizationId, customerId = prepared.Peer.CustomerId,
        integrationReceipt = new AutomationIncidentReceipt(AutomationTraceContract.IncidentVersion, prepared.Peer.ReceiverInstanceId,
            prepared.Peer.SourceNamespaceId.ToString("D"), prepared.Peer.SourceInstanceId.ToString("D"), prepared.ReceiverIdempotencyKey, prepared.ReceiverFingerprint,
            "committed", "incident-1", "INC-DEMO-001", prepared.Peer.OrganizationId, prepared.Peer.CustomerId, RatelDeskReceiverFixture.Now,
            RatelDeskReceiverFixture.Location, "https://desk.example.test/incidents/incident-1", payload.Context.FlowRunId, payload.Context.ActionId,
            new("not-applicable", null, null, null), payload.Presentation)
    };
    private static JsonDocument Fixture()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "contracts", "rateldesk-incident-create.v2.fixtures.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        throw new FileNotFoundException("Mirrored v2 fixture is required.");
    }
}
