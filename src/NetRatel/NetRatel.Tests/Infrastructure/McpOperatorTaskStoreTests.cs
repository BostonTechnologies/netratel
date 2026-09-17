using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetRatel.Application.Operations;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorTaskStoreTests
{
    [Fact]
    public async Task Task_creation_is_owner_scoped_idempotent_and_creates_its_activity_atomically()
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var request = CreateRequest(fixture, Guid.Parse("43bd3ab1-6ce6-49c2-9e3e-169fb4ee7050"));

        var first = await store.CreateOrGetAsync(request, CancellationToken.None);
        var replay = await store.CreateOrGetAsync(request, CancellationToken.None);
        var hidden = await store.GetOwnedAsync(first.TaskId, fixture.TenantId, fixture.AgentId, fixture.Principal with { Subject = "other@example.test" }, fixture.Resource, fixture.Instance, CancellationToken.None);
        var activity = await db.JobTaskActivities.SingleAsync();

        first.TaskId.Should().BePositive();
        replay.TaskId.Should().Be(first.TaskId);
        first.CommandId.Should().Be(request.CommandId);
        activity.RequestId.Should().Be(first.CommandId);
        activity.ClientIdentity.Should().Be($"agent:{fixture.AgentId:D}");
        hidden.Should().BeNull();
        (await db.McpOperatorTaskAudits.ToArrayAsync()).Select(audit => audit.Action).Should().Equal("create");
    }

    [Fact]
    public async Task Cancellation_fences_nonterminal_lifecycle_updates_and_retains_immutable_audit()
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var created = await store.CreateOrGetAsync(CreateRequest(fixture, Guid.Parse("7a038860-8972-4484-8e66-3b7d55b82ce7")), CancellationToken.None);

        var cancellation = await store.RequestCancellationAsync(new McpOperatorTaskCancelRequest(
            created.TaskId, fixture.Decision, fixture.Audit, fixture.Now.AddMinutes(1)), CancellationToken.None);
        await store.RecordLifecycleAsync(created.CommandId, fixture.TenantId, fixture.AgentId, "Processing", "password=should_not_change", fixture.Now.AddMinutes(2), CancellationToken.None);
        await store.RecordLifecycleAsync(created.CommandId, fixture.TenantId, fixture.AgentId, "Cancelled", "cancelled", fixture.Now.AddMinutes(3), CancellationToken.None);
        var stored = await store.GetOwnedAsync(created.TaskId, fixture.TenantId, fixture.AgentId, fixture.Principal, fixture.Resource, fixture.Instance, CancellationToken.None);

        cancellation!.State.Should().Be("CancelRequested");
        stored!.State.Should().Be("Cancelled");
        stored.ResultSummary.Should().Be("cancelled");
        stored.IsCancellationRequested.Should().BeTrue();
        (await db.McpOperatorTaskAudits.OrderBy(audit => audit.OccurredAtUtc).ToArrayAsync()).Select(audit => audit.Action).Should().Equal("create", "cancel");
    }

    [Fact]
    public async Task Task_admission_requires_current_task_limits()
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        fixture = fixture with
        {
            Decision = fixture.Decision with { EffectiveConstraints = new McpOperatorConstraints(MaxTaskTargetCount: 1, MaxFanOut: 1) }
        };
        var store = new McpOperatorTaskStore(db);

        var create = () => store.CreateOrGetAsync(CreateRequest(fixture, Guid.NewGuid()), CancellationToken.None);

        await create.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PostgreSql_task_creation_counts_active_leases_and_releases_capacity_after_completion()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        await db.Database.MigrateAsync();
        var fixture = CreateFixture();
        var requests = Enumerable.Range(0, 3).Select(_ => CreateRequest(fixture, Guid.NewGuid())).ToArray();
        await SeedPostgresAsync(db, fixture, requests);
        var store = new McpOperatorTaskStore(db);
        var first = await store.CreateOrGetAsync(requests[0], CancellationToken.None);
        await store.CreateOrGetAsync(requests[1], CancellationToken.None);
        var overLimit = () => store.CreateOrGetAsync(requests[2], CancellationToken.None);
        await overLimit.Should().ThrowAsync<McpOperatorTaskLimitException>();
        await store.RecordLifecycleAsync(first.CommandId, fixture.TenantId, fixture.AgentId,
            "Completed", "done", fixture.Now.AddSeconds(1), CancellationToken.None);
        var next = await store.CreateOrGetAsync(requests[2], CancellationToken.None);
        var replay = await store.CreateOrGetAsync(requests[2], CancellationToken.None);
        next.TaskId.Should().BeGreaterThan(first.TaskId);
        replay.TaskId.Should().Be(next.TaskId);
        (await db.JobTaskActivities.CountAsync()).Should().Be(3);
        (await db.McpOperatorTasks.CountAsync()).Should().Be(3);

        var jobAccess = fixture.Decision.Request with
        {
            OperationFamily = McpOperatorOperationFamily.AutomationWrite,
            Operation = "netratel_jobs/create", Tool = "netratel_jobs"
        };
        var jobDecision = fixture.Decision with
        {
            Request = jobAccess,
            EffectiveConstraints = new McpOperatorConstraints(MaxJobTargetCount: 1, MaxFanOut: 1)
        };
        var jobAudit = fixture.Audit with { OperationFamily = jobAccess.OperationFamily, Operation = jobAccess.Operation, Tool = jobAccess.Tool };
        var jobs = new McpOperatorJobStore(db);
        var job = await jobs.CreateAsync(new McpOperatorJobCreateRequest(jobDecision, jobAudit,
            new McpOperatorJobDraft("postgres-job", "/"), fixture.Now), CancellationToken.None);
        var run = new JobRunRecord { Id = 1, JobId = job.JobId, Status = (int)JobRunState.Running, CreatedAtUtc = fixture.Now };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync();
        var delete = () => jobs.DeleteAsync(job.JobId, job.Version, jobDecision, jobAudit, fixture.Now, CancellationToken.None);
        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("An active operator job run*");
        run.Status = (int)JobRunState.Succeeded;
        foreach (var state in new[] { JobRunState.Failed, JobRunState.Cancelled, JobRunState.TimedOut })
            db.JobRuns.Add(new JobRunRecord { Id = (long)state + 1, JobId = job.JobId, Status = (int)state, CreatedAtUtc = fixture.Now });
        await db.SaveChangesAsync();
        (await delete()).Should().NotBeNull();
    }

    [Theory]
    [InlineData("Processing", "CancelRequested")]
    [InlineData("Cancelled", "Cancelled")]
    public async Task GatewayLifecycle_RefreshesTaskStateAfterHttpCancellation(string incoming, string expected)
    {
        var database = Guid.NewGuid().ToString("N");
        await using var gatewayDb = CreateDb(database);
        await using var httpDb = CreateDb(database);
        var fixture = CreateFixture();
        var gateway = new McpOperatorTaskStore(gatewayDb);
        var created = await gateway.CreateOrGetAsync(CreateRequest(fixture, Guid.NewGuid()), CancellationToken.None);
        await gateway.RecordLifecycleAsync(created.CommandId, fixture.TenantId, fixture.AgentId, "Processing", null, fixture.Now, CancellationToken.None);
        await new McpOperatorTaskStore(httpDb).RequestCancellationAsync(CancelRequest(fixture, created.TaskId), CancellationToken.None);

        await gateway.RecordLifecycleAsync(created.CommandId, fixture.TenantId, fixture.AgentId, incoming, "agent-cancelled", fixture.Now.AddMinutes(2), CancellationToken.None);

        await using var verification = CreateDb(database);
        var saved = await verification.McpOperatorTasks.SingleAsync();
        saved.State.Should().Be(expected);
        saved.CancellationRequested.Should().BeTrue();
        saved.CancellationRequestedAtUtc.Should().Be(fixture.Now.AddMinutes(1));
        (await verification.McpOperatorTaskAudits.CountAsync(audit => audit.Action == "cancel")).Should().Be(1);
        if (incoming == "Cancelled")
        {
            saved.ResultSummary.Should().Be("agent-cancelled");
            saved.CompletedAtUtc.Should().Be(fixture.Now.AddMinutes(2));
        }
    }

    [Fact]
    public async Task TerminalLifecycle_RetriesCancellationCommittedBetweenReadAndSave()
    {
        var database = Guid.NewGuid().ToString("N");
        var race = new BeforeSaveInterceptor();
        await using var gatewayDb = CreateDb(database, race);
        await using var httpDb = CreateDb(database);
        var fixture = CreateFixture();
        var gateway = new McpOperatorTaskStore(gatewayDb);
        var created = await gateway.CreateOrGetAsync(CreateRequest(fixture, Guid.NewGuid()), CancellationToken.None);
        race.BeforeNextSave = async token =>
            await new McpOperatorTaskStore(httpDb).RequestCancellationAsync(CancelRequest(fixture, created.TaskId), token);

        await gateway.RecordLifecycleAsync(created.CommandId, fixture.TenantId, fixture.AgentId,
            "Cancelled", "agent-cancelled", fixture.Now.AddMinutes(2), CancellationToken.None);

        race.Executions.Should().Be(1);
        await using var verification = CreateDb(database);
        var saved = await verification.McpOperatorTasks.SingleAsync();
        saved.State.Should().Be("Cancelled");
        saved.CancellationRequested.Should().BeTrue();
        saved.ResultSummary.Should().Be("agent-cancelled");
    }

    [Fact]
    public async Task HttpCancellation_RefreshesAlreadyCompletedTaskWithoutCreatingCancelAudit()
    {
        var database = Guid.NewGuid().ToString("N");
        await using var httpDb = CreateDb(database);
        await using var gatewayDb = CreateDb(database);
        var fixture = CreateFixture();
        var http = new McpOperatorTaskStore(httpDb);
        var created = await http.CreateOrGetAsync(CreateRequest(fixture, Guid.NewGuid()), CancellationToken.None);
        await new McpOperatorTaskStore(gatewayDb).RecordLifecycleAsync(created.CommandId, fixture.TenantId, fixture.AgentId,
            "Completed", "completed-before-cancel", fixture.Now.AddSeconds(1), CancellationToken.None);

        var result = await http.RequestCancellationAsync(CancelRequest(fixture, created.TaskId), CancellationToken.None);

        result!.State.Should().Be("Completed");
        result.ResultSummary.Should().Be("completed-before-cancel");
        await using var verification = CreateDb(database);
        (await verification.McpOperatorTaskAudits.CountAsync(audit => audit.Action == "cancel")).Should().Be(0);
    }

    [Fact]
    public async Task PostgreSql_cancel_races_preserve_terminal_results_and_exactly_one_committed_audit()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();
        var connection = postgres.GetConnectionString();
        await using var seed = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
        await seed.Database.MigrateAsync();
        var fixture = CreateFixture();
        var requests = Enumerable.Range(0, 2).Select(_ => CreateRequest(fixture, Guid.NewGuid())).ToArray();
        await SeedPostgresAsync(seed, fixture, requests);
        var race = new BeforeSaveInterceptor();
        await using var httpDb = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(connection).AddInterceptors(race).Options);
        await using var gatewayDb = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
        var http = new McpOperatorTaskStore(httpDb);
        var gateway = new McpOperatorTaskStore(gatewayDb);
        var completed = await http.CreateOrGetAsync(requests[0], CancellationToken.None);
        race.BeforeNextSave = token => gateway.RecordLifecycleAsync(completed.CommandId, fixture.TenantId, fixture.AgentId,
            "Completed", "completed-during-cancel", fixture.Now.AddSeconds(1), token);

        var terminal = await http.RequestCancellationAsync(CancelRequest(fixture, completed.TaskId), CancellationToken.None);
        terminal!.State.Should().Be("Completed");
        terminal.ResultSummary.Should().Be("completed-during-cancel");
        // A later save in the same scope must not commit the abandoned retry's audit.
        await httpDb.SaveChangesAsync();
        (await seed.McpOperatorTaskAudits.CountAsync(audit => audit.Action == "cancel")).Should().Be(0);

        var cancelled = await http.CreateOrGetAsync(requests[1], CancellationToken.None);
        var cancel = CancelRequest(fixture, cancelled.TaskId);
        race.BeforeNextSave = async token => await gateway.RequestCancellationAsync(cancel, token);
        (await http.RequestCancellationAsync(cancel, CancellationToken.None))!.State.Should().Be("CancelRequested");
        await httpDb.SaveChangesAsync();
        (await seed.McpOperatorTaskAudits.CountAsync(audit => audit.Action == "cancel")).Should().Be(1);
        race.Executions.Should().Be(2);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task TerminalLogs_ProjectAvailableStdoutAndStderrWithoutPersistingFabricatedRows(string state)
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var task = await CreateResultTaskAsync(store, fixture, """{"stdout":"first\nsecond","stderr":"Bearer example-token"}""", state);

        var logs = await LogsAsync(store, fixture, task.TaskId);

        logs.Select(log => log.LogId).Should().Equal(1L, 2L);
        logs.Select(log => log.Sequence).Should().Equal(1L, 2L);
        logs.Select(log => log.Stream).Should().Equal("stdout", "stderr");
        logs.Select(log => log.Message).Should().Equal("first\nsecond", "Bearer [REDACTED]");
        logs.Should().OnlyContain(log => log.TaskId == task.TaskId && log.RequestId == task.CommandId &&
            log.TimestampUtc == fixture.Now.AddMinutes(1));
        (await db.JobTaskLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TerminalLogs_KeepStableCursorsBeforeStreamFilteringAndAcrossBoundedPages()
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var stdout = Enumerable.Range(1, 102).Select(index => $"output-{index}").ToArray();
        var task = await CreateResultTaskAsync(store, fixture, JsonSerializer.Serialize(new { stdout, stderr = new[] { "error-1", "error-2" } }));

        var first = await LogsAsync(store, fixture, task.TaskId, limit: 1000);
        first.Should().HaveCount(100);
        var next = await LogsAsync(store, fixture, task.TaskId, since: first[^1].LogId);
        var error = await LogsAsync(store, fixture, task.TaskId, stream: "stderr", limit: 1);
        var nextError = await LogsAsync(store, fixture, task.TaskId, since: error.Single().LogId, stream: "stderr");

        first.Select(log => log.LogId).Should().Equal(Enumerable.Range(1, 100).Select(index => (long)index));
        next.Select(log => log.LogId).Should().Equal(101L, 102L, 103L, 104L);
        next.Select(log => log.Message).Should().Equal("output-101", "output-102", "error-1", "error-2");
        error.Single().LogId.Should().Be(103);
        nextError.Single().LogId.Should().Be(104);
        (await LogsAsync(store, fixture, task.TaskId, since: 104)).Should().BeEmpty();
        (await LogsAsync(store, fixture, task.TaskId, stream: "STDERR")).Should().BeEmpty();
        (await LogsAsync(store, fixture, task.TaskId, since: 103, stream: "ALL")).Single().LogId.Should().Be(104);
    }

    [Fact]
    public async Task TerminalLogs_AnyPersistedRowExcludesSnapshotEvenWhenFilteredPageIsEmpty()
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var task = await CreateResultTaskAsync(store, fixture, """{"stdout":["snapshot-only"],"stderr":"snapshot-error"}""");
        db.JobTaskLogs.Add(new JobTaskLogRecord
        {
            Id = 500, JobTaskActivityId = task.TaskId, RequestId = task.CommandId,
            TimestampUtc = fixture.Now, Stream = "stderr", Message = "persisted", Sequence = 7
        });
        await db.SaveChangesAsync();

        var logs = await LogsAsync(store, fixture, task.TaskId);

        logs.Single().LogId.Should().Be(500);
        logs.Single().Sequence.Should().Be(7);
        logs.Single().Message.Should().Be("persisted");
        logs.Single().TimestampUtc.Should().Be(fixture.Now);
        (await LogsAsync(store, fixture, task.TaskId, stream: "stdout")).Should().BeEmpty();
        (await LogsAsync(store, fixture, task.TaskId, since: 500)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("client")]
    [InlineData("tenant")]
    [InlineData("agent")]
    [InlineData("resource")]
    [InlineData("instance")]
    public async Task TerminalLogs_RemainBoundToTheExactTaskOwner(string mismatch)
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var task = await CreateResultTaskAsync(store, fixture, """{"stdout":"private output"}""");
        var other = mismatch switch
        {
            "subject" => fixture with { Principal = fixture.Principal with { Subject = "other" } },
            "client" => fixture with { Principal = fixture.Principal with { ClientId = "other-client" } },
            "tenant" => fixture with { TenantId = fixture.TenantId + 1 },
            "agent" => fixture with { AgentId = Guid.NewGuid() },
            "resource" => fixture with { Resource = "https://other.example/mcp" },
            "instance" => fixture with { Instance = "other" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        (await LogsAsync(store, other, task.TaskId)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"stdout\":[\"partial output\"")]
    [InlineData("[]")]
    [InlineData("\"not process output\"")]
    [InlineData("{\"stdout\":true,\"stderr\":42}")]
    [InlineData("Result payload unavailable; terminal status recovered from authoritative command lifecycle.")]
    public async Task TerminalLogs_DoNotInventOutputFromUnavailableOrMalformedSummaries(string? summary)
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var task = await CreateResultTaskAsync(store, fixture, summary);

        (await LogsAsync(store, fixture, task.TaskId)).Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalLogs_IgnoreNonStringArrayElementsAndUnrecognizedFields()
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var task = await CreateResultTaskAsync(store, fixture,
            """{"stdout":[null,{},"one",true,7],"stderr":[["nested"],"two"],"message":"unrecognized"}""");

        var logs = await LogsAsync(store, fixture, task.TaskId);

        logs.Select(log => log.Message).Should().Equal("one", "two");
        logs.Select(log => log.LogId).Should().Equal(1L, 2L);
    }

    [Fact]
    public async Task TerminalLogs_DoNotProjectMutableNonterminalOrTruncatedResult()
    {
        await using var db = CreateDb();
        var fixture = CreateFixture();
        var store = new McpOperatorTaskStore(db);
        var pending = await CreateResultTaskAsync(store, fixture, """{"stdout":"not final"}""", "Processing");
        var truncated = await CreateResultTaskAsync(store, fixture,
            JsonSerializer.Serialize(new { stdout = new string('x', 100_000) }));

        (await LogsAsync(store, fixture, pending.TaskId)).Should().BeEmpty();
        (await LogsAsync(store, fixture, truncated.TaskId)).Should().BeEmpty();
        var saved = await store.GetOwnedAsync(truncated.TaskId, fixture.TenantId, fixture.AgentId,
            fixture.Principal, fixture.Resource, fixture.Instance, CancellationToken.None);
        saved!.ResultSummary.Should().NotBeNull();
        Encoding.UTF8.GetByteCount(saved.ResultSummary!).Should().BeLessThanOrEqualTo(48 * 1024);
    }

    private static async Task<McpOperatorTaskLease> CreateResultTaskAsync(McpOperatorTaskStore store,
        Fixture fixture, string? summary, string state = "Completed")
    {
        var created = await store.CreateOrGetAsync(CreateRequest(fixture, Guid.NewGuid()), CancellationToken.None);
        await store.RecordLifecycleAsync(created.CommandId, fixture.TenantId, fixture.AgentId,
            state, summary, fixture.Now.AddMinutes(1), CancellationToken.None);
        return created;
    }

    private static Task<IReadOnlyList<McpOperatorTaskLogLease>> LogsAsync(McpOperatorTaskStore store,
        Fixture fixture, long taskId, long since = 0, string? stream = null, int limit = 100) =>
        store.ListLogsOwnedAsync(taskId, fixture.TenantId, fixture.AgentId, fixture.Principal,
            fixture.Resource, fixture.Instance, since, stream, limit, CancellationToken.None);

    private static McpOperatorTaskCancelRequest CancelRequest(Fixture fixture, long taskId) =>
        new(taskId, fixture.Decision, fixture.Audit, fixture.Now.AddMinutes(1));

    private static async Task SeedPostgresAsync(OrchestratorDbContext db, Fixture fixture, McpOperatorTaskCreateRequest[] requests)
    {
        var policyId = fixture.Decision.MatchingPolicyIds.Single();
        db.Tenants.Add(new Tenant { Id = fixture.TenantId, Name = "Task regression" });
        db.Agents.Add(new Agent { Id = fixture.AgentId, TenantId = fixture.TenantId, Name = "task-agent", CreatedAtUtc = fixture.Now });
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = policyId, Name = "Task regression", Environment = McpOperatorEnvironment.Production,
            TenantId = fixture.TenantId, AgentId = fixture.AgentId, Version = 5, CreatedAtUtc = fixture.Now
        });
        db.McpOperatorAcceptedAudits.Add(new McpOperatorAcceptedAuditRecord
        {
            Id = fixture.Audit.AuditId, PolicyId = policyId, Environment = McpOperatorEnvironment.Production,
            TenantId = fixture.TenantId, AgentId = fixture.AgentId, Subject = fixture.Principal.Subject,
            Operation = fixture.Decision.Request.Operation, OccurredAtUtc = fixture.Now
        });
        foreach (var request in requests)
            db.McpOperatorIdempotencyRecords.Add(new McpOperatorIdempotencyRecord
            {
                Id = request.IdempotencyId, PolicyId = policyId, PolicyVersion = 5, Environment = McpOperatorEnvironment.Production,
                TenantId = fixture.TenantId, AgentId = fixture.AgentId, Subject = fixture.Principal.Subject,
                ClientId = fixture.Principal.ClientId!, IdempotencyKey = request.IdempotencyId.ToString("N"),
                CreatedAtUtc = fixture.Now, Version = 1
            });
        await db.SaveChangesAsync();
    }

    private sealed class BeforeSaveInterceptor : SaveChangesInterceptor
    {
        public Func<CancellationToken, Task>? BeforeNextSave { get; set; }
        public int Executions { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var action = BeforeNextSave;
            BeforeNextSave = null;
            if (action is not null)
            {
                Executions++;
                await action(cancellationToken);
            }
            return result;
        }
    }

    private static McpOperatorTaskCreateRequest CreateRequest(Fixture fixture, Guid idempotencyId) => new(
        Guid.NewGuid().ToString("N"), fixture.Decision, fixture.Audit, idempotencyId, "corr-task-42",
        TaskKinds.ExecShellCommand, "pwsh", Hash("Write-Output task"), 17, null, null, null, 60, 1024, fixture.Now);

    private static Fixture CreateFixture()
    {
        var now = DateTimeOffset.UtcNow;
        const int tenantId = 42;
        const string resource = "https://mcp.prod.example/mcp";
        const string instance = "prod";
        var agentId = Guid.Parse("a2f006cc-418b-47fe-8308-9a13aa5c5150");
        var policyId = Guid.Parse("70cefc92-967b-4d2f-bf1a-52d3f93490fd");
        var auditId = Guid.Parse("b2546b1b-60c0-4e2a-9581-00e71fa9bf50");
        var principal = new McpOperatorPrincipal("operator@example.test", "operator-client", "operator-client", Set(), Set(), Set("netratel.mcp.execute"));
        var digest = Hash("tenant-42-task-agent");
        var access = new McpOperatorAccessRequest(McpOperatorEnvironment.Production, principal, tenantId, agentId,
            McpOperatorTargetClassification.ManagedStandard, McpOperatorOperationFamily.TaskExecution, "netratel_tasks/create_command",
            Set("netratel.mcp.execute"), McpOperatorConfirmationClass.RemoteExecution, "corr-task-42", "request-task-42", digest,
            McpResource: resource, McpInstance: instance, Tool: "netratel_tasks");
        var decision = new McpOperatorDecision(true, null, null, [policyId],
            new McpOperatorConstraints(MaxCommandDurationSeconds: 300, MaxOutputBytes: 4096, MaxConcurrentCommands: 2, MaxTaskTargetCount: 1, MaxFanOut: 1), digest, access, 5);
        var audit = new McpOperatorAcceptedAudit(auditId, policyId, access.Environment, "netratel-mcp-http-prod", principal.Subject,
            principal.ClientId, principal.AuthorizedParty, [], [], principal.Scopes.Order(StringComparer.Ordinal).ToArray(), resource, instance,
            access.Tool, tenantId, agentId, access.OperationFamily, access.Operation, access.CorrelationId, access.RequestId, now);
        return new Fixture(now, tenantId, agentId, principal, decision, audit, resource, instance);
    }

    private static OrchestratorDbContext CreateDb(string? database = null, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(database ?? Guid.NewGuid().ToString("N"));
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Fixture(DateTimeOffset Now, int TenantId, Guid AgentId, McpOperatorPrincipal Principal,
        McpOperatorDecision Decision, McpOperatorAcceptedAudit Audit, string Resource, string Instance);
}
