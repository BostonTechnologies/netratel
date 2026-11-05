using System.Security.Claims;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetRatel.Application.Jobs;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowExecutionPinsPostgresTests(PostgreSqlPersistenceFixture postgres) : IAsyncLifetime
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
    public async Task Published_script_sources_remain_protected_and_immutable_after_edits_and_tenant_switch()
    {
        await using var db = new OrchestratorDbContext(_options);
        var scripts = new ScriptService(db);
        const string content = "printf synthetic-pinned-source";
        var script = await scripts.CreateAsync(new("diagnostic", "/", "fixture", content, "bash", null));
        var nodeId = Guid.NewGuid();
        var node = new FlowNodeDto(nodeId, FlowNodeKind.RunLibraryScript, "Diagnostic", new(0, 0))
        {
            ExecutionAction = new(FlowExecutionSourceKind.Script,
                new(script.Id.ToString(), script.Name, script.SourceRevision.ToString(),
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))))
        };
        var graph = new FlowGraphDto(2, [node], [], new());
        var versionId = Guid.NewGuid();
        var pins = Service(db);
        var capture = await pins.CaptureAsync(7, new("operator"), versionId, graph);
        capture.ProtectedPinsJson.Should().NotContain(content);
        var flowId = Guid.NewGuid();
        db.FlowDefinitions.Add(new() { Id = flowId, TenantId = 7, Name = "synthetic", DraftJson = JsonSerializer.Serialize(capture.Graph) });
        db.FlowVersions.Add(new()
        {
            Id = versionId, FlowId = flowId, TenantId = 7, VersionNumber = 1, GraphJson = JsonSerializer.Serialize(capture.Graph),
            ConfigurationHash = "synthetic", PublishedBy = "operator", PublishedAtUtc = DateTimeOffset.UtcNow,
            ProtectedExecutionPinsJson = capture.ProtectedPinsJson
        });
        await db.SaveChangesAsync();
        await scripts.UpdateAsync(new(script.Id, null, null, null, "printf edited-source", null, null, script.SourceRevision));
        db.ChangeTracker.Clear();

        var pinned = await Service(db).ResolveAsync(7, versionId, nodeId);
        pinned!.Script!.Script.Content.Should().Be(content);
        pinned.Script.Script.SourceRevision.Should().Be(script.SourceRevision);
        (await Service(db).ResolveAsync(8, versionId, nodeId)).Should().BeNull();
        var changed = () => Service(db).CaptureAsync(7, new("operator"), Guid.NewGuid(), graph);
        await changed.Should().ThrowAsync<FlowExecutionPinException>().WithMessage("execution-definition-revision-conflict");
    }

    [Fact]
    public async Task A_job_pin_retains_its_canonical_target_and_referenced_library_revision()
    {
        await using var db = new OrchestratorDbContext(_options);
        var now = DateTimeOffset.UtcNow;
        var target = Guid.NewGuid();
        db.Agents.Add(new() { Id = target, TenantId = 7, Name = "synthetic-host", FriendlyName = "Synthetic worker", CreatedAtUtc = now });
        await db.SaveChangesAsync();
        var script = await new ScriptService(db).CreateAsync(new("repair", "/", "fixture", "printf repair", "bash", null));
        var jobs = new JobDefinitionService(db);
        var job = await jobs.CreateAsync(new("bound-job", "/", null, 7, string.Empty, AgentId: target));
        await jobs.AddStepAsync(new(job.Id, 1, NetRatel.Application.Jobs.JobStepKind.LibraryScript, null, null, script.Id, null, true));
        var node = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunJob, "Job", new(0, 0))
        {
            ExecutionAction = new(FlowExecutionSourceKind.Job, new(job.Id.ToString(), job.Name))
            { Target = new(target) }
        };
        var capture = await Service(db).CaptureAsync(7, new("operator"), Guid.NewGuid(), new(2, [node], [], new()));
        capture.Graph.Nodes[0].ExecutionAction!.Target!.AgentId.Should().Be(target);
        capture.Graph.Nodes[0].ExecutionAction!.Definition.ContentHash.Should().HaveLength(64);
        var wrongTarget = node with { ExecutionAction = node.ExecutionAction! with { Target = new(Guid.NewGuid()) } };
        var changed = () => Service(db).CaptureAsync(7, new("operator"), Guid.NewGuid(), new(2, [wrongTarget], [], new()));
        await changed.Should().ThrowAsync<FlowExecutionPinException>().WithMessage("job-bound-target-conflict");
    }


    [Theory]
    [InlineData("scripts")]
    [InlineData("mixed")]
    [InlineData("job-aliases")]
    public async Task Concurrent_publications_lock_the_complete_source_set_in_canonical_numeric_order(string shape)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        ulong firstScript; ulong secondScript;
        var target = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            var now = DateTimeOffset.UtcNow;
            seed.Agents.Add(new() { Id = target, TenantId = 7, Name = "synthetic-lock-worker", CreatedAtUtc = now });
            var scripts = new ScriptService(seed);
            firstScript = (await scripts.CreateAsync(new("first", "/", null, "printf first", "bash", null), ct)).Id;
            secondScript = (await scripts.CreateAsync(new("second", "/", null, "printf second", "bash", null), ct)).Id;
            seed.Jobs.AddRange(
                new() { Id = 2, Name = "job two", TenantId = 7, AgentId = target, CreatedAtUtc = now, UpdatedAtUtc = now },
                new() { Id = 10, Name = "job ten", TenantId = 7, AgentId = target, CreatedAtUtc = now, UpdatedAtUtc = now });
            seed.JobSteps.AddRange(
                new() { Id = 42, JobId = 2, Ordinal = 1, Type = (int)JobStepKind.LibraryScript, ScriptId = checked((long)secondScript), Enabled = true },
                new() { Id = 43, JobId = 10, Ordinal = 1, Type = (int)JobStepKind.LibraryScript, ScriptId = checked((long)firstScript), Enabled = true });
            await seed.SaveChangesAsync(ct);
        }
        var firstNode = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var secondNode = Guid.Parse("00000002-0000-0000-0000-000000000000");
        FlowNodeDto Script(Guid node, ulong id) => new(node, FlowNodeKind.RunLibraryScript, "Pinned script", new(0, 0))
        { ExecutionAction = new(FlowExecutionSourceKind.Script, new(id.ToString())) };
        FlowNodeDto Job(Guid node, string id) => new(node, FlowNodeKind.RunJob, "Pinned job", new(300, 0))
        { ExecutionAction = new(FlowExecutionSourceKind.Job, new(id)) { Target = new(target) } };
        FlowNodeDto[] left = shape switch
        {
            "mixed" => [Script(firstNode, firstScript), Job(secondNode, "2")],
            "job-aliases" => [Job(firstNode, "02"), Job(secondNode, "10")],
            _ => [Script(firstNode, firstScript), Script(secondNode, secondScript)]
        };
        FlowNodeDto[] right = shape switch
        {
            "mixed" => [Script(firstNode, secondScript), Job(secondNode, "10")],
            "job-aliases" => [Job(firstNode, "2"), Job(secondNode, "010")],
            _ => [Script(firstNode, secondScript), Script(secondNode, firstScript)]
        };
        var barrier = new FirstFenceBarrier(shape == "job-aliases" ? "Jobs" : "Scripts",
            shape == "job-aliases" ? 2 : checked((long)Math.Min(firstScript, secondScript)));
        async Task<FlowExecutionPinsCapture> Publish(FlowNodeDto[] nodes)
        {
            var options = new DbContextOptionsBuilder<OrchestratorDbContext>(_options).AddInterceptors(barrier.Participant()).Options;
            await using var db = new OrchestratorDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var versionId = Guid.NewGuid();
            var capture = await Service(db).CaptureAsync(7, new("operator"), versionId, new(2, nodes, [], new()), ct);
            var flowId = Guid.NewGuid();
            db.FlowDefinitions.Add(new() { Id = flowId, TenantId = 7, Name = "Concurrent synthetic publication", DraftJson = JsonSerializer.Serialize(capture.Graph) });
            db.FlowVersions.Add(new() { Id = versionId, FlowId = flowId, TenantId = 7, VersionNumber = 1,
                GraphJson = JsonSerializer.Serialize(capture.Graph), ProtectedExecutionPinsJson = capture.ProtectedPinsJson,
                ConfigurationHash = "synthetic", PublishedBy = "operator", PublishedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return capture;
        }
        var published = await Task.WhenAll(Publish(left), Publish(right));
        published.Should().OnlyContain(capture => capture.ProtectedPinsJson != null);
        published.SelectMany(capture => capture.Graph.Nodes).Where(node => node.Kind == FlowNodeKind.RunJob)
            .Should().OnlyContain(node => node.ExecutionAction!.Definition.Id == "2" || node.ExecutionAction.Definition.Id == "10");
        await using var verify = new OrchestratorDbContext(_options);
        (await verify.FlowVersions.CountAsync(ct)).Should().Be(2);
    }

    private sealed class FirstFenceBarrier(string table, long expected)
    {
        private readonly string _table = table;
        private readonly object _gate = new();
        private readonly List<long> _first = [];
        private readonly TaskCompletionSource<long[]> _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DbCommandInterceptor Participant() => new ParticipantInterceptor(this);
        private async Task EnterAsync(long id, CancellationToken ct)
        {
            lock (_gate)
            {
                _first.Add(id);
                if (_first.Count == 2) _both.TrySetResult(_first.ToArray());
            }
            var firstIds = await _both.Task.WaitAsync(ct);
            firstIds.Should().OnlyContain(value => value == expected,
                "opposite graph/GUID order and numeric aliases must not reverse source fence order");
        }
        private sealed class ParticipantInterceptor(FirstFenceBarrier owner) : DbCommandInterceptor
        {
            private int _entered;
            public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (command.CommandText.Contains("UPDATE \"" + owner._table + "\"", StringComparison.Ordinal) &&
                    Interlocked.Exchange(ref _entered, 1) == 0)
                    await owner.EnterAsync(Convert.ToInt64(command.Parameters[0].Value, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
                return result;
            }
        }
    }

    private FlowExecutionDefinitionPins Service(OrchestratorDbContext db)
    {
        var scripts = new ScriptService(db);
        return new(db, new PinnedJobExecutionSnapshotCapture(db, new JobDefinitionService(db), scripts),
            scripts, new Authority(), new Access(), _keys);
    }

    private sealed class Authority : IFlowExecutionAuthorityVerifier
    {
        public Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken ct = default)
            => Task.FromResult(tenantId == 7 && authority.PrincipalId == "operator");
    }
    private sealed class Access : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken ct = default)
            => Task.FromResult(tenantId == 7);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
