using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ExecutionAdmissionPostgresTests(PostgreSqlPersistenceFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<OrchestratorDbContext> _options = null!;
    private readonly IDataProtectionProvider _keys = new EphemeralDataProtectionProvider();

    public async ValueTask InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync()).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Concurrent_admission_and_a_lost_receipt_recover_one_exact_protected_execution_identity()
    {
        var identity = Identity();
        var submission = new ExecutionAdmissionSubmission(identity, ExecutionAdmissionKind.Job,
            "{\"definition\":\"pinned-revision\",\"inputs\":{\"credential\":\"synthetic-protected-value\"}}");
        var allocations = 0;
        async Task<ExecutionAdmissionBinding> Admit()
        {
            await using var db = new OrchestratorDbContext(_options);
            return await new ExecutionAdmissionStore(db, _keys, TimeProvider.System).ReserveAsync(submission,
                () => (9007199254740996L + Interlocked.Increment(ref allocations)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                CancellationToken.None);
        }
        var accepted = await Task.WhenAll(Admit(), Admit(), Admit());
        accepted.Select(binding => binding.ExecutionId).Distinct().Should().ContainSingle().Which.Should().Be("9007199254740997");
        allocations.Should().Be(1);
        // A new process scope sees the committed identity even if its original flow receipt was lost.
        await using var reloaded = new OrchestratorDbContext(_options);
        var replay = await new ExecutionAdmissionStore(reloaded, _keys, TimeProvider.System)
            .FindByExecutionAsync(ExecutionAdmissionKind.Job, "9007199254740997", CancellationToken.None);
        replay.Should().BeEquivalentTo(accepted[0]);
        replay!.PayloadJson.Should().Be(submission.PayloadJson);
        var row = await reloaded.Set<ExecutionAdmissionRecord>().SingleAsync();
        row.ProtectedPayloadJson.Should().NotContain("synthetic-protected-value").And.NotContain("pinned-revision");
    }

    [Fact]
    public async Task The_same_key_cannot_admit_another_principal_target_payload_or_attempt()
    {
        var identity = Identity();
        var original = new ExecutionAdmissionSubmission(identity, ExecutionAdmissionKind.Job, "{\"target\":\"original\"}");
        await using (var writer = new OrchestratorDbContext(_options))
            await new ExecutionAdmissionStore(writer, _keys, TimeProvider.System)
                .ReserveAsync(original, () => "123", CancellationToken.None);
        foreach (var changed in new[]
        {
            original with { Identity = identity with { TenantId = 8 } },
            original with { Identity = identity with { PrincipalId = "another@example.test" } },
            original with { Identity = identity with { NodeAttempt = 2 } },
            original with { PayloadJson = "{\"target\":\"different\"}" }
        })
        {
            await using var db = new OrchestratorDbContext(_options);
            var act = () => new ExecutionAdmissionStore(db, _keys, TimeProvider.System)
                .ReserveAsync(changed, () => "456", CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already bound*");
        }
        await using var read = new OrchestratorDbContext(_options);
        (await read.Set<ExecutionAdmissionRecord>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Authority_denial_evidence_commits_or_rolls_back_with_the_canonical_owner_transaction()
    {
        var submission = new ExecutionAdmissionSubmission(Identity(), ExecutionAdmissionKind.Job, "{}");
        await using (var reserve = new OrchestratorDbContext(_options))
            await new ExecutionAdmissionStore(reserve, _keys, TimeProvider.System)
                .ReserveAsync(submission, () => "314", CancellationToken.None);
        await using (var denied = new OrchestratorDbContext(_options))
        {
            await using var transaction = await denied.Database.BeginTransactionAsync();
            await new ExecutionAdmissionStore(denied, _keys, TimeProvider.System)
                .RecordAuthorityDeniedAsync(ExecutionAdmissionKind.Job, "314", CancellationToken.None);
            await transaction.RollbackAsync();
        }
        await using (var unchanged = new OrchestratorDbContext(_options))
            (await unchanged.Set<ExecutionAdmissionRecord>().SingleAsync()).AuthorityDeniedAtUtc.Should().BeNull();
        await using (var denied = new OrchestratorDbContext(_options))
        {
            await using var transaction = await denied.Database.BeginTransactionAsync();
            await new ExecutionAdmissionStore(denied, _keys, TimeProvider.System)
                .RecordAuthorityDeniedAsync(ExecutionAdmissionKind.Job, "314", CancellationToken.None);
            await transaction.CommitAsync();
        }
        await using var committed = new OrchestratorDbContext(_options);
        (await committed.Set<ExecutionAdmissionRecord>().SingleAsync()).AuthorityDeniedAtUtc.Should().NotBeNull();
    }


    [Fact]
    public async Task Concurrent_different_rerun_nodes_share_one_default_budget_and_retries_keep_the_accepted_id()
    {
        var first = Identity() with { Causation = new("original-occurrence", RecoveryAttempt: 1, SourceExecutionId: "original-execution") };
        var second = first with { ActionKey = Guid.NewGuid(), NodeId = Guid.NewGuid() };
        await using (var seed = new OrchestratorDbContext(_options))
        {
            var flowId = Guid.NewGuid();
            var graph = new FlowGraphDto(2, [
                new(first.NodeId, FlowNodeKind.RerunOriginalExecution, "First linked attempt", new(0, 0)) { Rerun = new() },
                new(second.NodeId, FlowNodeKind.RerunOriginalExecution, "Another branch", new(300, 0)) { Rerun = new() }
            ], [], new());
            seed.FlowDefinitions.Add(new() { Id = flowId, TenantId = first.TenantId, Name = "synthetic recovery" });
            seed.FlowVersions.Add(new() { Id = first.FlowVersionId, FlowId = flowId, TenantId = first.TenantId,
                VersionNumber = 1, GraphJson = JsonSerializer.Serialize(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ConfigurationHash = "synthetic", PublishedBy = first.PrincipalId, PublishedAtUtc = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }
        var allocations = 0;
        async Task<ExecutionAdmissionBinding?> Admit(ExecutionAdmissionIdentity identity)
        {
            await using var db = new OrchestratorDbContext(_options);
            try
            {
                return await new ExecutionAdmissionStore(db, _keys, TimeProvider.System)
                    .ReserveAsync(new(identity, ExecutionAdmissionKind.Job, "{}"),
                        () => (100 + Interlocked.Increment(ref allocations)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        CancellationToken.None);
            }
            catch (ExecutionAdmissionBudgetExhaustedException) { return null; }
        }
        var attempts = await Task.WhenAll(Admit(first), Admit(second));
        var accepted = attempts.Where(binding => binding is not null).Should().ContainSingle().Which!;
        allocations.Should().Be(1);
        var retry = await Admit(accepted.Identity);
        retry!.ExecutionId.Should().Be(accepted.ExecutionId);
        (await Admit(accepted.Identity with { ActionKey = Guid.NewGuid(), NodeAttempt = 2 })).Should().BeNull();
        await using var verify = new OrchestratorDbContext(_options);
        (await verify.Set<ExecutionAdmissionRecord>().CountAsync()).Should().Be(1);
    }

    private static ExecutionAdmissionIdentity Identity()
        => new(Guid.NewGuid(), 7, "operator@example.test", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
}
