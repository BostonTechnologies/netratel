using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowNativeScriptReplaySourceTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("tenant", false)]
    [InlineData("target", false)]
    [InlineData("definition", false)]
    [InlineData("revision", false)]
    [InlineData("hash", false)]
    [InlineData("request", false)]
    [InlineData("protected", false)]
    [InlineData("payload-hash", false)]
    public async Task Replay_is_advertised_only_for_the_exact_protected_native_script_identity(string mismatch, bool supported)
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var requestId = new string('原', 256);
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var pin = new ScriptExecutionReplayRecord
        {
            RequestId = requestId, TenantId = 7, AgentId = agent, ScriptId = 91, ScriptRevision = 4,
            ScriptName = "Synthetic pinned library definition", ScriptContentHash = new string('A', 64),
            PayloadHash = new string('B', 64), ProtectedSnapshotJson = "Synthetic protected complete payload", CreatedAtUtc = now
        };
        switch (mismatch)
        {
            case "tenant": pin.TenantId = 8; break;
            case "target": pin.AgentId = Guid.NewGuid(); break;
            case "definition": pin.ScriptId = 92; break;
            case "revision": pin.ScriptRevision = 5; break;
            case "hash": pin.ScriptContentHash = new string('C', 64); break;
            case "request": pin.RequestId = "other-native-request"; break;
            case "protected": pin.ProtectedSnapshotJson = ""; break;
            case "payload-hash": pin.PayloadHash = "unverified"; break;
        }
        db.Set<ScriptExecutionReplayRecord>().Add(pin);
        await db.SaveChangesAsync();
        db.JobTaskActivities.Add(new()
        {
            RequestId = requestId, TenantId = 7, AgentId = agent, TaskType = TaskKinds.ExecLibraryScript,
            Status = "Completed", ExitCode = 7, ResultJson = "{\"exitCode\":7}", CreatedAtUtc = now, CompletedAtUtc = now,
            ScriptName = "Native activity presentation",
            OutputProtectionJson = JsonSerializer.Serialize(TaskOutputPolicy.Create([], 91, 4, new string('A', 64)))
        });
        await db.SaveChangesAsync();
        var row = await db.OutboxMessages.AsNoTracking().SingleAsync(message => message.Type == FlowExecutionEventLimits.OutboxType);
        var source = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        Assert.Equal(FlowExecutionSourceKind.Script, source.SourceKind);
        Assert.Equal(FlowExecutionOutcome.Failed, source.Outcome);
        Assert.Equal(requestId, source.ExecutionId);
        Assert.Equal(supported, source.Replay!.Supported);
        Assert.Null(source.ParentExecutionId);
        Assert.True(FlowExecutionEventValidator.Validate(source).Valid);
        if (supported)
        {
            Assert.Equal(pin.ScriptName, source.Definition.Name);
            Assert.Equal(source.Definition, source.Replay.Definition);
            Assert.Equal(FlowExecutionSourceKind.Script, source.Replay.SourceKind);
            Assert.Equal(requestId, source.Replay.SourceExecutionId);
            Assert.Equal(requestId, source.Replay.SourceAttemptId);
            Assert.Equal("script-execution:" + requestId, source.Replay.InputsReference);
            Assert.DoesNotContain(pin.ProtectedSnapshotJson, row.PayloadJson);
        }
    }
}
