using Akka.Actor;
using Akka.Hosting;
using AwesomeAssertions;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.Akka.Jobs;
using NetRatel.Akka.Hosting;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class JobRunOwnedBoundaryPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("LegacyClient/Original:MixedCASE", false)]
    public async Task Owned_Agent_observations_persist_and_replay_while_preserving_the_legacy_run_identity(
        string legacyClientIdentity, bool recoverAtDeadline)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct, legacyClientIdentity);
        var run = await h.StartAsync(ct);
        Assert.Equal(legacyClientIdentity, run.ClientIdentity);
        Assert.Equal(h.AgentId, run.AgentId);
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Started, 3, null, false, ct);

        // This independent region reconstructs the already persisted run and step.
        var started = await h.Replica.GetStateAsync(run.Id, ct);
        Assert.Equal(JobRunState.Running, started.Status);
        Assert.Equal(JobStepRunState.Running, Assert.Single(started.Steps).Status);
        Assert.False(string.IsNullOrWhiteSpace(started.ClientIdentity));
        if (legacyClientIdentity.Length > 0) Assert.Equal(legacyClientIdentity, started.ClientIdentity);
        var originalDeadline = (await h.ControlAsync(run.Id, ct)).NativeDeadlineUtc;
        if (recoverAtDeadline)
        {
            await h.AdvanceWithPresenceHeartbeatsAsync(originalDeadline!.Value.AddMilliseconds(1), ct);
            await h.RecoverAsync(run.Id, true, ct);
        }
        else
        {
            await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Completed, 4,
                "{\"stdout\":[\"owned native completion\"],\"exitCode\":0}", true, ct);
        }

        var terminal = await h.DetailsAsync(run.Id, ct);
        var expectedRun = recoverAtDeadline ? JobRunState.TimedOut : JobRunState.Succeeded;
        var expectedStep = recoverAtDeadline ? JobStepRunState.Failed : JobStepRunState.Succeeded;
        Assert.Equal(expectedRun, terminal.Run.Status);
        Assert.Equal(expectedStep, Assert.Single(terminal.Steps).Status);
        Assert.Equal(legacyClientIdentity, terminal.Run.ClientIdentity);
        Assert.Equal(legacyClientIdentity, Assert.Single(terminal.Activities).ClientIdentity);
        Assert.Equal(originalDeadline, (await h.ControlAsync(run.Id, ct)).NativeDeadlineUtc);
        Assert.True(await h.TerminalReadyAsync(terminal, ct));
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
                .Jobs.AsNoTracking().SingleAsync(x => x.Id == 41, ct);
            Assert.Equal(legacyClientIdentity, job.ClientIdentity);
        }
        var observations = await h.ObservationsAsync(run.Id, ct);
        Assert.All(observations, observation => Assert.Equal(started.ClientIdentity, observation.ClientIdentity));
        Assert.Contains(observations, observation => observation.Kind == JobObservationKind.Run && observation.RunStatus == JobRunState.Pending);
        Assert.Contains(observations, observation => observation.Kind == JobObservationKind.Run && observation.RunStatus == expectedRun);
        Assert.Contains(observations, observation => observation.Kind == JobObservationKind.Step && observation.StepStatus == JobStepRunState.Running);
        Assert.Contains(observations, observation => observation.Kind == JobObservationKind.Step && observation.StepStatus == expectedStep);

        // A new region has no cached projection; replay cannot enqueue physical work.
        var replayed = await h.CreateActorRegion().GetStateAsync(run.Id, ct);
        Assert.Equal(expectedRun, replayed.Status);
        Assert.Equal(expectedStep, Assert.Single(replayed.Steps).Status);
        Assert.Equal(started.ClientIdentity, replayed.ClientIdentity);
        Assert.Equal(observations[^1].SourceEventId, replayed.LastAcceptedSourceEventId);
        Assert.Equal(1, h.Gateway.Dispatches);
        Assert.Equal(0, h.Gateway.Cancellations);
    }

    [Theory]
    [InlineData("missing-agent", false)]
    [InlineData("empty-agent", false)]
    [InlineData("missing-tenant", false)]
    [InlineData("zero-tenant", false)]
    [InlineData("negative-tenant", false)]
    [InlineData("missing-agent", true)]
    public async Task Blank_identity_recovery_rejects_invalid_targets_and_releases_ownership_for_a_repaired_retry(
        string invalidTarget, bool recordedHistory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct, string.Empty);
        ulong runId;
        if (recordedHistory)
        {
            var run = await h.StartAsync(ct);
            runId = run.Id;
            await h.LifecycleAsync(runId, JobGatewayLifecycleStatus.Started, 3, null, false, ct);
            await h.AdvanceWithPresenceHeartbeatsAsync((await h.ControlAsync(runId, ct)).NativeDeadlineUtc!.Value.AddMilliseconds(1), ct);
        }
        else
        {
            runId = (await h.SeedPendingIngressAsync(ct)).RunId;
            await h.AdvanceWithPresenceHeartbeatsAsync(h.Clock.GetUtcNow().AddSeconds(4), ct);
        }
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = await db.JobRuns.SingleAsync(x => x.Id == checked((long)runId), ct);
            // A direct persisted historical row retains whitespace; run upserts trim it.
            row.ClientIdentity = "   ";
            switch (invalidTarget)
            {
                case "missing-agent": row.AgentId = null; break;
                case "empty-agent": row.AgentId = Guid.Empty; break;
                case "missing-tenant": row.TenantId = null; break;
                case "zero-tenant": row.TenantId = 0; break;
                case "negative-tenant": row.TenantId = -1; break;
                default: throw new ArgumentOutOfRangeException(nameof(invalidTarget));
            }
            await db.SaveChangesAsync(ct);
        }
        var before = await h.DetailsAsync(runId, ct);
        var controlBefore = await h.ControlAsync(runId, ct);
        var observationsBefore = await h.ObservationsAsync(runId, ct);
        var dispatches = h.Gateway.Dispatches;
        // With no history recovery records a run first; recorded history reaches a step first.
        // ThrowsAsync requires the direct guard type, excluding derived transition rejections.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.RecoverAsync(runId, true, ct));
        var rejected = await h.DetailsAsync(runId, ct);
        Assert.Equal(before.Run, rejected.Run);
        Assert.Equal(before.Steps.ToArray(), rejected.Steps.ToArray());
        Assert.Equal(before.Activities.ToArray(), rejected.Activities.ToArray());
        var rejectedControl = await h.ControlAsync(runId, ct);
        Assert.Equal(controlBefore.Revision, rejectedControl.Revision);
        Assert.Equal(controlBefore.NativeDeadlineUtc, rejectedControl.NativeDeadlineUtc);
        Assert.Equal(controlBefore.DispatchPreparedAtUtc, rejectedControl.DispatchPreparedAtUtc);
        Assert.Equal(controlBefore.DispatchEnqueuedAtUtc, rejectedControl.DispatchEnqueuedAtUtc);
        Assert.Null(rejectedControl.TerminalReadyAtUtc);
        Assert.Null(rejectedControl.TerminalResultHash);
        var rejectedObservations = await h.ObservationsAsync(runId, ct);
        Assert.Equal(observationsBefore.Select(x => x.Id), rejectedObservations.Select(x => x.Id));
        Assert.Equal(observationsBefore.Select(x => x.ClientIdentity), rejectedObservations.Select(x => x.ClientIdentity));
        Assert.Equal(dispatches, h.Gateway.Dispatches);
        Assert.Equal(0, h.Gateway.Cancellations);

        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = await db.JobRuns.SingleAsync(x => x.Id == checked((long)runId), ct);
            row.TenantId = 7;
            row.AgentId = h.AgentId;
            // Keep the blank identity: a successful retry must derive a valid observation.
            await db.SaveChangesAsync(ct);
        }
        await h.RecoverAsync(runId, true, ct); // The same replica must reload after rollback and reacquire ownership.
        var terminal = await h.DetailsAsync(runId, ct);
        Assert.Equal(recordedHistory ? JobRunState.TimedOut : JobRunState.Failed, terminal.Run.Status);
        Assert.Equal(string.Empty, terminal.Run.ClientIdentity);
        Assert.True(await h.TerminalReadyAsync(terminal, ct));
        Assert.Equal(controlBefore.NativeDeadlineUtc, (await h.ControlAsync(runId, ct)).NativeDeadlineUtc);
        var replayed = await h.CreateActorRegion().GetStateAsync(runId, ct);
        Assert.Equal(terminal.Run.Status, replayed.Status);
        Assert.False(string.IsNullOrWhiteSpace(replayed.ClientIdentity));
        Assert.All(await h.ObservationsAsync(runId, ct), observation => Assert.Equal(replayed.ClientIdentity, observation.ClientIdentity));
        Assert.Equal(dispatches, h.Gateway.Dispatches);
        Assert.Equal(0, h.Gateway.Cancellations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Independent_actor_regions_obey_the_first_durable_cancel_or_completion(bool cancelFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var run = await h.StartAsync(ct);
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Started, 3, null, false, ct);
        h.Gate.Arm(run.Id, cancelFirst ? GateKind.CancelRequested : GateKind.TerminalRun);
        var secondEntered = h.Replica.ObserveNextOperation();
        Task first = cancelFirst ? h.CancelAsync(run.Id, false, ct) :
            h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Completed, 4, "{\"stdout\":[\"original native result\"],\"exitCode\":0}", false, ct);
        await h.Gate.Entered.WaitAsync(ct); // SQL has executed, transaction remains uncommitted.
        Task<bool>? cancelSecond = cancelFirst ? null : h.CancelAsync(run.Id, true, ct);
        Task second = cancelFirst ? h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Completed, 4,
            "{\"stdout\":[\"original native result\"],\"exitCode\":0}", true, ct) : cancelSecond!;
        await secondEntered.WaitAsync(ct); // Another actor region reached relational ownership acquisition.
        Assert.False(second.IsCompleted);
        h.Gate.Release();
        await Task.WhenAll(first, second).WaitAsync(ct);
        if (cancelSecond is not null) Assert.False(await cancelSecond);
        var details = await h.DetailsAsync(run.Id, ct);
        Assert.Equal(cancelFirst ? JobRunState.Cancelled : JobRunState.Succeeded, details.Run.Status);
        Assert.Contains("original native result", Assert.Single(details.Activities).ResultJson!);
        Assert.True(await h.TerminalReadyAsync(details, ct));
        // A late higher-sequence native receipt cannot change the accepted terminal body.
        var hash = OrchestrationCallbackProjection.ResultHash(details);
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Completed, 9, "{\"stdout\":[\"late\"]}", true, ct);
        Assert.Equal(hash, OrchestrationCallbackProjection.ResultHash(await h.DetailsAsync(run.Id, ct)));
    }

    [Fact]
    public async Task Terminal_run_results_and_callback_eligibility_become_visible_in_one_transaction()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var run = await h.StartAsync(ct);
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Started, 3, null, false, ct);
        h.Gate.Arm(run.Id, GateKind.TerminalRun);
        const string nativeResult = "{\"stderr\":[\"physical failure\"],\"exitCode\":1}";
        var failure = h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Failed, 4, nativeResult, false, ct);
        await h.Gate.Entered.WaitAsync(ct); // Pause after terminal JobRuns UPDATE, before activity ResultJson UPDATE.
        var concurrentRead = await h.DetailsAsync(run.Id, ct);
        Assert.Equal(JobRunState.Running, concurrentRead.Run.Status);
        Assert.Null(Assert.Single(concurrentRead.Activities).ResultJson);
        Assert.Null((await h.ControlAsync(run.Id, ct)).TerminalReadyAtUtc);
        h.Gate.Release();
        await failure.WaitAsync(ct);
        var complete = await h.DetailsAsync(run.Id, ct);
        Assert.Equal(JobRunState.Failed, complete.Run.Status);
        Assert.Equal(nativeResult, Assert.Single(complete.Activities).ResultJson);
        Assert.True(await h.TerminalReadyAsync(complete, ct));
        Assert.Equal(OrchestrationCallbackProjection.ResultHash(complete), (await h.ControlAsync(run.Id, ct)).TerminalResultHash);
        Assert.False(await h.TerminalReadyAsync(complete with
        { Activities = complete.Activities.Select(x => x with { ResultJson = null }).ToArray() }, ct));
    }

    [Fact]
    public async Task Restart_closes_a_committed_pending_ingress_and_exact_ACK_replay_never_dispatches()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var pending = await h.SeedPendingIngressAsync(ct);
        await h.AdvanceWithPresenceHeartbeatsAsync(h.Clock.GetUtcNow().AddSeconds(4), ct); // Existing configured three-second Ask admission budget.
        await h.RecoverAsync(pending.RunId, true, ct); // Fresh actor region, only durable database state survives.
        var details = await h.DetailsAsync(pending.RunId, ct);
        Assert.Equal(JobRunState.Failed, details.Run.Status);
        Assert.Contains("not replayed", details.Run.Error!);
        Assert.True(await h.TerminalReadyAsync(details, ct));
        for (var i = 0; i < 2; i++)
        {
            await using var scope = h.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleAsync(ct);
            var ack = await ManagedOrchestrationRecovery.RecoverAsync(db,
                scope.ServiceProvider.GetRequiredService<NetRatel.Application.Requests.IRequestService>(),
                scope.ServiceProvider.GetRequiredService<IJobRunService>(), binding, ct);
            Assert.NotNull(ack);
            Assert.Equal(pending.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture), ack.ExecutionId);
            Assert.Equal("Failed", ack.Status);
        }
        Assert.Equal(0, h.Gateway.Dispatches);
    }

    [Fact]
    public async Task Durable_cancel_before_original_managed_start_closes_recorded_intent_without_gateway_IO()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var pending = await h.SeedPendingIngressAsync(ct, authorized: true);
        var original = await h.DetailsAsync(pending.RunId, ct);
        h.Gate.Arm(pending.RunId, GateKind.CancelRequested);
        var cancel = h.CancelAsync(pending.RunId, true, ct);
        await h.Gate.Entered.WaitAsync(ct); // Cancellation owns the independent replica transaction first.
        var originalStartEntered = h.Primary.ObserveNextOperation();
        var start = h.StartManagedAsync(pending.RunId, pending.RequestId, ct);
        await originalStartEntered.WaitAsync(ct);
        Assert.False(start.IsCompleted); // Original start waits for the cancellation's PostgreSQL lock.
        h.Gate.Release();
        Assert.True(await cancel.WaitAsync(ct));
        var returned = await start.WaitAsync(ct);
        Assert.Equal(pending.RunId, returned.Id);
        Assert.Equal(JobRunState.Cancelled, returned.Status);
        var terminal = await h.DetailsAsync(pending.RunId, ct);
        Assert.Equal(original.Run.Id, terminal.Run.Id);
        Assert.Equal(original.Run.CreatedAtUtc, terminal.Run.CreatedAtUtc);
        Assert.Equal(original.Run.InputsJson, terminal.Run.InputsJson);
        Assert.Equal(original.Run.OptionsJson, terminal.Run.OptionsJson);
        Assert.Null(terminal.Run.StartedAtUtc);
        Assert.NotNull(terminal.Run.CompletedAtUtc);
        Assert.Contains("no native command was started", terminal.Run.Error!);
        Assert.Empty(terminal.Steps); // Ingress had no prepared steps or fabricated native activity.
        Assert.Empty(terminal.Activities);
        Assert.Equal(0, h.Gateway.Dispatches);
        Assert.Equal(0, h.Gateway.Cancellations);
        var control = await h.ControlAsync(pending.RunId, ct);
        Assert.NotNull(control.CancellationRequestedAtUtc);
        Assert.Equal("operator-cancelled", control.CancellationReason);
        Assert.Null(control.CancellationEnqueuedAtUtc);
        Assert.Null(control.DispatchPreparedAtUtc);
        Assert.Null(control.DispatchEnqueuedAtUtc);
        Assert.Null(control.NativeDeadlineUtc);
        Assert.True(await h.TerminalReadyAsync(terminal, ct));
        var terminalHash = OrchestrationCallbackProjection.ResultHash(terminal);
        Assert.Equal(terminalHash, control.TerminalResultHash);
        var actor = await h.Primary.GetStateAsync(pending.RunId, ct);
        Assert.Equal(JobRunState.Cancelled, actor.Status);
        Assert.Equal(2L, actor.LastAcceptedSourceEventId);
        Assert.Empty(actor.Steps);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var requests = scope.ServiceProvider.GetRequiredService<NetRatel.Application.Requests.IRequestService>();
            var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleAsync(ct);
            var request = (await requests.GetAsync(pending.RequestId, ct))!;
            Assert.True(OrchestrationCallbackProjection.Matches(request, terminal.Run, binding));
            var callback = OrchestrationCallbackProjection.Build(request, terminal, binding);
            Assert.Equal("failed", callback.Status);
            Assert.Null(callback.StartedAtUtc);
            Assert.Null(callback.ResultJson);
            Assert.Contains("Cancelled", callback.ErrorJson!);
            Assert.True(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, requests, request.Id,
                terminal.Run.Status, callback.Message, callback.ResultJson, h.Clock.GetUtcNow(), ct));
        }
        for (var i = 0; i < 2; i++)
        {
            await using var scope = h.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleAsync(ct);
            var ack = await ManagedOrchestrationRecovery.RecoverAsync(db,
                scope.ServiceProvider.GetRequiredService<NetRatel.Application.Requests.IRequestService>(),
                scope.ServiceProvider.GetRequiredService<IJobRunService>(), binding, ct);
            Assert.NotNull(ack);
            Assert.Equal(pending.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture), ack.ExecutionId);
            Assert.Equal("Cancelled", ack.Status);
            Assert.Equal(terminal.Run.Error, ack.ResultMessage);
            Assert.Null(ack.ResultData);
            Assert.Single(await db.JobRuns.AsNoTracking().ToArrayAsync(ct));
            Assert.Equal(2, await db.JobShadowObservations.CountAsync(x => x.JobRunId == pending.RunId, ct));
        }
        await h.RecoverAsync(pending.RunId, true, ct);
        Assert.Equal(terminalHash, OrchestrationCallbackProjection.ResultHash(await h.DetailsAsync(pending.RunId, ct)));
        Assert.Equal(0, h.Gateway.Dispatches);
        Assert.Equal(0, h.Gateway.Cancellations);
    }

    [Fact]
    public async Task Ambiguous_transport_waits_only_until_the_persisted_original_deadline_without_start_replay()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        h.Gateway.AmbiguousNextDispatch = true;
        await Assert.ThrowsAsync<TimeoutException>(() => h.StartAsync(ct));
        var runId = h.Gateway.LastDispatchRunId;
        var original = (await h.ControlAsync(runId, ct)).NativeDeadlineUtc!.Value;
        await h.AdvanceWithPresenceHeartbeatsAsync(original.AddMilliseconds(-1), ct);
        await h.RecoverAsync(runId, true, ct);
        Assert.Equal(JobRunState.Running, (await h.DetailsAsync(runId, ct)).Run.Status);
        await h.AdvanceWithPresenceHeartbeatsAsync(original.AddMilliseconds(1), ct);
        await h.RecoverAsync(runId, true, ct);
        var terminal = await h.DetailsAsync(runId, ct);
        Assert.Equal(JobRunState.TimedOut, terminal.Run.Status);
        Assert.Contains("physical outcome is unknown", terminal.Run.Error!);
        Assert.Equal(original, (await h.ControlAsync(runId, ct)).NativeDeadlineUtc);
        Assert.Equal(1, h.Gateway.Dispatches);
        Assert.True(await h.TerminalReadyAsync(terminal, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_ordinary_run_receipt_and_cancellation_do_not_invent_a_deadline(bool cancelFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var run = await h.StartAsync(ct);
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Started, 3, null, false, ct);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            // Keep the genuinely accepted dispatch tuple while modelling an ordinary
            // historical run whose native horizon is unknown. Missing owner is tested
            // separately and never receives current dispatch authority.
            var acceptedOwner = h.CommittedOwner.Owner;
            Assert.Equal(1, await db.Set<JobRunControlRecord>().Where(x => x.RunId == checked((long)run.Id))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.NativeDeadlineUtc, (DateTimeOffset?)null), ct));
            var control = await h.ControlAsync(run.Id, ct);
            Assert.Equal(acceptedOwner.ConnectionId, control.DispatchOwnerConnectionId);
            Assert.Equal(acceptedOwner.Epoch, control.DispatchOwnerEpoch);
        }
        if (cancelFirst)
        {
            Assert.True(await h.CancelAsync(run.Id, true, ct));
            Assert.Equal(JobRunState.Running, (await h.DetailsAsync(run.Id, ct)).Run.Status);
        }
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Completed, 4, "{\"stdout\":[\"historical native receipt\"]}", false, ct);
        var completed = await h.DetailsAsync(run.Id, ct);
        Assert.Equal(cancelFirst ? JobRunState.Cancelled : JobRunState.Succeeded, completed.Run.Status);
        Assert.Null((await h.ControlAsync(run.Id, ct)).NativeDeadlineUtc);
        Assert.Contains("historical native receipt", Assert.Single(completed.Activities).ResultJson!);
        Assert.True(await h.TerminalReadyAsync(completed, ct));
        Assert.Equal(1, h.Gateway.Dispatches);
    }

    [Fact]
    public async Task Historical_null_dispatch_owner_denies_authenticated_lifecycle_and_preserves_original_result_and_callback_state()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var run = await h.StartAsync(ct);
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Started, 3, null, false, ct);
        const string originalResult = "{\"stdout\":[\"accepted original native result\"],\"exitCode\":0}";
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Completed, 4, originalResult, false, ct);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            // Model the legal pre-upgrade control shape: unknown original dispatch owner
            // and native horizon. Erasing these fields grants no current authority. The
            // existing native result/publication marker was created by production APIs.
            (await db.Set<JobRunControlRecord>().Where(x => x.RunId == checked((long)run.Id))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.DispatchOwnerConnectionId, (Guid?)null)
                    .SetProperty(x => x.DispatchOwnerEpoch, (long?)null)
                    .SetProperty(x => x.NativeDeadlineUtc, (DateTimeOffset?)null), ct))
                .Should().Be(1);
        }
        var liveOwner = (await h.Services.GetRequiredService<IClientConnectionEpochStore>().GetCurrentAsync(h.Client, ct))!;
        liveOwner.Owner.Should().Be(h.CommittedOwner.Owner);
        liveOwner.IsEffective(h.Clock.GetUtcNow()).Should().BeTrue();
        h.Gateway.GetRegisteredOwner(h.Client).Should().Be(liveOwner.Owner);
        h.Registration.IsCurrent.Should().BeTrue();
        var before = await h.DetailsAsync(run.Id, ct);
        var beforeControl = await h.ControlAsync(run.Id, ct);
        var beforeSnapshot = await h.DurableSnapshotAsync(run.Id, ct);
        before.Run.Status.Should().Be(JobRunState.Succeeded);
        before.Activities.Should().ContainSingle().Which.ResultJson.Should().Be(originalResult);
        beforeControl.DispatchOwnerConnectionId.Should().BeNull();
        beforeControl.DispatchOwnerEpoch.Should().BeNull();
        beforeControl.NativeDeadlineUtc.Should().BeNull();
        (await h.TerminalReadyAsync(before, ct)).Should().BeTrue();
        var originalHash = OrchestrationCallbackProjection.ResultHash(before);
        beforeControl.TerminalResultHash.Should().Be(originalHash);
        Func<Task> incoming = () => h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Completed, 9,
            "{\"stdout\":[\"unfenced replacement receipt\"]}", true, ct);

        var denial = (await incoming.Should().ThrowAsync<InvalidOperationException>()).Which;

        denial.Message.Should().Be("The job lifecycle differs from its accepted dispatch owner.");
        (await h.DurableSnapshotAsync(run.Id, ct)).Should().Be(beforeSnapshot);
        var after = await h.DetailsAsync(run.Id, ct);
        after.Should().BeEquivalentTo(before);
        (await h.ControlAsync(run.Id, ct)).Should().BeEquivalentTo(beforeControl);
        OrchestrationCallbackProjection.ResultHash(after).Should().Be(originalHash);
        (await h.TerminalReadyAsync(after, ct)).Should().BeTrue();
        h.Gateway.Dispatches.Should().Be(1);
        h.Gateway.Cancellations.Should().Be(0);
        var current = (await h.Services.GetRequiredService<IClientConnectionEpochStore>().GetCurrentAsync(h.Client, ct))!;
        current.Should().BeEquivalentTo(liveOwner);
    }

    [Fact]
    public async Task Late_running_request_projection_cannot_overwrite_the_accepted_terminal_status_or_body()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var run = await h.StartAsync(ct);
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Started, 3, null, false, ct);
        await using var staleScope = h.Services.CreateAsyncScope();
        var staleRequests = staleScope.ServiceProvider.GetRequiredService<NetRatel.Application.Requests.IRequestService>();
        var request = await staleRequests.CreateAsync(new(run.StartedBy, run.ClientIdentity, "41", "{}", 7, h.AgentId), ct);
        var stale = await h.DetailsAsync(run.Id, ct);
        Assert.Equal(JobRunState.Running, stale.Run.Status);
        const string nativeResult = "{\"stderr\":[\"accepted native terminal body\"]}";
        await h.LifecycleAsync(run.Id, JobGatewayLifecycleStatus.Failed, 4, nativeResult, true, ct);
        var terminal = await h.DetailsAsync(run.Id, ct);
        Assert.True(await h.TerminalReadyAsync(terminal, ct));
        await using (var terminalScope = h.Services.CreateAsyncScope())
        {
            Assert.True(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(
                terminalScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(),
                terminalScope.ServiceProvider.GetRequiredService<NetRatel.Application.Requests.IRequestService>(),
                request.Id, terminal.Run.Status, terminal.Run.Error, nativeResult, h.Clock.GetUtcNow(), ct));
        }
        Assert.False(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(
            staleScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(), staleRequests,
            request.Id, stale.Run.Status, "stale running reader", "stale body", h.Clock.GetUtcNow(), ct));
        var current = (await staleRequests.GetAsync(request.Id, ct))!;
        Assert.Equal("Failed", current.Status);
        Assert.Equal(nativeResult, current.ResultData);
        Assert.Equal(terminal.Run.Error, current.ResultMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_failure_or_cancellation_rolls_back_and_releases_the_replica_lock(bool cancelled)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var run = await h.StartAsync(ct);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scope = h.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var failed = h.Primary.ExecuteOwnedAsync<bool>(run.Id, async (owner, ownedCt) =>
        {
            await using var boundary = await JobRunMutationBoundary.AcquireAsync(db, run.Id, h.Clock, ownedCt);
            await boundary.BeginAsync(ownedCt);
            await owner.ReloadAsync(boundary.Observations, ownedCt);
            (await db.JobRuns.SingleAsync(x => x.Id == checked((long)run.Id), ownedCt)).Error = "uncommitted mutation";
            await db.SaveChangesAsync(ownedCt);
            var observation = new JobRunObservation(3, run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy,
                JobRunState.Running, 1, run.CreatedAtUtc, run.StartedAtUtc, null, h.Clock.GetUtcNow(), $"akka-job-authority:{run.Id}", true);
            Assert.Equal(JobMessageDisposition.Accepted, (await owner.RecordAsync(observation, boundary.Observations, ownedCt)).Disposition);
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ownedCt);
            throw new InvalidOperationException("bounded rollback probe");
        }, operationCancellation.Token);
        await entered.Task.WaitAsync(ct);
        if (cancelled) operationCancellation.Cancel(); else release.TrySetResult(true);
        await Assert.ThrowsAnyAsync<Exception>(() => failed);
        Assert.True(await h.CancelAsync(run.Id, true, ct)); // Another actor/connection obtains the released lock.
        Assert.Null((await h.DetailsAsync(run.Id, ct)).Run.Error);
        await using var check = h.Services.CreateAsyncScope();
        var committedDb = check.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.False(await committedDb.JobShadowObservations.AnyAsync(x => x.JobRunId == run.Id && x.SourceEventId == 3, ct));
        var state = await h.Primary.GetStateAsync(run.Id, ct);
        Assert.Equal(2L, state.LastAcceptedSourceEventId);
    }

    [Fact]
    public async Task Configured_Ask_expiry_cancels_started_SQL_but_retains_scope_until_lock_cleanup_finishes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var run = await h.StartAsync(ct);
        var router = h.ProductionRouter(TimeSpan.FromSeconds(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwinding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scope = h.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var operation = router.ExecuteOwnedAsync<bool>(run.Id, async (owner, ownedCt) =>
        {
            await using var boundary = await JobRunMutationBoundary.AcquireAsync(db, run.Id, h.Clock, ownedCt);
            await boundary.BeginAsync(ownedCt);
            await owner.ReloadAsync(boundary.Observations, ownedCt);
            (await db.JobRuns.SingleAsync(x => x.Id == checked((long)run.Id), ownedCt)).Error = "expired uncommitted mutation";
            await db.SaveChangesAsync(ownedCt);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ownedCt); }
            finally { unwinding.TrySetResult(); await release.Task.WaitAsync(ct); }
            return true;
        }, ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            await unwinding.Task.WaitAsync(ct);
            Assert.False(operation.IsCompleted); // Caller still owns the scoped context during cleanup.
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => operation);
        Assert.True(await h.CancelAsync(run.Id, true, ct));
        Assert.Null((await h.DetailsAsync(run.Id, ct)).Run.Error);
    }

    [Fact]
    public async Task Configured_Ask_expiry_fences_queued_delegate_before_its_scope_can_be_used()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct);
        var run = await h.StartAsync(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = h.Primary.ExecuteOwnedAsync(run.Id, async (_, ownedCt) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(ownedCt); return true; }, ct);
        await entered.Task.WaitAsync(ct);
        var invoked = 0;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => h.ProductionRouter(TimeSpan.FromMilliseconds(200))
                .ExecuteOwnedAsync(run.Id, (_, _) => { Interlocked.Increment(ref invoked); return Task.FromResult(true); }, ct));
        }
        finally { release.TrySetResult(); }
        await blocker.WaitAsync(ct);
        _ = await h.Primary.GetStateAsync(run.Id, ct); // Mailbox progressed past the expired queued mutation.
        Assert.Equal(0, invoked);
    }

    private enum GateKind { CancelRequested, TerminalRun }
    private sealed class SaveGate : SaveChangesInterceptor
    {
        private ulong _runId;
        private GateKind _kind;
        private int _used;
        private TaskCompletionSource<bool> _entered = NewSignal();
        private TaskCompletionSource<bool> _release = NewSignal();
        public Task Entered => _entered.Task;
        public void Arm(ulong runId, GateKind kind) { _runId = runId; _kind = kind; _used = 0; _entered = NewSignal(); _release = NewSignal(); }
        public void Release() => _release.TrySetResult(true);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context;
            if (_runId == 0 || db is null) return result;
            var matches = _kind == GateKind.CancelRequested ? db.ChangeTracker.Entries<JobRunControlRecord>().Any(x =>
                x.Entity.RunId == checked((long)_runId) && x.Entity.CancellationRequestedAtUtc is not null) :
                db.ChangeTracker.Entries<JobRunRecord>().Any(x => x.Entity.Id == checked((long)_runId) &&
                    x.Entity.Status is (int)JobRunState.Succeeded or (int)JobRunState.Failed or (int)JobRunState.Cancelled);
            if (!matches || Interlocked.Exchange(ref _used, 1) != 0) return result;
            Assert.NotNull(db.Database.CurrentTransaction);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return result;
        }
        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public ServiceProvider Services { get; private set; } = null!;
        public SaveGate Gate { get; } = new();
        public MutableClock Clock { get; } = new();
        public RecordingGateway Gateway { get; private set; } = null!;
        public ActorRouter Primary { get; private set; } = null!;
        public ActorRouter Replica { get; private set; } = null!;
        public Guid AgentId { get; } = Guid.NewGuid();
        public ClientKey Client => new(7, AgentId);
        public OwnerSnapshot CommittedOwner { get; private set; } = null!;
        public AgentJobGatewayRegistration Registration { get; private set; } = null!;
        private IClientConnectionEpochStore Ownership => Services.GetRequiredService<IClientConnectionEpochStore>();
        private ActorSystem _actors = null!;
        public IJobRuntimeRouter ProductionRouter(TimeSpan timeout)
        {
            var required = DispatchProxy.Create<IRequiredActor<JobRuntimeRegion>, RequiredActorProxy>();
            ((RequiredActorProxy)(object)required).Actor = Primary.Region;
            return new AkkaJobRuntimeRouter(required, timeout);
        }
        public IJobRuntimeRouter CreateActorRegion() => new ActorRouter(
            _actors.ActorOf(JobCoordinatorActor.Props(Services.GetRequiredService<IJobObservationStore>())));
        public async Task<JobShadowObservationRecord[]> ObservationsAsync(ulong runId, CancellationToken ct)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().JobShadowObservations
                .AsNoTracking().Where(x => x.JobRunId == runId).OrderBy(x => x.SourceEventId).ToArrayAsync(ct);
        }
        public static async Task<Harness> CreateAsync(string connection, CancellationToken ct, string? legacyClientIdentity = null)
        {
            var h = new Harness();
            var services = new ServiceCollection();
            services.AddDbContext<OrchestratorDbContext>(o => o.UseNpgsql(connection).AddInterceptors(h.Gate));
            services.AddSingleton<TimeProvider>(h.Clock);
            services.AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)));
            services.AddSingleton<IClientConnectionEpochStore, ClientConnectionEpochStore>();
            services.AddNetRatelJobObservationPersistence();
            services.AddScoped<IJobDefinitionService, JobDefinitionService>();
            services.AddScoped<IJobRunService, JobRunService>();
            services.AddScoped<NetRatel.Application.Requests.IRequestService, RequestService>();
            h.Services = services.BuildServiceProvider();
            h._actors = ActorSystem.Create("owned-job-pg-" + Guid.NewGuid().ToString("N"));
            var store = h.Services.GetRequiredService<IJobObservationStore>();
            h.Primary = new(h._actors.ActorOf(JobCoordinatorActor.Props(store)));
            h.Replica = new(h._actors.ActorOf(JobCoordinatorActor.Props(store)));
            h.Gateway = new(h);
            await using var scope = h.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.MigrateAsync(ct);
            var now = h.Clock.GetUtcNow();
            db.Tenants.Add(new() { Id = 7, Name = "Bounded ownership tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Agents.Add(new() { Id = h.AgentId, TenantId = 7, CreatedAtUtc = now });
            db.Jobs.Add(new() { Id = 41, Name = "Bounded owned command", TenantId = 7, AgentId = h.AgentId,
                ClientIdentity = legacyClientIdentity ?? h.AgentId.ToString("D"), CreatedAtUtc = now, UpdatedAtUtc = now,
                OptionsJson = "{\"executionPolicy\":{\"expectedRuntimeSeconds\":60,\"hardTimeoutSeconds\":60}}" });
            db.JobSteps.Add(new() { Id = 42, JobId = 41, Ordinal = 1, Type = 0, Runner = "bash", Command = "true", Enabled = true });
            await db.SaveChangesAsync(ct);
            // Trusted server-ingress inputs are explicit provider fixture data. The real
            // reservation, first-heartbeat commit and deferred SQL acceptance guard grant
            // authority; no owner row/marker/fence is seeded by the fixture. Enrollment,
            // signed gRPC authentication and a physical native process are separate gates.
            h.Clock.ResetToUtcNow();
            var admittedAt = h.Clock.GetUtcNow();
            var request = new AdmissionRequest(h.Client, Guid.NewGuid(), Guid.NewGuid(), 0,
                admittedAt, admittedAt.AddSeconds(30), admittedAt.AddSeconds(600),
                new("owned-job-provider-fixture", ["presence"], null));
            var reserved = await h.Ownership.ReserveAsync(request, ct);
            reserved.Disposition.Should().Be(OwnershipDisposition.Accepted);
            reserved.Reservation.Should().NotBeNull();
            var committed = await h.Ownership.CommitAsync(reserved.Reservation!,
                new(reserved.Reservation!.Owner, 1, admittedAt), ct);
            committed.Disposition.Should().Be(OwnershipDisposition.Accepted);
            h.CommittedOwner = committed.Current!;
            h.CommittedOwner.Owner.Should().Be(reserved.Reservation.Owner);
            h.CommittedOwner.AcceptanceGuardAtUtc.Should().NotBeNull();
            h.CommittedOwner.Active.Should().BeTrue();
            h.CommittedOwner.AuthenticationExpiresAtUtc.Should().Be(request.AuthenticationExpiresAtUtc);
            h.Registration = h.Gateway.Register(h.CommittedOwner.Owner.Client, h.CommittedOwner.Owner.ConnectionId,
                checked((ulong)h.CommittedOwner.Owner.Epoch));
            h.Registration.IsCurrent.Should().BeTrue();
            h.Gateway.GetRegisteredOwner(h.Client).Should().Be(h.CommittedOwner.Owner);
            return h;
        }
        public async Task AdvanceWithPresenceHeartbeatsAsync(DateTimeOffset target, CancellationToken ct)
        {
            if (target < Clock.GetUtcNow()) throw new ArgumentOutOfRangeException(nameof(target));
            var originalOwner = CommittedOwner.Owner;
            var originalAuthenticationExpiry = CommittedOwner.AuthenticationExpiresAtUtc;
            while (Clock.GetUtcNow() < target)
            {
                var next = Clock.GetUtcNow().AddSeconds(30);
                Clock.UtcNow = next < target ? next : target;
                // Presence-only heartbeat: no optional validated authentication renewal.
                // Each <=30s step remains inside the original60s presence horizon. This
                // preserves the original600s auth lifetime and every native/Ask deadline.
                var heartbeat = await Ownership.RecordHeartbeatAsync(new(originalOwner,
                    checked(CommittedOwner.Sequence + 1), Clock.GetUtcNow()), ct);
                heartbeat.Disposition.Should().Be(OwnershipDisposition.Accepted);
                CommittedOwner = heartbeat.Current!;
                CommittedOwner.Owner.Should().Be(originalOwner);
                CommittedOwner.AuthenticationExpiresAtUtc.Should().Be(originalAuthenticationExpiry);
                Gateway.GetRegisteredOwner(Client).Should().Be(originalOwner);
            }
        }
        private AkkaJobAuthorityService Authority(IServiceProvider services, bool replica, ManagedOrchestrationInvocationGuard? managedGuard = null) => new(
            services.GetRequiredService<IJobDefinitionService>(), services.GetRequiredService<IJobRunService>(),
            new JobTaskBridge(new EmptyScripts(), NullLogger<JobTaskBridge>.Instance), Gateway, replica ? Replica : Primary,
            new RecordingRealtimeFanoutSink(), new JobAuthorityIdGenerator(), new TestEnvironment(),
            managedGuard: managedGuard, db: services.GetRequiredService<OrchestratorDbContext>(), clock: Clock);
        public async Task<JobRunInfo> StartAsync(CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return await Authority(scope.ServiceProvider, false).StartAsync(41, new("operator-test", null, null), ct); }
        private ClaimsPrincipal? _managedPrincipal;
        public async Task<JobRunInfo> StartManagedAsync(ulong runId, int requestId, CancellationToken ct)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = (await scope.ServiceProvider.GetRequiredService<IJobRunService>().GetAsync(runId, ct))!;
            var registry = Registry(db);
            return await Authority(scope.ServiceProvider, false, new(db, registry, Clock)).StartManagedAsync(41,
                new(run.StartedBy, run.InputsJson, null), requestId, _managedPrincipal!, ct);
        }
        public async Task<bool> CancelAsync(ulong runId, bool replica, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return await Authority(scope.ServiceProvider, replica).CancelAsync(runId, "operator-cancelled", ct); }
        public async Task RecoverAsync(ulong runId, bool replica, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); await Authority(scope.ServiceProvider, replica).RecoverAsync(runId, ct); }
        public async Task LifecycleAsync(ulong runId, JobGatewayLifecycleStatus status, ulong sequence, string? result, bool replica, CancellationToken ct)
        {
            await using var scope = Services.CreateAsyncScope();
            var details = (await scope.ServiceProvider.GetRequiredService<IJobRunService>().GetDetailsAsync(runId, ct))!;
            var step = Assert.Single(details.Steps);
            await Authority(scope.ServiceProvider, replica).RecordLifecycleAsync(new(7, AgentId), new(runId, step.JobStepId!.Value,
                step.Id, step.Ordinal, step.TaskRequestId!, "actual-boundary", status, sequence, sequence,
                details.Run.StartedAtUtc!.Value, Clock.GetUtcNow(), 100, result, status == JobGatewayLifecycleStatus.Failed ? 1 : 0,
                AuthenticatedOwner: CommittedOwner.Owner), ct);
        }
        public async Task<JobRunDetails> DetailsAsync(ulong runId, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return (await scope.ServiceProvider.GetRequiredService<IJobRunService>().GetDetailsAsync(runId, ct))!; }
        public async Task<JobRunControlRecord> ControlAsync(ulong runId, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<JobRunControlRecord>().AsNoTracking().SingleAsync(x => x.RunId == checked((long)runId), ct); }
        public async Task<string> DurableSnapshotAsync(ulong runId, CancellationToken ct)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var observations = await db.JobShadowObservations.AsNoTracking().Where(row => row.JobRunId == runId)
                .OrderBy(row => row.SourceEventId).ToArrayAsync(ct);
            return JsonSerializer.Serialize(new
            {
                Details = await DetailsAsync(runId, ct), Control = await ControlAsync(runId, ct), Observations = observations
            });
        }
        public async Task<bool> TerminalReadyAsync(JobRunDetails details, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return await OrchestrationCallbackProjection.TerminalReadyAsync(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(), details, ct); }
        public async Task<(ulong RunId, int RequestId)> SeedPendingIngressAsync(CancellationToken ct, bool authorized = false)
        {
            const ulong runId = 8001;
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var requests = scope.ServiceProvider.GetRequiredService<NetRatel.Application.Requests.IRequestService>();
            ServicePrincipalRegistration? registration = null;
            if (authorized)
            {
                var registry = Registry(db);
                var constraints = JsonSerializer.Serialize(new ServiceLinkResourceConstraints
                    { TenantId = "7", ResourceIds = [AgentId.ToString("D")], RequestDefinitionIds = ["41"] }, ServiceLinkCanonicalJson.Json);
                var created = await registry.CreateAsync(new ServiceClientCreateRequest("Bounded cancel invoker", 7,
                    "owned-peer", "owned-peer-tenant", [ServiceIdentityScopes.OrchestrationInvoke], constraints), "owned-test-admin", ct: ct);
                var authenticated = await registry.AuthenticateClientAsync(created.Principal.ClientId, created.ClientSecret, ct);
                Assert.NotNull(authenticated);
                Assert.True(await registry.CanIssueScopesAsync(authenticated, [ServiceIdentityScopes.OrchestrationInvoke], ct));
                registration = created.Principal;
                _managedPrincipal = Principal(registration);
                Assert.NotNull(await registry.ResolvePrincipalAsync(_managedPrincipal, ServiceIdentityScopes.OrchestrationInvoke, ct));
            }
            var principalId = registration?.Id ?? Guid.NewGuid();
            var source = $"service:{principalId:N}";
            var request = await requests.CreateAsync(new(source, AgentId.ToString("D"), "41", "{}", 7, AgentId), ct);
            var inputs = $"{{\"meta\":{{\"netratelRequestId\":\"{request.Id}\"}}}}";
            await scope.ServiceProvider.GetRequiredService<IJobRunService>().UpsertRunAsync(new(runId, 41, 7, AgentId.ToString("D"), source,
                JobRunState.Pending, 0, Clock.GetUtcNow(), null, null, null, inputs, null, AgentId), ct);
            db.Set<JobRunControlRecord>().Add(new() { RunId = checked((long)runId) });
            db.Set<ManagedOrchestrationRequestBinding>().Add(new() { RequestId = request.Id, ServicePrincipalId = principalId, TenantId = 7,
                AgentId = AgentId, JobDefinitionId = "41", ExecutionId = "8001", ParentRequestId = "actual-parent", RequestTaskId = "actual-task", CorrelationId = "actual-correlation",
                LinkRevision = registration?.LinkRevision ?? 0, PeerInstanceId = registration?.PeerInstanceId ?? "", PeerTenantId = registration?.PeerTenantId ?? "" });
            if (authorized)
                await requests.UpdateAsync(new(request.Id, null, null, null, "8001", null, null, null, null, null), ct);
            await db.SaveChangesAsync(ct);
            return (runId, request.Id);
        }
        private ServicePrincipalRegistry Registry(OrchestratorDbContext db)
        {
            var settings = new OwnedPublicSettings();
            var options = new OptionsMonitor<ServiceIdentityOptions>(new OptionsFactory<ServiceIdentityOptions>([], []), [], new OptionsCache<ServiceIdentityOptions>());
            return new(db, new ServiceIdentityRuntimeOptions(settings), options, settings, new EmptyServiceClientDeploymentCatalog(), Clock);
        }
        private static ClaimsPrincipal Principal(ServicePrincipalRegistration row) => new(new ClaimsIdentity(new[]
        {
            new Claim("sub", $"service:{row.Id:N}"), new Claim("client_id", row.ClientId),
            new Claim("token_use", ServiceIdentityClaims.Purpose), new Claim("auth_mode", "service"),
            new Claim(ServiceIdentityClaims.PrincipalId, row.Id.ToString("N")), new Claim(ServiceIdentityClaims.CredentialRevision, "1"),
            new Claim(ServiceIdentityClaims.GrantRevision, row.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ServiceIdentityClaims.TenantId, "7"), new Claim(ServiceIdentityClaims.PeerInstanceId, row.PeerInstanceId),
            new Claim(ServiceIdentityClaims.PeerTenantId, row.PeerTenantId),
            new Claim(ServiceIdentityClaims.LinkRevision, row.LinkRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim("scope", ServiceIdentityScopes.OrchestrationInvoke)
        }, "owned-managed-test"));
        public async ValueTask DisposeAsync() { Gate.Release(); Registration.Dispose(); await _actors.Terminate(); await Services.DisposeAsync(); }
    }

    private sealed class OwnedPublicSettings : IServicePublicSettingsResolver
    {
        public Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) => Task.FromResult(new ServicePublicSettingsEffective(
            new ServiceIdentityOptions { Enabled = true, Issuer = "https://owned.example.test/services", ApiBaseUrl = "https://owned.example.test",
                WebBaseUrl = "https://web.owned.example.test", InstanceId = "d47bd363-5b79-4c9f-ae3b-47f79ae5ea06" }, new ServiceLinkOptions(), 1, []));
        public Task<ServicePublicSettingsEffective> UpdateAsync(ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingGateway(Harness harness) : IAgentJobGatewaySessionRegistry
    {
        private readonly AgentJobGatewaySessionRegistry _backend = new();
        public int Dispatches;
        public int Cancellations;
        public bool AmbiguousNextDispatch;
        public ulong LastDispatchRunId;
        public AgentJobGatewayRegistration Register(ClientKey client, Guid id, ulong epoch, bool provisional = false) =>
            _backend.Register(client, id, epoch, provisional);
        public bool IsAvailable(ClientKey client) => _backend.IsAvailable(client);
        public OwnerKey? GetRegisteredOwner(ClientKey client) => _backend.GetRegisteredOwner(client);
        public async Task DispatchAsync(ClientKey client, JobGatewayStepDispatch dispatch, CancellationToken ct)
        {
            dispatch.ExpectedOwner.Should().Be(harness.CommittedOwner.Owner);
            var stored = await harness.DetailsAsync(dispatch.JobRunId, ct);
            var control = await harness.ControlAsync(dispatch.JobRunId, ct);
            Assert.Equal(JobRunState.Running, stored.Run.Status);
            Assert.NotNull(control.DispatchPreparedAtUtc); // Independent connection sees committed intent before IO.
            Assert.Equal(dispatch.RequestedAtUtc.AddSeconds(60), control.NativeDeadlineUtc);
            control.DispatchOwnerConnectionId.Should().Be(harness.CommittedOwner.Owner.ConnectionId);
            control.DispatchOwnerEpoch.Should().Be(harness.CommittedOwner.Owner.Epoch);
            await _backend.DispatchAsync(client, dispatch, ct);
            var frame = await harness.Registration.Reader.ReadAsync(ct);
            frame.ConnectionId.Should().Be(harness.CommittedOwner.Owner.ConnectionId.ToString("D"));
            frame.ConnectionEpoch.Should().Be(checked((ulong)harness.CommittedOwner.Owner.Epoch));
            frame.Dispatch.Should().NotBeNull();
            frame.Dispatch.JobRunId.Should().Be(dispatch.JobRunId);
            LastDispatchRunId = dispatch.JobRunId;
            Interlocked.Increment(ref Dispatches);
            if (AmbiguousNextDispatch)
            {
                AmbiguousNextDispatch = false;
                throw new TimeoutException("Physical enqueue acknowledgement was lost.");
            }
        }
        public Task CancelAsync(ClientKey client, ulong runId, string reason, CancellationToken ct) =>
            CancelAsync(GetRegisteredOwner(client) ?? throw new AgentJobGatewaySessionUnavailableException(client), runId, reason, ct);
        public async Task CancelAsync(OwnerKey expectedOwner, ulong runId, string reason, CancellationToken ct)
        {
            expectedOwner.Should().Be(harness.CommittedOwner.Owner);
            Assert.NotNull((await harness.ControlAsync(runId, ct)).CancellationRequestedAtUtc);
            await _backend.CancelAsync(expectedOwner, runId, reason, ct);
            var frame = await harness.Registration.Reader.ReadAsync(ct);
            frame.ConnectionId.Should().Be(expectedOwner.ConnectionId.ToString("D"));
            frame.ConnectionEpoch.Should().Be(checked((ulong)expectedOwner.Epoch));
            frame.Cancel.Should().NotBeNull();
            frame.Cancel.JobRunId.Should().Be(runId);
            Interlocked.Increment(ref Cancellations);
        }
    }
    public sealed class MutableClock : TimeProvider
    {
        // PostgreSQL stores microseconds. Begin the synthetic clock at that precision so
        // the exact committed-deadline assertion also checks the dispatch value unchanged.
        public DateTimeOffset UtcNow = MicrosecondUtcNow();
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void ResetToUtcNow() => UtcNow = MicrosecondUtcNow();
        private static DateTimeOffset MicrosecondUtcNow()
        {
            var now = DateTimeOffset.UtcNow;
            return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        }
    }
    private sealed class ActorRouter(IActorRef actor) : IJobRuntimeRouter
    {
        public IActorRef Region => actor;
        private TaskCompletionSource<bool>? _next;
        public Task ObserveNextOperation() { _next = new(TaskCreationOptions.RunContinuationsAsynchronously); return _next.Task; }
        public async Task<T> ExecuteOwnedAsync<T>(ulong id, Func<IJobRunOwner, CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            var result = await actor.Ask<object>(new ExecuteOwnedJobRun(id, async (owner, ownedCt) =>
            { Interlocked.Exchange(ref _next, null)?.TrySetResult(true); return await operation(owner, ownedCt); }, ct), TimeSpan.FromSeconds(30), ct);
            return (T)result;
        }
        public Task<JobMessageResult> RecordAsync(RecordJobObservation message, CancellationToken ct) => actor.Ask<JobMessageResult>(message, TimeSpan.FromSeconds(30), ct);
        public Task<JobRunView> GetStateAsync(ulong id, CancellationToken ct) => actor.Ask<JobRunView>(new GetJobRunProjection(id), TimeSpan.FromSeconds(30), ct);
        public Task<JobRuntimeStatus> ProbeAsync(CancellationToken ct) => actor.Ask<JobRuntimeStatus>(new ProbeJobRuntime(), TimeSpan.FromSeconds(30), ct);
    }
    public class RequiredActorProxy : DispatchProxy
    {
        public IActorRef Actor { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetAsync" when method.ReturnType == typeof(Task<IActorRef>) => Task.FromResult(Actor),
            "GetAsync" when method.ReturnType == typeof(ValueTask<IActorRef>) => new ValueTask<IActorRef>(Actor),
            "get_ActorRef" => Actor,
            _ => throw new NotSupportedException($"Unexpected actor requirement member {method?.Name}.")
        };
    }
    private sealed class EmptyScripts : IScriptService
    {
        public Task<IReadOnlyList<ScriptInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptInfo>>([]);
        public Task<ScriptInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult<ScriptInfo?>(null);
        public Task<IReadOnlyList<ScriptParamInfo>> GetParamsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptParamInfo>>([]);
        public Task<ScriptInfo> CreateAsync(CreateScriptCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> UpdateAsync(UpdateScriptCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> DeleteAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> ParseManifestAsync(ulong id, string? raw, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "NetRatel.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
