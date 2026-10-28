using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.ComputerUse;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ComputerUseSessionStoreTests
{
    private static readonly ComputerUseOwner Owner = new("operator@example.test", "agent-client", "mcp-service", "https://mcp.example.test/mcp", "dev");
    private static readonly ComputerUseTarget Target = new(3, "fixture-sid-hash", 500, "window-10");

    [Fact]
    public async Task Lease_conflict_and_ownership_are_durable_across_store_instances()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var request = Create();
        await using (var db = new OrchestratorDbContext(options))
        {
            var created = await new ComputerUseSessionStore(db).CreateAsync(request, default);
            created.Dispatch.Should().BeTrue();
        }
        await using var reconnected = new OrchestratorDbContext(options);
        var store = new ComputerUseSessionStore(reconnected);
        var competing = await store.CreateAsync(request with { SessionId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Owner = Owner with { Subject = "other@example.test" } }, default);
        competing.FailureCode.Should().Be("computer_use_controller_conflict");
        (await store.GetOwnedAsync(request.SessionId, 8, request.AgentId, Owner, default)).Should().BeNull();
        (await store.GetOwnedAsync(request.SessionId, request.TenantId, request.AgentId, Owner with { ClientId = "other-client" }, default)).Should().BeNull();
        (await store.GetOwnedAsync(request.SessionId, request.TenantId, request.AgentId, Owner, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Action_receipt_is_committed_before_dispatch_and_replay_has_no_desktop_content()
    {
        await using var db = Db();
        var store = new ComputerUseSessionStore(db);
        var request = Create();
        await OpenAsync(store, request);
        var action = Start(request, "act");
        var first = await store.BeginAsync(action, default);
        first.Dispatch.Should().BeTrue();
        (await store.BeginAsync(action, default)).FailureCode.Should().Be("computer_use_operation_pending");
        var conflicting = await store.BeginAsync(action with { OperationId = Guid.NewGuid() }, default);
        conflicting.FailureCode.Should().Be("computer_use_operation_pending");
        await store.CompleteAsync(action.OperationId, new(1, action.OperationId, request.SessionId,
            ComputerUseStatuses.AppliedUnverified, Observation: new("observation-2", request.Now, Target, "private-ui-content"), MayHaveApplied: true), request.Now.AddSeconds(2), default);
        var replay = await store.BeginAsync(action, default);
        replay.Dispatch.Should().BeFalse();
        replay.Replayed.Should().BeTrue();
        replay.PreviousResult!.Status.Should().Be(ComputerUseStatuses.AppliedUnverified);
        replay.PreviousResult.Observation.Should().BeNull();
        replay.PreviousResult.MayHaveApplied.Should().BeTrue();
        (await db.ComputerUseOperations.CountAsync()).Should().Be(2);
        (await db.ComputerUseAudits.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task Human_pause_fences_inflight_action_and_late_result_cannot_resume_it()
    {
        await using var db = Db();
        var store = new ComputerUseSessionStore(db);
        var request = Create();
        await OpenAsync(store, request);
        var action = Start(request, "act");
        var acting = await store.BeginAsync(action, default);
        var human = Owner with { Subject = "human@example.test", ClientId = "browser" };
        var pause = Start(request, "pause") with { Owner = human, HumanTakeover = true };
        var paused = await store.BeginAsync(pause, default);
        paused.Dispatch.Should().BeTrue();
        paused.Session!.Revision.Should().BeGreaterThan(acting.Session!.Revision);
        await store.CompleteAsync(action.OperationId, new(1, action.OperationId, request.SessionId, ComputerUseStatuses.AppliedUnverified, MayHaveApplied: true), request.Now.AddSeconds(2), default);
        var lease = await store.GetOwnedAsync(request.SessionId, request.TenantId, request.AgentId, Owner, default);
        lease!.State.Should().Be("paused");
        lease.PendingOperationId.Should().Be(pause.OperationId);
        await store.CompleteAsync(pause.OperationId, new(1, pause.OperationId, request.SessionId, ComputerUseStatuses.Completed), request.Now.AddSeconds(3), default);
        (await store.BeginAsync(Start(request, "act"), default)).FailureCode.Should().Be("computer_use_session_paused");
    }

    [Theory]
    [InlineData(false, "act")]
    [InlineData(false, "resume")]
    [InlineData(true, "close_view")]
    public async Task View_and_control_leases_do_not_escalate(bool control, string operation)
    {
        await using var db = Db();
        var store = new ComputerUseSessionStore(db);
        var request = Create() with { ControlAllowed = control };
        await OpenAsync(store, request);
        (await store.BeginAsync(Start(request, operation), default)).FailureCode.Should().Be("computer_use_control_not_allowed");
    }

    [Fact]
    public async Task Expiry_releases_controller_slot_but_old_session_cannot_execute()
    {
        await using var db = Db();
        var store = new ComputerUseSessionStore(db);
        var request = Create();
        await OpenAsync(store, request);
        var expiredAction = await store.BeginAsync(Start(request, "act") with { Now = request.Now.AddSeconds(61) }, default);
        expiredAction.FailureCode.Should().Be("computer_use_lease_expired");
        var replacement = request with { SessionId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Now = request.Now.AddSeconds(62) };
        (await store.CreateAsync(replacement, default)).Dispatch.Should().BeTrue();
        (await store.BeginAsync(Start(request, "act") with { Now = replacement.Now }, default)).Dispatch.Should().BeFalse();
    }

    [Fact]
    public async Task A_consumed_confirmation_without_a_command_receipt_cannot_start_work_on_replay()
    {
        await using var db = Db();
        var store = new ComputerUseSessionStore(db);
        var request = Create();
        var openReplay = await store.CreateAsync(request with { ReplayOnly = true }, default);
        openReplay.Dispatch.Should().BeFalse();
        openReplay.FailureCode.Should().Be("computer_use_outcome_unavailable");
        await OpenAsync(store, request);
        var actionReplay = await store.BeginAsync(Start(request, "act") with { ReplayOnly = true }, default);
        actionReplay.Dispatch.Should().BeFalse();
        actionReplay.FailureCode.Should().Be("computer_use_outcome_unavailable");
        (await db.ComputerUseOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Audit_rows_are_append_only_and_open_unknown_retains_the_controller_fence()
    {
        await using var db = Db();
        var store = new ComputerUseSessionStore(db);
        var request = Create();
        await store.CreateAsync(request, default);
        await store.CompleteAsync(request.OperationId, new(1, request.OperationId, request.SessionId, ComputerUseStatuses.Unknown, "transport_lost"), request.Now.AddSeconds(1), default);
        (await store.CreateAsync(request with { SessionId = Guid.NewGuid(), OperationId = Guid.NewGuid() }, default)).FailureCode.Should().Be("computer_use_controller_conflict");
        var audit = await db.ComputerUseAudits.FirstAsync();
        audit.Outcome = "tampered";
        Func<Task> save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
    }

    private static OrchestratorDbContext Db() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static ComputerUseSessionCreate Create()
    {
        var id = Guid.NewGuid();
        return new(id, id, 7, Guid.NewGuid(), Guid.NewGuid(), 8, Owner, Target, ComputerUseProviders.CuaDriver,
            true, 300, 60, new string('a', 64), Guid.NewGuid(), "fixture-correlation", DateTimeOffset.UtcNow);
    }
    private static ComputerUseOperationStart Start(ComputerUseSessionCreate request, string operation) =>
        new(request.SessionId, Guid.NewGuid(), request.TenantId, request.AgentId, Owner, operation,
            new string('b', 64), Guid.NewGuid(), "fixture-operation", request.Now.AddSeconds(1));
    private static async Task OpenAsync(ComputerUseSessionStore store, ComputerUseSessionCreate request)
    {
        await store.CreateAsync(request, default);
        await store.CompleteAsync(request.OperationId, new(1, request.OperationId, request.SessionId, ComputerUseStatuses.Completed), request.Now, default);
    }
}
