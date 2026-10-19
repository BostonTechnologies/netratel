using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Integration;
using NetRatel.API.Services.Jobs;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorJobEndpointTests
{
    private const string MutationResource = "https://mcp.example.test/mcp";
    private const string MutationSubject = "definition-operator";
    private const string MutationClientId = "definition-client";
    private const string ScriptContent = "printf 'definition-marker'";
    private const long MutationJobId = 901;
    private const long MutationScriptId = 801;
    private const long MutationParameterId = 601;
    private const long MutationStepId = 701;
    private static readonly Guid MutationAgentId = Guid.Parse("965d8a10-b2aa-4f11-93c0-a4d2f3a400e0");
    private static string MutationRoot => $"/api/v2/mcp/operator/agents/42/{MutationAgentId:D}/jobs";

    [Theory]
    [InlineData("create", false)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    [InlineData("param_add", false)]
    [InlineData("param_update", false)]
    [InlineData("param_delete", false)]
    [InlineData("step_add", false)]
    [InlineData("step_update", false)]
    [InlineData("step_reorder", false)]
    [InlineData("step_delete", false)]
    [InlineData("create", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("param_add", true)]
    [InlineData("param_update", true)]
    [InlineData("param_delete", true)]
    [InlineData("step_add", true)]
    [InlineData("step_update", true)]
    [InlineData("step_reorder", true)]
    [InlineData("step_delete", true)]
    public async Task Completed_definition_mutations_replay_the_original_result_without_persistence(string action, bool localDelegation)
    {
        await using var app = await BuildMutationAppAsync(localDelegation);
        using var client = app.GetTestClient();
        var boundaryApp = localDelegation ? app : null;
        var request = await PreviewMutationAsync(client, action, MutationRequest(action), boundaryApp);
        var confirmed = await ConfirmMutationAsync(client, action, request, boundaryApp);
        confirmed.Replayed.Should().BeFalse();
        confirmed.Deleted.Should().Be(action == "delete");
        confirmed.Version.Should().Be(action == "create" ? "1" : "2");
        if (action is "param_add" or "step_add") confirmed.ItemId.Should().NotBeNullOrEmpty();
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var afterConfirmation = writes.Count;

        var replayed = await ConfirmMutationAsync(client, action, request, boundaryApp);

        replayed.Should().Be(confirmed with { Replayed = true });
        writes.Count.Should().Be(afterConfirmation);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
            (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
            (await db.McpOperatorJobAudits.CountAsync()).Should().Be(1);
            var jobId = long.Parse(confirmed.JobId);
            var owner = await db.McpOperatorJobs.SingleAsync(row => row.JobId == jobId);
            owner.Version.Should().Be(long.Parse(confirmed.Version));
            // Later edits/deletion and a changed script must not replace the
            // recorded outcome or require a new validation of execution material.
            owner.Version++;
            owner.DeletedAtUtc = DateTimeOffset.UtcNow;
            (await db.Jobs.SingleAsync(row => row.Id == jobId)).Name = "later-definition";
            (await db.McpOperatorScripts.SingleAsync()).Version++;
            await db.SaveChangesAsync();
            var store = scope.ServiceProvider.GetRequiredService<IMcpOperatorJobStore>();
            (await store.GetOwnedAsync(jobId, 42, MutationAgentId, MutationPrincipal(), MutationResource, "prod", default)).Should().BeNull();
        }
        var afterLaterChange = writes.Count;

        var replayedAfterChange = await ConfirmMutationAsync(client, action, request, boundaryApp);

        replayedAfterChange.Should().Be(confirmed with { Replayed = true });
        writes.Count.Should().Be(afterLaterChange);
    }

    [Fact]
    public async Task A_local_deleted_job_receipt_cannot_authorize_a_read_preview_changed_body_or_another_fresh_plan()
    {
        await using var app = await BuildMutationAppAsync(localDelegation: true);
        using var client = app.GetTestClient();
        var original = await PreviewMutationAsync(client, "delete", MutationRequest("delete"), app);
        await ConfirmMutationAsync(client, "delete", original, app);
        var create = await PreviewMutationAsync(client, "create", MutationRequest("create"), app);
        var other = await ConfirmMutationAsync(client, "create", create, app);
        var fresh = await PreviewMutationAsync(client, "delete", new McpOperatorJobMutationRequest(JobId: long.Parse(other.JobId), ExpectedVersion: 1), app);
        var assertion = await MutationAssertionAsync(app, client, "delete", original);
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeAttempt = writes.Count;

        foreach (var operation in new[] { "get", "delete" })
        {
            using var ordinary = await McpJobLocalDelegationTestBoundary.ExchangeAsync(app, client, "netratel_jobs", operation, original.JobId!.ToString(), MutationAgentId);
            ordinary.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ordinary.Content.ReadAsStringAsync()).Should().Contain("local_delegation_binding_mismatch");
        }
        using var preview = await McpJobLocalDelegationTestBoundary.PostAsync(client, MutationRoot + "/preview/delete", original, assertion);
        preview.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var changed = await McpJobLocalDelegationTestBoundary.PostAsync(client, MutationRoot + "/confirm/delete", original with { ExpectedVersion = 2 }, assertion);
        changed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var anotherPlan = await McpJobLocalDelegationTestBoundary.PostAsync(client, MutationRoot + "/confirm/delete", fresh, assertion);
        anotherPlan.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anotherPlan.Content.ReadAsStringAsync()).Should().Contain("delegated_identity_invalid");
        McpOperatorJobConfirmationPayload.TryCreateBinding("netratel_jobs", "delete", JsonSerializer.SerializeToElement(original with { ExpectedVersion = 2 }), out var changedBinding).Should().BeTrue();
        using var changedExchange = await McpJobLocalDelegationTestBoundary.ExchangeAsync(app, client, "netratel_jobs", "delete", original.JobId!.ToString(), MutationAgentId, changedBinding);
        changedExchange.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        writes.Count.Should().Be(beforeAttempt);
        await using var verification = app.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var freshHash = McpOperatorReplayBinding.Hash(fresh.PlanToken!);
        (await db.McpOperatorConfirmationPlans.SingleAsync(row => row.TokenHash == freshHash)).ConsumedAtUtc.Should().BeNull();
        (await db.McpOperatorJobs.SingleAsync(row => row.JobId == long.Parse(other.JobId))).DeletedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("permission", "local_credential_permission_denied", HttpStatusCode.Forbidden)]
    [InlineData("credential-expired", "local_credential_expired", HttpStatusCode.Unauthorized)]
    [InlineData("credential-revoked", "local_credential_revoked", HttpStatusCode.Unauthorized)]
    [InlineData("policy-disabled", "local_delegation_binding_mismatch", HttpStatusCode.Forbidden)]
    [InlineData("policy-deny", "target_policy_denied", HttpStatusCode.Forbidden)]
    [InlineData("plan-expired", "local_delegation_binding_mismatch", HttpStatusCode.Forbidden)]
    [InlineData("target-disabled", "target_disabled", HttpStatusCode.Forbidden)]
    public async Task An_exchanged_deleted_job_replay_rechecks_current_credential_receipt_and_route_policy(string change, string code, HttpStatusCode status)
    {
        await using var app = await BuildMutationAppAsync(localDelegation: true);
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, "delete", MutationRequest("delete"), app);
        await ConfirmMutationAsync(client, "delete", request, app);
        var assertion = await MutationAssertionAsync(app, client, "delete", request);
        var boundary = app.Services.GetRequiredService<McpJobLocalDelegationTestBoundary>();
        if (change == "permission") boundary.HasPermission = false;
        else if (change == "credential-expired") boundary.CurrentFailureCode = "local_credential_expired";
        else if (change == "credential-revoked") boundary.CurrentFailureCode = "local_credential_revoked";
        else
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (change == "policy-disabled") (await db.McpOperatorPolicies.SingleAsync()).DisabledAtUtc = DateTimeOffset.UtcNow;
            else if (change == "plan-expired") (await db.McpOperatorConfirmationPlans.SingleAsync()).ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            else if (change == "policy-deny") db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
            {
                Id = Guid.NewGuid(), Name = "new current denial", Environment = McpOperatorEnvironment.Production,
                Effect = McpOperatorPolicyEffect.Deny, Priority = 20, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
                PrincipalSelectorValue = MutationSubject, TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
                TenantId = 42, AgentId = MutationAgentId, OperationFamily = McpOperatorOperationFamily.AutomationWrite,
                CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "independent-admin", Version = 1
            });
            else (await db.Agents.SingleAsync()).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeAttempt = writes.Count;

        using var response = await McpJobLocalDelegationTestBoundary.PostAsync(client, MutationRoot + "/confirm/delete", request, assertion);

        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain(code);
        writes.Count.Should().Be(beforeAttempt);
    }

    [Fact]
    public async Task A_signed_confirmation_proof_requires_the_actual_ingress_credential_as_its_client_even_for_a_live_job()
    {
        await using var app = await BuildMutationAppAsync(localDelegation: true);
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, "update", MutationRequest("update"), app);
        var assertion = await MutationAssertionAsync(app, client, "update", request);
        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        tokens.TryValidate(assertion, out var verified).Should().BeTrue();
        var wrongClient = tokens.Create(verified!.Identity with { ClientId = "another-signed-application-client" },
            new McpOperatorDelegationRequest(verified.Tool, verified.Operation, Guid.NewGuid().ToString("N"), verified.Resource,
                verified.Instance, verified.TenantId, verified.AgentId, verified.CorrelationId)
            {
                ObjectReference = verified.ObjectReference, ObjectTargetResolutionEnabled = true,
                ReplayBinding = verified.ReplayBinding, IngressCredentialId = verified.IngressCredentialId,
                IngressPermission = verified.IngressPermission
            });
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeAttempt = writes.Count;

        using var denied = await McpJobLocalDelegationTestBoundary.PostAsync(client, MutationRoot + "/confirm/update", request, wrongClient);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("local_delegation_binding_mismatch");
        writes.Count.Should().Be(beforeAttempt);
        await using var verification = app.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.McpOperatorConfirmationPlans.SingleAsync()).ConsumedAtUtc.Should().BeNull();
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("delete")]
    public async Task Ordinary_local_object_requests_keep_ownership_failure_distinct_from_current_permission_denial(string operation)
    {
        await using var app = await BuildMutationAppAsync(localDelegation: true);
        using var client = app.GetTestClient();
        app.Services.GetRequiredService<McpJobLocalDelegationTestBoundary>().HasPermission = false;
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeAttempt = writes.Count;

        using var unknown = await McpJobLocalDelegationTestBoundary.ExchangeAsync(app, client, "netratel_jobs", operation, "999999", MutationAgentId);
        unknown.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("local_delegation_binding_mismatch");
        using var owned = await McpJobLocalDelegationTestBoundary.ExchangeAsync(app, client, "netratel_jobs", operation, MutationJobId.ToString(), MutationAgentId);
        owned.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owned.Content.ReadAsStringAsync()).Should().Contain("local_credential_permission_denied");
        writes.Count.Should().Be(beforeAttempt);
    }

    [Fact]
    public async Task Changed_confirmation_does_not_consume_the_reviewed_parameter_add()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, "param_add", MutationRequest("param_add"));
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeAttempt = writes.Count;

        var changed = await client.PostAsJsonAsync(MutationRoot + "/confirm/param_add",
            request with { Parameter = request.Parameter! with { DefaultValue = "unreviewed" } });

        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await changed.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");
        writes.Count.Should().Be(beforeAttempt);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorConfirmationPlans.SingleAsync()).ConsumedAtUtc.Should().BeNull();
            (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
            (await db.JobParameters.CountAsync()).Should().Be(1);
        }

        var confirmed = await ConfirmMutationAsync(client, "param_add", request);
        confirmed.ItemId.Should().NotBeNullOrEmpty();
        confirmed.Version.Should().Be("2");
    }

    [Fact]
    public async Task Fresh_competing_stale_confirmation_remains_unconsumed_after_the_winning_write()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var stale = await PreviewMutationAsync(client, "update", MutationRequest("update"));
        var winning = await PreviewMutationAsync(client, "update",
            MutationRequest("update") with { Job = new McpOperatorJobDraft("winning-definition", "/") });
        await ConfirmMutationAsync(client, "update", winning);
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeAttempt = writes.Count;

        var rejected = await client.PostAsJsonAsync(MutationRoot + "/confirm/update", stale);

        rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("job_etag_mismatch");
        writes.Count.Should().Be(beforeAttempt);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.McpOperatorConfirmationPlans.CountAsync(plan => plan.ConsumedAtUtc == null)).Should().Be(1);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
        (await db.Jobs.SingleAsync()).Name.Should().Be("winning-definition");
        (await db.McpOperatorJobs.SingleAsync()).Version.Should().Be(2);
        (await db.McpOperatorJobAudits.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("policy-version", "confirmation_plan_stale")]
    [InlineData("policy-disabled", "tenant_not_authorized")]
    [InlineData("expired", "confirmation_plan_expired")]
    [InlineData("scope", "oauth_scope_missing")]
    [InlineData("client", "confirmation_plan_stale")]
    [InlineData("resource", "confirmation_plan_stale")]
    [InlineData("target-version", "confirmation_plan_stale")]
    [InlineData("owner", "idempotency_replay_unavailable")]
    [InlineData("owner-tenant", "idempotency_replay_unavailable")]
    public async Task Deleted_definition_replay_rechecks_current_authority_binding_and_tombstone_ownership(string change, string failureCode)
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, "delete", MutationRequest("delete"));
        await ConfirmMutationAsync(client, "delete", request);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var caller = app.Services.GetRequiredService<MutationCaller>();
            switch (change)
            {
                case "policy-version": (await db.McpOperatorPolicies.SingleAsync()).Version++; break;
                case "policy-disabled": (await db.McpOperatorPolicies.SingleAsync()).DisabledAtUtc = DateTimeOffset.UtcNow; break;
                case "expired": (await db.McpOperatorConfirmationPlans.SingleAsync()).ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "scope": caller.Scopes = []; break;
                case "client": caller.ClientId = "other-client"; break;
                case "resource": caller.Resource = "https://other.example.test/mcp"; break;
                case "target-version": (await db.McpOperatorTargetProfiles.SingleAsync()).Version++; break;
                case "owner": (await db.McpOperatorJobs.SingleAsync()).Subject = "other-owner"; break;
                case "owner-tenant": (await db.McpOperatorJobs.SingleAsync()).TenantId = 43; break;
            }
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeReplay = writes.Count;

        var replayed = await client.PostAsJsonAsync(MutationRoot + "/confirm/delete", request);

        replayed.IsSuccessStatusCode.Should().BeFalse();
        (await replayed.Content.ReadAsStringAsync()).Should().Contain(failureCode);
        writes.Count.Should().Be(beforeReplay);
        await using var verification = app.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorJobAudits.CountAsync()).Should().Be(1);
        (await verificationDb.McpOperatorIdempotencyRecords.SingleAsync()).Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
    }

    [Theory]
    [InlineData("param_add")]
    [InlineData("delete")]
    public async Task Legacy_job_id_receipts_replay_with_the_authorized_current_version(string action)
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, action, MutationRequest(action));
        var confirmed = await ConfirmMutationAsync(client, action, request);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorIdempotencyRecords.SingleAsync()).ResultReference = confirmed.JobId;
            (await db.McpOperatorJobs.SingleAsync()).Version++;
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeReplay = writes.Count;

        var replayed = await ConfirmMutationAsync(client, action, request);

        replayed.JobId.Should().Be(confirmed.JobId);
        replayed.Version.Should().Be("3");
        replayed.ItemId.Should().BeNull();
        replayed.Deleted.Should().Be(action == "delete");
        replayed.Replayed.Should().BeTrue();
        writes.Count.Should().Be(beforeReplay);
    }

    [Fact]
    public void Gateway_terminal_result_supplies_bounded_redacted_job_output()
    {
        var activity = Activity("""{"stdout":["job-marker","token=synthetic-value"],"stderr":"diagnostic","unrelatedSecret":"never expose"}""");

        var logs = McpOperatorJobEndpoints.ProjectActivityLogs(activity, [], 100);

        logs.Select(log => log.Stream).Should().Equal("stdout", "stdout", "stderr");
        logs.Select(log => log.Message).Should().Equal("job-marker", "token=[REDACTED]", "diagnostic");
        logs.Select(log => log.Sequence).Should().Equal(1, 2, 3);
        logs.Should().OnlyContain(log => log.RequestId == activity.RequestId && log.TimestampUtc == activity.CompletedAtUtc);
    }

    [Fact]
    public void Streamed_logs_take_precedence_over_terminal_snapshot()
    {
        var activity = Activity("""{"stdout":["duplicate snapshot"]}""");
        var persisted = new JobTaskLogInfo(1, activity.RequestId, activity.Id, "client", 42,
            "stdout", "streamed marker", 7, activity.CreatedAtUtc);

        McpOperatorJobEndpoints.ProjectActivityLogs(activity, [persisted], 100)
            .Should().ContainSingle().Which.Message.Should().Be("streamed marker");
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("[]")]
    [InlineData("{\"stdout\":[null,123,{}],\"stderr\":false}")]
    public void Malformed_or_nontext_results_do_not_become_log_output(string result)
        => McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 100).Should().BeEmpty();

    [Fact]
    public void Output_respects_remaining_line_budget_and_message_bound()
    {
        var result = System.Text.Json.JsonSerializer.Serialize(new { stdout = new[] { new string('x', 5000), "second", "third" } });
        var logs = McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 2);
        logs.Should().HaveCount(2);
        logs[0].Message.Should().HaveLength(4096);
        logs[1].Message.Should().Be("second");
        McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 0).Should().BeEmpty();
    }

    private static JobTaskActivityInfo Activity(string result) => new(1, "job-request", 2, 3,
        "client", 42, "exec-library-script", "Completed", null,
        DateTimeOffset.Parse("2026-09-05T00:00:00Z"), DateTimeOffset.Parse("2026-09-05T00:00:01Z"), ResultJson: result);

    private static McpOperatorJobMutationRequest MutationRequest(string action) => action switch
    {
        "create" => new(Job: new McpOperatorJobDraft("created-definition", "/")),
        "update" => new(Job: new McpOperatorJobDraft("updated-definition", "/"), JobId: MutationJobId, ExpectedVersion: 1),
        "delete" => new(JobId: MutationJobId, ExpectedVersion: 1),
        "param_add" => new(JobId: MutationJobId, ExpectedVersion: 1, Parameter: new McpOperatorJobParameter("new_parameter", "string", false, DefaultValue: "reviewed")),
        "param_update" => new(JobId: MutationJobId, ExpectedVersion: 1, ParameterId: MutationParameterId, Parameter: new McpOperatorJobParameter("region", "string", false, DefaultValue: "updated")),
        "param_delete" => new(JobId: MutationJobId, ExpectedVersion: 1, ParameterId: MutationParameterId),
        "step_add" => new(JobId: MutationJobId, ExpectedVersion: 1, Step: new McpOperatorJobStep(2, MutationScriptId, 1, MutationHash(ScriptContent))),
        "step_update" => new(JobId: MutationJobId, ExpectedVersion: 1, StepId: MutationStepId, Step: new McpOperatorJobStep(1, MutationScriptId, 1, MutationHash(ScriptContent), Enabled: false)),
        "step_reorder" => new(JobId: MutationJobId, ExpectedVersion: 1, StepId: MutationStepId, Ordinal: 2),
        "step_delete" => new(JobId: MutationJobId, ExpectedVersion: 1, StepId: MutationStepId),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static async Task<McpOperatorJobMutationRequest> PreviewMutationAsync(HttpClient client, string action, McpOperatorJobMutationRequest request, WebApplication? app = null)
    {
        using var response = app is null ? await client.PostAsJsonAsync(MutationRoot + "/preview/" + action, request) :
            await McpJobLocalDelegationTestBoundary.PostAsync(client, MutationRoot + "/preview/" + action, request,
                await McpJobLocalDelegationTestBoundary.AssertionAsync(app, client, "netratel_jobs", action, request.JobId?.ToString(), MutationAgentId));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var preview = (await response.Content.ReadFromJsonAsync<McpOperatorJobPreview>())!;
        return request with { PlanToken = preview.PlanToken, IdempotencyKey = preview.IdempotencyKey };
    }

    private static async Task<McpOperatorJobMutationResult> ConfirmMutationAsync(HttpClient client, string action, McpOperatorJobMutationRequest request, WebApplication? app = null)
    {
        using var response = app is null ? await client.PostAsJsonAsync(MutationRoot + "/confirm/" + action, request) :
            await McpJobLocalDelegationTestBoundary.PostAsync(client, MutationRoot + "/confirm/" + action, request,
                await MutationAssertionAsync(app, client, action, request));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<McpOperatorJobMutationResult>())!;
    }

    private static Task<string> MutationAssertionAsync(WebApplication app, HttpClient client, string action, McpOperatorJobMutationRequest request)
    {
        McpOperatorJobConfirmationPayload.TryCreateBinding("netratel_jobs", action, JsonSerializer.SerializeToElement(request), out var binding).Should().BeTrue();
        return McpJobLocalDelegationTestBoundary.AssertionAsync(app, client, "netratel_jobs", action, request.JobId?.ToString(), MutationAgentId, binding);
    }

    private static async Task<WebApplication> BuildMutationAppAsync(bool localDelegation = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, MutationAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
        var databaseName = "job-definition-" + Guid.NewGuid();
        builder.Services.AddSingleton<MutationWriteCounter>();
        builder.Services.AddSingleton<MutationCaller>();
        builder.Services.AddDbContext<OrchestratorDbContext>((provider, options) => options.UseInMemoryDatabase(databaseName)
            .AddInterceptors(provider.GetRequiredService<MutationWriteCounter>()));
        builder.Services.AddScoped<IMcpOperatorJobStore, McpOperatorJobStore>();
        builder.Services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
        builder.Services.AddScoped<IMcpOperatorRouteAdmission, McpOperatorRouteAdmissionService>();
        builder.Services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        builder.Services.AddScoped<IAkkaJobAuthorityService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IJobRunService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IClientPresenceRouter, MutationPresence>();
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher, MutationDispatcher>();
        builder.Services.AddSingleton<McpOperatorLocalAgentOptions>();
        if (localDelegation) McpJobLocalDelegationTestBoundary.Configure(builder, MutationSubject, MutationClientId, MutationResource);
        var app = builder.Build();
        app.UseAuthentication();
        if (localDelegation) app.UseMiddleware<McpOperatorDelegationMiddleware>();
        else
        {
            // Keep the original direct route/store fixture for focused policy
            // tests; local cases use the full signed exchange and middleware.
            app.Use(async (context, next) =>
            {
                var caller = context.RequestServices.GetRequiredService<MutationCaller>();
                var action = context.Request.Path.Value!.Split('/')[^1];
                context.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                    new McpOperatorDelegationIdentity(MutationSubject, caller.ClientId, null, [], ["AutomationOperator"], caller.Scopes),
                    "mcp-service", "netratel_jobs", action, "definition-request", DateTimeOffset.UtcNow.AddMinutes(1),
                    caller.Resource, "prod", 42, MutationAgentId, "definition-correlation");
                await next(context);
            });
        }
        app.UseAuthorization();
        if (localDelegation) app.MapMcpLocalDelegationEndpoints();
        app.MapMcpOperatorJobEndpoints();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var policyId = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = MutationAgentId, TenantId = 42, IsEnabled = true, Status = AgentStatus.Active, CreatedAtUtc = now });
        db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
        {
            AgentId = MutationAgentId, TenantId = 42, Classification = McpOperatorTargetClassification.ManagedStandard,
            TagsJson = "[]", Version = 1, UpdatedAtUtc = now, UpdatedBy = "independent-admin"
        });
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = policyId, Name = "owned definition mutations", Environment = McpOperatorEnvironment.Production,
            Effect = McpOperatorPolicyEffect.Allow, Priority = 10,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = MutationSubject,
            TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent, TenantId = 42, AgentId = MutationAgentId,
            TargetClassification = McpOperatorTargetClassification.ManagedStandard, OperationFamily = McpOperatorOperationFamily.AutomationWrite,
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(MaxJobTargetCount: 1, MaxFanOut: 1), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = now, CreatedBy = "independent-admin", Version = 1
        });
        await db.SaveChangesAsync();
        var route = new McpOperatorRouteAccessRequest(McpOperatorEnvironment.Production, MutationPrincipal(), "mcp-service", MutationResource,
            "prod", "netratel_jobs", "update", 42, MutationAgentId, new HashSet<string>(["netratel.mcp.write"], StringComparer.Ordinal),
            "definition-correlation", "definition-request", true, true);
        var admitted = await scope.ServiceProvider.GetRequiredService<IMcpOperatorRouteAdmission>().EvaluateAsync(route, default);
        admitted.Decision.IsAllowed.Should().BeTrue();
        db.Jobs.Add(new JobDefinition { Id = MutationJobId, Name = "owned-definition", TenantId = 42, AgentId = MutationAgentId, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.McpOperatorJobs.Add(new McpOperatorJobRecord
        {
            Id = Guid.NewGuid(), JobId = MutationJobId, TenantId = 42, AgentId = MutationAgentId, Subject = MutationSubject, ClientId = MutationClientId,
            McpResource = MutationResource, McpInstance = "prod", PolicyId = policyId, PolicyVersion = 1,
            TargetSetDigest = admitted.Decision.TargetSetDigest!, CreatedAtUtc = now, UpdatedAtUtc = now, Version = 1
        });
        db.JobParameters.Add(new JobParameterDefinition { Id = MutationParameterId, JobId = MutationJobId, Name = "region", Type = "string" });
        db.Scripts.Add(new ScriptDefinition { Id = MutationScriptId, Name = "owned-script", ScriptType = "bash", Content = ScriptContent });
        db.McpOperatorScripts.Add(new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(), ScriptId = MutationScriptId, TenantId = 42, Subject = MutationSubject, ClientId = MutationClientId,
            McpResource = MutationResource, McpInstance = "prod", PolicyId = policyId, PolicyVersion = 1,
            Name = "owned-script", ShellType = "sh", ContentHash = MutationHash(ScriptContent), ManifestHash = MutationHash(string.Empty),
            TimeoutSeconds = 15, WorkingDirectory = "/tmp", Version = 1
        });
        for (var ordinal = 1; ordinal <= 2; ordinal++) db.JobSteps.Add(new JobStepDefinition
        {
            Id = MutationStepId + ordinal - 1, JobId = MutationJobId, Ordinal = ordinal, Type = (int)JobStepKind.LibraryScript,
            ScriptId = MutationScriptId, Enabled = true,
            PayloadJson = JsonSerializer.Serialize(new { ScriptVersion = 1, ScriptContentHash = MutationHash(ScriptContent) })
        });
        await db.SaveChangesAsync();
        return app;
    }

    private static McpOperatorPrincipal MutationPrincipal() => new(MutationSubject, MutationClientId, null,
        new HashSet<string>(), new HashSet<string>(["AutomationOperator"], StringComparer.Ordinal),
        new HashSet<string>(["netratel.mcp.write"], StringComparer.Ordinal));

    private static string MutationHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class MutationCaller
    {
        public string ClientId { get; set; } = MutationClientId;
        public string Resource { get; set; } = MutationResource;
        public string[] Scopes { get; set; } = ["netratel.mcp.write"];
    }

    private sealed class MutationWriteCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MutationAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(Context.RequestServices.GetService<McpJobLocalDelegationTestBoundary>()?.Identity(Scheme.Name) ??
                new ClaimsIdentity([new Claim("sub", "mcp-service")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class MutationPresence : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutationDispatcher : IAgentCommandAuthorityDispatcher
    {
        public bool IsAvailable(ClientKey client) => throw new NotSupportedException();
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void Production_job_routes_are_versioned_owned_and_m2m_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IClientPresenceRouter>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorJobStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IJobRunService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IAkkaJobAuthorityService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<NetRatelAkkaOptions>();
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorJobEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/details",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/params",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/steps",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/validate",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/preview/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/confirm/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/query",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/steps",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/logs",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/runs/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/runs/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/cancel/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/cancel/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/delete/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/delete/confirm");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}

// The existing definition/run fixtures retain real policy, confirmation and
// owner stores. Only account lookup/grants are controlled here so each test
// can withdraw authority between the real exchange and signed execution.
internal sealed class McpJobLocalDelegationTestBoundary(string subject, string credentialId, string resource)
    : IIntegrationCredentialCurrentVerifier, IEffectiveAccessService
{
    public string Subject { get; set; } = subject;
    public string CredentialId { get; set; } = credentialId;
    public string Resource { get; } = resource;
    public bool HasPermission { get; set; } = true;
    public string? CurrentFailureCode { get; set; }

    public static void Configure(WebApplicationBuilder builder, string subject, string credentialId, string resource)
    {
        var boundary = new McpJobLocalDelegationTestBoundary(subject, credentialId, resource);
        builder.Services.AddSingleton(boundary);
        builder.Services.AddSingleton<IEffectiveAccessService>(boundary);
        builder.Services.AddSingleton<IIntegrationCredentialCurrentVerifier>(boundary);
        builder.Services.AddScoped<IMcpOperationObjectTargetResolver, McpOperationObjectTargetResolver>();
        builder.Services.AddSingleton(new McpOperatorDelegationOptions
        {
            Enabled = true, Issuer = "job-replay-tests", Audience = "job-replay-api", ServicePrincipal = "mcp-service",
            KeyId = "test-key", SharedKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        });
        builder.Services.AddSingleton<McpOperatorDelegationTokenService>();
        builder.Services.AddAuthorization(options => options.AddPolicy("McpLocalDelegationExchange", policy => policy.RequireAuthenticatedUser()));
    }

    public ClaimsIdentity Identity(string scheme) => new(
    [
        new Claim("sub", "mcp-service"), new Claim("netratel_principal_id", Subject),
        new Claim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, CredentialId),
        new Claim("integration_credential_resource", Resource), new Claim("integration_credential_purpose", "http_mcp")
    ], scheme);

    public static async Task<HttpResponseMessage> ExchangeAsync(WebApplication app, HttpClient client, string tool, string operation,
        string? objectReference, Guid agentId, McpOperatorReplayBinding? binding = null)
    {
        var boundary = app.Services.GetRequiredService<McpJobLocalDelegationTestBoundary>();
        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        var pairing = tokens.Create(new McpOperatorDelegationIdentity("gateway-is-not-the-owner", "netratel-local-gateway", null, [], [], []),
            new McpOperatorDelegationRequest(tool, operation, Guid.NewGuid().ToString("N"), boundary.Resource, "prod", 42, agentId, "local-replay-correlation")
            { ObjectReference = objectReference, ObjectTargetResolutionEnabled = true, ReplayBinding = binding });
        using var message = new HttpRequestMessage(HttpMethod.Post, McpLocalDelegationEndpoints.ExchangePath);
        message.Headers.Add(McpLocalDelegationEndpoints.PairingHeaderName, pairing);
        return await client.SendAsync(message);
    }

    public static async Task<string> AssertionAsync(WebApplication app, HttpClient client, string tool, string operation,
        string? objectReference, Guid agentId, McpOperatorReplayBinding? binding = null)
    {
        using var response = await ExchangeAsync(app, client, tool, operation, objectReference, agentId, binding);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var assertion = (await response.Content.ReadFromJsonAsync<McpLocalDelegationEndpoints.McpLocalExecutionResponse>())!.Assertion;
        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        tokens.TryValidate(assertion, out var verified).Should().BeTrue();
        var boundary = app.Services.GetRequiredService<McpJobLocalDelegationTestBoundary>();
        verified!.Identity.Subject.Should().Be(boundary.Subject);
        verified.Identity.ClientId.Should().Be(boundary.CredentialId);
        verified.ReplayBinding.Should().Be(binding);
        return assertion;
    }

    public static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body, string assertion)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        message.Headers.Add(McpOperatorDelegationOptions.HeaderName, assertion);
        return await client.SendAsync(message);
    }

    public Task<IntegrationCredentialCurrentResult> CheckCurrentAsync(string credentialId, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default)
    {
        var valid = CurrentFailureCode is null && credentialId == CredentialId && purpose == IntegrationCredentialPurpose.HttpMcp;
        return Task.FromResult(new IntegrationCredentialCurrentResult(valid
            ? new VerifiedIntegrationCredential(CredentialId, Subject, purpose, []) { Resource = Resource } : null,
            valid ? null : CurrentFailureCode ?? "delegated_identity_invalid"));
    }
    public async Task<VerifiedIntegrationCredential?> VerifyCurrentAsync(string credentialId, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default) =>
        (await CheckCurrentAsync(credentialId, purpose, cancellationToken)).Credential;
    public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(HasPermission && permission == "job.manage" && tenantId == 42 &&
            principal.HasClaim("netratel_principal_id", Subject) && principal.HasClaim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, CredentialId));
    public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EffectiveAccessSnapshot(Subject, false, false, new HashSet<string>(HasPermission ? ["job.manage"] : [])));
    public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
