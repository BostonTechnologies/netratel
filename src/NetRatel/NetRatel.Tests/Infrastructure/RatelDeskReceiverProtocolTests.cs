using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class RatelDeskReceiverProtocolTests
{
    private const string GoldenV1Json = """
        {"TenantId":71,"RunId":"00000000-0000-0000-0000-000000000013","ActionNodeId":"00000000-0000-0000-0000-000000000005","ConnectorId":"00000000-0000-0000-0000-000000000006","ConnectorRevision":1,"SourceInstanceId":"00000000-0000-0000-0000-000000000001","IdempotencyKey":"00000000000000000000000000000001:00000000000000000000000000000002:00000000000000000000000000000003:00000000000000000000000000000004:00000000000000000000000000000005","Event":{"TenantId":71,"EventId":"00000000-0000-0000-0000-000000000003","OccurrenceId":"00000000-0000-0000-0000-000000000002","FlowVersionId":"00000000-0000-0000-0000-000000000004","OccurredAtUtc":"2026-10-05T00:00:00+00:00","Data":{"AgentId":"00000000-0000-0000-0000-000000000010","RuleId":"00000000-0000-0000-0000-000000000011","RuleName":"rule","ClientName":"client","Resource":"resource","Metric":"cpu","Severity":"warning","NumericValue":95,"ServiceState":null,"ObservedAtUtc":"2026-10-05T00:00:00+00:00"},"Authority":{"PrincipalId":"human-owner","IntegrationCredentialId":null}},"Fields":{"Title":"Alert","Description":"Description","Priority":1},"Target":{"OrganizationId":"org-1","CustomerId":"customer-1","AssignedToId":null,"CategoryIds":[]},"SupportsSafeReplay":true,"SemanticFingerprint":""}
        """;

    [Theory]
    [MemberData(nameof(ReceiverVectors))]
    public void Fingerprint_matches_the_frozen_receiver_vectors(string name, string requestJson, string expected)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));
        var body = JsonSerializer.Deserialize<ReceiverCreateIncidentBody>(requestJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
            });
        Assert.NotNull(body);
        Assert.Equal(expected, new RatelDeskReceiverFingerprint().ComputeFull(body));
    }

    public static IEnumerable<object[]> ReceiverVectors()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "contracts", "rateldesk-incident-create.v1.fixtures.json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
                yield return [vector.GetProperty("name").GetString()!, vector.GetProperty("request").GetRawText(),
                    vector.GetProperty("fingerprint").GetString()!];
            yield break;
        }
        throw new FileNotFoundException("The frozen receiver conformance fixture is required.");
    }

    [Fact]
    public void Receiver_key_has_a_golden_projection_and_preserves_the_local_action_identity()
    {
        var draft = RatelDeskReceiverFixture.Draft();
        var first = RatelDeskReceiverKey.Prepare(draft, null);
        Assert.Equal("8ba5abac281f0a20cf5034acd7e1e1a6e4ac703ff567ab90d105aadf4488d683", first);
        Assert.Equal(RatelDeskReceiverFixture.LocalKey, draft.IdempotencyKey);
        Assert.Equal(first, RatelDeskReceiverKey.Prepare(draft with { RunId = RatelDeskReceiverFixture.Id(14) }, first));
        Assert.NotEqual(first, RatelDeskReceiverKey.Prepare(draft with
        {
            ActionNodeId = RatelDeskReceiverFixture.Id(9),
            IdempotencyKey = RatelDeskReceiverFixture.LocalKey[..(RatelDeskReceiverFixture.LocalKey.LastIndexOf(':') + 1)] +
                RatelDeskReceiverFixture.Id(9).ToString("N")
        }, null));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("persisted_AbC~123")]
    public void Durable_conforming_keys_are_preserved_without_encoding(string key)
    {
        Assert.Equal(key, RatelDeskReceiverKey.Prepare(RatelDeskReceiverFixture.Draft(), key));
        Assert.Equal(RatelDeskReceiverFixture.Api + "/api/v1/integrations/netratel/incident-receipts/" + key,
            RatelDeskReceiverKey.ReceiptEndpoint(RatelDeskReceiverFixture.Api + ReceiverWireValidation.ReceiptPath, key));
        Assert.Equal(key, RatelDeskReceiverKey.Prepare(RatelDeskReceiverFixture.Draft() with { IdempotencyKey = key }, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("a/b")]
    [InlineData("%2e")]
    [InlineData("a b")]
    [InlineData("α")]
    public void Invalid_durable_keys_require_read_only_recovery(string key) =>
        Assert.Throws<InvalidOperationException>(() => RatelDeskReceiverKey.Prepare(RatelDeskReceiverFixture.Draft(), key));

    [Fact]
    public void Key_length_boundary_is_256_ASCII_characters()
    {
        var accepted = new string('a', 256);
        Assert.Equal(accepted, RatelDeskReceiverKey.Prepare(RatelDeskReceiverFixture.Draft(), accepted));
        Assert.Throws<InvalidOperationException>(() => RatelDeskReceiverKey.Prepare(RatelDeskReceiverFixture.Draft(), accepted + "a"));
    }

    [Fact]
    public void Additive_receiver_preparation_keeps_the_golden_V1_JSON_and_semantic_hash()
    {
        const string expected = GoldenV1Json;
        const string expectedHash = "779c42f79681e3418169233ec2eb5432e66b059dde3901ec02d0693867ac3cfd";
        var draft = RatelDeskReceiverFixture.Draft();
        var action = RatelDeskReceiverFixture.Action() with { SemanticFingerprint = "" };
        Assert.Equal(Encoding.UTF8.GetBytes(expected), JsonSerializer.SerializeToUtf8Bytes(action));
        Assert.Equal(expectedHash, FlowContractValidation.Fingerprint(action));
        action = action with { SemanticFingerprint = expectedHash };
        var before = JsonSerializer.SerializeToUtf8Bytes(action);
        var peer = RatelDeskReceiverFixture.Peer();
        var capability = ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Capability(peer), peer, RatelDeskReceiverFixture.Now);
        var prepared = new ReceiverPreparationBuilder(new RatelDeskReceiverFingerprint())
            .Build(draft, action, peer, capability, RatelDeskReceiverFixture.Now, null);
        Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(action));
        Assert.Equal(expectedHash, FlowContractValidation.Fingerprint(action));
        Assert.True(ReceiverPreparedBinding.Valid(prepared, action, draft, RatelDeskReceiverFixture.Now, new RatelDeskReceiverFingerprint()));
        Assert.DoesNotContain("Receiver", Encoding.UTF8.GetString(before));
    }

    [Fact]
    public void Discovering_a_receiver_cannot_retrofit_safe_replay_into_a_historical_V1_request()
    {
        const string expectedHash = "566d9b5c39e978b5cf1387a7ee4bce0dbd28c7dfe025a40c6c5ea5aa372eb357";
        var expected = GoldenV1Json.Replace("\"SupportsSafeReplay\":true", "\"SupportsSafeReplay\":false", StringComparison.Ordinal);
        var historical = RatelDeskReceiverFixture.Action() with { SupportsSafeReplay = false, SemanticFingerprint = "" };
        Assert.Equal(Encoding.UTF8.GetBytes(expected), JsonSerializer.SerializeToUtf8Bytes(historical));
        Assert.Equal(expectedHash, FlowContractValidation.Fingerprint(historical));
        historical = historical with { SemanticFingerprint = expectedHash };
        var original = JsonSerializer.SerializeToUtf8Bytes(historical);
        var peer = RatelDeskReceiverFixture.Peer();
        var capability = ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Capability(peer), peer, RatelDeskReceiverFixture.Now);
        Assert.Throws<InvalidOperationException>(() => new ReceiverPreparationBuilder(new RatelDeskReceiverFingerprint())
            .Build(RatelDeskReceiverFixture.Draft(), historical, peer, capability, RatelDeskReceiverFixture.Now, null));
        Assert.False(historical.SupportsSafeReplay);
        Assert.Equal(original, JsonSerializer.SerializeToUtf8Bytes(historical));
        Assert.Equal(expectedHash, FlowContractValidation.Fingerprint(historical));
    }

    [Fact]
    public void A_self_consistent_sidecar_cannot_bind_another_body_key_or_target()
    {
        var draft = RatelDeskReceiverFixture.Draft();
        var action = RatelDeskReceiverFixture.Action();
        var peer = RatelDeskReceiverFixture.Peer();
        var fingerprint = new RatelDeskReceiverFingerprint();
        var prepared = new ReceiverPreparationBuilder(fingerprint).Build(draft, action, peer,
            ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Capability(peer), peer, RatelDeskReceiverFixture.Now),
            RatelDeskReceiverFixture.Now, null);
        foreach (var changed in new[]
        {
            prepared with { ExactCreateBodyJson = "{}" },
            prepared with { ReceiverIdempotencyKey = "another-logical-action" },
            prepared with { Peer = peer with { ConnectorId = RatelDeskReceiverFixture.Id(9) } },
            prepared with { Peer = peer with { AssignedToId = "other-assignee" } },
            prepared with { OriginalActionCreatedAtUtc = prepared.OriginalActionCreatedAtUtc.AddSeconds(1) },
            prepared with { AutomaticReplayUntilUtc = prepared.AutomaticReplayUntilUtc.AddSeconds(1) }
        })
            Assert.False(ReceiverPreparedBinding.Valid(changed with
            {
                EvidenceFingerprint = ReceiverPreparationBuilder.EvidenceHash(changed)
            }, action, draft, RatelDeskReceiverFixture.Now, fingerprint));
    }

    [Theory]
    [InlineData("receiverInstanceId")]
    [InlineData("sourceInstanceId")]
    [InlineData("sourceNamespaceId")]
    [InlineData("createEndpoint")]
    [InlineData("receiptEndpointTemplate")]
    [InlineData("targetValidationEndpoint")]
    [InlineData("keyHeader")]
    [InlineData("sourceHeader")]
    [InlineData("keyPattern")]
    public void Capability_binds_every_identity_endpoint_and_header(string field)
    {
        var peer = RatelDeskReceiverFixture.Peer();
        var body = JsonNode.Parse(RatelDeskReceiverFixture.Capability(peer))!;
        body[field] = "different";
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Bytes(body), peer, RatelDeskReceiverFixture.Now));
    }

    [Theory]
    [InlineData("maxKeyLength", "\"256\"")]
    [InlineData("minimumReceiptRetentionSeconds", "\"7776000\"")]
    [InlineData("maximumAutomaticReplaySeconds", "2592000.5")]
    [InlineData("receiptEvictionEnabled", "\"false\"")]
    [InlineData("atomicIncidentReceiptAndEffects", "1")]
    [InlineData("supportsReceiptLookup", "null")]
    [InlineData("supportsSafeSameKeyReplay", "[]")]
    [InlineData("authenticationModes", "[1]")]
    public void Capability_rejects_JSON_type_coercion(string field, string invalidJson)
    {
        var peer = RatelDeskReceiverFixture.Peer();
        var body = JsonNode.Parse(RatelDeskReceiverFixture.Capability(peer))!;
        body[field] = JsonNode.Parse(invalidJson);
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Bytes(body), peer, RatelDeskReceiverFixture.Now));
    }

    [Fact]
    public void Capability_accepts_additions_but_requires_each_replay_guarantee_and_selected_authentication_mode()
    {
        var peer = RatelDeskReceiverFixture.Peer();
        var body = JsonNode.Parse(RatelDeskReceiverFixture.Capability(peer))!;
        body["futureObservation"] = new JsonObject { ["version"] = 2 };
        _ = ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Bytes(body), peer, RatelDeskReceiverFixture.Now);
        foreach (var flag in new[] { "supportsReceiptLookup", "supportsSafeSameKeyReplay", "atomicIncidentReceiptAndEffects" })
        {
            body[flag] = false;
            Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Bytes(body), peer, RatelDeskReceiverFixture.Now));
            body[flag] = true;
        }
        body["authenticationModes"] = new JsonArray("api_bearer");
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Bytes(body), peer, RatelDeskReceiverFixture.Now));
        _ = ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Bytes(body), peer with { Mode = RatelDeskAuthenticationMode.ManualApiBearer }, RatelDeskReceiverFixture.Now);
    }

    [Theory]
    [MemberData(nameof(TargetFields))]
    public void Target_validation_requires_every_typed_identity_and_exact_mapping_field(string path)
    {
        var peer = RatelDeskReceiverFixture.Peer() with
        {
            AssignedToId = "assignee-1", CategoryIds = [RatelDeskReceiverFixture.Id(20).ToString("D"), RatelDeskReceiverFixture.Id(21).ToString("D")]
        };
        var valid = JsonNode.Parse(RatelDeskReceiverFixture.Target(peer))!;
        ReceiverWireValidation.Target(RatelDeskReceiverFixture.Bytes(valid), peer);
        foreach (var replacement in new[] { "\"different\"", "null", "17" })
        {
            var changed = JsonNode.Parse(RatelDeskReceiverFixture.Target(peer))!;
            var owner = path.StartsWith("mapping.", StringComparison.Ordinal) ? changed["mapping"]! : changed;
            var field = path[(path.LastIndexOf('.') + 1)..];
            owner[field] = JsonNode.Parse(replacement);
            Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Target(RatelDeskReceiverFixture.Bytes(changed), peer));
        }
        var missing = JsonNode.Parse(RatelDeskReceiverFixture.Target(peer))!;
        var missingOwner = path.StartsWith("mapping.", StringComparison.Ordinal) ? missing["mapping"]! : missing;
        missingOwner.AsObject().Remove(path[(path.LastIndexOf('.') + 1)..]);
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Target(RatelDeskReceiverFixture.Bytes(missing), peer));
    }

    public static IEnumerable<object[]> TargetFields() => new[]
    {
        "contractVersion", "receiverInstanceId", "sourceInstanceId", "sourceNamespaceId", "valid", "mapping",
        "mapping.organizationId", "mapping.customerId", "mapping.assignedToId", "mapping.categoryIds"
    }.Select(x => new object[] { x });

    [Fact]
    public void Target_categories_must_be_exact_canonical_ordered_and_not_duplicated()
    {
        var a = RatelDeskReceiverFixture.Id(20).ToString("D");
        var b = RatelDeskReceiverFixture.Id(21).ToString("D");
        var peer = RatelDeskReceiverFixture.Peer() with { CategoryIds = [a, b] };
        var valid = JsonNode.Parse(RatelDeskReceiverFixture.Target(peer))!;
        ReceiverWireValidation.Target(RatelDeskReceiverFixture.Bytes(valid), peer);
        foreach (var values in new[]
        {
            JsonSerializer.Serialize(new[] { b, a }), JsonSerializer.Serialize(new[] { a, a }),
            JsonSerializer.Serialize(new[] { a }), JsonSerializer.Serialize(new[] { a, b, RatelDeskReceiverFixture.Id(22).ToString("D") }),
            JsonSerializer.Serialize(new[] { RatelDeskReceiverFixture.Id(20).ToString("N"), b }), "[17,18]"
        })
        {
            var changed = JsonNode.Parse(RatelDeskReceiverFixture.Target(peer))!;
            changed["mapping"]!["categoryIds"] = JsonNode.Parse(values);
            Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Target(RatelDeskReceiverFixture.Bytes(changed), peer));
        }
        valid["mapping"]!["assignedToId"] = "";
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Target(RatelDeskReceiverFixture.Bytes(valid), peer));
        var duplicate = Encoding.UTF8.GetString(RatelDeskReceiverFixture.Target(peer)).Replace(
            "\"mapping\":{", "\"mapping\":{\"customerId\":\"customer-1\",", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Target(Encoding.UTF8.GetBytes(duplicate), peer));
    }

    [Fact]
    public void Manual_bootstrap_captures_registered_identities_and_keeps_all_managed_authentication_fields_absent()
    {
        var peer = RatelDeskReceiverFixture.Peer(RatelDeskAuthenticationMode.ManualApiBearer);
        var result = ReceiverWireValidation.CaptureManual(RatelDeskReceiverFixture.Capability(peer),
            RatelDeskReceiverFixture.Connector(), RatelDeskReceiverFixture.Api, peer.SourceInstanceId, RatelDeskReceiverFixture.Now);
        Assert.Equal(peer.SourceInstanceId, result.Peer.SourceInstanceId);
        Assert.Equal(peer.SourceNamespaceId, result.Peer.SourceNamespaceId);
        Assert.Equal(peer.ReceiverInstanceId, result.Peer.ReceiverInstanceId);
        Assert.Equal(RatelDeskAuthenticationMode.ManualApiBearer, result.Peer.Mode);
        Assert.Null(result.Peer.LinkId);
        Assert.Null(result.Peer.LinkRevision);
        Assert.Null(result.Peer.GrantHash);
        Assert.Null(result.Peer.Issuer);
        Assert.Null(result.Peer.Audience);
        Assert.Null(result.Peer.TokenEndpoint);
        Assert.Null(result.Peer.ClientId);
        Assert.Null(result.Peer.DirectionId);
    }

    [Theory]
    [InlineData("receiverInstanceId", "00000000000000000000000000000008")]
    [InlineData("receiverInstanceId", "ABCDEFAB-1234-4234-8234-123456789ABC")]
    [InlineData("sourceNamespaceId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("sourceNamespaceId", "00000000000000000000000000000007")]
    [InlineData("sourceInstanceId", "00000000-0000-0000-0000-000000000002")]
    public void Manual_bootstrap_rejects_noncanonical_or_mismatched_registered_source_identity(string field, string value)
    {
        var peer = RatelDeskReceiverFixture.Peer(RatelDeskAuthenticationMode.ManualApiBearer);
        var body = JsonNode.Parse(RatelDeskReceiverFixture.Capability(peer))!;
        body[field] = value;
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.CaptureManual(RatelDeskReceiverFixture.Bytes(body),
            RatelDeskReceiverFixture.Connector(), peer.ApiBaseUrl, peer.SourceInstanceId, RatelDeskReceiverFixture.Now));
    }

    [Theory]
    [InlineData("{\"id\":\"one\",\"id\":\"two\"}")]
    [InlineData("{\"integrationReceipt\":{\"key\":\"one\",\"key\":\"two\"}}")]
    [InlineData("{\"additive\":[{\"value\":1,\"value\":2}]}")]
    public void Duplicate_JSON_fields_are_rejected_at_every_depth(string json) =>
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void JSON_response_size_boundary_is_enforced_before_interpretation()
    {
        var accepted = Encoding.UTF8.GetBytes("{\"padding\":\"" + new string('a', 131058) + "\"}");
        Assert.Equal(131072, accepted.Length);
        using var parsed = ReceiverWireValidation.Parse(accepted);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Parse([.. accepted, (byte)' ']));
    }

    [Theory]
    [InlineData("2026-10-05T12:34:56Z")]
    [InlineData("2026-10-05T12:34:56.1234567+00:00")]
    public void Full_receipt_accepts_receiver_UTC_precision_and_keeps_all_original_bytes(string timestamp)
    {
        var prepared = RatelDeskReceiverFixture.Prepared();
        var node = JsonNode.Parse(RatelDeskReceiverFixture.Receipt(prepared, timestamp))!;
        node["futureIncidentField"] = new JsonObject { ["future"] = "retained" };
        var body = RatelDeskReceiverFixture.Bytes(node);
        var receipt = ReceiverWireValidation.Receipt(body, RatelDeskReceiverFixture.Location, prepared);
        Assert.Equal(Encoding.UTF8.GetString(body), receipt.ExactAcceptedBodyJson);
        Assert.Equal("incident-1", receipt.IncidentId);
        Assert.Equal("INC-0001", receipt.TrackingId);
        Assert.Equal(RatelDeskReceiverFixture.Location, receipt.Location);
    }

    [Theory]
    [InlineData("contractVersion")]
    [InlineData("receiverInstanceId")]
    [InlineData("sourceInstanceId")]
    [InlineData("sourceNamespaceId")]
    [InlineData("key")]
    [InlineData("fingerprint")]
    [InlineData("outcome")]
    [InlineData("incidentId")]
    [InlineData("trackingId")]
    [InlineData("organizationId")]
    [InlineData("customerId")]
    [InlineData("committedAtUtc")]
    [InlineData("location")]
    public void Every_nested_receipt_field_is_required_typed_and_bound(string field)
    {
        var prepared = RatelDeskReceiverFixture.Prepared();
        foreach (var replacement in new[] { "\"changed\"", "null", "17", "[]" })
        {
            var node = JsonNode.Parse(RatelDeskReceiverFixture.Receipt(prepared))!;
            node["integrationReceipt"]![field] = JsonNode.Parse(replacement);
            Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Receipt(RatelDeskReceiverFixture.Bytes(node), RatelDeskReceiverFixture.Location, prepared));
        }
        var missing = JsonNode.Parse(RatelDeskReceiverFixture.Receipt(prepared))!;
        missing["integrationReceipt"]!.AsObject().Remove(field);
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Receipt(RatelDeskReceiverFixture.Bytes(missing), RatelDeskReceiverFixture.Location, prepared));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("trackingId")]
    [InlineData("organizationId")]
    [InlineData("customerId")]
    public void Top_level_incident_identity_and_mapping_must_equal_the_receipt(string field)
    {
        var prepared = RatelDeskReceiverFixture.Prepared();
        var node = JsonNode.Parse(RatelDeskReceiverFixture.Receipt(prepared))!;
        node[field] = "different";
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Receipt(RatelDeskReceiverFixture.Bytes(node), RatelDeskReceiverFixture.Location, prepared));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/api/v1/incidents/incident-1")]
    [InlineData("https://foreign.example.test/incident-1")]
    [InlineData("https://api.example.test/help/api/v1/incidents/incident-1")]
    public void Location_header_must_equal_the_receivers_captured_resource_path(string? location) =>
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Receipt(
            RatelDeskReceiverFixture.Receipt(RatelDeskReceiverFixture.Prepared()), location, RatelDeskReceiverFixture.Prepared()));

    [Theory]
    [InlineData("2026-10-05T12:34:56+01:00")]
    [InlineData("2026-10-05T12:34:56")]
    [InlineData("2026-02-30T12:34:56Z")]
    public void Receipt_commit_time_requires_a_real_UTC_timestamp(string timestamp) =>
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Receipt(
            RatelDeskReceiverFixture.Receipt(RatelDeskReceiverFixture.Prepared(), timestamp), RatelDeskReceiverFixture.Location, RatelDeskReceiverFixture.Prepared()));

    [Theory]
    [InlineData(false, FlowIncidentActionResultKind.Failed)]
    [InlineData(true, FlowIncidentActionResultKind.DeliveryUnknown)]
    public void Expired_or_exhausted_uncertainty_cannot_become_an_ordinary_failure(bool uncertain, FlowIncidentActionResultKind expected)
    {
        var prepared = RatelDeskReceiverFixture.Prepared();
        var rateLimited = new RatelDeskReceiverObservation(RatelDeskReceiverObservationKind.RateLimited, "receiver-rate-limited");
        Assert.Equal(expected, ReceiverDispatchPolicy.Complete(new(prepared, uncertain, 1, null), rateLimited, prepared.AutomaticReplayUntilUtc).Kind);
        Assert.Equal(expected, ReceiverDispatchPolicy.Complete(new(prepared, uncertain, FlowLimits.MaximumActionAttempts, null), rateLimited, RatelDeskReceiverFixture.Now).Kind);
        Assert.Equal(uncertain ? FlowActionStatus.DeliveryUnknown : FlowActionStatus.Failed, ReceiverDispatchPolicy.BudgetExhausted(uncertain));
    }

    [Theory]
    [InlineData(true, FlowIncidentActionResultKind.RetryableSafe)]
    [InlineData(false, FlowIncidentActionResultKind.DeliveryUnknown)]
    public void Fifth_possible_commit_has_only_its_persisted_final_read_reconciliation(bool pending, FlowIncidentActionResultKind expected)
    {
        var prepared = RatelDeskReceiverFixture.Prepared();
        var evidence = new RatelDeskDispatchEvidence(prepared, true, FlowLimits.MaximumActionAttempts,
            RatelDeskReceiverFixture.Now, FinalReconciliationPending: pending);
        var result = ReceiverDispatchPolicy.Complete(evidence,
            new(RatelDeskReceiverObservationKind.PossibleCommit, "receiver-commit-unconfirmed"), RatelDeskReceiverFixture.Now);
        Assert.Equal(expected, result.Kind);
        if (pending) Assert.Equal(TimeSpan.FromSeconds(5), result.RetryAfter);
        Assert.Equal(FlowIncidentActionResultKind.DeliveryUnknown, ReceiverDispatchPolicy.Complete(evidence,
            new(RatelDeskReceiverObservationKind.Missing, "receiver-receipt-missing"), prepared.AutomaticReplayUntilUtc).Kind);
        Assert.Equal(FlowIncidentActionResultKind.DeliveryUnknown, ReceiverDispatchPolicy.Complete(evidence,
            new(RatelDeskReceiverObservationKind.Gone, "receiver-receipt-expired"), RatelDeskReceiverFixture.Now).Kind);
        Assert.Equal(FlowIncidentActionResultKind.DeliveryUnknown, ReceiverDispatchPolicy.Complete(evidence with
            { Attempts = FlowLimits.MaximumActionAttempts + 1 }, new(RatelDeskReceiverObservationKind.Missing,
                "receiver-receipt-missing"), RatelDeskReceiverFixture.Now).Kind);
    }

    [Fact]
    public void Verified_receipt_reconciles_the_fifth_post_even_after_the_send_budget_is_exhausted()
    {
        var prepared = RatelDeskReceiverFixture.Prepared();
        var receipt = ReceiverWireValidation.Receipt(RatelDeskReceiverFixture.Receipt(prepared),
            RatelDeskReceiverFixture.Location, prepared);
        var result = ReceiverDispatchPolicy.Complete(new(prepared, true, FlowLimits.MaximumActionAttempts,
            RatelDeskReceiverFixture.Now), new(RatelDeskReceiverObservationKind.Committed, "receiver-receipt-verified", receipt),
            prepared.AutomaticReplayUntilUtc);
        Assert.Equal(FlowIncidentActionResultKind.Succeeded, result.Kind);
        Assert.Equal(receipt.IncidentId, result.Receipt!.IncidentId);
    }

    [Theory]
    [InlineData(RatelDeskReceiverObservationKind.AuthenticationRejected)]
    [InlineData(RatelDeskReceiverObservationKind.PayloadRejected)]
    [InlineData(RatelDeskReceiverObservationKind.FingerprintConflict)]
    [InlineData(RatelDeskReceiverObservationKind.Gone)]
    public void A_current_rejection_cannot_erase_a_durably_recorded_prior_possible_commit(RatelDeskReceiverObservationKind kind)
    {
        var prepared = RatelDeskReceiverFixture.Prepared();
        var observation = new RatelDeskReceiverObservation(kind, "current-receiver-rejection");
        Assert.Equal(FlowIncidentActionResultKind.DeliveryUnknown,
            ReceiverDispatchPolicy.Complete(new(prepared, true, 1, RatelDeskReceiverFixture.Now), observation, RatelDeskReceiverFixture.Now).Kind);
        Assert.Equal(FlowIncidentActionResultKind.Failed,
            ReceiverDispatchPolicy.Complete(new(prepared, false, 1, null), observation, RatelDeskReceiverFixture.Now).Kind);
    }
}

