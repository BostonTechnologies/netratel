using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using Xunit;

namespace NetRatel.Tests.AgentClient;

public sealed class McpOperatorV2ClientTests
{
    [Fact]
    public async Task Terminal_stream_cursor_preserves_the_full_unsigned_sequence()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorTerminalV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        await client.GetStreamWindowAfterAsync(target, "0123456789abcdef0123456789abcdef", ulong.MaxValue, 5, 10);
        outbound.Requests.Should().ContainSingle().Which.Path.Should().EndWith("/stream-window?windowSeconds=5&maxRecords=10&afterSequence=18446744073709551615");
    }

    [Fact]
    public async Task Task_client_uses_only_exact_owned_v2_preview_routes()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorTaskV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));

        await client.PreviewCreateCommandAsync(target, new McpOperatorTaskCommandV2("bash", "printf retained-value", "/srv/netratel", 30, 4096), CancellationToken.None);

        outbound.Requests.Should().ContainSingle();
        var call = outbound.Requests.Single();
        call.Method.Should().Be(HttpMethod.Post);
        call.Path.Should().Be("/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/tasks/preview/create_command");
        call.Body!.AsObject()["command"]!.AsObject()["command"]!.GetValue<string>().Should().Be("printf retained-value");
    }

    [Fact]
    public async Task Request_client_preserves_etag_and_confirmation_inputs_on_exact_v2_route()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorRequestV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.ConfirmUpdateAsync(target, new McpOperatorRequestUpdateV2(14, 3, "Apply reviewed setting"), plan, key, CancellationToken.None);

        var call = outbound.Requests.Should().ContainSingle().Subject;
        call.Path.Should().Be("/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/requests/confirm/update");
        var body = call.Body!.AsObject();
        body["requestId"]!.GetValue<int>().Should().Be(14);
        body["expectedVersion"]!.GetValue<long>().Should().Be(3);
        body["planToken"]!.GetValue<string>().Should().Be(plan);
        body["idempotencyKey"]!.GetValue<string>().Should().Be(key);
    }

    [Fact]
    public async Task V2_clients_use_the_same_owned_route_for_a_dev_outbound_target()
    {
        var outbound = new RecordingOutbound("dev");
        var client = new McpOperatorRequestV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));

        await client.GetAsync(target, 14, CancellationToken.None);

        outbound.Requests.Should().ContainSingle().Which.Path
            .Should().Be("/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/requests/14");
    }

    [Fact]
    public async Task Tenant_client_uses_only_control_plane_v2_route_and_preserves_etag_credentials()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorTenantV2Client(outbound);
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.ConfirmUpdateAsync(new McpOperatorTenantUpdateV2(7, 3,
            new McpOperatorTenantDraftV2("Camelot", ["camelot.example"], true, Description: "Reviewed tenant")), plan, key, CancellationToken.None);

        var call = outbound.Requests.Should().ContainSingle().Subject;
        call.Path.Should().Be("/api/v2/mcp/operator/tenants/confirm/update");
        var body = call.Body!.AsObject();
        body["tenantId"]!.GetValue<int>().Should().Be(7);
        body["expectedVersion"]!.GetValue<long>().Should().Be(3);
        body["planToken"]!.GetValue<string>().Should().Be(plan);
        body["idempotencyKey"]!.GetValue<string>().Should().Be(key);
        body.ContainsKey("agentId").Should().BeFalse();
    }

    [Fact]
    public async Task Onboarding_client_uses_tenant_scoped_preview_confirmation_routes_without_an_agent_target()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorOnboardingV2Client(outbound);
        var enrollmentCodeId = Guid.Parse("10d5f8a2-4d9b-41e8-8a16-b0b7f230a525");
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.GetCollateralAsync(7, "linux-x64", CancellationToken.None);
        await client.DownloadCollateralAsync(7, "linux-x64", CancellationToken.None);
        await client.ListEnrollmentsAsync(7, "active", 123, 25, CancellationToken.None);
        await client.ConfirmCreateEnrollmentAsync(7, new McpOperatorOnboardingEnrollmentV2("linux-x64", 5, 1), plan, key, CancellationToken.None);
        await client.ConfirmRevokeEnrollmentAsync(7, enrollmentCodeId, plan, key, CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/tenants/7/onboarding/collateral/linux-x64",
            "/api/v2/mcp/operator/tenants/7/onboarding/collateral/linux-x64/download",
            "/api/v2/mcp/operator/tenants/7/onboarding/enrollments?status=active&cursor=123&limit=25",
            "/api/v2/mcp/operator/tenants/7/onboarding/confirm/create-enrollment",
            "/api/v2/mcp/operator/tenants/7/onboarding/confirm/revoke-enrollment");
        var create = outbound.Requests[3].Body!.AsObject();
        create["runtime"]!.GetValue<string>().Should().Be("linux-x64");
        create["planToken"]!.GetValue<string>().Should().Be(plan);
        create.ContainsKey("agentId").Should().BeFalse();
        var revoke = outbound.Requests[4].Body!.AsObject();
        revoke["enrollmentCodeId"]!.GetValue<string>().Should().Be(enrollmentCodeId.ToString("D"));
        revoke["idempotencyKey"]!.GetValue<string>().Should().Be(key);
    }

    [Fact]
    public async Task Client_facade_uses_only_exact_policy_admitted_v2_routes()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorClientV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));

        await client.GetAsync(target, CancellationToken.None);
        await client.GetPresenceAsync(target, CancellationToken.None);
        await client.GetCapabilitiesAsync(target, CancellationToken.None);
        await client.GetBindingAsync(target, CancellationToken.None);
        await client.GetTelemetryAsync(target, CancellationToken.None);
        await client.GetUpdateAttemptsAsync(target, CancellationToken.None);
        await client.GetUpdateMetadataAsync(target, CancellationToken.None);
        await client.PreviewPingAsync(target, CancellationToken.None);
        await client.ConfirmPingAsync(target, "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko", "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0", CancellationToken.None);
        await client.PreviewSoftwareUpdateAsync(target, CancellationToken.None);
        await client.ConfirmSoftwareUpdateAsync(target, "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko", "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0", CancellationToken.None);
        await client.PreviewDisableAsync(target, "Approved maintenance window", CancellationToken.None);
        await client.ConfirmDisableAsync(target, "Approved maintenance window", "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko", "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0", CancellationToken.None);
        await client.PreviewEnableAsync(target, CancellationToken.None);
        await client.ConfirmEnableAsync(target, "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko", "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0", CancellationToken.None);
        await client.PreviewDeleteAsync(target, "Machine retired", CancellationToken.None);
        await client.ConfirmDeleteAsync(target, "Machine retired", "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko", "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0", CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/presence",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/capabilities",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/binding",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/telemetry",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/update-attempts",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/update-metadata",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/ping/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/ping/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/software-update/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/software-update/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/disable/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/disable/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/enable/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/enable/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/delete/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/clients/delete/confirm");
        outbound.Requests[..7].Should().OnlyContain(call => call.Method == HttpMethod.Get);
        outbound.Requests[7].Method.Should().Be(HttpMethod.Post);
        var ping = outbound.Requests[8].Body!.AsObject();
        ping["planToken"]!.GetValue<string>().Should().StartWith("1niCn");
        ping["idempotencyKey"]!.GetValue<string>().Should().StartWith("M79he");
        outbound.Requests[10].Body!.AsObject()["planToken"]!.GetValue<string>().Should().StartWith("1niCn");
        outbound.Requests[12].Body!.AsObject()["reason"]!.GetValue<string>().Should().Be("Approved maintenance window");
        outbound.Requests[13].Body.Should().BeNull();
        outbound.Requests[15].Body!.AsObject()["reason"]!.GetValue<string>().Should().Be("Machine retired");
    }

    [Fact]
    public async Task Notification_facade_uses_only_delegated_operator_v2_routes_and_preserves_plan_credentials()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorNotificationV2Client(outbound);
        var notificationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.GetSummaryAsync(CancellationToken.None);
        await client.ConfirmMarkReadAsync([notificationId], plan, key, CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/notifications/summary",
            "/api/v2/mcp/operator/notifications/confirm/mark-read");
        var body = outbound.Requests[1].Body!.AsObject();
        body["ids"]!.AsArray().Single()!.GetValue<string>().Should().Be(notificationId.ToString("D"));
        body["planToken"]!.GetValue<string>().Should().Be(plan);
        body["idempotencyKey"]!.GetValue<string>().Should().Be(key);
    }

    [Fact]
    public async Task Event_and_connectivity_facades_use_only_control_plane_v2_routes()
    {
        var outbound = new RecordingOutbound("prod");
        var events = new McpOperatorEventV2Client(outbound);
        var connectivity = new McpOperatorConnectivityV2Client(outbound);
        var eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await events.PreviewRetryAsync(eventId, CancellationToken.None);
        await events.ConfirmDisableAsync(eventId, plan, key, CancellationToken.None);
        await connectivity.GetSettingsAsync(CancellationToken.None);
        await connectivity.ConfirmTestAsync(plan, key, CancellationToken.None);

        outbound.Requests.Select(request => request.Path).Should().Equal(
            "/api/v2/mcp/operator/events/11111111-2222-3333-4444-555555555555/retry/preview",
            "/api/v2/mcp/operator/events/11111111-2222-3333-4444-555555555555/disable/confirm",
            "/api/v2/mcp/operator/connectivity/settings",
            "/api/v2/mcp/operator/connectivity/test/confirm");
        outbound.Requests[1].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be(plan);
        outbound.Requests[3].Body!.AsObject()["idempotencyKey"]!.GetValue<string>().Should().Be(key);
    }

    [Fact]
    public async Task File_facade_uses_only_exact_policy_admitted_v2_routes_and_preserves_confirmation_inputs()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorFileV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var artifactId = Guid.Parse("99999999-2222-3333-4444-555555555555");
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        var write = new McpOperatorFileWriteTextV2("C:\\netratel\\review.txt", "approved content");
        var upload = new McpOperatorFileUploadV2("C:\\netratel\\upload.bin", "AQID");
        var relocation = new McpOperatorFileRelocationV2("C:\\netratel\\review.txt", "C:\\netratel\\published.txt");

        await client.BrowseAsync(target, "C:\\netratel", 25, CancellationToken.None);
        await client.StatAsync(target, "C:\\netratel\\review.txt", CancellationToken.None);
        await client.ReadAsync(target, "C:\\netratel\\review.txt", CancellationToken.None);
        await client.PreviewCollectArtifactAsync(target, "C:\\netratel\\review.txt", CancellationToken.None);
        await client.ConfirmCollectArtifactAsync(target, "C:\\netratel\\review.txt", plan, key, CancellationToken.None);
        await client.GetArtifactStatusAsync(target, artifactId, CancellationToken.None);
        await client.DownloadArtifactAsync(target, artifactId, CancellationToken.None);
        await client.PreviewCleanupArtifactAsync(target, artifactId, CancellationToken.None);
        await client.ConfirmCleanupArtifactAsync(target, artifactId, plan, key, CancellationToken.None);
        await client.PreviewWriteTextAsync(target, write, CancellationToken.None);
        await client.ConfirmWriteTextAsync(target, write, plan, key, CancellationToken.None);
        await client.PreviewUploadAsync(target, upload, CancellationToken.None);
        await client.ConfirmUploadAsync(target, upload, plan, key, CancellationToken.None);
        await client.PreviewCreateDirectoryAsync(target, "C:\\netratel\\review", CancellationToken.None);
        await client.ConfirmCreateDirectoryAsync(target, "C:\\netratel\\review", plan, key, CancellationToken.None);
        await client.PreviewDeleteAsync(target, "C:\\netratel\\review.txt", CancellationToken.None);
        await client.ConfirmDeleteAsync(target, "C:\\netratel\\review.txt", plan, key, CancellationToken.None);
        await client.PreviewCopyAsync(target, relocation, CancellationToken.None);
        await client.ConfirmCopyAsync(target, relocation, plan, key, CancellationToken.None);
        await client.PreviewMoveAsync(target, relocation, CancellationToken.None);
        await client.ConfirmMoveAsync(target, relocation, plan, key, CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/browse?path=C%3A%5Cnetratel&pageSize=25",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/stat?path=C%3A%5Cnetratel%5Creview.txt",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/read?path=C%3A%5Cnetratel%5Creview.txt",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/artifacts/collect/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/artifacts/collect/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/artifacts/99999999-2222-3333-4444-555555555555",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/artifacts/99999999-2222-3333-4444-555555555555/download",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/artifacts/99999999-2222-3333-4444-555555555555/cleanup/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/artifacts/99999999-2222-3333-4444-555555555555/cleanup/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/write-text/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/write-text/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/upload/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/upload/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/create-directory/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/create-directory/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/delete/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/delete/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/copy/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/copy/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/move/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/files/move/confirm");
        outbound.Requests[..3].Should().OnlyContain(call => call.Method == HttpMethod.Get);
        outbound.Requests[4].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be(plan);
        outbound.Requests[8].Body!.AsObject()["idempotencyKey"]!.GetValue<string>().Should().Be(key);
        outbound.Requests[10].Body!.AsObject()["text"]!.GetValue<string>().Should().Be("approved content");
        outbound.Requests[12].Body!.AsObject()["contentBase64"]!.GetValue<string>().Should().Be("AQID");
        outbound.Requests[18].Body!.AsObject()["sourcePath"]!.GetValue<string>().Should().Be("C:\\netratel\\review.txt");
        outbound.Requests[20].Body!.AsObject()["destinationPath"]!.GetValue<string>().Should().Be("C:\\netratel\\published.txt");
    }

    [Fact]
    public void File_facade_rejects_invalid_or_over_limit_values_before_outbound_dispatch()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorFileV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));

        Action invalidPath = () => client.BrowseAsync(target, "\0", cancellationToken: CancellationToken.None);
        Action invalidUpload = () => client.PreviewUploadAsync(target, new McpOperatorFileUploadV2("C:\\netratel\\upload.bin", "not-base64"), CancellationToken.None);
        Action oversizedText = () => client.PreviewWriteTextAsync(target, new McpOperatorFileWriteTextV2("C:\\netratel\\review.txt", new string('€', 6000)), CancellationToken.None);

        invalidPath.Should().Throw<AgentClientValidationException>();
        invalidUpload.Should().Throw<AgentClientValidationException>();
        oversizedText.Should().Throw<AgentClientValidationException>();
        outbound.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task File_facade_allows_an_empty_text_payload_for_a_policy_admitted_truncate()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorFileV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));

        await client.PreviewWriteTextAsync(target, new McpOperatorFileWriteTextV2("C:\\netratel\\review.txt", string.Empty), CancellationToken.None);

        outbound.Requests.Should().ContainSingle().Which.Body!.AsObject()["text"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public async Task Command_facade_uses_only_exact_owned_v2_routes_and_preserves_preview_credentials()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorCommandV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var command = new McpOperatorCommandExecutionV2("bash", "printf reviewed-value", "/srv/netratel", 30, 4096, ["NETRATEL_TOKEN"]);
        const string commandId = "0123456789abcdef0123456789abcdef";
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.GetAvailabilityAsync(target, CancellationToken.None);
        await client.PreviewExecuteAsync(target, command, CancellationToken.None);
        await client.ConfirmExecuteAsync(target, command, plan, key, CancellationToken.None);
        await client.GetAsync(target, commandId, CancellationToken.None);
        await client.CancelAsync(target, commandId, CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/commands/availability",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/commands/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/commands/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/commands/0123456789abcdef0123456789abcdef",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/commands/0123456789abcdef0123456789abcdef/cancel");
        outbound.Requests[0].Method.Should().Be(HttpMethod.Get);
        outbound.Requests[1].Body!.AsObject()["environmentReferences"]!.AsArray().Single()!.GetValue<string>().Should().Be("NETRATEL_TOKEN");
        outbound.Requests[2].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be(plan);
        outbound.Requests[2].Body!.AsObject()["idempotencyKey"]!.GetValue<string>().Should().Be(key);
        outbound.Requests[4].Should().Be((HttpMethod.Post, "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/commands/0123456789abcdef0123456789abcdef/cancel", null));
    }

    [Fact]
    public async Task Terminal_facade_uses_only_exact_owned_v2_routes_and_preserves_preview_credentials()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorTerminalV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var open = new McpOperatorTerminalOpenV2("bash", "/srv/netratel", 120, 40);
        var resize = new McpOperatorTerminalResizeV2(140, 50);
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.GetAvailabilityAsync(target, CancellationToken.None);
        await client.PreviewOpenAsync(target, open, CancellationToken.None);
        await client.ConfirmOpenAsync(target, open, plan, key, CancellationToken.None);
        await client.GetAsync(target, sessionId, CancellationToken.None);
        await client.GetDiagnosticsAsync(target, sessionId, CancellationToken.None);
        await client.SendInputAsync(target, sessionId, "help\r\n", CancellationToken.None);
        await client.GetStreamWindowAsync(target, sessionId, 5, 10, CancellationToken.None);
        await client.ResizeAsync(target, sessionId, resize, CancellationToken.None);
        await client.CloseAsync(target, sessionId, CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/availability",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/0123456789abcdef0123456789abcdef",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/0123456789abcdef0123456789abcdef/diagnostics",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/0123456789abcdef0123456789abcdef/input",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/0123456789abcdef0123456789abcdef/stream-window?windowSeconds=5&maxRecords=10",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/0123456789abcdef0123456789abcdef/resize",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/0123456789abcdef0123456789abcdef/close");
        outbound.Requests[2].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be(plan);
        outbound.Requests[5].Body!.AsObject()["input"]!.GetValue<string>().Should().Be("help\r\n");
        outbound.Requests[7].Body!.AsObject()["columns"]!.GetValue<int>().Should().Be(140);
        outbound.Requests[8].Should().Be((HttpMethod.Post, "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/terminal/sessions/0123456789abcdef0123456789abcdef/close", null));
    }

    [Fact]
    public async Task Script_facade_uses_only_exact_owned_v2_routes_and_preserves_script_contracts()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorScriptV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var draft = CreateScriptDraft();
        var create = new McpOperatorScriptMutationV2(Script: draft);
        var update = new McpOperatorScriptMutationV2(Script: draft, ScriptId: 91, ExpectedVersion: 2);
        var parseManifest = new McpOperatorScriptMutationV2(ScriptId: 91, ExpectedVersion: 2, ManifestJson: """{"schemaVersion":1}""");
        var delete = new McpOperatorScriptMutationV2(ScriptId: 91, ExpectedVersion: 2);
        var run = new McpOperatorScriptRunV2(91, 2, draft.ContentHash, new Dictionary<string, string> { ["scope"] = "reviewed" });
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.ListAsync(target, CancellationToken.None);
        await client.GetAsync(target, 91, CancellationToken.None);
        await client.GetParametersAsync(target, 91, CancellationToken.None);
        await client.ValidateAsync(target, draft, CancellationToken.None);
        await client.PreviewMutationAsync(target, "create", create, CancellationToken.None);
        await client.ConfirmMutationAsync(target, "update", update, plan, key, CancellationToken.None);
        await client.PreviewMutationAsync(target, "parse_manifest", parseManifest, CancellationToken.None);
        await client.ConfirmMutationAsync(target, "delete", delete, plan, key, CancellationToken.None);
        await client.PreviewRunAsync(target, run, CancellationToken.None);
        await client.ConfirmRunAsync(target, run, plan, key, CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/91",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/91/params",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/validate",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/preview/create",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/confirm/update",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/preview/parse_manifest",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/confirm/delete",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/91/runs/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/scripts/91/runs/confirm");
        outbound.Requests[..3].Should().OnlyContain(call => call.Method == HttpMethod.Get);
        var validatedDraft = outbound.Requests[3].Body!.AsObject();
        validatedDraft["contentHash"]!.GetValue<string>().Should().Be(draft.ContentHash);
        validatedDraft["parameters"]!.AsArray().Single()!.AsObject()["name"]!.GetValue<string>().Should().Be("scope");
        outbound.Requests[5].Body!.AsObject()["expectedVersion"]!.GetValue<long>().Should().Be(2);
        outbound.Requests[5].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be(plan);
        outbound.Requests[6].Body!.AsObject()["manifestJson"]!.GetValue<string>().Should().Be("""{"schemaVersion":1}""");
        outbound.Requests[7].Body!.AsObject()["idempotencyKey"]!.GetValue<string>().Should().Be(key);
        outbound.Requests[8].Body!.AsObject()["parameters"]!.AsObject()["scope"]!.GetValue<string>().Should().Be("reviewed");
        outbound.Requests[9].Body!.AsObject()["contentHash"]!.GetValue<string>().Should().Be(draft.ContentHash);
    }

    [Fact]
    public void Script_facade_rejects_invalid_action_hash_and_secret_parameter_default_before_dispatch()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorScriptV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var validDraft = CreateScriptDraft();
        var wrongHash = validDraft with { ContentHash = new string('0', 64) };
        var sensitiveDefault = validDraft with
        {
            Parameters = [new McpOperatorScriptParameterV2("api_token", "string", true, DefaultValue: "not-permitted")]
        };

        Action invalidAction = () => client.PreviewMutationAsync(target, "run", new McpOperatorScriptMutationV2(Script: validDraft), CancellationToken.None);
        Action invalidHash = () => client.ValidateAsync(target, wrongHash, CancellationToken.None);
        Action inlineSecret = () => client.ValidateAsync(target, sensitiveDefault, CancellationToken.None);

        invalidAction.Should().Throw<AgentClientValidationException>();
        invalidHash.Should().Throw<AgentClientValidationException>();
        inlineSecret.Should().Throw<AgentClientValidationException>();
        outbound.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Job_facade_uses_only_exact_owned_v2_routes_and_preserves_lifecycle_credentials()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorJobV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var draft = CreateJobDraft();
        var parameter = new McpOperatorJobParameterV2("scope", "string", true, Description: "Bounded job scope.");
        var step = new McpOperatorJobStepV2(1, 44, 3, new string('A', 64));
        var run = new McpOperatorJobRunV2(new Dictionary<string, string> { ["scope"] = "reviewed" });
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.ListAsync(target, CancellationToken.None);
        await client.GetAsync(target, 91, CancellationToken.None);
        await client.GetDetailsAsync(target, 91, CancellationToken.None);
        await client.GetParametersAsync(target, 91, CancellationToken.None);
        await client.GetStepsAsync(target, 91, CancellationToken.None);
        await client.ValidateAsync(target, draft, CancellationToken.None);
        await client.PreviewMutationAsync(target, "create", new McpOperatorJobMutationV2(Job: draft), CancellationToken.None);
        await client.ConfirmMutationAsync(target, "update", new McpOperatorJobMutationV2(Job: draft, JobId: 91, ExpectedVersion: 2), plan, key, CancellationToken.None);
        await client.PreviewMutationAsync(target, "param_add", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, Parameter: parameter), CancellationToken.None);
        await client.ConfirmMutationAsync(target, "param_update", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, ParameterId: 4, Parameter: parameter), plan, key, CancellationToken.None);
        await client.PreviewMutationAsync(target, "param_delete", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, ParameterId: 4), CancellationToken.None);
        await client.ConfirmMutationAsync(target, "step_add", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, Step: step), plan, key, CancellationToken.None);
        await client.PreviewMutationAsync(target, "step_update", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, StepId: 5, Step: step), CancellationToken.None);
        await client.ConfirmMutationAsync(target, "step_reorder", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, StepId: 5, Ordinal: 2), plan, key, CancellationToken.None);
        await client.PreviewMutationAsync(target, "step_delete", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, StepId: 5), CancellationToken.None);
        await client.ConfirmMutationAsync(target, "delete", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2), plan, key, CancellationToken.None);
        await client.ListRunsAsync(target, cancellationToken: CancellationToken.None);
        await client.QueryRunsAsync(target, 91, CancellationToken.None);
        await client.GetRunAsync(target, 811, CancellationToken.None);
        await client.GetRunStepsAsync(target, 811, CancellationToken.None);
        await client.GetRunLogsAsync(target, 811, CancellationToken.None);
        await client.PreviewStartRunAsync(target, 91, run, CancellationToken.None);
        await client.ConfirmStartRunAsync(target, 91, run, plan, key, CancellationToken.None);
        await client.PreviewRunActionAsync(target, "cancel", 811, new McpOperatorJobRunActionV2("Operator requested cancellation."), CancellationToken.None);
        await client.ConfirmRunActionAsync(target, "delete", 811, new McpOperatorJobRunActionV2("Retention cleanup."), plan, key, CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/91",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/91/details",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/91/params",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/91/steps",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/validate",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/preview/create",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/confirm/update",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/preview/param_add",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/confirm/param_update",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/preview/param_delete",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/confirm/step_add",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/preview/step_update",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/confirm/step_reorder",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/preview/step_delete",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/confirm/delete",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/runs",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/runs/query?jobId=91",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/runs/811",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/runs/811/steps",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/runs/811/logs",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/91/runs/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/91/runs/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/runs/811/cancel/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/jobs/runs/811/delete/confirm");
        outbound.Requests[..5].Should().OnlyContain(call => call.Method == HttpMethod.Get);
        outbound.Requests[7].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be(plan);
        outbound.Requests[9].Body!.AsObject()["parameterId"]!.GetValue<long>().Should().Be(4);
        outbound.Requests[11].Body!.AsObject()["step"]!.AsObject()["scriptId"]!.GetValue<long>().Should().Be(44);
        outbound.Requests[13].Body!.AsObject()["ordinal"]!.GetValue<int>().Should().Be(2);
        outbound.Requests[22].Body!.AsObject()["idempotencyKey"]!.GetValue<string>().Should().Be(key);
        outbound.Requests[23].Body!.AsObject()["reason"]!.GetValue<string>().Should().Be("Operator requested cancellation.");
        outbound.Requests[24].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be(plan);
    }

    [Fact]
    public void Job_facade_rejects_invalid_actions_hashes_and_inline_secret_defaults_before_dispatch()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorJobV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var draft = CreateJobDraft();
        var invalidStep = new McpOperatorJobStepV2(1, 44, 3, "invalid");
        var sensitiveParameter = new McpOperatorJobParameterV2("api_token", "string", true, DefaultValue: "not-permitted");

        Action invalidAction = () => client.PreviewMutationAsync(target, "run", new McpOperatorJobMutationV2(Job: draft), CancellationToken.None);
        Action invalidStepHash = () => client.PreviewMutationAsync(target, "step_add", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, Step: invalidStep), CancellationToken.None);
        Action inlineSecret = () => client.PreviewMutationAsync(target, "param_add", new McpOperatorJobMutationV2(JobId: 91, ExpectedVersion: 2, Parameter: sensitiveParameter), CancellationToken.None);

        invalidAction.Should().Throw<AgentClientValidationException>();
        invalidStepHash.Should().Throw<AgentClientValidationException>();
        inlineSecret.Should().Throw<AgentClientValidationException>();
        outbound.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Observability_facade_uses_only_exact_owned_v2_routes_and_preserves_resync_credentials()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorObservabilityV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var history = new McpOperatorLogQueryV2("agent-log", Cursor: "cursor-1", PageSize: 25);
        var search = new McpOperatorLogQueryV2(
            "agent-log",
            PageSize: 25,
            Severity: ["warning"],
            Prefix: ["svc"],
            Category: ["system"],
            Provider: ["netratel"],
            EventId: [44],
            Text: "disk");
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.GetLogSourcesAsync(target, CancellationToken.None);
        await client.GetLogHistoryAsync(target, history, CancellationToken.None);
        await client.SearchLogsAsync(target, search, CancellationToken.None);
        await client.GetLogTailAsync(target, new McpOperatorLogTailV2("agent-log", 5, 10), CancellationToken.None);
        await client.PreviewLogResyncAsync(target, "agent-log", CancellationToken.None);
        await client.ConfirmLogResyncAsync(target, "agent-log", plan, key, CancellationToken.None);
        await client.GetTelemetrySnapshotAsync(target, CancellationToken.None);
        await client.GetTelemetryWindowAsync(target, new McpOperatorTelemetryWindowV2(5, 10), CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/logs/sources",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/logs/history?sourceId=agent-log&cursor=cursor-1&pageSize=25",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/logs/search?sourceId=agent-log&pageSize=25&text=disk&severity=warning&prefix=svc&category=system&provider=netratel&eventId=44",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/logs/tail?sourceId=agent-log&windowSeconds=5&maxRecords=10",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/logs/resync/preview",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/logs/resync/confirm",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/telemetry/snapshot",
            "/api/v2/mcp/operator/agents/7/11111111-2222-3333-4444-555555555555/telemetry/stream-window?windowSeconds=5&maxSamples=10");
        outbound.Requests[..4].Should().OnlyContain(call => call.Method == HttpMethod.Get);
        outbound.Requests[4].Body!.AsObject()["sourceId"]!.GetValue<string>().Should().Be("agent-log");
        outbound.Requests[5].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be(plan);
        outbound.Requests[5].Body!.AsObject()["idempotencyKey"]!.GetValue<string>().Should().Be(key);
    }

    [Fact]
    public void Observability_facade_rejects_unbounded_source_search_and_window_inputs_before_dispatch()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorObservabilityV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));

        Action invalidSource = () => client.PreviewLogResyncAsync(target, "\0", CancellationToken.None);
        Action missingSearchText = () => client.SearchLogsAsync(target, new McpOperatorLogQueryV2("agent-log"), CancellationToken.None);
        Action invalidWindow = () => client.GetTelemetryWindowAsync(target, new McpOperatorTelemetryWindowV2(16, 1), CancellationToken.None);

        invalidSource.Should().Throw<AgentClientValidationException>();
        missingSearchText.Should().Throw<AgentClientValidationException>();
        invalidWindow.Should().Throw<AgentClientValidationException>();
        outbound.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Access_facade_uses_only_exact_delegation_bound_v2_routes()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorAccessV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));

        await client.WhoAmIAsync(CancellationToken.None);
        await client.GetTargetAsync(target, CancellationToken.None);
        await client.GetEffectiveAsync(target, CancellationToken.None);
        await client.EvaluateAsync(target, "netratel_files", "browse", CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/access/whoami",
            "/api/v2/mcp/operator/access/agents/7/11111111-2222-3333-4444-555555555555",
            "/api/v2/mcp/operator/access/agents/7/11111111-2222-3333-4444-555555555555/effective",
            "/api/v2/mcp/operator/access/agents/7/11111111-2222-3333-4444-555555555555/evaluate?tool=netratel_files&operation=browse");
        outbound.Requests.Should().OnlyContain(call => call.Method == HttpMethod.Get);
    }

    [Fact]
    public void Access_facade_rejects_an_unbounded_or_unsafe_catalog_identifier_before_dispatch()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorAccessV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));

        Action invalid = () => client.EvaluateAsync(target, "netratel_files", "../browse", CancellationToken.None);

        invalid.Should().Throw<AgentClientValidationException>();
        outbound.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Policy_facade_uses_only_exact_delegation_bound_v2_routes()
    {
        var outbound = new RecordingOutbound("dev");
        var client = new McpOperatorPolicyV2Client(outbound);
        var target = new McpOperatorV2Target(7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var policyId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        var selector = new McpOperatorPolicyTargetSelectorV2(McpOperatorPolicyTargetSelectorKindV2.Tenant, 7);
        var policy = new McpOperatorPolicyDraftV2(
            "reviewed tenant observation",
            McpOperatorPolicyEnvironmentV2.Development,
            McpOperatorPolicyEffectV2.Allow,
            10,
            new McpOperatorPolicyPrincipalSelectorV2(McpOperatorPolicyPrincipalSelectorKindV2.OidcGroup, "incident-responders"),
            selector,
            McpOperatorPolicyOperationFamilyV2.Observability,
            new McpOperatorPolicyConstraintsV2());
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        await client.ListAsync(McpOperatorPolicyEnvironmentV2.Development, 7, CancellationToken.None);
        await client.GetAsync(policyId, CancellationToken.None);
        await client.ListChangeAuditsAsync(7, policyId, target.AgentId, CancellationToken.None);
        await client.ListAcceptedAuditsAsync(7, target.AgentId, "policy.admin@example.test", 25, CancellationToken.None);
        await client.GetTargetAsync(target, CancellationToken.None);
        await client.GetMatchesAsync(target, CancellationToken.None);
        await client.EvaluateAsync(target, "netratel_terminal", "availability", CancellationToken.None);
        await client.PreviewCreateAsync(policy, CancellationToken.None);
        await client.ConfirmCreateAsync(policy, plan, key, CancellationToken.None);
        await client.PreviewReplaceAsync(policyId, 1, policy, CancellationToken.None);
        await client.ConfirmReplaceAsync(policyId, 1, policy, plan, key, CancellationToken.None);
        await client.PreviewDisableAsync(policyId, 1, selector, CancellationToken.None);
        await client.ConfirmDisableAsync(policyId, 1, selector, plan, key, CancellationToken.None);
        await client.PreviewRevokeAsync(policyId, 1, selector, CancellationToken.None);
        await client.ConfirmRevokeAsync(policyId, 1, selector, plan, key, CancellationToken.None);

        outbound.Requests.Select(call => call.Path).Should().Equal(
            "/api/v2/mcp/operator/policy?environment=Development&tenantId=7",
            "/api/v2/mcp/operator/policy/policies/66666666-7777-8888-9999-aaaaaaaaaaaa",
            "/api/v2/mcp/operator/policy/change-audits?tenantId=7&policyId=66666666-7777-8888-9999-aaaaaaaaaaaa&agentId=11111111-2222-3333-4444-555555555555",
            "/api/v2/mcp/operator/policy/accepted-audits?tenantId=7&agentId=11111111-2222-3333-4444-555555555555&subject=policy.admin%40example.test&limit=25",
            "/api/v2/mcp/operator/policy/targets/7/11111111-2222-3333-4444-555555555555",
            "/api/v2/mcp/operator/policy/matches/7/11111111-2222-3333-4444-555555555555",
            "/api/v2/mcp/operator/policy/evaluate/7/11111111-2222-3333-4444-555555555555?tool=netratel_terminal&operation=availability",
            "/api/v2/mcp/operator/policy/create/preview",
            "/api/v2/mcp/operator/policy/create/confirm",
            "/api/v2/mcp/operator/policy/replace/preview",
            "/api/v2/mcp/operator/policy/replace/confirm",
            "/api/v2/mcp/operator/policy/disable/preview",
            "/api/v2/mcp/operator/policy/disable/confirm",
            "/api/v2/mcp/operator/policy/revoke/preview",
            "/api/v2/mcp/operator/policy/revoke/confirm");
        outbound.Requests.Skip(7).Should().OnlyContain(call => call.Method == HttpMethod.Post);
        outbound.Requests[7].Body!.AsObject()["policy"]!.AsObject()["environment"]!.GetValue<short>().Should().Be(1);
        outbound.Requests[8].Body!.AsObject()["idempotencyKey"]!.GetValue<string>().Should().Be(key);
        outbound.Requests[14].Body!.AsObject()["target"]!.AsObject()["tenantId"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public void Policy_facade_rejects_a_cross_environment_draft_before_dispatch()
    {
        var outbound = new RecordingOutbound("prod");
        var client = new McpOperatorPolicyV2Client(outbound);
        var policy = new McpOperatorPolicyDraftV2(
            "reviewed tenant observation",
            McpOperatorPolicyEnvironmentV2.Development,
            McpOperatorPolicyEffectV2.Allow,
            10,
            new McpOperatorPolicyPrincipalSelectorV2(McpOperatorPolicyPrincipalSelectorKindV2.OidcGroup, "incident-responders"),
            new McpOperatorPolicyTargetSelectorV2(McpOperatorPolicyTargetSelectorKindV2.Tenant, 7),
            McpOperatorPolicyOperationFamilyV2.Observability,
            new McpOperatorPolicyConstraintsV2());

        Action invalid = () => client.PreviewCreateAsync(policy, CancellationToken.None);

        invalid.Should().Throw<AgentClientValidationException>();
        outbound.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Policy_facade_rejects_an_unapproved_outbound_instance_before_dispatch()
    {
        var outbound = new RecordingOutbound("staging");
        var client = new McpOperatorPolicyV2Client(outbound);

        Action invalid = () => client.GetAsync(Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"), CancellationToken.None);

        invalid.Should().Throw<AgentClientValidationException>();
        outbound.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Outbound_registration_resolves_every_route_bound_v2_facade()
    {
        var target = new NetRatelMcpTarget(
            "prod",
            new Uri("https://api.example/"),
            new Uri("https://mcp.example/mcp"),
            NetRatelMcpCatalog.Revision);
        var options = NetRatelMcpOutboundOptions.CreateApiM2M(
            target,
            new Uri("https://api.example/connect/token"),
            "test-client",
            "test-secret",
            "netratel.mcp.read");

        using var provider = new ServiceCollection()
            .AddNetRatelMcpOutboundClient(options)
            .BuildServiceProvider();

        provider.GetRequiredService<IMcpOperatorFileV2Client>().Should().BeOfType<McpOperatorFileV2Client>();
        provider.GetRequiredService<IMcpOperatorCommandV2Client>().Should().BeOfType<McpOperatorCommandV2Client>();
        provider.GetRequiredService<IMcpOperatorTerminalV2Client>().Should().BeOfType<McpOperatorTerminalV2Client>();
        provider.GetRequiredService<IMcpOperatorJobV2Client>().Should().BeOfType<McpOperatorJobV2Client>();
        provider.GetRequiredService<IMcpOperatorObservabilityV2Client>().Should().BeOfType<McpOperatorObservabilityV2Client>();
        provider.GetRequiredService<IMcpOperatorPolicyV2Client>().Should().BeOfType<McpOperatorPolicyV2Client>();
        provider.GetRequiredService<IMcpOperatorAccessV2Client>().Should().BeOfType<McpOperatorAccessV2Client>();
        provider.GetRequiredService<IMcpOperatorScriptV2Client>().Should().BeOfType<McpOperatorScriptV2Client>();
        provider.GetRequiredService<IMcpOperatorEventV2Client>().Should().BeOfType<McpOperatorEventV2Client>();
        provider.GetRequiredService<IMcpOperatorConnectivityV2Client>().Should().BeOfType<McpOperatorConnectivityV2Client>();
    }

    private static McpOperatorScriptDraftV2 CreateScriptDraft()
    {
        const string content = "printf reviewed-value";
        return new McpOperatorScriptDraftV2(
            "reviewed-script",
            "Runs one bounded reviewed command.",
            "bash",
            content,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
            [new McpOperatorScriptParameterV2("scope", "string", true, Description: "Bounded run scope.")],
            30,
            "/srv/netratel",
            [1]);
    }

    private static McpOperatorJobDraftV2 CreateJobDraft() =>
        new("reviewed-job", "Review", "Runs the reviewed script against one exact agent.", """{"schedule":"manual"}""");

    private sealed class RecordingOutbound(string instance) : INetRatelMcpOutboundClient
    {
        public NetRatelMcpTarget Target { get; } = new(instance, new Uri("https://api.example/"), new Uri("https://mcp.example/mcp"), NetRatelMcpCatalog.Revision);
        public List<(HttpMethod Method, string Path, JsonNode? Body)> Requests { get; } = [];
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Requests.Add((HttpMethod.Get, path, null));
            return Task.FromResult<JsonNode?>(new JsonObject());
        }
        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            Requests.Add((method, path, body));
            return Task.FromResult<JsonNode?>(new JsonObject());
        }
    }
}
