using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorJobStoreTests
{
    [Fact]
    public async Task Deleting_an_executed_step_preserves_run_history_and_unrelated_step_links()
    {
        await using var db = CreateDb();
        var fixture = await CreateFixtureAsync(db);
        var store = new McpOperatorJobStore(db);
        var job = await store.CreateAsync(new McpOperatorJobCreateRequest(fixture.Decision, fixture.Audit,
            new McpOperatorJobDraft("step-history", "/"), fixture.Now), CancellationToken.None);
        var first = (await store.AddStepAsync(new McpOperatorJobStepMutationRequest(job.JobId, null,
            job.Version, fixture.Decision, fixture.Audit,
            new McpOperatorJobStep(1, fixture.ScriptId, fixture.ScriptVersion, fixture.ScriptHash), fixture.Now), CancellationToken.None))!.Value;
        var second = (await store.AddStepAsync(new McpOperatorJobStepMutationRequest(job.JobId, null,
            first.Job.Version, fixture.Decision, fixture.Audit,
            new McpOperatorJobStep(2, fixture.ScriptId, fixture.ScriptVersion, fixture.ScriptHash), fixture.Now), CancellationToken.None))!.Value;
        var steps = await db.JobSteps.OrderBy(step => step.Ordinal).ToArrayAsync();
        db.JobRuns.Add(new JobRunRecord
        {
            Id = 8100, JobId = job.JobId, TenantId = fixture.TenantId, StartedBy = "test",
            Status = (int)JobRunState.Succeeded, CreatedAtUtc = fixture.Now,
            Steps = [new JobStepRunRecord { Id = 8101, JobStepId = steps[0].Id, Ordinal = 1,
                Status = (int)JobStepRunState.Succeeded }],
            Activities = [new JobTaskActivityRecord { Id = 8102, JobStepId = steps[0].Id,
                RequestId = "step-history", Status = "Completed", ResultJson = "{\"stdout\":\"retained-output\"}",
                CreatedAtUtc = fixture.Now,
                Logs = [new JobTaskLogRecord { RequestId = "step-history", Stream = "stdout",
                    Message = "retained-output", Sequence = 1, TimestampUtc = fixture.Now }] }]
        });
        db.JobTaskActivities.Add(new JobTaskActivityRecord { Id = 8103, JobStepId = steps[1].Id,
            RequestId = "other-step", Status = "Completed", CreatedAtUtc = fixture.Now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var deleted = await store.DeleteStepAsync(job.JobId, steps[0].Id, second.Job.Version,
            fixture.Decision, fixture.Audit, fixture.Now.AddSeconds(1), CancellationToken.None);
        db.ChangeTracker.Clear();

        deleted.Should().NotBeNull();
        deleted!.Version.Should().Be(second.Job.Version + 1);
        (await db.JobSteps.SingleAsync()).Ordinal.Should().Be(1);
        (await db.JobStepRuns.SingleAsync()).JobStepId.Should().BeNull();
        var activity = await db.JobTaskActivities.SingleAsync(row => row.Id == 8102);
        activity.JobStepId.Should().BeNull();
        activity.JobRunId.Should().Be(8100);
        activity.ResultJson.Should().Contain("retained-output");
        (await db.JobTaskLogs.SingleAsync()).Message.Should().Be("retained-output");
        (await db.JobTaskActivities.SingleAsync(row => row.Id == 8103)).JobStepId.Should().Be(steps[1].Id);
        (await db.McpOperatorJobAudits.SingleAsync(row => row.Action == "step_deleted"))
            .JobVersion.Should().Be(deleted.Version);
    }

    [Fact]
    public async Task Retained_run_remains_owned_readable_and_deletable_after_job_deletion()
    {
        await using var db = CreateDb();
        var fixture = await CreateFixtureAsync(db);
        var store = new McpOperatorJobStore(db);
        var job = await store.CreateAsync(new McpOperatorJobCreateRequest(fixture.Decision, fixture.Audit,
            new McpOperatorJobDraft("retained-run", "/"), fixture.Now), CancellationToken.None);
        const ulong runId = 9007199254740993;
        await new JobRunService(db).UpsertRunAsync(new UpsertJobRunCommand(runId, (ulong)job.JobId,
            fixture.TenantId, "client", fixture.Principal.Subject, JobRunState.Succeeded, 1,
            fixture.Now, fixture.Now, fixture.Now, null, null, null, fixture.AgentId));
        await store.RecordStartedRunAsync(job.JobId, runId, Guid.NewGuid(), fixture.Decision, fixture.Audit,
            fixture.Now, CancellationToken.None);
        await store.DeleteAsync(job.JobId, job.Version, fixture.Decision, fixture.Audit, fixture.Now.AddSeconds(1), CancellationToken.None);

        (await store.GetRunOwnedAsync(runId, fixture.TenantId, fixture.AgentId, fixture.Principal,
            fixture.Resource, fixture.Instance, CancellationToken.None)).Should().NotBeNull();
        (await store.ListRunsOwnedAsync(job.JobId, fixture.TenantId, fixture.AgentId, fixture.Principal,
            fixture.Resource, fixture.Instance, CancellationToken.None)).Should().ContainSingle();
        (await store.GetRunOwnedAsync(runId, fixture.TenantId, fixture.AgentId,
            fixture.Principal with { Subject = "different-user" }, fixture.Resource, fixture.Instance, CancellationToken.None)).Should().BeNull();
        (await store.GetRunOwnedAsync(runId, fixture.TenantId + 1, fixture.AgentId,
            fixture.Principal, fixture.Resource, fixture.Instance, CancellationToken.None)).Should().BeNull();
        var wrongOwner = fixture.Decision with { Request = fixture.Decision.Request with
            { Principal = fixture.Principal with { Subject = "different-user" } } };
        (await store.DeleteRunAsync(runId, wrongOwner, fixture.Audit with { Subject = "different-user" },
            fixture.Now.AddSeconds(2), CancellationToken.None)).Should().BeNull();
        (await store.DeleteRunAsync(runId, fixture.Decision, fixture.Audit,
            fixture.Now.AddSeconds(3), CancellationToken.None)).Should().NotBeNull();
        (await store.GetRunOwnedAsync(runId, fixture.TenantId, fixture.AgentId, fixture.Principal,
            fixture.Resource, fixture.Instance, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Job_lifecycle_is_explicitly_owned_and_rejects_stale_etags()
    {
        await using var db = CreateDb();
        var fixture = await CreateFixtureAsync(db);
        var store = new McpOperatorJobStore(db);

        var created = await store.CreateAsync(new McpOperatorJobCreateRequest(
            fixture.Decision, fixture.Audit, new McpOperatorJobDraft("inventory", "/daily", "Inventory", "{\"retries\":2}"), fixture.Now), CancellationToken.None);
        var otherPrincipal = fixture.Principal with { Subject = "other@example.test" };
        var hidden = await store.GetOwnedAsync(created.JobId, fixture.TenantId, fixture.AgentId, otherPrincipal, fixture.Resource, fixture.Instance, CancellationToken.None);

        var parameter = await store.AddParameterAsync(new McpOperatorJobParameterMutationRequest(
            created.JobId, null, created.Version, fixture.Decision, fixture.Audit,
            new McpOperatorJobParameter("region", "choice", true, Options: ["za", "us"]), fixture.Now.AddMinutes(1)), CancellationToken.None);
        var stale = () => store.AddParameterAsync(new McpOperatorJobParameterMutationRequest(
            created.JobId, null, created.Version, fixture.Decision, fixture.Audit,
            new McpOperatorJobParameter("site", "string", true), fixture.Now.AddMinutes(2)), CancellationToken.None);

        created.FolderPath.Should().Be("/mcp-operator/42/daily/");
        hidden.Should().BeNull();
        parameter.Should().NotBeNull();
        parameter!.Value.Job.Version.Should().Be(2);
        await stale.Should().ThrowAsync<McpOperatorJobConcurrencyException>();
        (await db.McpOperatorJobAudits.OrderBy(audit => audit.OccurredAtUtc).ToArrayAsync())
            .Select(audit => audit.Action).Should().Equal("created", "param_added");
    }

    [Fact]
    public async Task Job_steps_require_an_owned_exact_library_script_revision_before_execution()
    {
        await using var db = CreateDb();
        var fixture = await CreateFixtureAsync(db);
        var store = new McpOperatorJobStore(db);
        var created = await store.CreateAsync(new McpOperatorJobCreateRequest(
            fixture.Decision, fixture.Audit, new McpOperatorJobDraft("inventory", "/daily"), fixture.Now), CancellationToken.None);
        var wrongHash = Hash("changed");
        var invalidStep = () => store.AddStepAsync(new McpOperatorJobStepMutationRequest(
            created.JobId, null, created.Version, fixture.Decision, fixture.Audit,
            new McpOperatorJobStep(1, fixture.ScriptId, fixture.ScriptVersion, wrongHash), fixture.Now.AddMinutes(1)), CancellationToken.None);

        await invalidStep.Should().ThrowAsync<ArgumentException>();
        var step = await store.AddStepAsync(new McpOperatorJobStepMutationRequest(
            created.JobId, null, created.Version, fixture.Decision, fixture.Audit,
            new McpOperatorJobStep(1, fixture.ScriptId, fixture.ScriptVersion, fixture.ScriptHash), fixture.Now.AddMinutes(1)), CancellationToken.None);
        var executable = await store.IsExecutableAsync(created.JobId, fixture.Decision, fixture.Principal, fixture.Resource, fixture.Instance, CancellationToken.None);

        var ownedScript = await db.McpOperatorScripts.SingleAsync();
        ownedScript.ContentHash = wrongHash;
        await db.SaveChangesAsync();
        var tamperedExecutable = await store.IsExecutableAsync(created.JobId, fixture.Decision, fixture.Principal, fixture.Resource, fixture.Instance, CancellationToken.None);

        step.Should().NotBeNull();
        step!.Value.Job.Version.Should().Be(2);
        executable.Should().BeTrue();
        tamperedExecutable.Should().BeFalse();
    }

    [Fact]
    public async Task Secret_parameters_never_accept_inline_defaults()
    {
        await using var db = CreateDb();
        var fixture = await CreateFixtureAsync(db);
        var store = new McpOperatorJobStore(db);

        var validate = () => store.ValidateParameterAsync(
            new McpOperatorJobParameter("credential", "secret_reference", true, DefaultValue: "plaintext", SecretReference: "DB_PASSWORD"),
            CancellationToken.None);

        await validate.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Oidc_azp_only_principal_can_manage_its_job_without_a_client_id_claim()
    {
        await using var db = CreateDb();
        var fixture = await CreateFixtureAsync(db);
        var principal = fixture.Principal with { ClientId = null };
        var decision = fixture.Decision with { Request = fixture.Decision.Request with { Principal = principal } };
        var audit = fixture.Audit with { ClientId = null };
        var store = new McpOperatorJobStore(db);
        var created = await store.CreateAsync(new McpOperatorJobCreateRequest(decision, audit,
            new McpOperatorJobDraft("oidc-job", "/"), fixture.Now), CancellationToken.None);
        var read = await store.GetOwnedAsync(created.JobId, fixture.TenantId, fixture.AgentId,
            principal, fixture.Resource, fixture.Instance, CancellationToken.None);
        read.Should().NotBeNull();
        (await store.GetOwnedAsync(created.JobId, fixture.TenantId, fixture.AgentId,
            principal with { Subject = "different-user" }, fixture.Resource, fixture.Instance, CancellationToken.None)).Should().BeNull();
        (await store.GetOwnedAsync(created.JobId, fixture.TenantId, fixture.AgentId,
            principal with { ClientId = "different-client" }, fixture.Resource, fixture.Instance, CancellationToken.None)).Should().BeNull();
        (await store.DeleteAsync(created.JobId, created.Version, decision, audit, fixture.Now.AddSeconds(1), CancellationToken.None)).Should().NotBeNull();
    }

    private static async Task<Fixture> CreateFixtureAsync(OrchestratorDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        const int tenantId = 42;
        const long scriptId = 801;
        const long scriptVersion = 7;
        const string resource = "https://mcp.prod.example/mcp";
        const string instance = "prod";
        var agentId = Guid.Parse("bb69ba7b-1e56-4ea8-bc36-533950b50610");
        var policyId = Guid.Parse("9252da81-08cf-4f62-a546-241428329d2b");
        var auditId = Guid.Parse("3252da81-08cf-4f62-a546-241428329d2b");
        var principal = new McpOperatorPrincipal("operator@example.test", "operator-client", "operator-client", Set(), Set(), Set("netratel.mcp.write"));
        var scriptHash = Hash("Write-Output 'inventory'");
        var targetDigest = Hash("tenant-42-agent-inventory");
        var access = new McpOperatorAccessRequest(McpOperatorEnvironment.Production, principal, tenantId, agentId,
            McpOperatorTargetClassification.ManagedStandard, McpOperatorOperationFamily.AutomationWrite, "netratel_jobs/create",
            Set("netratel.mcp.write"), McpOperatorConfirmationClass.StandardMutation, "corr-42", "request-42", targetDigest,
            McpResource: resource, McpInstance: instance, Tool: "netratel_jobs");
        var constraints = new McpOperatorConstraints(MaxJobTargetCount: 1, MaxFanOut: 1);
        var decision = new McpOperatorDecision(true, null, null, [policyId], constraints, targetDigest, access, 4);
        var audit = new McpOperatorAcceptedAudit(auditId, policyId, access.Environment, "netratel-mcp-http-prod", principal.Subject,
            principal.ClientId, principal.AuthorizedParty, [], [], principal.Scopes.Order(StringComparer.Ordinal).ToArray(), resource, instance,
            access.Tool, tenantId, agentId, access.OperationFamily, access.Operation, access.CorrelationId, access.RequestId, now);

        db.Scripts.Add(new ScriptDefinition
        {
            Id = scriptId,
            Name = "inventory-script",
            FolderPath = "/mcp-operator/42/",
            Description = "Inventory",
            Content = "Write-Output 'inventory'",
            ScriptType = "powershell",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.McpOperatorScripts.Add(new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(),
            ScriptId = scriptId,
            TenantId = tenantId,
            Subject = principal.Subject,
            ClientId = principal.ClientId!,
            McpResource = resource,
            McpInstance = instance,
            Name = "inventory-script",
            Description = "Inventory",
            ShellType = "powershell",
            PolicyId = policyId,
            PolicyVersion = 4,
            ContentHash = scriptHash,
            ManifestHash = Hash("manifest"),
            TimeoutSeconds = 60,
            WorkingDirectory = "C:\\NetRatel",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = scriptVersion
        });
        await db.SaveChangesAsync();
        return new Fixture(now, tenantId, agentId, principal, decision, audit, resource, instance, scriptId, scriptVersion, scriptHash);
    }

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Fixture(DateTimeOffset Now, int TenantId, Guid AgentId, McpOperatorPrincipal Principal,
        McpOperatorDecision Decision, McpOperatorAcceptedAudit Audit, string Resource, string Instance,
        long ScriptId, long ScriptVersion, string ScriptHash);
}