internal static class RatelDeskReceiverFixture
{
    internal const string Api = "https://api.example.test/help";
    internal const string Location = "/help/api/v1/incidents/incident-1";
    internal const string LocalKey = "00000000000000000000000000000001:00000000000000000000000000000002:00000000000000000000000000000003:00000000000000000000000000000004:00000000000000000000000000000005";
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T00:00:00Z");
    internal static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");

    internal static FlowIncidentActionDraft Draft() => new(71, Id(13), Id(5), Id(6), 1, Id(1), LocalKey,
        new(71, Id(3), Id(2), Id(4), Now,
            new(Id(10), Id(11), "rule", "client", "resource", "cpu", "warning", 95, null, Now), new("human-owner")),
        new("Alert", "Description", 1));

    internal static FlowIncidentActionRequest Action()
    {
        var draft = Draft();
        var action = new FlowIncidentActionRequest(draft.TenantId, draft.RunId, draft.ActionNodeId, draft.ConnectorId,
            draft.ConnectorRevision, draft.SourceInstanceId, draft.IdempotencyKey, draft.Event, draft.Fields,
            new("org-1", "customer-1", null, []), true, "");
        return action with { SemanticFingerprint = FlowContractValidation.Fingerprint(action) };
    }

    internal static RatelDeskSemanticPeer Peer(RatelDeskAuthenticationMode mode = RatelDeskAuthenticationMode.ManagedServiceLink) => new(mode, 71,
        Id(6), mode == RatelDeskAuthenticationMode.ManagedServiceLink ? "approved-link" : null,
        mode == RatelDeskAuthenticationMode.ManagedServiceLink ? 1 : null,
        mode == RatelDeskAuthenticationMode.ManagedServiceLink ? new string('a', 64) : null, Id(8).ToString("D"), "org-1", Api,
        mode == RatelDeskAuthenticationMode.ManagedServiceLink ? "https://issuer.example.test/services" : null,
        mode == RatelDeskAuthenticationMode.ManagedServiceLink ? "rateldesk-api" : null,
        mode == RatelDeskAuthenticationMode.ManagedServiceLink ? Api + "/connect/token" : null,
        mode == RatelDeskAuthenticationMode.ManagedServiceLink ? "stable-client" : null,
        mode == RatelDeskAuthenticationMode.ManagedServiceLink ? "initiator_to_responder" : null,
        Id(1), Id(7), "org-1", "customer-1", null, []);

