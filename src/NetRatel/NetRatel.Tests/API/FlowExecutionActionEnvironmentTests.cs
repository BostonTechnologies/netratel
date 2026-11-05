using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using NetRatel.API.Services.Flows;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class FlowExecutionActionEnvironmentTests
{
    // This is the adapter boundary only. Native owner, source admission, transport and
    // terminal verification are exercised by the owning PostgreSQL regressions.
    [Theory]
    [InlineData(ClientEnvironment.None, null, ClientEnvironment.Prod)]
    [InlineData(ClientEnvironment.Dev, ClientEnvironment.Dev, ClientEnvironment.Dev)]
    [InlineData(ClientEnvironment.Prod, ClientEnvironment.Prod, ClientEnvironment.Prod)]
    [InlineData(ClientEnvironment.Both, ClientEnvironment.Both, ClientEnvironment.Both)]
    public async Task Run_job_inherits_the_pinned_environment_only_when_no_override_is_selected(
        ClientEnvironment selected, ClientEnvironment? expectedRequest, ClientEnvironment effective)
    {
        var now = DateTimeOffset.UtcNow;
        var boundAgent = Guid.NewGuid();
        var definition = new JobDefinitionInfo(41, "Synthetic pinned job", "/", null, 7, "synthetic-target", now, now,
            """{"executionEnvironment":2}""", boundAgent);
        var snapshot = new PinnedJobExecutionSnapshot(new(definition, [], []), []);
        var reference = new FlowExecutionDefinitionDto("41", definition.Name, snapshot.ComputeDigest(), snapshot.ComputeDigest());
        var node = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunJob, "Run pinned job", new(0, 0))
        { ExecutionAction = new(FlowExecutionSourceKind.Job, reference)
            { Environment = selected, Target = new(Guid.NewGuid()) } };
        var pin = new FlowPinnedExecutionDefinition(node.Id, node.Kind, reference, new(boundAgent), Job: snapshot);
        var jobs = new CaptureJobs();
        var dispatcher = new FlowExecutionActionDispatcher(null!, new Pins(pin), jobs, null!, null!, null!, null!);

        await Assert.ThrowsAsync<CanonicalAdmissionReached>(() => dispatcher.DispatchAsync(Request(node)));

        jobs.Captured.Should().NotBeNull();
        jobs.Captured!.Snapshot.Should().BeSameAs(snapshot);
        jobs.Captured.Snapshot.Definition.Job.AgentId.Should().Be(boundAgent);
        jobs.Captured.Request.Environment.Should().Be(expectedRequest);
        (jobs.Captured.Request.Environment ?? JobExecutionContextPolicy.FromJob(jobs.Captured.Snapshot.Definition.Job))
            .Should().Be(effective);
    }

    [Theory]
    [InlineData(ClientEnvironment.None)]
    [InlineData(ClientEnvironment.Dev)]
    public async Task A_script_keeps_its_explicit_native_environment_and_pinned_target(ClientEnvironment selected)
    {
        var now = DateTimeOffset.UtcNow;
        var target = Guid.NewGuid();
        var script = new ScriptInfo(10, "Synthetic pinned script", "/", null, "printf synthetic", null, "bash", now, now, 1);
        var snapshot = new PinnedJobScriptSnapshot(script, [],
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Content))));
        var reference = new FlowExecutionDefinitionDto("10", script.Name, "1", snapshot.ContentHash);
        var node = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunLibraryScript, "Run pinned script", new(0, 0))
        { ExecutionAction = new(FlowExecutionSourceKind.Script, reference) { Environment = selected } };
        var pin = new FlowPinnedExecutionDefinition(node.Id, node.Kind, reference, new(target), Script: snapshot);
        var scripts = new CaptureScripts();
        var dispatcher = new FlowExecutionActionDispatcher(null!, new Pins(pin), null!, scripts, null!, null!, null!);

        await Assert.ThrowsAsync<CanonicalAdmissionReached>(() => dispatcher.DispatchAsync(Request(node)));

        scripts.Captured.Should().NotBeNull();
        scripts.Captured!.Environment.Should().Be(selected);
        scripts.Captured.AgentId.Should().Be(target);
        scripts.Captured.Snapshot.Should().BeSameAs(snapshot);
    }

    private static FlowExecutionActionRequest Request(FlowNodeDto node)
    {
        var now = DateTimeOffset.UtcNow;
        var version = new FlowVersionDto(Guid.NewGuid(), Guid.NewGuid(), 7, 1,
            new(2, [node], [], new()), new string('A', 64), "synthetic-operator", now);
        var input = new FlowEventEnvelope(7, Guid.NewGuid(), Guid.NewGuid(), version.Id, now, null,
            new("synthetic-operator"));
        var lease = new FlowRunLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, now.AddMinutes(1),
            Guid.NewGuid(), version, input, 1);
        return new(lease, node.Id, FlowActionKeys.CreateExecution(lease, node.Id), 1, "{}");
    }

    private sealed class CanonicalAdmissionReached : Exception;
    private sealed class Pins(FlowPinnedExecutionDefinition definition) : IFlowExecutionDefinitionPins
    {
        public Task<FlowPinnedExecutionDefinition?> ResolveAsync(int tenantId, Guid versionId, Guid nodeId, CancellationToken ct = default)
            => Task.FromResult<FlowPinnedExecutionDefinition?>(definition);
        public Task<FlowExecutionPinsCapture> CaptureAsync(int tenantId, FlowExecutionAuthorityDto authority, Guid versionId,
            FlowGraphDto graph, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected pin capture.");
    }
    private sealed class CaptureJobs : IAkkaJobAuthorityService
    {
        public KeyedJobAdmissionRequest? Captured { get; private set; }
        public Task<JobRunInfo> StartKeyedAsync(KeyedJobAdmissionRequest admission, CancellationToken ct)
        { Captured = admission; throw new CanonicalAdmissionReached(); }
        public Task<JobRunInfo> StartAsync(ulong id, RunJobRequest request, CancellationToken ct) => throw new InvalidOperationException("Unexpected unkeyed start.");
        public Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken ct) => throw new InvalidOperationException("Unexpected lifecycle write.");
        public Task<bool> CancelAsync(ulong id, string reason, CancellationToken ct) => throw new InvalidOperationException("Unexpected cancellation.");
    }
    private sealed class CaptureScripts : IKeyedScriptExecutionService
    {
        public KeyedScriptAdmissionRequest? Captured { get; private set; }
        public Task<KeyedScriptAdmissionResult> StartKeyedAsync(KeyedScriptAdmissionRequest request, CancellationToken ct = default)
        { Captured = request; throw new CanonicalAdmissionReached(); }
    }
}
