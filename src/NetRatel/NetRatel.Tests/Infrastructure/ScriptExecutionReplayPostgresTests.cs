using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ScriptExecutionReplayPostgresTests(PostgreSqlPersistenceFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<OrchestratorDbContext> _options = null!;
    private readonly IDataProtectionProvider _keys = new EphemeralDataProtectionProvider();
    public async ValueTask InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(await postgres.CreateDatabaseAsync()).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Native_replay_material_commits_with_activity_and_retains_exact_source_inputs_and_runtime_after_definition_edits()
    {
        const string requestId = "synthetic-native-script-request";
        ScriptExecutionReplaySnapshot original;
        await using (var create = new OrchestratorDbContext(_options))
        {
            original = await SnapshotAsync(create);
            await using var transaction = await create.Database.BeginTransactionAsync();
            await new ScriptExecutionReplayStore(create, _keys, TimeProvider.System).CaptureAsync(requestId, original);
            await new JobRunService(create, _keys).CreateTaskActivityAsync(new(requestId, null, null, "", 7,
                TaskKinds.ExecLibraryScript, "Pending", null, DateTimeOffset.UtcNow, null, AgentId: original.AgentId,
                ScriptName: original.Script.Script.Name, ScriptContentHash: original.Script.ContentHash, OutputPolicy: original.OutputPolicy));
            await transaction.CommitAsync();
            await new ScriptService(create).UpdateAsync(new(original.Script.Script.Id, null, null, null,
                "printf edited-source", null, null, original.Script.Script.SourceRevision));
        }
        await using var read = new OrchestratorDbContext(_options);
        var store = new ScriptExecutionReplayStore(read, _keys, TimeProvider.System);
        var exact = await store.ResolveAsync(7, requestId);
        exact!.Script.Script.Content.Should().Be("printf synthetic-original-source");
        exact.Script.Script.SourceRevision.Should().Be(original.Script.Script.SourceRevision);
        exact.NativePayloadJson.Should().Be(original.NativePayloadJson);
        (await store.ResolveAsync(8, requestId)).Should().BeNull();
        var saved = await read.Set<ScriptExecutionReplayRecord>().SingleAsync();
        saved.ProtectedSnapshotJson.Should().NotContain(original.Script.Script.Content).And.NotContain("synthetic-private-input");
        saved.PayloadHash.Should().HaveLength(64);
        saved.ScriptContentHash = new string('0', 64);
        await read.SaveChangesAsync();
        var tampered = () => store.ResolveAsync(7, requestId);
        await tampered.Should().ThrowAsync<InvalidOperationException>().WithMessage("*metadata differs*");
    }

    [Fact]
    public async Task A_failed_native_admission_rolls_back_both_activity_and_replay_material()
    {
        await using (var create = new OrchestratorDbContext(_options))
        {
            var snapshot = await SnapshotAsync(create);
            await using var transaction = await create.Database.BeginTransactionAsync();
            await new ScriptExecutionReplayStore(create, _keys, TimeProvider.System).CaptureAsync("rolled-back", snapshot);
            await new JobRunService(create, _keys).CreateTaskActivityAsync(new("rolled-back", null, null, "", 7,
                TaskKinds.ExecLibraryScript, "Pending", null, DateTimeOffset.UtcNow, null, AgentId: snapshot.AgentId,
                OutputPolicy: snapshot.OutputPolicy));
            await transaction.RollbackAsync();
        }
        await using var read = new OrchestratorDbContext(_options);
        (await read.Set<ScriptExecutionReplayRecord>().CountAsync()).Should().Be(0);
        (await read.JobTaskActivities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Native_transport_retries_reuse_identical_protected_material_and_reject_a_different_attempt_payload()
    {
        const string requestId = "synthetic-idempotent-native-script";
        ScriptExecutionReplaySnapshot original;
        string firstProtection;
        DateTimeOffset firstCapturedAt;
        await using (var admission = new OrchestratorDbContext(_options))
        {
            original = await SnapshotAsync(admission);
            await using var transaction = await admission.Database.BeginTransactionAsync();
            var store = new ScriptExecutionReplayStore(admission, _keys, TimeProvider.System);
            await store.CaptureAsync(requestId, original);
            await store.CaptureAsync(requestId, original);
            await admission.SaveChangesAsync();
            await transaction.CommitAsync();
            var row = await admission.Set<ScriptExecutionReplayRecord>().AsNoTracking().SingleAsync();
            firstProtection = row.ProtectedSnapshotJson;
            firstCapturedAt = row.CreatedAtUtc;
        }
        await using (var retry = new OrchestratorDbContext(_options))
        {
            await using var transaction = await retry.Database.BeginTransactionAsync();
            var store = new ScriptExecutionReplayStore(retry, _keys, TimeProvider.System);
            await store.CaptureAsync(requestId, original);
            var changed = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(original.NativePayloadJson)!;
            changed.Parameters!["message"] = "a-different-deliberate-attempt";
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CaptureAsync(requestId, original with { NativePayloadJson = JsonSerializer.Serialize(changed) }));
            await retry.SaveChangesAsync();
            await transaction.CommitAsync();
            var exact = await retry.Set<ScriptExecutionReplayRecord>().AsNoTracking().SingleAsync();
            exact.ProtectedSnapshotJson.Should().Be(firstProtection);
            exact.CreatedAtUtc.Should().Be(firstCapturedAt);
            (await store.ResolveAsync(original.TenantId, requestId))!.NativePayloadJson.Should().Be(original.NativePayloadJson);
        }
        await using var read = new OrchestratorDbContext(_options);
        (await read.Set<ScriptExecutionReplayRecord>().CountAsync()).Should().Be(1);
    }

    private static async Task<ScriptExecutionReplaySnapshot> SnapshotAsync(OrchestratorDbContext db)
    {
        var script = await new ScriptService(db).CreateAsync(new("Native diagnostic", "/", null,
            "printf synthetic-original-source", "bash", null));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Content)));
        var policy = TaskOutputPolicy.Create([("message", "secret")], checked((long)script.Id), script.SourceRevision, hash);
        var payload = new ExecLibraryScriptPayload
        {
            ScriptId = checked((int)script.Id), ScriptType = ScriptType.Bash, ScriptContent = script.Content,
            Parameters = new() { ["message"] = "synthetic-private-input" }, TimeoutSeconds = 37,
            WorkingDirectory = "/tmp/synthetic-original", OutputPolicy = policy
        };
        return new(7, Guid.NewGuid(), new(script, [new(1, script.Id, "message", "secret", false, null, null, null)], hash),
            ClientEnvironment.Dev, JsonSerializer.Serialize(payload), policy);
    }
}
