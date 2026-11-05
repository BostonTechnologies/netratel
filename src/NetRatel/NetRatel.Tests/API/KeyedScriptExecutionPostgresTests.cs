using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Flows;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class KeyedScriptExecutionPostgresTests(PostgreSqlPersistenceFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<OrchestratorDbContext> _options = null!;
    private readonly IDataProtectionProvider _keys = new EphemeralDataProtectionProvider();
    private readonly Access _access = new();
    private readonly SourceAdmission _source = new();
    private readonly Commands _commands = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync()).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_lost_flow_receipt_recovers_the_same_canonical_script_and_real_terminal_result_without_resending()
    {
        var request = await SeedAsync();
        KeyedScriptAdmissionResult accepted;
        await using (var first = new OrchestratorDbContext(_options))
            accepted = await Service(first).StartKeyedAsync(request);
        accepted.Activity!.Status.Should().Be("Pending");
        accepted.Binding.DispatchAcceptedAtUtc.Should().NotBeNull();
        await using (var edit = new OrchestratorDbContext(_options))
        {
            await new ScriptService(edit).UpdateAsync(new(request.Snapshot.Script.Id, null, null, null,
                "printf changed-source", null, null, request.Snapshot.Script.SourceRevision));
        }
        await using (var recovered = new OrchestratorDbContext(_options))
        {
            var replay = await Service(recovered).StartKeyedAsync(request);
            replay.Binding.ExecutionId.Should().Be(accepted.Binding.ExecutionId);
            replay.Activity!.Status.Should().Be("Pending");
            var changed = () => Service(recovered).StartKeyedAsync(request with { TimeoutSeconds = 120 });
            await changed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*semantic*");
        }
        _commands.Dispatches.Should().Be(1);
        _commands.Payload.Should().Contain(request.Snapshot.Script.Content).And.NotContain("changed-source");
        _source.Checks.Should().BeGreaterThanOrEqualTo(2);
        await using (var terminal = new OrchestratorDbContext(_options))
            await new JobRunService(terminal, _keys).UpdateTaskActivityStatusAsync(new(accepted.Binding.ExecutionId,
                "Completed", null, DateTimeOffset.UtcNow, ResultJson: """{"stdout":"synthetic result","exitCode":0}""", ExitCode: 0));
        await using var read = new OrchestratorDbContext(_options);
        var actual = await Service(read).StartKeyedAsync(request);
        actual.Activity!.Status.Should().Be("Completed");
        actual.Activity.ExitCode.Should().Be(0);
        actual.Activity.ResultJson.Should().Contain("synthetic result");
        _commands.Dispatches.Should().Be(1);
        (await read.Set<ExecutionAdmissionRecord>().SingleAsync()).ProtectedPayloadJson
            .Should().NotContain(request.Snapshot.Script.Content);
    }

    [Fact]
    public async Task An_ambiguous_command_send_retains_the_request_identity_and_never_blindly_resends()
    {
        var request = await SeedAsync();
        _commands.ThrowAfterSend = true;
        KeyedScriptAdmissionResult first;
        await using (var db = new OrchestratorDbContext(_options)) first = await Service(db).StartKeyedAsync(request);
        first.DispatchUncertain.Should().BeTrue();
        await using var restarted = new OrchestratorDbContext(_options);
        var recovered = await Service(restarted).StartKeyedAsync(request);
        recovered.Binding.ExecutionId.Should().Be(first.Binding.ExecutionId);
        recovered.Binding.DispatchStartedAtUtc.Should().NotBeNull();
        recovered.Binding.DispatchAcceptedAtUtc.Should().BeNull();
        recovered.DispatchUncertain.Should().BeTrue();
        _commands.Dispatches.Should().Be(1);
        (await restarted.JobTaskActivities.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Revocation_after_claim_commits_typed_denial_and_never_exposes_a_command()
    {
        var request = await SeedAsync();
        await using var db = new OrchestratorDbContext(_options);
        var store = new AfterClaim(new ExecutionAdmissionStore(db, _keys, TimeProvider.System), () => _access.Allowed = false);
        var result = await Service(db, store).StartKeyedAsync(request);
        result.AuthorityDenied.Should().BeTrue();
        result.Activity!.Status.Should().Be("Failed");
        _commands.Dispatches.Should().Be(0);
        db.ChangeTracker.Clear();
        (await db.Set<ExecutionAdmissionRecord>().SingleAsync()).AuthorityDeniedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task A_stale_flow_lease_cannot_create_or_dispatch_a_script_activity()
    {
        var request = await SeedAsync();
        await using var db = new OrchestratorDbContext(_options);
        var stale = () => Service(db).StartKeyedAsync(request with
        { FlowLease = request.FlowLease! with { Fence = request.FlowLease.Fence + 1 } });
        await stale.Should().ThrowAsync<ExecutionAdmissionDeniedException>().WithMessage("*reclaimed*");
        (await db.JobTaskActivities.CountAsync()).Should().Be(0);
        _commands.Dispatches.Should().Be(0);
    }


    [Fact]
    public async Task Flow_action_observes_pending_then_real_native_output_and_current_permission_revocation()
    {
        var original = await SeedAsync();
        await using var db = new OrchestratorDbContext(_options);
        var authority = new FlowExecutionAuthority(db, new Verifier(), _access, _source, TimeProvider.System);
        var native = new JobRunService(db, _keys);
        var pins = new Pins(new(original.Identity.NodeId, FlowNodeKind.RunLibraryScript,
            original.FlowLease!.Version.Graph.Nodes[0].ExecutionAction!.Definition, new(original.AgentId), Script: original.Snapshot));
        var dispatcher = new FlowExecutionActionDispatcher(db, pins, new NoJobs(), Service(db), native,
            new ExecutionAdmissionStore(db, _keys, TimeProvider.System), authority);
        var request = new FlowExecutionActionRequest(original.FlowLease, original.Identity.NodeId, original.Identity.ActionKey, 1);
        var queued = await dispatcher.DispatchAsync(request);
        queued.Disposition.Should().Be(FlowExecutionActionDisposition.Pending);
        queued.Receipt!.ExecutionId.Should().NotBeEmpty();
        _commands.Dispatches.Should().Be(1);
        await native.UpdateTaskActivityStatusAsync(new(queued.Receipt.ExecutionId, "Completed", null,
            DateTimeOffset.UtcNow, ResultJson: """{"stdout":["synthetic terminal output","second line"],"exitCode":0}""", ExitCode: 0));
        var terminal = await dispatcher.ObserveAsync(request, queued.Receipt);
        terminal.Disposition.Should().Be(FlowExecutionActionDisposition.Succeeded);
        terminal.Output!.StandardOutput.Should().Be("synthetic terminal output\nsecond line");
        terminal.Output.ExecutionId.Should().Be(queued.Receipt.ExecutionId);
        _commands.Dispatches.Should().Be(1);
        _access.Allowed = false;
        (await dispatcher.ObserveAsync(request, queued.Receipt)).Disposition.Should().Be(FlowExecutionActionDisposition.Denied);
        _commands.Dispatches.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_and_nonreplayable_original_results_never_send_a_repair_or_rerun()
    {
        var original = await SeedAsync();
        await using var db = new OrchestratorDbContext(_options);
        var node = new FlowNodeDto(original.Identity.NodeId, FlowNodeKind.RerunOriginalExecution, "Rerun original", new(0, 0)) { Rerun = new() };
        var replay = new FlowExecutionReplayDescriptorDto(FlowExecutionSourceKind.Script, new(original.Snapshot.Script.Id.ToString()),
            new(original.AgentId), "script-execution:unavailable", "unavailable", "unavailable", true);
        var source = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Script,
            replay.Definition, replay.SourceExecutionId, replay.SourceAttemptId, replay.Target, FlowExecutionOutcome.Unknown,
            "DeliveryUnknown", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Replay = replay };
        var lease = original.FlowLease! with { Version = original.FlowLease.Version with { Graph = new(2, [node], [], new()) },
            Event = original.FlowLease.Event with { ExecutionEvent = source } };
        var dispatcher = new FlowExecutionActionDispatcher(db, new Pins(null), new NoJobs(), Service(db), new JobRunService(db, _keys),
            new ExecutionAdmissionStore(db, _keys, TimeProvider.System),
            new FlowExecutionAuthority(db, new Verifier(), _access, _source, TimeProvider.System));
        var action = new FlowExecutionActionRequest(lease, node.Id, Guid.NewGuid(), 1, OriginalReplay: replay);
        (await dispatcher.DispatchAsync(action)).Disposition.Should().Be(FlowExecutionActionDisposition.Unknown);
        (await dispatcher.DispatchAsync(action with { Lease = lease with { Event = lease.Event with
        { ExecutionEvent = source with { Outcome = FlowExecutionOutcome.Failed, RawStatus = "Failed" } } },
            OriginalReplay = null })).Disposition.Should().Be(FlowExecutionActionDisposition.Unsupported);
        var hopExhausted = action with { Lease = lease with { Event = lease.Event with
            { ExecutionEvent = source with { Causation = new("bounded-original", HopCount: FlowExecutionEventLimits.MaximumHopCount) } } } };
        (await dispatcher.DispatchAsync(hopExhausted)).Disposition.Should().Be(FlowExecutionActionDisposition.Denied);
        _commands.Dispatches.Should().Be(0);
        (await db.Set<ExecutionAdmissionRecord>().CountAsync()).Should().Be(0);
    }


    [Theory]
    [InlineData("Failed", """{"timedOut":true}""", null, FlowExecutionActionDisposition.TimedOut)]
    [InlineData("Failed", """{"outcomeUnknown":true}""", null, FlowExecutionActionDisposition.Unknown)]
    [InlineData("Completed", "{}", null, FlowExecutionActionDisposition.Unknown)]
    public async Task Flow_action_classifies_committed_native_terminal_flags_without_fabricating_success(
        string rawStatus, string resultJson, int? exitCode, FlowExecutionActionDisposition expected)
    {
        var original = await SeedAsync();
        await using var db = new OrchestratorDbContext(_options);
        var native = new JobRunService(db, _keys);
        var dispatcher = new FlowExecutionActionDispatcher(db, new Pins(new(original.Identity.NodeId, FlowNodeKind.RunLibraryScript,
            original.FlowLease!.Version.Graph.Nodes[0].ExecutionAction!.Definition, new(original.AgentId), Script: original.Snapshot)),
            new NoJobs(), Service(db), native, new ExecutionAdmissionStore(db, _keys, TimeProvider.System),
            new FlowExecutionAuthority(db, new Verifier(), _access, _source, TimeProvider.System));
        var request = new FlowExecutionActionRequest(original.FlowLease, original.Identity.NodeId, original.Identity.ActionKey, 1);
        var accepted = await dispatcher.DispatchAsync(request);
        await native.UpdateTaskActivityStatusAsync(new(accepted.Receipt!.ExecutionId, rawStatus, null,
            DateTimeOffset.UtcNow, ResultJson: resultJson, ExitCode: exitCode));
        var observed = await dispatcher.ObserveAsync(request, accepted.Receipt);
        observed.Disposition.Should().Be(expected);
        _commands.Dispatches.Should().Be(1);
    }

    private sealed class Pins(FlowPinnedExecutionDefinition? value) : IFlowExecutionDefinitionPins
    {
        public Task<FlowPinnedExecutionDefinition?> ResolveAsync(int tenant, Guid version, Guid node, CancellationToken ct = default)
            => Task.FromResult(value);
        public Task<FlowExecutionPinsCapture> CaptureAsync(int tenant, FlowExecutionAuthorityDto authority, Guid version,
            FlowGraphDto graph, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class NoJobs : IAkkaJobAuthorityService
    {
        public Task<JobRunInfo> StartAsync(ulong id, NetRatel.Shared.Contracts.Jobs.RunJobRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(ulong id, string reason, CancellationToken ct) => throw new NotSupportedException();
    }


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Expiry_or_revocation_during_activity_save_rolls_back_activity_and_claim_then_a_new_lease_recovers_the_same_key(bool expireLease)
    {
        var request = await SeedAsync();
        var clock = new AdmissionClock();
        var interceptor = new AfterActivitySave(() =>
        {
            if (expireLease) clock.UtcNow = request.FlowLease!.ExpiresAtUtc.AddSeconds(1);
            else _access.Allowed = false;
        });
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>(_options).AddInterceptors(interceptor).Options;
        await using (var dispatch = new OrchestratorDbContext(options))
        {
            var attempt = () => Service(dispatch, clock: clock).StartKeyedAsync(request);
            await attempt.Should().ThrowAsync<ExecutionAdmissionDeniedException>();
        }
        string reserved;
        await using (var verify = new OrchestratorDbContext(_options))
        {
            (await verify.JobTaskActivities.CountAsync()).Should().Be(0);
            var binding = await verify.Set<ExecutionAdmissionRecord>().SingleAsync();
            binding.DispatchStartedAtUtc.Should().BeNull();
            binding.DispatchAcceptedAtUtc.Should().BeNull();
            reserved = binding.ExecutionId;
        }
        _commands.Dispatches.Should().Be(0);
        _access.Allowed = true;
        var newLease = request.FlowLease! with { Token = Guid.NewGuid(), WorkerId = Guid.NewGuid(),
            Fence = request.FlowLease.Fence + 1, ExpiresAtUtc = clock.GetUtcNow().AddMinutes(2) };
        await using (var renew = new OrchestratorDbContext(_options))
        {
            var run = await renew.FlowRuns.SingleAsync();
            run.LeaseToken = newLease.Token; run.LeaseOwner = newLease.WorkerId;
            run.Fence = newLease.Fence; run.LeaseExpiresAtUtc = newLease.ExpiresAtUtc;
            await renew.SaveChangesAsync();
        }
        await using var recovered = new OrchestratorDbContext(_options);
        var result = await Service(recovered, clock: clock).StartKeyedAsync(request with { FlowLease = newLease });
        result.Binding.ExecutionId.Should().Be(reserved);
        result.Binding.DispatchAcceptedAtUtc.Should().NotBeNull();
        _commands.Dispatches.Should().Be(1);
        (await recovered.JobTaskActivities.CountAsync()).Should().Be(1);
    }

    private sealed class AdmissionClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class AfterActivitySave(Action after) : SaveChangesInterceptor
    {
        private int _changed;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<JobTaskActivityRecord>().Any() &&
                Interlocked.Exchange(ref _changed, 1) == 0) after();
            return ValueTask.FromResult(result);
        }
    }


    [Fact]
    public async Task Original_native_script_rerun_keeps_full_source_identity_null_parameters_shell_target_and_runtime_after_edits()
    {
        var original = await SeedAsync();
        var requestId = "9007199254740997-native-" + new string('x', 180);
        var schema = new ScriptParamInfo(1, original.Snapshot.Script.Id, "optional", "string", false,
            "must-not-be-added-during-replay", null, null);
        var pin = original.Snapshot with { Parameters = [schema] };
        var policy = TaskOutputPolicy.Create([("optional", "string")], checked((long)pin.Script.Id),
            pin.Script.SourceRevision, pin.ContentHash);
        var invocation = new ExecLibraryScriptPayload
        {
            ScriptId = checked((int)pin.Script.Id), ScriptType = ScriptType.Bash, ScriptContent = pin.Script.Content,
            Preferred = ShellExecutor.Bash, Parameters = null, TimeoutSeconds = 37,
            WorkingDirectory = "/tmp/synthetic-original", OutputPolicy = policy
        };
        var replay = new FlowExecutionReplayDescriptorDto(FlowExecutionSourceKind.Script,
            new(pin.Script.Id.ToString(), pin.Script.Name, pin.Script.SourceRevision.ToString(), pin.ContentHash),
            new(original.AgentId, "Synthetic source"), "script-execution:" + requestId, requestId, requestId, true);
        var now = DateTimeOffset.UtcNow;
        var source = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Script,
            replay.Definition, requestId, requestId, replay.Target, FlowExecutionOutcome.Failed, "Failed", now, now)
            { Replay = replay, ExitCode = 1, CompletedAtUtc = now, Provenance = "script-activity-state-commit" };
        var node = new FlowNodeDto(original.Identity.NodeId, FlowNodeKind.RerunOriginalExecution, "Rerun original", new(0, 0))
            { Rerun = new() };
        var graph = new FlowGraphDto(2, [node], [], new());
        var graphJson = JsonSerializer.Serialize(graph, Json);
        var lease = original.FlowLease! with
        {
            Version = original.FlowLease.Version with { Graph = graph, ConfigurationHash = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(graphJson)) },
            Event = original.FlowLease.Event with { EventId = source.EventId, OccurrenceId = source.OccurrenceId,
                OccurredAtUtc = source.OccurredAtUtc, ExecutionEvent = source }
        };
        await using (var setup = new OrchestratorDbContext(_options))
        {
            await using (var transaction = await setup.Database.BeginTransactionAsync())
            {
                await new ScriptExecutionReplayStore(setup, _keys, TimeProvider.System).CaptureAsync(requestId,
                    new(7, original.AgentId, pin, ClientEnvironment.Dev, JsonSerializer.Serialize(invocation), policy));
                await new JobRunService(setup, _keys).CreateTaskActivityAsync(new(requestId, null, null, "", 7,
                    TaskKinds.ExecLibraryScript, "Pending", null, now, null, AgentId: original.AgentId,
                    ScriptName: pin.Script.Name, ScriptType: ScriptType.Bash.ToString(), ScriptContentHash: pin.ContentHash, OutputPolicy: policy));
                await transaction.CommitAsync();
            }
            await new JobRunService(setup, _keys).UpdateTaskActivityStatusAsync(new(requestId, "Failed", "Synthetic native failure.",
                now, ResultJson: """{"exitCode":1}""", ExitCode: 1));
            await new ScriptService(setup).UpdateAsync(new(pin.Script.Id, null, null, null,
                "printf changed-native-source", null, null, pin.Script.SourceRevision));
            var version = await setup.FlowVersions.SingleAsync();
            setup.Entry(version).Property(row => row.GraphJson).CurrentValue = graphJson;
            setup.Entry(version).Property(row => row.ConfigurationHash).CurrentValue = lease.Version.ConfigurationHash;
            var run = await setup.FlowRuns.SingleAsync();
            run.EventId = source.EventId; run.OccurrenceId = source.OccurrenceId;
            run.EventJson = JsonSerializer.Serialize(lease.Event, Json);
            run.EventFingerprint = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(run.EventJson));
            await setup.SaveChangesAsync();
        }
        var identity = original.Identity with { Causation = new(source.OccurrenceId.ToString("N"), source.EventId,
            HopCount: 1, RecoveryAttempt: 1, SourceExecutionId: requestId) };
        KeyedScriptAdmissionResult accepted;
        await using (var first = new OrchestratorDbContext(_options))
            accepted = await Service(first).StartReplayAsync(identity, lease, replay);
        accepted.Binding.ExecutionId.Should().NotBe(requestId);
        accepted.Binding.Identity.Causation!.SourceExecutionId.Should().Be(requestId);
        var actual = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(_commands.Payload!)!;
        actual.ScriptContent.Should().Be(pin.Script.Content).And.NotContain("changed-native-source");
        actual.Parameters.Should().BeNull();
        actual.Preferred.Should().Be(ShellExecutor.Bash);
        actual.TimeoutSeconds.Should().Be(37);
        actual.WorkingDirectory.Should().Be("/tmp/synthetic-original");
        actual.OutputPolicy!.Binding.Should().Be(identity.ActionKey.ToString("N"));
        await using var restarted = new OrchestratorDbContext(_options);
        var same = await Service(restarted).StartReplayAsync(identity, lease, replay);
        same.Binding.ExecutionId.Should().Be(accepted.Binding.ExecutionId);
        _commands.Dispatches.Should().Be(1);
    }

    private KeyedScriptExecutionService Service(OrchestratorDbContext db, IExecutionAdmissionStore? store = null, TimeProvider? clock = null) =>
        new(db, store ?? new ExecutionAdmissionStore(db, _keys, clock ?? TimeProvider.System), new JobRunService(db, _keys),
            new FlowExecutionAuthority(db, new Verifier(), _access, _source, clock ?? TimeProvider.System), _commands, clock ?? TimeProvider.System,
            new ScriptExecutionReplayStore(db, _keys, clock ?? TimeProvider.System));

    private async Task<KeyedScriptAdmissionRequest> SeedAsync()
    {
        await using var db = new OrchestratorDbContext(_options);
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var agent = Guid.NewGuid();
        db.Tenants.Add(new() { Id = 7, Name = "Synthetic tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
        db.Agents.Add(new() { Id = agent, TenantId = 7, Name = "synthetic-worker", CreatedAtUtc = now });
        var script = await new ScriptService(db).CreateAsync(new("synthetic-diagnostic", "/", null,
            "printf pinned-synthetic-source", "bash", null));
        var nodeId = Guid.NewGuid();
        var graph = new FlowGraphDto(2, [new(nodeId, FlowNodeKind.RunLibraryScript, "Run diagnostic", new(0, 0))
        { ExecutionAction = new(FlowExecutionSourceKind.Script, new(script.Id.ToString(), script.Name,
            script.SourceRevision.ToString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Content)))) )
            { Target = new(agent) } }], [], new());
        var flow = Guid.NewGuid(); var versionId = Guid.NewGuid(); var runId = Guid.NewGuid();
        var authority = new FlowExecutionAuthorityDto("operator");
        var version = new FlowVersionDto(versionId, flow, 7, 1, graph,
            FlowContractValidation.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(graph, Json))), "operator", now);
        var envelope = new FlowEventEnvelope(7, Guid.NewGuid(), Guid.NewGuid(), versionId, now,
            new(agent, Guid.NewGuid(), "Synthetic rule", "Synthetic worker", "resource", "metric", "Warning", 1, null, now), authority);
        var lease = new FlowRunLease(runId, Guid.NewGuid(), Guid.NewGuid(), 1, now.AddMinutes(2),
            Guid.NewGuid(), version, envelope, 1);
        db.FlowDefinitions.Add(new() { Id = flow, TenantId = 7, Name = "Synthetic flow", Enabled = true,
            DraftJson = JsonSerializer.Serialize(graph, Json) });
        db.FlowVersions.Add(new() { Id = versionId, TenantId = 7, FlowId = flow, VersionNumber = 1,
            GraphJson = JsonSerializer.Serialize(graph, Json), ConfigurationHash = version.ConfigurationHash,
            PublishedBy = "operator", PublishedAtUtc = now, ExecutionAuthorityJson = JsonSerializer.Serialize(authority, Json) });
        db.FlowRuns.Add(new() { Id = runId, TenantId = 7, FlowId = flow, FlowVersionId = versionId,
            EventId = envelope.EventId, OccurrenceId = envelope.OccurrenceId, EventJson = JsonSerializer.Serialize(envelope, Json),
            EventFingerprint = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, Json))),
            Status = FlowRunStatus.Running, Fence = 1, LeaseToken = lease.Token, LeaseOwner = lease.WorkerId,
            LeaseExpiresAtUtc = lease.ExpiresAtUtc, CreatedAtUtc = now });
        db.FlowRuntimeIdentity.Add(new() { Id = 1, SourceInstanceId = lease.SourceInstanceId });
        await db.SaveChangesAsync();
        var pin = new PinnedJobScriptSnapshot(script, [], Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Content))));
        return new(new(Guid.NewGuid(), 7, "operator", versionId, runId, nodeId, 1), pin, agent,
            new Dictionary<string, string>()) { FlowLease = lease };
    }

    private sealed class Verifier : IFlowExecutionAuthorityVerifier
    {
        public Task<bool> AuthorizeAsync(int tenant, FlowExecutionAuthorityDto authority, CancellationToken ct = default)
            => Task.FromResult(tenant == 7 && authority.PrincipalId == "operator" && authority.IntegrationCredentialId is null);
    }
    private sealed class Access : IEffectiveAccessService
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenant, CancellationToken ct = default) =>
            Task.FromResult(Allowed && tenant == 7 && principal.FindFirst("netratel_principal_id")?.Value == "operator" &&
                !principal.IsInRole("Operator") && permission == NetRatelPermissions.ScriptExecute);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class SourceAdmission : IFlowTransactionAdmission
    {
        public int Checks { get; private set; }
        public Task<FlowDispatchDecision> CanStartActionAsync(OrchestratorDbContext db, FlowRunLease lease, CancellationToken ct = default)
        { Checks++; return Task.FromResult(new FlowDispatchDecision(db.Database.CurrentTransaction is not null, "synthetic-source-admitted")); }
    }
    private sealed class Commands : IAgentCommandAuthorityDispatcher
    {
        public int Dispatches { get; private set; }
        public string Payload { get; private set; } = "";
        public bool ThrowAfterSend { get; set; }
        public bool IsAvailable(ClientKey client) => true;
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payload, int environment, CancellationToken ct)
        {
            Dispatches++; Payload = payload;
            if (ThrowAfterSend) throw new IOException("synthetic ambiguous transport");
            return Task.CompletedTask;
        }
    }
    private sealed class AfterClaim(IExecutionAdmissionStore inner, Action after) : IExecutionAdmissionStore
    {
        public Task<ExecutionAdmissionBinding> ReserveAsync(ExecutionAdmissionSubmission submission, Func<string> allocate, CancellationToken ct) => inner.ReserveAsync(submission, allocate, ct);
        public Task<ExecutionAdmissionBinding?> FindByExecutionAsync(ExecutionAdmissionKind kind, string id, CancellationToken ct) => inner.FindByExecutionAsync(kind, id, ct);
        public Task RecordAuthorityDeniedAsync(ExecutionAdmissionKind kind, string id, CancellationToken ct) => inner.RecordAuthorityDeniedAsync(kind, id, ct);
        public async Task<bool> TryBeginDispatchAsync(ExecutionAdmissionBinding binding, Func<CancellationToken, Task> prepare,
            Func<CancellationToken, Task> validate, CancellationToken ct)
        { var claimed = await inner.TryBeginDispatchAsync(binding, prepare, validate, ct); if (claimed) after(); return claimed; }
        public Task RecordDispatchAcceptedAsync(ExecutionAdmissionBinding binding, CancellationToken ct) => inner.RecordDispatchAcceptedAsync(binding, ct);
    }
}
