using FluentAssertions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services.Operations;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetRatel.Application.Scripts;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ScriptSourceRevisionPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private DbContextOptions<OrchestratorDbContext> options = null!;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrchestratorDbContext(options);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Source_deletion_and_reference_writers_share_a_transaction_fence(bool owned, bool update, bool deleteFirst)
    {
        var fixture = await CreateReferenceFixtureAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var winner = new OrchestratorDbContext(options);
        await using var loser = new OrchestratorDbContext(options);
        await loser.Database.OpenConnectionAsync(ct);
        var loserPid = ((NpgsqlConnection)loser.Database.GetDbConnection()).ProcessID;
        await using var transaction = await winner.Database.BeginTransactionAsync(ct);
        Task pending;
        if (deleteFirst)
        {
            await new ScriptService(winner).DeleteAsync(fixture.Source.Id, fixture.Source.SourceRevision, ct);
            pending = WriteReferenceAsync(loser, fixture, owned, update, ct);
        }
        else
        {
            await WriteReferenceAsync(winner, fixture, owned, update, ct);
            pending = new ScriptService(loser).DeleteAsync(fixture.Source.Id, fixture.Source.SourceRevision, ct);
        }

        // Observe a real PostgreSQL lock wait before releasing the winning
        // transaction. An implementation missing the common fence cannot pass.
        await WaitForDatabaseBlockAsync(loserPid, pending, ct);
        await transaction.CommitAsync(ct);
        var finish = async () => await pending;
        if (deleteFirst) await finish.Should().ThrowAsync<ArgumentException>();
        else await finish.Should().ThrowAsync<ScriptSourceInUseException>();

        await using var verify = new OrchestratorDbContext(options);
        var source = await verify.Scripts.IgnoreQueryFilters().SingleAsync(row => row.Id == (long)fixture.Source.Id, ct);
        source.DeletedAtUtc.HasValue.Should().Be(deleteFirst);
        source.SourceRevision.Should().Be(deleteFirst ? 2 : 1);
        (await verify.JobSteps.CountAsync(row => row.ScriptId == (long)fixture.Source.Id, ct)).Should().Be(deleteFirst ? 0 : 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Already_tombstoned_source_rejects_reference_without_changing_job(bool owned, bool update)
    {
        var fixture = await CreateReferenceFixtureAsync();
        await using var db = new OrchestratorDbContext(options);
        await new ScriptService(db).DeleteAsync(fixture.Source.Id, fixture.Source.SourceRevision);
        var write = () => WriteReferenceAsync(db, fixture, owned, update, CancellationToken.None);
        await write.Should().ThrowAsync<ArgumentException>();
        await using var verify = new OrchestratorDbContext(options);
        (await verify.JobSteps.SingleAsync()).ScriptId.Should().BeNull();
        (await verify.McpOperatorJobs.SingleAsync()).Version.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_writer_rejects_missing_source(bool update)
    {
        var fixture = await CreateReferenceFixtureAsync();
        fixture = fixture with { Source = fixture.Source with { Id = ulong.MaxValue / 2 } };
        await using var db = new OrchestratorDbContext(options);
        var write = () => WriteReferenceAsync(db, fixture, false, update, CancellationToken.None);
        await write.Should().ThrowAsync<ArgumentException>();
        (await db.JobSteps.SingleAsync()).ScriptId.Should().BeNull();
    }

    private async Task WaitForDatabaseBlockAsync(int processId, Task pending, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", connection);
            command.Parameters.AddWithValue("pid", processId);
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return;
            pending.IsCompleted.Should().BeFalse("the competing source operation must wait for the transaction fence");
        } while (await timer.WaitForNextTickAsync(ct));
        throw new InvalidOperationException("The database lock observation ended before a fence was acquired.");
    }

    private static async Task WriteReferenceAsync(OrchestratorDbContext db, ReferenceFixture fixture,
        bool owned, bool update, CancellationToken ct)
    {
        if (owned)
        {
            var request = new McpOperatorJobStepMutationRequest(fixture.JobId, update ? fixture.StepId : null, 1,
                fixture.Decision, fixture.Audit, new McpOperatorJobStep(1, (long)fixture.Source.Id, 1,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Source.Content)))), DateTimeOffset.UtcNow);
            var store = new McpOperatorJobStore(db);
            if (update) await store.ReplaceStepAsync(request, ct);
            else await store.AddStepAsync(request, ct);
        }
        else
        {
            var service = new JobDefinitionService(db);
            if (update) await service.UpdateStepAsync(new((ulong)fixture.StepId, JobStepKind.LibraryScript,
                null, null, fixture.Source.Id, null, null), ct);
            else await service.AddStepAsync(new((ulong)fixture.JobId, 1, JobStepKind.LibraryScript,
                null, null, fixture.Source.Id, null, true), ct);
        }
    }

    private async Task<ReferenceFixture> CreateReferenceFixtureAsync()
    {
        var source = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var policy = Guid.NewGuid();
        var principal = new McpOperatorPrincipal("reference-test", "reference-client", "reference-client", new HashSet<string>(), new HashSet<string>(),
            new HashSet<string> { "netratel.mcp.write" });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("reference-target")));
        var access = new McpOperatorAccessRequest(McpOperatorEnvironment.Production, principal, 42, Guid.NewGuid(),
            McpOperatorTargetClassification.ManagedStandard, McpOperatorOperationFamily.AutomationWrite, "netratel_jobs/create",
            principal.Scopes, McpOperatorConfirmationClass.StandardMutation, "reference-correlation", "reference-request", digest,
            McpResource: "https://mcp.example/mcp", McpInstance: "prod", Tool: "netratel_jobs");
        var decision = new McpOperatorDecision(true, null, null, [policy], new(MaxJobTargetCount: 1, MaxFanOut: 1), digest, access, 1);
        var audit = new McpOperatorAcceptedAudit(Guid.NewGuid(), policy, access.Environment, "mcp-test", principal.Subject,
            principal.ClientId, principal.AuthorizedParty, [], [], ["netratel.mcp.write"], access.McpResource, access.McpInstance,
            access.Tool, access.TenantId, access.AgentId, access.OperationFamily, access.Operation, access.CorrelationId, access.RequestId, now);
        await using var db = new OrchestratorDbContext(options);
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = policy, Name = "reference-test-policy", Environment = access.Environment,
            Effect = McpOperatorPolicyEffect.Allow, TenantId = access.TenantId, AgentId = access.AgentId,
            PrincipalSelectorValue = principal.Subject, OperationFamily = access.OperationFamily,
            Operation = access.Operation, CreatedAtUtc = now, CreatedBy = principal.Subject, Version = 1
        });
        db.McpOperatorAcceptedAudits.Add(new McpOperatorAcceptedAuditRecord
        {
            Id = audit.AuditId, PolicyId = policy, Environment = access.Environment,
            ServicePrincipal = audit.ServicePrincipal, Subject = principal.Subject, ClientId = principal.ClientId,
            AuthorizedParty = principal.AuthorizedParty, McpResource = access.McpResource, McpInstance = access.McpInstance,
            Tool = access.Tool, TenantId = access.TenantId, AgentId = access.AgentId,
            OperationFamily = access.OperationFamily, Operation = access.Operation,
            CorrelationId = access.CorrelationId, RequestId = access.RequestId, OccurredAtUtc = now
        });
        db.McpOperatorScripts.Add(new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(), ScriptId = (long)source.Id, TenantId = 42, Subject = principal.Subject,
            ClientId = principal.ClientId!, McpResource = access.McpResource!, McpInstance = access.McpInstance!,
            Name = "source", Description = "reference test", ShellType = "sh", PolicyId = policy, PolicyVersion = 1,
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Content))),
            ManifestHash = digest, TimeoutSeconds = 60, WorkingDirectory = "/tmp", CreatedAtUtc = now, UpdatedAtUtc = now, Version = 1
        });
        await db.SaveChangesAsync();
        var job = await new McpOperatorJobStore(db).CreateAsync(new(decision, audit, new("reference-job", "/"), now), CancellationToken.None);
        var step = await new JobDefinitionService(db).AddStepAsync(new((ulong)job.JobId, 1, JobStepKind.RunCommand,
            "sh", "true", null, null, true));
        return new(source, job.JobId, (long)step!.Id, decision, audit);
    }

    private sealed record ReferenceFixture(ScriptInfo Source, long JobId, long StepId,
        McpOperatorDecision Decision, McpOperatorAcceptedAudit Audit);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_tracked_writer_cannot_overwrite_a_committed_UI_edit(bool synchronousSave)
    {
        var source = await CreateAsync();
        await using var stale = new OrchestratorDbContext(options);
        var tracked = await stale.Scripts.SingleAsync(script => script.Id == (long)source.Id);
        await using (var winner = new OrchestratorDbContext(options))
        {
            var updated = await new ScriptService(winner).UpdateAsync(Edit(source, "winning UI content"));
            updated!.SourceRevision.Should().Be(2);
        }
        tracked.Content = "stale MCP source content";
        if (synchronousSave)
        {
            Action save = () => stale.SaveChanges();
            save.Should().Throw<DbUpdateConcurrencyException>();
        }
        else
        {
            var save = () => stale.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }
        await using var verify = new OrchestratorDbContext(options);
        var actual = await new ScriptService(verify).GetAsync(source.Id);
        actual!.Content.Should().Be("winning UI content");
        actual.SourceRevision.Should().Be(2);
    }

    [Fact]
    public async Task Read_save_race_conflicts_and_rolls_back_parameter_replacement()
    {
        var source = await CreateAsync();
        var interceptor = new BeforeSave(async () =>
        {
            await using var winner = new OrchestratorDbContext(options);
            await new ScriptService(winner).ParseManifestAsync(source.Id,
                """{"params":[{"name":"winner","type":"string"}]}""", source.SourceRevision);
        });
        var racedOptions = new DbContextOptionsBuilder<OrchestratorDbContext>(options).AddInterceptors(interceptor).Options;
        await using var loser = new OrchestratorDbContext(racedOptions);
        var mutate = () => new ScriptService(loser).UpdateAsync(Edit(source, "loser") with
        { ManifestRaw = """{"params":[{"name":"loser","type":"string"}]}""" });
        await mutate.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var verify = new OrchestratorDbContext(options);
        var library = new ScriptService(verify);
        (await library.GetAsync(source.Id))!.Content.Should().Be(source.Content);
        (await library.GetAsync(source.Id))!.SourceRevision.Should().Be(2);
        (await library.GetParamsAsync(source.Id)).Should().ContainSingle(parameter => parameter.Name == "winner");
    }

    [Fact]
    public async Task Referenced_source_cannot_be_deleted_and_unreferenced_deletion_retains_source_identity()
    {
        var source = await CreateAsync();
        await using (var setup = new OrchestratorDbContext(options))
        {
            setup.Jobs.Add(new JobDefinition { Name = "source reference", Steps = [new JobStepDefinition { ScriptId = (long)source.Id }] });
            await setup.SaveChangesAsync();
        }
        await using (var db = new OrchestratorDbContext(options))
        {
            var delete = () => new ScriptService(db).DeleteAsync(source.Id, source.SourceRevision);
            await delete.Should().ThrowAsync<ScriptSourceInUseException>();
            (await db.Scripts.SingleAsync(script => script.Id == (long)source.Id)).SourceRevision.Should().Be(1);
        }
        var free = await CreateAsync();
        await using (var db = new OrchestratorDbContext(options))
        {
            var deleted = await new ScriptService(db).DeleteAsync(free.Id, free.SourceRevision);
            deleted!.SourceRevision.Should().Be(2);
        }
        await using var verify = new OrchestratorDbContext(options);
        (await new ScriptService(verify).GetAsync(free.Id)).Should().BeNull();
        (await new ScriptService(verify).GetParamsAsync(free.Id)).Should().BeEmpty();
        var retained = await verify.Scripts.IgnoreQueryFilters().SingleAsync(script => script.Id == (long)free.Id);
        retained.DeletedAtUtc.Should().NotBeNull();
        retained.Content.Should().Be(free.Content);
    }

    [Fact]
    public async Task Manifest_revision_fences_later_UI_edits_even_when_content_hash_is_unchanged()
    {
        var source = await CreateAsync();
        await using (var db = new OrchestratorDbContext(options))
            await new ScriptService(db).ParseManifestAsync(source.Id, "{}", source.SourceRevision);
        await using var stale = new OrchestratorDbContext(options);
        var update = () => new ScriptService(stale).UpdateAsync(Edit(source, "outdated edit"));
        await update.Should().ThrowAsync<ScriptSourceConcurrencyException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_outbox_and_success_receipt_commit_together_or_roll_back(bool failOutbox)
    {
        var admission = new CanonicalAdmission();
        var services = new ServiceCollection().AddLogging();
        services.AddScoped(_ => new OrchestratorDbContext(options));
        services.AddScoped<IScriptService, ScriptService>();
        services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        services.AddSingleton<IMcpOperatorRouteAdmission>(admission);
        services.AddScoped<McpDevelopmentScriptAdapter>();
        if (failOutbox) services.AddScoped<IEventRecorder, FailingEvents>();
        else services.AddScoped<IEventRecorder, OutboxEventRecorder>();
        await using var provider = services.BuildServiceProvider();
        var request = new McpOperatorScriptMutationRequest(Source: new("source", "transaction content", "bash"));
        await using (var scope = provider.CreateAsyncScope())
        {
            var preview = JsonSerializer.SerializeToElement(await scope.ServiceProvider.GetRequiredService<McpDevelopmentScriptAdapter>()
                .PreviewAsync("create", request, admission.Decision, CancellationToken.None));
            request = request with { PlanToken = preview.GetProperty("PlanToken").GetString(), IdempotencyKey = preview.GetProperty("IdempotencyKey").GetString() };
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var confirm = () => scope.ServiceProvider.GetRequiredService<McpDevelopmentScriptAdapter>()
                .ConfirmAsync("create", request, admission.Decision, admission.Route, CancellationToken.None);
            if (failOutbox) await confirm.Should().ThrowAsync<InvalidOperationException>().WithMessage("outbox unavailable");
            else (await confirm()).SourceRevision.Should().Be(1);
        }
        await using var verify = new OrchestratorDbContext(options);
        (await verify.Scripts.CountAsync()).Should().Be(failOutbox ? 0 : 1);
        (await verify.OutboxMessages.CountAsync(message => message.Source == "McpDevelopmentScriptAdapter")).Should().Be(failOutbox ? 0 : 1);
        var receipt = await verify.McpOperatorIdempotencyRecords.SingleAsync();
        receipt.Outcome.Should().Be(failOutbox ? McpOperatorIdempotencyOutcome.Pending : McpOperatorIdempotencyOutcome.Succeeded);
        (await verify.McpOperatorScripts.CountAsync()).Should().Be(0);
        await using var replayScope = provider.CreateAsyncScope();
        var replay = () => replayScope.ServiceProvider.GetRequiredService<McpDevelopmentScriptAdapter>()
            .ConfirmAsync("create", request, admission.Decision, admission.Route, CancellationToken.None);
        if (failOutbox)
            (await replay.Should().ThrowAsync<McpDevelopmentScriptException>()).Which.Code.Should().Be("idempotency_pending");
        else (await replay()).Replayed.Should().BeTrue();
    }

    private sealed class FailingEvents : IEventRecorder
    {
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) => throw new InvalidOperationException("outbox unavailable");
    }

    private sealed class CanonicalAdmission : IMcpOperatorRouteAdmission
    {
        private readonly Guid policy = Guid.NewGuid();
        public McpOperatorRouteAccessRequest Route { get; } = new(McpOperatorEnvironment.Development,
            new McpOperatorPrincipal("source-tester", "source-client", "source-client", new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "netratel.mcp.write" }),
            "service", "https://mcp.dev.example/mcp", "dev", "netratel_scripts", "create", 1, Guid.NewGuid(),
            new HashSet<string> { "netratel.mcp.write" }, "source-transaction", "source-request", true, true);
        public McpOperatorDecision Decision
        {
            get
            {
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("target")));
                var access = new McpOperatorAccessRequest(Route.Environment, Route.Principal, Route.TenantId, Route.AgentId, null,
                    McpOperatorOperationFamily.ScriptsWrite, "netratel_scripts/create", Route.RequiredScopes, McpOperatorConfirmationClass.StandardMutation,
                    Route.CorrelationId, Route.RequestId, digest, McpResource: Route.McpResource, McpInstance: "dev", Tool: "netratel_scripts");
                return new(true, null, null, [policy], new McpOperatorConstraints(), digest, access, 1, DevelopmentEnvironmentAccess: true);
            }
        }
        public Task<McpOperatorRouteAdmission> EvaluateAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new McpOperatorRouteAdmission(Decision, McpOperatorOperationCatalog.Find("netratel_scripts", "create")));
        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken) => Task.FromResult(
            new McpOperatorAcceptedAudit(Guid.NewGuid(), policy, request.Environment, request.ServicePrincipal, request.Principal.Subject,
                request.Principal.ClientId, request.Principal.AuthorizedParty, [], [], [], request.McpResource, request.McpInstance,
                request.Tool, request.TenantId, request.AgentId, McpOperatorOperationFamily.ScriptsWrite, "netratel_scripts/create",
                request.CorrelationId, request.RequestId, DateTimeOffset.UtcNow));
    }

    private async Task<ScriptInfo> CreateAsync()
    {
        await using var db = new OrchestratorDbContext(options);
        return await new ScriptService(db).CreateAsync(new("source", "/", "test", "original", "bash", null));
    }

    private static UpdateScriptCommand Edit(ScriptInfo source, string content) =>
        new(source.Id, null, null, null, content, null, null, source.SourceRevision);

    private sealed class BeforeSave(Func<Task> interleave) : SaveChangesInterceptor
    {
        private bool invoked;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!invoked) { invoked = true; await interleave(); }
            return result;
        }
    }
}