    internal static RatelDeskConnectorState Connector() => new(Id(6), 71, 1, 1, "human-owner",
        new("Helpdesk", Api, "org-1", "customer-1", null, [], new(), true), "protected-test-value", 1,
        new(RatelDeskAuthenticationMode.ManualApiBearer, null));

    internal static byte[] Target(RatelDeskSemanticPeer peer) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        contractVersion = ReceiverWireValidation.Contract, receiverInstanceId = peer.ReceiverInstanceId,
        sourceInstanceId = peer.SourceInstanceId.ToString("D"), sourceNamespaceId = peer.SourceNamespaceId.ToString("D"), valid = true,
        mapping = new { organizationId = peer.OrganizationId, customerId = peer.CustomerId, assignedToId = peer.AssignedToId, categoryIds = peer.CategoryIds }
    });

    internal static byte[] Capability(RatelDeskSemanticPeer peer) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        contractVersion = ReceiverWireValidation.Contract, receiverInstanceId = peer.ReceiverInstanceId,
        sourceInstanceId = peer.SourceInstanceId.ToString("D"), sourceNamespaceId = peer.SourceNamespaceId.ToString("D"),
        createEndpoint = Api + ReceiverWireValidation.CreatePath, receiptEndpointTemplate = Api + ReceiverWireValidation.ReceiptPath,
        targetValidationEndpoint = Api + ReceiverWireValidation.TargetsPath, keyHeader = "Idempotency-Key", sourceHeader = "X-NetRatel-Source-Instance",
        maxKeyLength = 256, keyPattern = ReceiverWireValidation.KeyPattern, minimumReceiptRetentionSeconds = 7776000,
        maximumAutomaticReplaySeconds = 2592000, receiptEvictionEnabled = false, atomicIncidentReceiptAndEffects = true,
        supportsReceiptLookup = true, supportsSafeSameKeyReplay = true,
        authenticationModes = new[] { peer.Mode == RatelDeskAuthenticationMode.ManagedServiceLink ? "oauth_client_credentials" : "api_bearer" }
    });

    internal static RatelDeskReceiverPreparationV2 Prepared(RatelDeskAuthenticationMode mode = RatelDeskAuthenticationMode.ManagedServiceLink)
    {
        var peer = Peer(mode);
        var action = Action();
        return new ReceiverPreparationBuilder(new RatelDeskReceiverFingerprint()).Build(Draft(), action, peer,
            ReceiverWireValidation.Capability(Capability(peer), peer, Now), Now, null);
    }

    internal static byte[] Receipt(RatelDeskReceiverPreparationV2 prepared, string timestamp = "2026-10-05T12:34:56Z") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        id = "incident-1", trackingId = "INC-0001", organizationId = "org-1", customerId = "customer-1",
        integrationReceipt = new
        {
            contractVersion = ReceiverWireValidation.Contract, receiverInstanceId = prepared.Peer.ReceiverInstanceId,
            sourceNamespaceId = prepared.Peer.SourceNamespaceId.ToString("D"), sourceInstanceId = prepared.Peer.SourceInstanceId.ToString("D"),
            key = prepared.ReceiverIdempotencyKey, fingerprint = prepared.ReceiverFingerprint, outcome = "committed", incidentId = "incident-1",
            trackingId = "INC-0001", organizationId = "org-1", customerId = "customer-1", committedAtUtc = timestamp, location = Location
        }
    });

    internal static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
}
