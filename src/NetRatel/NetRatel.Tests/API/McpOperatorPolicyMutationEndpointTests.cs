using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorPolicyMutationEndpointTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("disable")]
    [InlineData("revoke")]
    public async Task Development_wide_lifecycle_requires_development_wide_parent_at_preview_and_confirmation(string action)
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var administration = new McpOperatorPolicyAdministration(db);
        var draft = Draft() with { TargetSelector = new(McpOperatorTargetSelectorKind.DevelopmentEnvironment, 0) };
        var parentDraft = draft with
        {
            Name = "independent policy administration authority",
            PrincipalSelector = new(McpOperatorPrincipalSelectorKind.OAuthSubject, "policy.admin@example.test"),
            TargetSelector = new(McpOperatorTargetSelectorKind.ControlPlane, 0),
            OperationFamily = McpOperatorOperationFamily.PolicyAdministration,
            Operation = $"netratel_policy/{action}",
            Constraints = new(RequiredConfirmationClass: McpOperatorConfirmationClass.PolicyAdministration)
        };
        var parent = await administration.CreateAsync(parentDraft, "independent-admin@example.test", CancellationToken.None);
        var stored = await administration.CreateAsync(draft, "independent-admin@example.test", CancellationToken.None);
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration,
            new McpOperatorConfirmationService(db), accountRoutes: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        var path = $"/api/v2/mcp-operator/policies/manage/{action}";
        object previewRequest = action == "create" ? new PreviewMcpOperatorPolicyCreateRequest(draft)
            : PolicyPreviewRequest(action, stored, draft);

        // Both selectors use tenant zero and no agent. A control-plane parent
        // must still be unable to administer an all-tenants Development grant.
        var deniedPreview = await client.PostAsJsonAsync($"{path}/preview", previewRequest);
        deniedPreview.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await db.McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);

        parent = await administration.ReplaceAsync(parent.PolicyId, parent.Version,
            parentDraft with { TargetSelector = draft.TargetSelector }, "independent-admin@example.test", CancellationToken.None);
        var previewResponse = await client.PostAsJsonAsync($"{path}/preview", previewRequest);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        object confirmation = action == "create" ? new ConfirmMcpOperatorPolicyCreateRequest(draft, preview.PlanToken, preview.IdempotencyKey)
            : PolicyConfirmRequest(action, stored, draft, preview);

        parent = await administration.ReplaceAsync(parent.PolicyId, parent.Version, parentDraft,
            "independent-admin@example.test", CancellationToken.None);
        var deniedConfirmation = await client.PostAsJsonAsync($"{path}/confirm", confirmation);
        deniedConfirmation.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
        (await administration.GetAsync(stored.PolicyId, CancellationToken.None)).Should().BeEquivalentTo(stored);

        await administration.ReplaceAsync(parent.PolicyId, parent.Version,
            parentDraft with { TargetSelector = draft.TargetSelector }, "independent-admin@example.test", CancellationToken.None);
        var deniedParent = await administration.CreateAsync(parentDraft with { Effect = McpOperatorPolicyEffect.Deny },
            "independent-admin@example.test", CancellationToken.None);
        var deniedByExistingControlPlaneRule = await client.PostAsJsonAsync($"{path}/preview", previewRequest);
        deniedByExistingControlPlaneRule.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "restricting positive parent authority must preserve matching control-plane deny rules");
        await administration.DisableAsync(deniedParent.PolicyId, deniedParent.Version, "independent-admin@example.test", CancellationToken.None);
        var freshPreviewResponse = await client.PostAsJsonAsync($"{path}/preview", previewRequest);
        freshPreviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var freshPreview = (await freshPreviewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        object freshConfirmation = action == "create" ? new ConfirmMcpOperatorPolicyCreateRequest(draft, freshPreview.PlanToken, freshPreview.IdempotencyKey)
            : PolicyConfirmRequest(action, stored, draft, freshPreview);
        var accepted = await client.PostAsJsonAsync($"{path}/confirm", freshConfirmation);
        accepted.StatusCode.Should().Be(action == "create" ? HttpStatusCode.Created : HttpStatusCode.OK);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("disable")]
    [InlineData("revoke")]
    public async Task Account_lifecycle_uses_the_same_target_bound_durable_confirmation_and_audit(string action)
    {
        var draft = ExactAgentDraft() with { Constraints = new(WorkingDirectories: ["/tmp"]) };
        await using var db = await CreatePolicyDbAsync(action, draft.TargetSelector);
        var administration = new McpOperatorPolicyAdministration(db);
        var stored = await CreateStoredPolicyAsync(db, administration, draft);
        var proposed = draft with { Constraints = draft.Constraints with { WorkingDirectories = ["/tmp", "/tmp/example"] } };
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db), accountRoutes: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        var path = $"/api/v2/mcp-operator/policies/manage/{action}";
        object previewRequest = action == "create" ? new PreviewMcpOperatorPolicyCreateRequest(proposed) : PolicyPreviewRequest(action, stored, proposed);
        var previewResponse = await client.PostAsJsonAsync($"{path}/preview", previewRequest);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        object confirmRequest = action == "create" ? new ConfirmMcpOperatorPolicyCreateRequest(proposed, preview.PlanToken, preview.IdempotencyKey)
            : PolicyConfirmRequest(action, stored, proposed, preview);

        var response = await client.PostAsJsonAsync($"{path}/confirm", confirmRequest);
        response.StatusCode.Should().Be(action == "create" ? HttpStatusCode.Created : HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>())!;
        var replay = await client.PostAsJsonAsync($"{path}/confirm", confirmRequest);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>())!.Replay.Should().BeTrue();
        var persisted = (await administration.GetAsync(result.Policy.PolicyId, CancellationToken.None))!;
        if (action is "create" or "replace") persisted.Constraints.WorkingDirectories.Should().Equal("/tmp", "/tmp/example");
        else persisted.LifecycleState.Should().Be(action == "disable" ? McpOperatorPolicyLifecycleState.Disabled : McpOperatorPolicyLifecycleState.Revoked);
        var audit = await db.McpOperatorAcceptedAudits.SingleAsync();
        audit.Subject.Should().Be("policy.admin@example.test");
        audit.ServicePrincipal.Should().Be("netratel-web");
        audit.McpResource.Should().Be("netratel-web-policy-administration");
        audit.TenantId.Should().Be(draft.TargetSelector.TenantId);
        audit.AgentId.Should().Be(draft.TargetSelector.AgentId);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("AccountReadOnly", HttpStatusCode.Forbidden)]
    [InlineData("AccountCredential", HttpStatusCode.Unauthorized)]
    public async Task Account_policy_route_rejects_non_administrators_and_integration_credentials(string authentication, HttpStatusCode expected)
    {
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(new RecordingAuthorization(true), administration, confirmations, accountRoutes: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(authentication);
        var response = await client.PostAsJsonAsync("/api/v2/mcp-operator/policies/manage/create/preview", new PreviewMcpOperatorPolicyCreateRequest(Draft()));
        response.StatusCode.Should().Be(expected);
        confirmations.Plans.Should().BeEmpty();
        administration.Creates.Should().Be(0);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    public async Task Account_lifecycle_rejects_padded_caller_selectors_before_plan_or_persistence(string action)
    {
        var draft = ExactAgentDraft();
        await using var db = await CreatePolicyDbAsync(action, draft.TargetSelector);
        var administration = new McpOperatorPolicyAdministration(db);
        var stored = await CreateStoredPolicyAsync(db, administration, draft);
        var proposed = draft with { PrincipalSelector = new(McpOperatorPrincipalSelectorKind.OAuthSubject, " policy.admin@example.test ") };
        var policyCount = await db.McpOperatorPolicies.CountAsync();
        var auditCount = await db.McpOperatorPolicyChangeAudits.CountAsync();
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db), accountRoutes: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        object request = action == "create" ? new PreviewMcpOperatorPolicyCreateRequest(proposed)
            : new PreviewMcpOperatorPolicyReplaceRequest(stored.PolicyId, stored.Version, proposed);

        var response = await client.PostAsJsonAsync($"/api/v2/mcp-operator/policies/manage/{action}/preview", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("validation_error");
        (await db.McpOperatorPolicies.CountAsync()).Should().Be(policyCount);
        (await db.McpOperatorPolicyChangeAudits.CountAsync()).Should().Be(auditCount);
        (await administration.GetAsync(stored.PolicyId, CancellationToken.None)).Should().BeEquivalentTo(stored);
        (await db.McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("version")]
    [InlineData("self")]
    public async Task Account_confirmation_rejects_lost_authority_stale_versions_and_self_escalation(string change)
    {
        var draft = ExactAgentDraft();
        await using var db = await CreatePolicyDbAsync("replace", draft.TargetSelector);
        var administration = new McpOperatorPolicyAdministration(db);
        var stored = await CreateStoredPolicyAsync(db, administration, draft);
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db), accountRoutes: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        const string path = "/api/v2/mcp-operator/policies/manage/replace";
        var previewResponse = await client.PostAsJsonAsync($"{path}/preview", new PreviewMcpOperatorPolicyReplaceRequest(stored.PolicyId, 1, draft));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        if (change == "authority") client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("AccountReadOnly");
        if (change == "version") await administration.ReplaceAsync(stored.PolicyId, 1, draft with { Name = "Independent reviewed edit" }, "independent-admin@example.test", CancellationToken.None);
        if (change == "self") draft = draft with { PrincipalSelector = new(McpOperatorPrincipalSelectorKind.OAuthSubject, "policy.admin@example.test") };
        var before = (await administration.GetAsync(stored.PolicyId, CancellationToken.None))!;
        var response = await client.PostAsJsonAsync($"{path}/confirm", new ConfirmMcpOperatorPolicyReplaceRequest(stored.PolicyId, 1, draft, preview.PlanToken, preview.IdempotencyKey));
        response.StatusCode.Should().Be(change == "authority" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict);
        (await administration.GetAsync(stored.PolicyId, CancellationToken.None)).Should().BeEquivalentTo(before);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("disable")]
    [InlineData("revoke")]
    public async Task Exact_agent_policy_lifecycle_uses_persisted_classification_and_canonical_action(string action)
    {
        var draft = ExactAgentDraft();
        await using var db = await CreatePolicyDbAsync(action, draft.TargetSelector);
        var administration = new RecordingAdministration { CurrentTargetProfile = TargetProfile(draft.TargetSelector) };
        administration.CurrentPolicy = administration.CreatedPolicy with { TargetSelector = draft.TargetSelector };
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db));
        var client = M2mClient(app);
        object previewRequest = action switch
        {
            "create" => new PreviewMcpOperatorPolicyCreateRequest(draft),
            "replace" => new PreviewMcpOperatorPolicyReplaceRequest(administration.CreatedPolicy.PolicyId, 1, draft),
            _ => new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, draft.TargetSelector)
        };
        SetDelegation(client, app, $"preview_{action}", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);

        var previewResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/preview", previewRequest);

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        object confirmRequest = action switch
        {
            "create" => new ConfirmMcpOperatorPolicyCreateRequest(draft, preview.PlanToken, preview.IdempotencyKey),
            "replace" => new ConfirmMcpOperatorPolicyReplaceRequest(administration.CreatedPolicy.PolicyId, 1, draft, preview.PlanToken, preview.IdempotencyKey),
            _ => new ConfirmMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, draft.TargetSelector, preview.PlanToken, preview.IdempotencyKey)
        };
        SetDelegation(client, app, $"confirm_{action}", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);

        var confirmed = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/confirm", confirmRequest);

        confirmed.StatusCode.Should().Be(action == "create" ? HttpStatusCode.Created : HttpStatusCode.OK);
        var audit = await db.McpOperatorAcceptedAudits.SingleAsync();
        audit.Operation.Should().Be($"netratel_policy/{action}");
        audit.TenantId.Should().Be(draft.TargetSelector.TenantId);
        audit.AgentId.Should().Be(draft.TargetSelector.AgentId);
        administration.GuardedMutations.Should().Be(1);
        (administration.Creates + administration.Replaces + administration.Disables + administration.Revocations).Should().Be(1);
    }

    [Theory]
    [InlineData("replace", false)]
    [InlineData("disable", false)]
    [InlineData("revoke", false)]
    [InlineData("disable", true)]
    public async Task Policy_mutation_replay_preserves_the_original_result_and_does_not_consume_a_fresh_stale_plan(string action, bool simulateTimestampPrecisionLoss)
    {
        var draft = ExactAgentDraft() with
        {
            Name = "  reviewed observation policy  ",
            PrincipalSelector = new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "incident-responders"),
            AuditReference = "  change-fixture  "
        };
        await using var db = await CreatePolicyDbAsync(action, draft.TargetSelector);
        var administration = new McpOperatorPolicyAdministration(db);
        var policy = await CreateStoredPolicyAsync(db, administration, draft);
        if (simulateTimestampPrecisionLoss)
        {
            db.SavingChanges += SetSubMicrosecondTimestamp;
            void SetSubMicrosecondTimestamp(object? sender, SavingChangesEventArgs args)
            {
                var record = db.ChangeTracker.Entries<McpOperatorPolicyRecord>().Single(entry => entry.Entity.Id == policy.PolicyId).Entity;
                if (record.DisabledAtUtc is not { } timestamp) return;
                record.DisabledAtUtc = new DateTimeOffset(timestamp.UtcTicks - timestamp.UtcTicks % TimeSpan.TicksPerMicrosecond + 7, TimeSpan.Zero);
                db.SavingChanges -= SetSubMicrosecondTimestamp;
            }
        }
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db));
        var client = M2mClient(app);
        SetDelegation(client, app, $"preview_{action}", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewRequest = PolicyPreviewRequest(action, policy, draft);
        var previewResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/preview", previewRequest);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        var stalePreviewResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/preview", previewRequest);
        stalePreviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var stalePreview = (await stalePreviewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        SetDelegation(client, app, $"confirm_{action}", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var request = PolicyConfirmRequest(action, policy, draft, preview);

        var confirmedResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/confirm", request);
        confirmedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmed = (await confirmedResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>())!;
        if (action == "disable" && !simulateTimestampPrecisionLoss)
        {
            confirmed.Policy.DisabledAtUtc.Should().NotBeNull();
            (confirmed.Policy.DisabledAtUtc!.Value.Ticks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
            confirmed.Policy.DisabledAtUtc.Should().Be((await administration.GetAsync(policy.PolicyId, CancellationToken.None))!.DisabledAtUtc);
        }
        if (simulateTimestampPrecisionLoss)
        {
            var originalTimestamp = confirmed.Policy.DisabledAtUtc!.Value;
            (originalTimestamp.UtcTicks % TimeSpan.TicksPerMicrosecond).Should().Be(7);
            var record = await db.McpOperatorPolicies.SingleAsync(candidate => candidate.Id == policy.PolicyId);
            // Simulate the timestamp returned by a PostgreSQL reload, leaving the receipt intact.
            record.DisabledAtUtc = new DateTimeOffset(originalTimestamp.UtcTicks - originalTimestamp.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
            await db.SaveChangesAsync();
            record.DisabledAtUtc.Should().NotBe(originalTimestamp);
        }
        var replayResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/confirm", request);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = (await replayResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>())!;
        replay.Replay.Should().BeTrue();
        replay.Policy.Should().BeEquivalentTo(confirmed.Policy);

        var staleResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/confirm",
            PolicyConfirmRequest(action, policy, draft, stalePreview));
        staleResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await staleResponse.Content.ReadAsStringAsync()).Should().Contain("operator_policy_version_conflict");
        var unused = await db.McpOperatorConfirmationPlans.SingleAsync(plan => plan.IdempotencyKey == stalePreview.IdempotencyKey);
        unused.ConsumedAtUtc.Should().BeNull();
        unused.ConsumedIdempotencyId.Should().BeNull();
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
        (await db.McpOperatorPolicyChangeAudits.CountAsync(audit => audit.PolicyId == policy.PolicyId)).Should().Be(2);

        if (action == "replace")
        {
            var later = await administration.ReplaceAsync(policy.PolicyId, confirmed.Policy.Version,
                draft with { Name = "later replacement", Constraints = new McpOperatorConstraints(MaxOutputBytes: 256) }, "independent-admin@example.test", CancellationToken.None);
            await administration.DisableAsync(policy.PolicyId, later.Version, "independent-admin@example.test", CancellationToken.None);
        }
        else if (action == "disable")
        {
            await administration.RevokeAsync(policy.PolicyId, confirmed.Policy.Version, "independent-admin@example.test", CancellationToken.None);
        }
        var changesBeforeReplay = await db.McpOperatorPolicyChangeAudits.CountAsync();
        var laterReplayResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/confirm", request);
        laterReplayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var laterReplay = (await laterReplayResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>())!;
        laterReplay.Replay.Should().BeTrue();
        laterReplay.Policy.Should().BeEquivalentTo(confirmed.Policy);
        (await db.McpOperatorPolicyChangeAudits.CountAsync()).Should().Be(changesBeforeReplay);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Create_replay_retains_original_definition_and_persisted_timestamps_after_later_changes()
    {
        var timestamp = DateTimeOffset.UtcNow.AddHours(1);
        timestamp = new DateTimeOffset(timestamp.UtcTicks - timestamp.UtcTicks % TimeSpan.TicksPerMicrosecond + 7, TimeSpan.Zero);
        var draft = ExactAgentDraft() with { Name = "  original reviewed grant  ", ExpiresAtUtc = timestamp,
            ReviewByUtc = timestamp.AddMinutes(-15), Constraints = new(WorkingDirectories: ["/tmp"], MaxOutputBytes: 1024) };
        await using var db = await CreatePolicyDbAsync("create", draft.TargetSelector);
        var administration = new McpOperatorPolicyAdministration(db);
        await CreateStoredPolicyAsync(db, administration, draft);
        // Model PostgreSQL's persisted timestamp precision on the first response.
        db.SavingChanges += (_, _) =>
        {
            foreach (var entry in db.ChangeTracker.Entries<McpOperatorPolicyRecord>().Where(entry => entry.State == EntityState.Added))
            {
                entry.Entity.CreatedAtUtc = Microseconds(entry.Entity.CreatedAtUtc);
                entry.Entity.ExpiresAtUtc = entry.Entity.ExpiresAtUtc is { } expiry ? Microseconds(expiry) : null;
                entry.Entity.ReviewByUtc = entry.Entity.ReviewByUtc is { } review ? Microseconds(review) : null;
            }
        };
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db), accountRoutes: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        const string path = "/api/v2/mcp-operator/policies/manage/create";
        var previewResponse = await client.PostAsJsonAsync($"{path}/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        var request = new ConfirmMcpOperatorPolicyCreateRequest(draft, preview.PlanToken, preview.IdempotencyKey);
        var response = await client.PostAsJsonAsync($"{path}/confirm", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var accepted = (await response.Content.ReadFromJsonAsync<McpOperatorPolicyCreateResult>())!;
        accepted.Policy.ExpiresAtUtc.Should().Be(Microseconds(timestamp));
        var receipt = await db.McpOperatorIdempotencyRecords.SingleAsync();
        var originalReference = receipt.ResultReference;
        originalReference.Should().StartWith("policy-create:");
        originalReference!.Length.Should().BeLessThanOrEqualTo(512);

        receipt.ResultReference = accepted.Policy.PolicyId.ToString("D");
        await db.SaveChangesAsync();
        var unchangedLegacy = await client.PostAsJsonAsync($"{path}/confirm", request);
        unchangedLegacy.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unchangedLegacy.Content.ReadFromJsonAsync<McpOperatorPolicyCreateResult>())!.Policy.Should().BeEquivalentTo(accepted.Policy);
        receipt.ResultReference = originalReference;
        await db.SaveChangesAsync();

        var replaced = await administration.ReplaceAsync(accepted.Policy.PolicyId, 1,
            draft with { Name = "later grant", Constraints = new(WorkingDirectories: ["/tmp/later"], MaxOutputBytes: 2048),
                ExpiresAtUtc = timestamp.AddHours(1), ReviewByUtc = timestamp.AddMinutes(20) }, "independent-admin@example.test", CancellationToken.None);
        var disabled = await administration.DisableAsync(replaced.PolicyId, replaced.Version, "independent-admin@example.test", CancellationToken.None);
        var auditsBeforeReplay = await db.McpOperatorPolicyChangeAudits.CountAsync();
        var replayResponse = await client.PostAsJsonAsync($"{path}/confirm", request);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = (await replayResponse.Content.ReadFromJsonAsync<McpOperatorPolicyCreateResult>())!;
        replay.Replay.Should().BeTrue();
        replay.Policy.Should().BeEquivalentTo(accepted.Policy);
        (await administration.GetAsync(replaced.PolicyId, CancellationToken.None)).Should().BeEquivalentTo(disabled);

        receipt.ResultReference = accepted.Policy.PolicyId.ToString("D");
        await db.SaveChangesAsync();
        var unavailableLegacy = await client.PostAsJsonAsync($"{path}/confirm", request);
        unavailableLegacy.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await unavailableLegacy.Content.ReadAsStringAsync()).Should().Contain("idempotency_replay_unavailable");
        (await db.McpOperatorPolicyChangeAudits.CountAsync()).Should().Be(auditsBeforeReplay);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);

        static DateTimeOffset Microseconds(DateTimeOffset value) =>
            new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("disable")]
    [InlineData("revoke")]
    public async Task Policy_mutation_with_a_lost_response_preserves_its_successful_receipt(string action)
    {
        using var disconnected = new CancellationTokenSource();
        var writes = new DisconnectAfterPolicyWrite(disconnected.Cancel);
        var draft = ExactAgentDraft();
        await using var db = await CreatePolicyDbAsync(action, draft.TargetSelector, writes);
        var administration = new McpOperatorPolicyAdministration(db);
        var policy = await CreateStoredPolicyAsync(db, administration, draft);
        writes.PolicyId = policy.PolicyId;
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db));
        var client = M2mClient(app);
        SetDelegation(client, app, $"preview_{action}", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/preview", PolicyPreviewRequest(action, policy, draft));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        var request = PolicyConfirmRequest(action, policy, draft, preview);
        SetDelegation(client, app, $"confirm_{action}", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);

        var lostResponse = async () => await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/confirm", request, disconnected.Token);

        await lostResponse.Should().ThrowAsync<OperationCanceledException>();
        await writes.ReceiptSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var persisted = (await administration.GetAsync(policy.PolicyId, CancellationToken.None))!;
        persisted.Version.Should().Be(2);
        var receipt = await db.McpOperatorIdempotencyRecords.AsNoTracking().SingleAsync();
        receipt.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
        var replayResponse = await client.PostAsJsonAsync($"/api/v2/mcp/operator/policy/{action}/confirm", request);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = (await replayResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>())!;
        replay.Replay.Should().BeTrue();
        replay.Policy.Should().BeEquivalentTo(persisted);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
        (await db.McpOperatorPolicyChangeAudits.CountAsync(audit => audit.PolicyId == policy.PolicyId)).Should().Be(2);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Policy_confirmation_with_a_pending_receipt_returns_retryable_conflict_without_redispatch()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new McpOperatorConfirmationService(db);
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var draft = Draft();
        var policy = administration.CurrentPolicy;
        SetDelegation(client, app, "preview_replace", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/replace/preview", PolicyPreviewRequest("replace", policy, draft));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        var currentDecision = await authorization.EvaluateAsync(authorization.Evaluated.Single(), CancellationToken.None);
        var plan = await db.McpOperatorConfirmationPlans.SingleAsync();
        // Reserve the first request without completing its operation, as when
        // another confirmation arrives while that admitted dispatch is in flight.
        var inFlight = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(
            preview.PlanToken, preview.IdempotencyKey, plan.PayloadHash, currentDecision), CancellationToken.None);
        inFlight.IsNewDispatch.Should().BeTrue();
        SetDelegation(client, app, "confirm_replace", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);

        var duplicate = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/replace/confirm", PolicyConfirmRequest("replace", policy, draft, preview));

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var failure = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        failure.RootElement.GetProperty("failure").GetProperty("code").GetString().Should().Be("idempotency_pending");
        failure.RootElement.GetProperty("failure").GetProperty("retryable").GetBoolean().Should().BeTrue();
        administration.Replaces.Should().Be(0);
        authorization.Accepted.Should().BeEmpty();
        var retained = await db.McpOperatorIdempotencyRecords.SingleAsync();
        retained.Id.Should().Be(inFlight.IdempotencyId!.Value);
        retained.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Pending);
    }

    [Theory]
    [InlineData("payload", HttpStatusCode.Conflict)]
    [InlineData("subject", HttpStatusCode.Forbidden)]
    [InlineData("client", HttpStatusCode.Conflict)]
    [InlineData("resource", HttpStatusCode.Conflict)]
    [InlineData("instance", HttpStatusCode.Unauthorized)]
    [InlineData("target", HttpStatusCode.Unauthorized)]
    [InlineData("policy_revoked", HttpStatusCode.Forbidden)]
    [InlineData("expired", HttpStatusCode.Conflict)]
    public async Task Policy_replay_requires_current_authority_exact_binding_and_an_unexpired_plan(string change, HttpStatusCode expectedStatus)
    {
        var draft = ExactAgentDraft();
        await using var db = await CreatePolicyDbAsync("replace", draft.TargetSelector);
        var administration = new McpOperatorPolicyAdministration(db);
        var policy = await CreateStoredPolicyAsync(db, administration, draft);
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db));
        var client = M2mClient(app);
        SetDelegation(client, app, "preview_replace", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/replace/preview", PolicyPreviewRequest("replace", policy, draft));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        SetDelegation(client, app, "confirm_replace", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var request = (ConfirmMcpOperatorPolicyReplaceRequest)PolicyConfirmRequest("replace", policy, draft, preview);
        var confirmed = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/replace/confirm", request);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        if (change == "policy_revoked")
        {
            var authority = await db.McpOperatorPolicies.SingleAsync(value => value.Id != policy.PolicyId);
            authority.DisabledAtUtc = DateTimeOffset.UtcNow;
            authority.LifecycleState = McpOperatorPolicyLifecycleState.Revoked;
            authority.Version++;
        }
        if (change == "expired")
            (await db.McpOperatorConfirmationPlans.SingleAsync()).ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        SetDelegation(client, app, "confirm_replace",
            change == "target" ? draft.TargetSelector with { AgentId = Guid.NewGuid() } : draft.TargetSelector,
            ["PolicyAdministrator"], ["netratel.mcp.admin"],
            subject: change == "subject" ? "other.admin@example.test" : "policy.admin@example.test",
            clientId: change == "client" ? "other-policy-client" : "policy-admin-client",
            resource: change == "resource" ? "https://other-mcp.example/mcp" : "https://mcp.dev.example/mcp",
            instance: change == "instance" ? "other" : "dev");
        if (change == "payload") request = request with { Policy = draft with { Name = "changed payload" } };

        var denied = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/replace/confirm", request);

        denied.StatusCode.Should().Be(expectedStatus);
        if (change == "expired") (await denied.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_expired");
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
        (await db.McpOperatorPolicyChangeAudits.CountAsync(audit => audit.PolicyId == policy.PolicyId)).Should().Be(2);
        (await administration.GetAsync(policy.PolicyId, CancellationToken.None))!.Version.Should().Be(2);
    }

    private static async Task<McpOperatorPolicy> CreateStoredPolicyAsync(
        OrchestratorDbContext db, McpOperatorPolicyAdministration administration, McpOperatorPolicyDraft draft)
    {
        var agentId = draft.TargetSelector.AgentId!.Value;
        db.Agents.Add(new Agent { Id = agentId, TenantId = draft.TargetSelector.TenantId, IsEnabled = true,
            Status = AgentStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await administration.UpsertTargetProfileAsync(draft.TargetSelector.TenantId, agentId,
            McpOperatorTargetClassification.ManagedStandard, ["campaign-fixture"], null, "independent-admin@example.test", CancellationToken.None);
        return await administration.CreateAsync(draft, "independent-admin@example.test", CancellationToken.None);
    }

    private static object PolicyPreviewRequest(string action, McpOperatorPolicy policy, McpOperatorPolicyDraft draft) => action == "replace"
        ? new PreviewMcpOperatorPolicyReplaceRequest(policy.PolicyId, policy.Version, draft)
        : new PreviewMcpOperatorPolicyDisableRequest(policy.PolicyId, policy.Version, draft.TargetSelector);

    private static object PolicyConfirmRequest(string action, McpOperatorPolicy policy, McpOperatorPolicyDraft draft, McpOperatorPolicyMutationPreview preview) => action == "replace"
        ? new ConfirmMcpOperatorPolicyReplaceRequest(policy.PolicyId, policy.Version, draft, preview.PlanToken, preview.IdempotencyKey)
        : new ConfirmMcpOperatorPolicyDisableRequest(policy.PolicyId, policy.Version, draft.TargetSelector, preview.PlanToken, preview.IdempotencyKey);

    [Theory]
    [InlineData("version", "confirmation_plan_stale")]
    [InlineData("tags", "confirmation_plan_stale")]
    [InlineData("classification", "target_classification_denied")]
    [InlineData("missing", "target_profile_missing")]
    public async Task Profile_changes_after_preview_cannot_reuse_policy_confirmation(string change, string failureCode)
    {
        var draft = ExactAgentDraft();
        await using var db = await CreatePolicyDbAsync("create", draft.TargetSelector);
        var profile = TargetProfile(draft.TargetSelector);
        var administration = new RecordingAdministration { CurrentTargetProfile = profile };
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db));
        var client = M2mClient(app);
        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyCreatePreview>())!;
        administration.CurrentTargetProfile = change switch
        {
            "version" => profile with { Version = profile.Version + 1 },
            "tags" => profile with { Tags = ["changed-tag"] },
            "classification" => profile with { Classification = McpOperatorTargetClassification.Restricted },
            _ => null
        };
        SetDelegation(client, app, "confirm_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);

        var confirmed = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/confirm",
            new ConfirmMcpOperatorPolicyCreateRequest(draft, preview.PlanToken, preview.IdempotencyKey));

        confirmed.StatusCode.Should().Be(failureCode == "confirmation_plan_stale" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden);
        (await confirmed.Content.ReadAsStringAsync()).Should().Contain(failureCode);
        administration.Creates.Should().Be(0);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Proposed_policy_classification_cannot_override_the_persisted_target()
    {
        var draft = ExactAgentDraft() with { TargetClassification = McpOperatorTargetClassification.ManagedStandard };
        await using var db = await CreatePolicyDbAsync("create", draft.TargetSelector);
        var administration = new RecordingAdministration
        {
            CurrentTargetProfile = TargetProfile(draft.TargetSelector) with { Classification = McpOperatorTargetClassification.Restricted }
        };
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db));
        var client = M2mClient(app);
        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);

        var response = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("target_classification_denied");
        administration.Creates.Should().Be(0);
        (await db.McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);
    }

    private static McpOperatorPolicyDraft ExactAgentDraft() => Draft() with
    {
        TargetSelector = new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.ExactAgent, 42,
            Guid.Parse("ef6a3f18-0d0d-45e0-8211-18067a95d9b9"))
    };

    private static McpOperatorTargetProfile TargetProfile(McpOperatorTargetSelector target) => new(
        target.AgentId!.Value, target.TenantId, McpOperatorTargetClassification.ManagedStandard,
        ["campaign-fixture"], DateTimeOffset.UtcNow, "independent-admin@example.test", 2);

    private static async Task<OrchestratorDbContext> CreatePolicyDbAsync(string action, McpOperatorTargetSelector target,
        SaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new OrchestratorDbContext(options.Options);
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = Guid.NewGuid(), Name = "exact classified policy administrator", Environment = McpOperatorEnvironment.Development,
            Effect = McpOperatorPolicyEffect.Allow, Priority = 10,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = "policy.admin@example.test",
            TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent, TenantId = target.TenantId, AgentId = target.AgentId,
            TargetClassification = McpOperatorTargetClassification.ManagedStandard,
            OperationFamily = McpOperatorOperationFamily.PolicyAdministration, Operation = $"netratel_policy/{action}",
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(
                AllowedTargetClassifications: [McpOperatorTargetClassification.ManagedStandard],
                RequiredConfirmationClass: McpOperatorConfirmationClass.PolicyAdministration), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "independent-admin@example.test", Version = 1
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Preview_RequiresExactDelegatedAdministratorPolicyAndDoesNotCreateAPolicy()
    {
        var authorization = new RecordingAuthorization(allowed: false);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var request = new PreviewMcpOperatorPolicyCreateRequest(Draft());

        var missing = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", request);

        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("delegated_identity_required");

        SetDelegation(client, app, "preview_create", Draft().TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var denied = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", request);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("target_policy_missing");
        var evaluated = authorization.Evaluated.Should().ContainSingle().Subject;
        evaluated.OperationFamily.Should().Be(McpOperatorOperationFamily.PolicyAdministration);
        evaluated.Operation.Should().Be("netratel_policy/create");
        evaluated.RequiredScopes.Should().BeEquivalentTo(["netratel.mcp.admin"]);
        evaluated.ConfirmationClass.Should().Be(McpOperatorConfirmationClass.PolicyAdministration);
        evaluated.TenantId.Should().Be(42);
        evaluated.AgentId.Should().BeNull();
        administration.Validations.Should().Be(0);
        administration.Creates.Should().Be(0);
        confirmations.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewThenConfirm_BindsTheExactDraftAndRecordsAcceptedAndChangeAuditBoundaries()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var draft = Draft();

        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/create/preview",
            new PreviewMcpOperatorPolicyCreateRequest(draft));
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyCreatePreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.Should().NotBeNull();
        preview!.PlanToken.Should().Be(RecordingConfirmations.PlanToken);
        preview.IdempotencyKey.Should().Be(RecordingConfirmations.IdempotencyKey);
        administration.Validations.Should().Be(1);
        administration.Creates.Should().Be(0);
        confirmations.Plans.Should().ContainSingle().Which.PayloadHash.Should().HaveLength(64);

        SetDelegation(client, app, "confirm_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmed = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/create/confirm",
            new ConfirmMcpOperatorPolicyCreateRequest(draft, preview.PlanToken, preview.IdempotencyKey));
        var result = await confirmed.Content.ReadFromJsonAsync<McpOperatorPolicyCreateResult>();

        confirmed.StatusCode.Should().Be(HttpStatusCode.Created);
        result.Should().NotBeNull();
        result!.Replay.Should().BeFalse();
        result.Policy.PolicyId.Should().Be(administration.CreatedPolicy.PolicyId);
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/create");
        administration.Creates.Should().Be(1);
        confirmations.Confirmed.Should().ContainSingle().Which.PayloadHash.Should().Be(confirmations.Plans[0].PayloadHash);
        var completion = confirmations.Completed.Should().ContainSingle().Which;
        completion.IdempotencyId.Should().Be(RecordingConfirmations.AdmissionId);
        completion.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
        completion.ResultReference.Should().StartWith($"policy-create:{administration.CreatedPolicy.PolicyId:D}:1:");
    }

    [Fact]
    public async Task TargetProfile_PreviewsAndConfirmsAnExactVersionedServerOwnedClassification()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var agentId = Guid.Parse("ef6a3f18-0d0d-45e0-8211-18067a95d9b9");
        var target = new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.ExactAgent, 42, agentId);
        var previewRequest = new PreviewMcpOperatorTargetProfileUpsertRequest(
            42,
            agentId,
            McpOperatorTargetClassification.ManagedStandard,
            [],
            null);

        SetDelegation(client, app, "preview_target_profile", target, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/preview", previewRequest);
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.Should().NotBeNull();
        authorization.Evaluated.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/upsert_target_profile");
        administration.TargetProfileUpserts.Should().Be(0);

        SetDelegation(client, app, "confirm_target_profile", target, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmed = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/target-profile/confirm",
            new ConfirmMcpOperatorTargetProfileUpsertRequest(
                42,
                agentId,
                McpOperatorTargetClassification.ManagedStandard,
                [],
                null,
                preview!.PlanToken,
                preview.IdempotencyKey));
        var result = await confirmed.Content.ReadFromJsonAsync<McpOperatorTargetProfileMutationResult>();

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Replay.Should().BeFalse();
        result.Profile.Should().BeEquivalentTo(new McpOperatorTargetProfile(agentId, 42, McpOperatorTargetClassification.ManagedStandard, [], result.Profile.UpdatedAtUtc, "policy.admin@example.test", 1));
        administration.TargetProfileUpserts.Should().Be(1);
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/upsert_target_profile");
        confirmations.Completed.Should().ContainSingle().Which.Should().Be((RecordingConfirmations.AdmissionId, McpOperatorIdempotencyOutcome.Succeeded, agentId.ToString("D")));
    }

    [Fact]
    public async Task TargetProfile_ExactConfirmationReplayReturnsPersistedProfileAfterVersionAdvances()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var agentId = Guid.Parse("ef6a3f18-0d0d-45e0-8211-18067a95d9b9");
        var target = new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.ExactAgent, 42, agentId);
        administration.CurrentTargetProfile = new McpOperatorTargetProfile(
            agentId, 42, McpOperatorTargetClassification.DedicatedQa, ["campaign"], DateTimeOffset.UtcNow,
            "policy.admin@example.test", 1);
        var previewRequest = new PreviewMcpOperatorTargetProfileUpsertRequest(
            42, agentId, McpOperatorTargetClassification.DedicatedQa, ["campaign"], 1);

        SetDelegation(client, app, "preview_target_profile", target, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/preview", previewRequest);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        var confirmation = new ConfirmMcpOperatorTargetProfileUpsertRequest(
            42, agentId, McpOperatorTargetClassification.DedicatedQa, ["campaign"], 1,
            preview.PlanToken, preview.IdempotencyKey);

        SetDelegation(client, app, "confirm_target_profile", target, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var firstResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/confirm", confirmation);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var first = (await firstResponse.Content.ReadFromJsonAsync<McpOperatorTargetProfileMutationResult>())!;
        first.Replay.Should().BeFalse();
        first.Profile.Version.Should().Be(2);

        confirmations.ReplayCompleted = true;
        var replayResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/confirm", confirmation);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = (await replayResponse.Content.ReadFromJsonAsync<McpOperatorTargetProfileMutationResult>())!;
        replay.Replay.Should().BeTrue();
        replay.Profile.Should().BeEquivalentTo(first.Profile);
        administration.TargetProfileUpserts.Should().Be(1);
        confirmations.Completed.Should().ContainSingle();
    }

    [Fact]
    public async Task TargetProfile_PersistedReplaySucceedsAndFreshStalePlanRemainsUnused()
    {
        var target = ExactAgentDraft().TargetSelector;
        await using var db = await CreatePolicyDbAsync("upsert_target_profile", target);
        var administration = new RecordingAdministration
        {
            CurrentTargetProfile = TargetProfile(target) with { Version = 1 }
        };
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db));
        var client = M2mClient(app);
        var previewRequest = new PreviewMcpOperatorTargetProfileUpsertRequest(
            target.TenantId, target.AgentId!.Value, McpOperatorTargetClassification.ManagedStandard,
            ["campaign-fixture"], 1);
        SetDelegation(client, app, "preview_target_profile", target, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var firstPreviewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/preview", previewRequest);
        var stalePreviewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/preview", previewRequest);
        firstPreviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        stalePreviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPreview = (await firstPreviewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        var stalePreview = (await stalePreviewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        var request = new ConfirmMcpOperatorTargetProfileUpsertRequest(
            target.TenantId, target.AgentId.Value, McpOperatorTargetClassification.ManagedStandard,
            ["campaign-fixture"], 1, firstPreview.PlanToken, firstPreview.IdempotencyKey);
        SetDelegation(client, app, "confirm_target_profile", target, ["PolicyAdministrator"], ["netratel.mcp.admin"]);

        var firstResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/confirm", request);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var first = (await firstResponse.Content.ReadFromJsonAsync<McpOperatorTargetProfileMutationResult>())!;
        first.Replay.Should().BeFalse();
        first.Profile.Version.Should().Be(2);

        var replayResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/confirm", request);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK, await replayResponse.Content.ReadAsStringAsync());
        var replay = (await replayResponse.Content.ReadFromJsonAsync<McpOperatorTargetProfileMutationResult>())!;
        replay.Replay.Should().BeTrue();
        replay.Profile.Should().BeEquivalentTo(first.Profile);

        var changedPayloadReplay = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/confirm",
            request with { Tags = ["different-campaign"] });
        changedPayloadReplay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await changedPayloadReplay.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");

        var staleRequest = request with { PlanToken = stalePreview.PlanToken, IdempotencyKey = stalePreview.IdempotencyKey };
        var staleResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/confirm", staleRequest);
        staleResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await staleResponse.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");
        var unused = await db.McpOperatorConfirmationPlans.SingleAsync(plan => plan.IdempotencyKey == stalePreview.IdempotencyKey);
        unused.ConsumedAtUtc.Should().BeNull();
        unused.ConsumedIdempotencyId.Should().BeNull();
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
        administration.TargetProfileUpserts.Should().Be(1);

        administration.CurrentTargetProfile = first.Profile with { Version = 3, UpdatedAtUtc = DateTimeOffset.UtcNow };
        var changedProfileReplay = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/confirm", request);
        changedProfileReplay.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await changedProfileReplay.Content.ReadAsStringAsync()).Should().Contain("idempotency_replay_unavailable");
    }

    [Fact]
    public async Task Preview_RejectsAnAllowPolicyThatWouldEscalateTheDelegatedAdministrator()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var draft = Draft() with
        {
            PrincipalSelector = new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "policy-admins")
        };
        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"], ["policy-admins"]);

        var response = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("operator_policy_self_escalation");
        administration.Validations.Should().Be(0);
        administration.Creates.Should().Be(0);
        confirmations.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_BindsAnExplicitControlPlanePolicyWithoutAnAgentTarget()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var draft = Draft() with
        {
            Name = "reviewed tenant control-plane administration",
            TargetSelector = new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.ControlPlane, 0),
            OperationFamily = McpOperatorOperationFamily.TenantAdministration,
            Operation = "netratel_tenants/create"
        };

        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var response = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var evaluated = authorization.Evaluated.Should().ContainSingle().Subject;
        evaluated.TenantId.Should().Be(0);
        evaluated.AgentId.Should().BeNull();
    }

    [Fact]
    public async Task Replace_PreviewsAndConfirmsTheExactVersionedPolicyWithoutChangingItsTarget()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var replacement = Draft() with { Name = "reviewed tenant observation - revised" };

        SetDelegation(client, app, "preview_replace", replacement.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/replace/preview",
            new PreviewMcpOperatorPolicyReplaceRequest(administration.CreatedPolicy.PolicyId, 1, replacement));
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.Should().NotBeNull();
        authorization.Evaluated.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/replace");
        administration.Validations.Should().Be(1);
        administration.Replaces.Should().Be(0);

        SetDelegation(client, app, "confirm_replace", replacement.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmation = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/replace/confirm",
            new ConfirmMcpOperatorPolicyReplaceRequest(administration.CreatedPolicy.PolicyId, 1, replacement, preview!.PlanToken, preview.IdempotencyKey));
        var result = await confirmation.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>();

        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Policy.Name.Should().Be(replacement.Name);
        result.Policy.Version.Should().Be(2);
        administration.Replaces.Should().Be(1);
        administration.LastReplacement.Should().Be((administration.CreatedPolicy.PolicyId, 1L, replacement));
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/replace");
        confirmations.Completed.Should().ContainSingle().Which.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
    }

    [Fact]
    public async Task Disable_PreviewsAndConfirmsTheExactVersionedPolicy()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);

        SetDelegation(client, app, "preview_disable", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/disable/preview",
            new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector));
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        authorization.Evaluated.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/disable");
        administration.Disables.Should().Be(0);

        SetDelegation(client, app, "confirm_disable", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmation = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/disable/confirm",
            new ConfirmMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector, preview!.PlanToken, preview.IdempotencyKey));
        var result = await confirmation.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>();

        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Policy.DisabledAtUtc.Should().NotBeNull();
        administration.Disables.Should().Be(1);
        administration.LastDisable.Should().Be((administration.CreatedPolicy.PolicyId, 1L));
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/disable");
    }

    [Fact]
    public async Task Revoke_PreviewsAndConfirmsTheExactVersionedPolicyAsATerminalLifecycleChange()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);

        SetDelegation(client, app, "preview_revoke", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/revoke/preview",
            new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector));
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        authorization.Evaluated.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/revoke");
        administration.Revocations.Should().Be(0);

        SetDelegation(client, app, "confirm_revoke", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmation = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/revoke/confirm",
            new ConfirmMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector, preview!.PlanToken, preview.IdempotencyKey));
        var result = await confirmation.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>();

        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Policy.LifecycleState.Should().Be(McpOperatorPolicyLifecycleState.Revoked);
        administration.Revocations.Should().Be(1);
        administration.LastRevocation.Should().Be((administration.CreatedPolicy.PolicyId, 1L));
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/revoke");
    }

    [Fact]
    public async Task Disable_RejectsAnExistingPolicyThatSelectsTheDelegatedAdministrator()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        SetDelegation(
            client,
            app,
            "preview_disable",
            administration.CreatedPolicy.TargetSelector,
            ["PolicyAdministrator"],
            ["netratel.mcp.admin"],
            ["incident-responders"]);

        var response = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/disable/preview",
            new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("operator_policy_self_escalation");
        authorization.Evaluated.Should().BeEmpty();
        administration.Disables.Should().Be(0);
        confirmations.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_RejectsATargetSelectorThatDoesNotMatchThePersistedPolicy()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        SetDelegation(client, app, "preview_disable", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var mismatchedTarget = administration.CreatedPolicy.TargetSelector with { TenantId = 43 };

        var response = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/disable/preview",
            new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, mismatchedTarget));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("policy_target_immutable");
        authorization.Evaluated.Should().BeEmpty();
        confirmations.Plans.Should().BeEmpty();
    }

    private static HttpClient M2mClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        return client;
    }

    private static void SetDelegation(
        HttpClient client,
        IHost app,
        string operation,
        McpOperatorTargetSelector target,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> scopes,
        IReadOnlyList<string>? groups = null,
        string subject = "policy.admin@example.test",
        string clientId = "policy-admin-client",
        string resource = "https://mcp.dev.example/mcp",
        string instance = "dev")
    {
        client.DefaultRequestHeaders.Remove(McpOperatorDelegationOptions.HeaderName);
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity(
                subject,
                clientId,
                clientId,
                groups ?? [],
                roles,
                scopes),
            new McpOperatorDelegationRequest(
                "netratel_policy",
                operation,
                $"request-{operation}",
                resource,
                instance,
                target.Kind == McpOperatorTargetSelectorKind.ControlPlane ? null : target.TenantId,
                target.Kind == McpOperatorTargetSelectorKind.ExactAgent ? target.AgentId : null,
                $"correlation-{operation}"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);
    }

    private static async Task<IHost> BuildAppAsync(
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        bool accountRoutes = false)
    {
        var delegationOptions = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "M2M";
                    options.DefaultChallengeScheme = "M2M";
                }).AddScheme<AuthenticationSchemeOptions, TestM2mAuthenticationHandler>("M2M", _ => { });
                services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy =>
                {
                    policy.AddAuthenticationSchemes("M2M");
                    policy.RequireAuthenticatedUser();
                }));
                services.AddAuthorization(options => options.AddPolicy("McpOperatorPolicyAdmin", policy =>
                    policy.RequireAuthenticatedUser().RequireClaim("test-policy-permission", "mcp.policy.admin")));
                services.AddSingleton(delegationOptions);
                services.AddSingleton<McpOperatorDelegationTokenService>();
                services.AddSingleton<IMcpOperatorAuthorization>(authorization);
                services.AddSingleton<IMcpOperatorPolicyAdministration>(administration);
                services.AddSingleton<IMcpOperatorPolicyRevocation>((IMcpOperatorPolicyRevocation)administration);
                services.AddSingleton<IMcpOperatorConfirmationService>(confirmations);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMcpOperatorPolicyMutationEndpoints();
                    if (accountRoutes) endpoints.MapMcpOperatorAccountPolicyMutationEndpoints();
                });
            });
        });
        return await builder.StartAsync();
    }

    private static McpOperatorPolicyDraft Draft() => new(
        "reviewed tenant observation",
        McpOperatorEnvironment.Development,
        McpOperatorPolicyEffect.Allow,
        10,
        new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "incident-responders"),
        new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.Tenant, 42),
        null,
        McpOperatorOperationFamily.Observability,
        null,
        new McpOperatorConstraints(),
        DateTimeOffset.UtcNow.AddDays(7),
        DateTimeOffset.UtcNow.AddDays(1),
        "change-347");

    private sealed class RecordingAuthorization(bool allowed) : IMcpOperatorAuthorization
    {
        private static readonly Guid GovernancePolicyId = Guid.Parse("6d321d63-efbd-47e5-aaef-a4e5408215e5");
        public List<McpOperatorAccessRequest> Evaluated { get; } = [];
        public List<McpOperatorAcceptedAudit> Accepted { get; } = [];

        public Task<bool> HasTenantVisibilityAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, CancellationToken cancellationToken) => Task.FromResult(allowed);

        public Task<McpOperatorDecision> EvaluateAsync(McpOperatorAccessRequest request, CancellationToken cancellationToken)
        {
            Evaluated.Add(request);
            return Task.FromResult(allowed
                ? new McpOperatorDecision(true, null, null, [GovernancePolicyId], new McpOperatorConstraints(), request.TargetSetDigest, request, 4)
                : McpOperatorDecision.Denied(request, "target_policy_missing", McpOperatorAuthorizationLayer.Policy));
        }

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorDecision decision, string servicePrincipal, CancellationToken cancellationToken)
        {
            var access = decision.Request;
            var audit = new McpOperatorAcceptedAudit(
                Guid.NewGuid(),
                GovernancePolicyId,
                access.Environment,
                servicePrincipal,
                access.Principal.Subject,
                access.Principal.ClientId,
                access.Principal.AuthorizedParty,
                access.Principal.Groups.Order(StringComparer.Ordinal).ToArray(),
                access.Principal.Roles.Order(StringComparer.Ordinal).ToArray(),
                access.Principal.Scopes.Order(StringComparer.Ordinal).ToArray(),
                access.McpResource,
                access.McpInstance,
                access.Tool,
                access.TenantId,
                access.AgentId,
                access.OperationFamily,
                access.Operation,
                access.CorrelationId,
                access.RequestId,
                DateTimeOffset.UtcNow);
            Accepted.Add(audit);
            return Task.FromResult(audit);
        }
    }

    private sealed class DisconnectAfterPolicyWrite(Action disconnect) : SaveChangesInterceptor
    {
        public Guid? PolicyId { get; set; }
        public TaskCompletionSource ReceiptSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } db)
            {
                if (PolicyId is { } policyId && db.ChangeTracker.Entries<McpOperatorPolicyRecord>()
                    .Any(entry => entry.Entity.Id == policyId && entry.Entity.Version == 2))
                {
                    // Disconnect only after the real administration service has persisted its mutation.
                    PolicyId = null;
                    disconnect();
                }
                if (db.ChangeTracker.Entries<McpOperatorIdempotencyRecord>()
                    .Any(entry => entry.Entity.Outcome == McpOperatorIdempotencyOutcome.Succeeded))
                    ReceiptSaved.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingAdministration : IMcpOperatorPolicyAdministration, IMcpOperatorPolicyRevocation
    {
        public int GuardedMutations { get; private set; }
        public int Validations { get; private set; }
        public int Creates { get; private set; }
        public int Replaces { get; private set; }
        public int Disables { get; private set; }
        public int Revocations { get; private set; }
        public int TargetProfileUpserts { get; private set; }
        public (Guid PolicyId, long ExpectedVersion, McpOperatorPolicyDraft Policy)? LastReplacement { get; private set; }
        public (Guid PolicyId, long ExpectedVersion)? LastDisable { get; private set; }
        public (Guid PolicyId, long ExpectedVersion)? LastRevocation { get; private set; }
        public McpOperatorPolicy CreatedPolicy { get; } = new(
            Guid.Parse("68310e19-5f49-4e63-a73d-342e98940c26"),
            "reviewed tenant observation",
            McpOperatorEnvironment.Development,
            McpOperatorPolicyEffect.Allow,
            10,
            new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "incident-responders"),
            new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.Tenant, 42),
            null,
            McpOperatorOperationFamily.Observability,
            null,
            new McpOperatorConstraints(),
            DateTimeOffset.UtcNow,
            "policy.admin@example.test",
            null,
            null,
            null,
            null,
            1,
            "change-347");

        public McpOperatorPolicy CurrentPolicy { get; set; }
        public McpOperatorTargetProfile? CurrentTargetProfile { get; set; }

        public RecordingAdministration() => CurrentPolicy = CreatedPolicy;

        public async Task<T> ExecuteWithTargetProfileAsync<T>(int tenantId, Guid agentId,
            McpOperatorTargetProfile? expectedProfile, Func<CancellationToken, Task<T>> mutation, CancellationToken cancellationToken)
        {
            var current = await GetTargetProfileAsync(tenantId, agentId, cancellationToken);
            if (current != expectedProfile)
                throw new McpOperatorAdmissionRejectedException("confirmation_plan_stale");
            GuardedMutations++;
            return await mutation(cancellationToken);
        }

        public Task<McpOperatorPolicyPage> ListAsync(McpOperatorEnvironment? environment, int? tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> ListChangeAuditsAsync(int? tenantId, Guid? policyId, Guid? agentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<McpOperatorAcceptedAuditPage> ListAcceptedAuditsAsync(McpOperatorAcceptedAuditFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<McpOperatorPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken) => Task.FromResult<McpOperatorPolicy?>(policyId == CreatedPolicy.PolicyId ? CurrentPolicy : null);
        public Task ValidateDraftAsync(McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken)
        {
            Validations++;
            return Task.CompletedTask;
        }
        public Task<McpOperatorPolicy> CreateAsync(McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken)
        {
            Creates++;
            return Task.FromResult(CreatedPolicy);
        }
        public Task<McpOperatorPolicy> ReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken)
        {
            Replaces++;
            LastReplacement = (policyId, expectedVersion, draft);
            CurrentPolicy = CurrentPolicy with
            {
                Name = draft.Name,
                Effect = draft.Effect,
                Priority = draft.Priority,
                PrincipalSelector = draft.PrincipalSelector,
                TargetSelector = draft.TargetSelector,
                TargetClassification = draft.TargetClassification,
                OperationFamily = draft.OperationFamily,
                Operation = draft.Operation,
                Constraints = draft.Constraints,
                ExpiresAtUtc = draft.ExpiresAtUtc,
                ReviewByUtc = draft.ReviewByUtc,
                Version = expectedVersion + 1,
                AuditReference = draft.AuditReference
            };
            return Task.FromResult(CurrentPolicy);
        }

        public Task<McpOperatorPolicy?> DisableAsync(Guid policyId, long expectedVersion, string actorId, CancellationToken cancellationToken)
        {
            Disables++;
            LastDisable = (policyId, expectedVersion);
            CurrentPolicy = CurrentPolicy with
            {
                DisabledAtUtc = DateTimeOffset.UtcNow,
                DisabledBy = actorId,
                LifecycleState = McpOperatorPolicyLifecycleState.Disabled,
                Version = expectedVersion + 1
            };
            return Task.FromResult<McpOperatorPolicy?>(CurrentPolicy);
        }

        public Task<McpOperatorPolicy?> RevokeAsync(Guid policyId, long expectedVersion, string actorId, CancellationToken cancellationToken)
        {
            Revocations++;
            LastRevocation = (policyId, expectedVersion);
            CurrentPolicy = CurrentPolicy with
            {
                DisabledAtUtc = CurrentPolicy.DisabledAtUtc ?? DateTimeOffset.UtcNow,
                DisabledBy = CurrentPolicy.DisabledBy ?? actorId,
                LifecycleState = McpOperatorPolicyLifecycleState.Revoked,
                Version = expectedVersion + 1
            };
            return Task.FromResult<McpOperatorPolicy?>(CurrentPolicy);
        }
        public Task<McpOperatorTargetProfile?> GetTargetProfileAsync(int tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(CurrentTargetProfile is { TenantId: var currentTenant, AgentId: var currentAgent } profile && currentTenant == tenantId && currentAgent == agentId ? profile : null);

        public Task<McpOperatorTargetProfile> UpsertTargetProfileAsync(int tenantId, Guid agentId, McpOperatorTargetClassification classification, IReadOnlyCollection<string> tags, long? expectedVersion, string actorId, CancellationToken cancellationToken)
        {
            TargetProfileUpserts++;
            CurrentTargetProfile = new McpOperatorTargetProfile(
                agentId,
                tenantId,
                classification,
                tags.Order(StringComparer.Ordinal).ToArray(),
                DateTimeOffset.UtcNow,
                actorId,
                CurrentTargetProfile is null ? 1 : CurrentTargetProfile.Version + 1);
            return Task.FromResult(CurrentTargetProfile);
        }
    }

    private sealed class RecordingConfirmations : IMcpOperatorConfirmationService
    {
        public const string PlanToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        public const string IdempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        public static readonly Guid AdmissionId = Guid.Parse("9cb863f4-ff02-43c8-b60d-6703e8ca508f");
        public List<McpOperatorConfirmationPlanRequest> Plans { get; } = [];
        public List<McpOperatorConfirmationRequest> Confirmed { get; } = [];
        public List<(Guid IdempotencyId, McpOperatorIdempotencyOutcome Outcome, string? ResultReference)> Completed { get; } = [];
        public bool ReplayCompleted { get; set; }

        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken)
        {
            Plans.Add(request);
            return Task.FromResult(new McpOperatorConfirmationPlan(PlanToken, IdempotencyKey, DateTimeOffset.UtcNow.AddMinutes(5), McpOperatorConfirmationClass.PolicyAdministration, request.PayloadHash, request.Decision.TargetSetDigest));
        }

        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken)
        {
            if (request.ReplayOnly)
            {
                if (ReplayCompleted)
                    return Task.FromResult(new McpOperatorConfirmationAdmission(false, true, null, null,
                        McpOperatorIdempotencyOutcome.Succeeded, Completed.Single().ResultReference));
                return Task.FromResult(new McpOperatorConfirmationAdmission(false, false, null, null, null, null));
            }
            Confirmed.Add(request);
            return Task.FromResult(new McpOperatorConfirmationAdmission(true, false, null, AdmissionId, McpOperatorIdempotencyOutcome.Pending, null));
        }

        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken)
        {
            Completed.Add((idempotencyId, outcome, resultReference));
            return Task.CompletedTask;
        }
    }

    private sealed class TestM2mAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var mode = Request.Headers.Authorization.ToString();
            if (mode == "M2M") return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "netratel-mcp-http-dev")], Scheme.Name)), Scheme.Name)));
            if (mode is not ("Account" or "AccountReadOnly" or "AccountCredential"))
                return Task.FromResult(AuthenticateResult.Fail("Missing authentication."));
            var claims = new List<Claim> { new("sub", "policy.admin@example.test"), new("netratel_principal_id", "policy-admin-principal") };
            if (mode != "AccountReadOnly") claims.Add(new("test-policy-permission", "mcp.policy.admin"));
            if (mode == "AccountCredential") claims.Add(new("netratel_integration_credential_id", "synthetic-credential"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, "Account")), Scheme.Name)));
        }
    }
}
