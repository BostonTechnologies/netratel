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
        var first = Identity() with { Causation = new("original-occurrence", RecoveryAttempt: 1, SourceExecutionId: "original-execution")
            { RootExecutionId = "9007199254740997-original-root" } };
        var second = first with { ActionKey = Guid.NewGuid(), NodeId = Guid.NewGuid() };
        await using (var seed = new OrchestratorDbContext(_options))
        {
            var flowId = Guid.NewGuid();
            var graph = new FlowGraphDto(2, [
                new(first.NodeId, FlowNodeKind.RerunOriginalExecution, "First linked attempt", new(0, 0)) { Rerun = new() },
                new(second.NodeId, FlowNodeKind.RerunOriginalExecution, "Another branch", new(300, 0)) { Rerun = new() }
            ], [], new());
            seed.FlowDefinitions.Add(new() { Id = flowId, TenantId = first.TenantId, Name = "synthetic recovery", DraftJson = FlowGraphSerialization.Serialize(graph) });
            seed.FlowVersions.Add(new() { Id = first.FlowVersionId, FlowId = flowId, TenantId = first.TenantId,
                VersionNumber = 1, GraphJson = FlowGraphSerialization.Serialize(graph),
                ConfigurationHash = FlowGraphSerialization.Hash(graph), PublishedBy = first.PrincipalId, PublishedAtUtc = DateTimeOffset.UtcNow });
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
        retry.Identity.Causation!.RootExecutionId.Should().Be("9007199254740997-original-root");
        await using (var conflict = new OrchestratorDbContext(_options))
        {
            var changedRoot = accepted.Identity with { Causation = accepted.Identity.Causation! with { RootExecutionId = "another-root" } };
            var changed = () => new ExecutionAdmissionStore(conflict, _keys, TimeProvider.System)
                .ReserveAsync(new(changedRoot, ExecutionAdmissionKind.Job, "{}"), () => "must-not-allocate", CancellationToken.None);
            await changed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already bound*");
        }
        (await Admit(accepted.Identity with { ActionKey = Guid.NewGuid(), NodeAttempt = 2 })).Should().BeNull();
        await using var verify = new OrchestratorDbContext(_options);
        (await verify.Set<ExecutionAdmissionRecord>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_dispatch_claim_and_its_canonical_activity_prepare_commit_atomically_and_are_never_replayed()
    {
        var submission = new ExecutionAdmissionSubmission(Identity(), ExecutionAdmissionKind.LibraryScript, "{}");
        ExecutionAdmissionBinding binding;
        await using (var reserve = new OrchestratorDbContext(_options))
            binding = await new ExecutionAdmissionStore(reserve, _keys, TimeProvider.System)
                .ReserveAsync(submission, () => "synthetic-script-request", CancellationToken.None);
        await using (var crashed = new OrchestratorDbContext(_options))
        {
            var store = new ExecutionAdmissionStore(crashed, _keys, TimeProvider.System);
            var prepare = () => store.TryBeginDispatchAsync(binding, async ct =>
            {
                await new JobRunService(crashed, _keys).CreateTaskActivityAsync(new(binding.ExecutionId,
                    null, null, "", 7, "os-info", "Pending", null, DateTimeOffset.UtcNow, null), ct);
                throw new IOException("synthetic crash before commit");
            }, _ => Task.CompletedTask, CancellationToken.None);
            await prepare.Should().ThrowAsync<IOException>();
        }
        await using (var read = new OrchestratorDbContext(_options))
        {
            (await read.JobTaskActivities.CountAsync()).Should().Be(0);
            (await read.Set<ExecutionAdmissionRecord>().SingleAsync()).DispatchStartedAtUtc.Should().BeNull();
        }
        await using (var accepted = new OrchestratorDbContext(_options))
        {
            var store = new ExecutionAdmissionStore(accepted, _keys, TimeProvider.System);
            (await store.TryBeginDispatchAsync(binding, async ct =>
            {
                await new JobRunService(accepted, _keys).CreateTaskActivityAsync(new(binding.ExecutionId,
                    null, null, "", 7, "os-info", "Pending", null, DateTimeOffset.UtcNow, null), ct);
            }, _ => Task.CompletedTask, CancellationToken.None))
                .Should().BeTrue();
        }
        await using var restarted = new OrchestratorDbContext(_options);
        var replayed = false;
        var reloaded = new ExecutionAdmissionStore(restarted, _keys, TimeProvider.System);
        (await reloaded.TryBeginDispatchAsync(binding, _ => { replayed = true; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None))
            .Should().BeFalse();
        replayed.Should().BeFalse();
        (await restarted.JobTaskActivities.CountAsync()).Should().Be(1);
        await reloaded.RecordDispatchAcceptedAsync(binding, CancellationToken.None);
        (await reloaded.FindByExecutionAsync(binding.Kind, binding.ExecutionId, CancellationToken.None))!
            .DispatchAcceptedAtUtc.Should().NotBeNull();
    }

    private static ExecutionAdmissionIdentity Identity()
        => new(Guid.NewGuid(), 7, "operator@example.test", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
}
