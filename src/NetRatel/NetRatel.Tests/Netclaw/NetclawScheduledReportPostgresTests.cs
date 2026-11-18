using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed partial class NetclawPersistencePostgresTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scheduled_finals_commit_in_native_order_and_only_discharge_their_own_reminders(bool buffered)
    {
        await using var run = await StartScheduledTurnAsync();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await run.RegisterAsync("first-report", first);
        await run.WaitForOutputAsync("type tool_result", "reason Processing");
        var beforeParent = await run.LoadAsync();
        Assert.False(beforeParent.FollowUp!.ParentCompleted);
        Assert.Equal("first-report", Assert.Single(beforeParent.FollowUp.Reminders).Id);
        await run.RegisterAsync("second-report", second);

        await run.ParentAsync();
        if (!buffered)
        {
            await run.Lifecycle.FollowUpWaiting.Task.WaitAsync(run.Token);
            var refreshed = await run.LoadAsync();
            Assert.Equal(InvestigationState.WaitingForFollowUp, refreshed.Investigation.State);
            Assert.Equal(2, refreshed.FollowUp!.Reminders.Count);
        }
        await run.TerminalAsync(2, "first-report", first, "Same final text");
        await run.WaitForOutputAsync("type turn_completed, turn 2", "reason WaitingForFollowUp");
        var partial = await run.LoadAsync();
        Assert.Equal(InvestigationState.WaitingForFollowUp, partial.Investigation.State);
        Assert.Equal("second-report", Assert.Single(partial.FollowUp!.Reminders).Id);
        AssertSavedReport(partial, 2, run.MessageId, first);

        await run.TerminalAsync(2, "first-report", first, "Duplicate final");
        await run.TerminalAsync(3, "first-report", first, "Completed reminder replay");
        await run.TerminalAsync(3, "second-report", second, "Same final text");
        await run.Daemon.TurnClosed.Reader.ReadAsync(run.Token);

        var final = await run.LoadAsync();
        Assert.Equal(InvestigationState.Idle, final.Investigation.State);
        Assert.Null(final.FollowUp);
        Assert.Null(final.Recovery);
        AssertSavedReport(final, 3, run.MessageId, second);
        Assert.Equal(new[] { "Parent ack", "Same final text", "Same final text" },
            final.Events.Where(x => x.Type == "assistant").Select(x => x.Text));
        Assert.Single(final.Events, x => x.Type == "operator");
        Assert.Equal(1, run.Daemon.Sends);

        run.Daemon.AttachSnapshot = false;
        await using var db = new OrchestratorDbContext(run.Options);
        var next = Guid.NewGuid();
        await new InvestigationStore(db, run.Clock).AcceptMessageAsync(17, run.Id, new(next, "New operator request"), "operator", null, false, run.Token);
        var admitted = await run.Daemon.Sent.Reader.ReadAsync(run.Token);
        Assert.Equal(run.SessionId, admitted.SessionId);
        await run.RegisterAsync("second-report", second, turn: 4);
        await run.TerminalAsync(4, "second-report", second, "Late final from the old request");
        await run.OutputAsync(new { type = "text", sessionId = run.SessionId, text = "New operator reply" });
        await run.OutputAsync(new { type = "turn_completed", sessionId = run.SessionId, turnNumber = 4, turnOutcome = "completed" });
        await run.Daemon.TurnClosed.Reader.ReadAsync(run.Token);
        var newer = await run.LoadAsync();
        Assert.Equal(InvestigationState.Idle, newer.Investigation.State);
        Assert.Equal(next, Assert.Single(newer.Events, x => x.Type == "assistant" && x.Text == "New operator reply").Trace!.TurnMessageId);
        Assert.DoesNotContain(newer.Events, x => x.Text == "Late final from the old request");
    }

    [Fact]
    public async Task Recurring_occurrence_keeps_its_schedule_and_child_and_foreign_terminals_leave_no_report()
    {
        await using var run = await StartScheduledTurnAsync();
        var recurring = Guid.NewGuid(); var oneShot = Guid.NewGuid();
        await run.RegisterAsync("daily", recurring, recurring: true);
        await run.RegisterAsync("once", oneShot);
        await run.ParentAsync(["child-1"]);
        await run.Lifecycle.FollowUpWaiting.Task.WaitAsync(run.Token);

        for (var index = 0; index < 140; index++)
            await run.OutputAsync(new { type = "usage", sessionId = run.SessionId });
        await run.TerminalAsync(2, "daily", recurring, "Wrong session", sessionId: "other-session");
        await run.TerminalAsync(2, "daily", recurring, "Wrong message", messageId: Guid.NewGuid());
        await run.OutputAsync(new { type = "turn_completed", sessionId = run.SessionId, turnNumber = 2, turnOutcome = "completed",
            openChildRunIds = new[] { "child-1" }, sourceReminderId = "daily:1795007000000" });
        await run.WaitForOutputAsync("type turn_completed, turn 2", "reason follow_up_identity_unconfirmed");
        var awaiting = await run.LoadAsync();
        Assert.Single(awaiting.Events, x => x.Type == "turn_completed");
        Assert.Null(run.Worker.Draft(run.Id));
        Assert.Equal(2, awaiting.FollowUp!.Reminders.Count);

        await run.TerminalAsync(2, "daily", recurring, "Daily report");
        await run.TerminalAsync(3, "daily", recurring, "Duplicate occurrence");
        await run.TerminalAsync(3, "once", oneShot, "Only once");
        await run.WaitForOutputAsync("type turn_completed, turn 3", "reason WaitingForFollowUp");
        var afterReports = await run.LoadAsync();
        Assert.Equal("child-1", Assert.Single(afterReports.FollowUp!.ChildRunIds));
        Assert.True(Assert.Single(afterReports.FollowUp.Reminders).Recurring);
        AssertSavedReport(afterReports, 2, run.MessageId, recurring);
        AssertSavedReport(afterReports, 3, run.MessageId, oneShot);
        Assert.DoesNotContain(afterReports.Events, x => x.Text == "Duplicate occurrence");

        await run.OutputAsync(new { type = "text", sessionId = run.SessionId, text = "Child reply" });
        await run.OutputAsync(new { type = "turn_completed", sessionId = run.SessionId, turnNumber = 4, turnOutcome = "completed", openChildRunIds = Array.Empty<string>() });
        await run.WaitForOutputAsync("type turn_completed, turn 4", "reason WaitingForFollowUp");
        var waiting = await run.LoadAsync();
        Assert.Empty(waiting.FollowUp!.ChildRunIds);
        Assert.Equal("daily", Assert.Single(waiting.FollowUp.Reminders).Id);
        Assert.Single(waiting.Events, x => x.Type == "assistant" && x.Text == "Child reply");
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("expired")]
    [InlineData("disconnect")]
    [InlineData("restart")]
    [InlineData("archive")]
    [InlineData("missing-final")]
    [InlineData("failed-commit")]
    public async Task Scheduled_work_keeps_its_reference_when_exact_delivery_cannot_be_established(string stop)
    {
        await using var run = await StartScheduledTurnAsync(supportsScheduledReports: stop != "unsupported");
        var registration = Guid.NewGuid();
        await run.RegisterAsync("pending-report", registration);
        await run.WaitForOutputAsync("type tool_result", "reason Processing");
        if (stop == "restart")
        {
            await run.Worker.StopAsync(run.Token);
            var restarted = new WorkerLifecycle();
            using var worker = new NetclawInvestigationWorker(run.Provider.GetRequiredService<IServiceScopeFactory>(), run.Daemon, run.Clock, restarted);
            await worker.StartAsync(run.Token);
            await restarted.Ready.Task.WaitAsync(run.Token);
            await Assert.ThrowsAsync<InvestigationConflictException>(() => worker.ReconcileAsync(17, run.Id, "operator", run.Token));
            await worker.StopAsync(run.Token);
        }
        else
        {
            await run.ParentAsync();
            if (stop != "unsupported") await run.Lifecycle.FollowUpWaiting.Task.WaitAsync(run.Token);
            if (stop == "expired") run.Clock.Advance(TimeSpan.FromHours(2));
            if (stop == "disconnect") await run.OutputAsync(new { type = "transport_closed" });
            if (stop == "archive")
            {
                await using var db = new OrchestratorDbContext(run.Options);
                await new InvestigationStore(db, run.Clock).ArchiveAsync(17, run.Id, new(run.MessageId, true), "operator", run.Token);
            }
            if (stop == "failed-commit")
            {
                await using var db = new OrchestratorDbContext(run.Options);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION reject_scheduled_terminal() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        IF NEW."Type" = 'turn_completed' AND NEW."CallId" IS NOT NULL THEN
                            RAISE EXCEPTION 'synthetic terminal write failure';
                        END IF;
                        RETURN NEW;
                    END $$;
                    CREATE TRIGGER reject_scheduled_terminal BEFORE INSERT ON "AiInvestigationEvents"
                        FOR EACH ROW EXECUTE FUNCTION reject_scheduled_terminal();
                    """, run.Token);
            }
            await run.TerminalAsync(2, "pending-report", registration, stop == "missing-final" ? null : "Unsafe late reply");
        }
        await run.Daemon.TurnClosed.Reader.ReadAsync(run.Token);
        var saved = await run.LoadAsync();
        Assert.Equal(stop == "archive" ? InvestigationState.Archived : InvestigationState.DeliveryUnknown, saved.Investigation.State);
        Assert.Equal(stop != "restart", saved.FollowUp!.ParentCompleted);
        var pending = Assert.Single(saved.FollowUp.Reminders);
        Assert.Equal("pending-report", pending.Id);
        Assert.Equal(stop != "archive", pending.Unconfirmed);
        if (stop != "archive") Assert.Contains("administrator", saved.Recovery!.Summary);
        if (stop == "unsupported") Assert.Contains("does not provide exact scheduled-result delivery", saved.Recovery!.Summary);
        if (stop == "expired") Assert.Contains("expired", saved.Recovery!.Summary);
        var scheduled = saved.Events.Where(x => x.Trace?.ScheduledReport is not null).ToArray();
        if (stop == "missing-final") Assert.Equal("turn_completed", Assert.Single(scheduled).Type);
        else Assert.Empty(scheduled);
        Assert.DoesNotContain(saved.Events, x => x.Text == "Unsafe late reply");
        Assert.Equal(1, run.Daemon.Sends);
    }

    private static void AssertSavedReport(InvestigationSnapshotDto snapshot, int nativeTurn, Guid messageId, Guid registrationId)
    {
        var events = snapshot.Events.Where(x => x.Trace?.ScheduledReport?.TurnNumber == nativeTurn).ToArray();
        Assert.Equal(new[] { "assistant", "turn_completed" }, events.Select(x => x.Type));
        Assert.All(events, x => Assert.Equal(messageId, x.Trace!.TurnMessageId));
        Assert.All(events, x => Assert.Equal(registrationId, x.Trace!.ScheduledReport!.RegistrationId));
    }

    private async Task<ScheduledTurn> StartScheduledTurnAsync(bool supportsScheduledReports = true)
    {
        var options = await DatabaseAsync();
        await using var source = new OrchestratorDbContext(options);
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(source.Database.GetConnectionString()).Options;
        var daemon = new Daemon { TextAdmissionVersion = 1, ScheduledReportVersion = supportsScheduledReports ? 1 : 0 };
        var clock = new RecoveryClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(options); services.AddScoped<OrchestratorDbContext>();
        services.AddSingleton(identityOptions); services.AddScoped<NetRatelIdentityDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<INetclawPairingService, Pairing>();
        services.AddSingleton<IAiAssistantChatClientFactory>(daemon);
        services.AddScoped<NetclawProfileService>(); services.AddScoped<InvestigationStore>();
        services.AddScoped<NetclawAuthority>(); services.AddScoped<IEffectiveAccessService, ContextAccess>();
        var provider = services.BuildServiceProvider();
        var messageId = Guid.NewGuid();
        Guid id;
        await using (var scope = provider.CreateAsyncScope())
        {
            var scoped = scope.ServiceProvider;
            var identity = scoped.GetRequiredService<NetRatelIdentityDbContext>();
            await identity.Database.MigrateAsync();
            identity.ApplicationPrincipals.Add(new() { Id = "operator", ExternalIssuer = "https://issuer.example.test", ExternalSubject = "scheduled-report-operator" });
            await identity.SaveChangesAsync();
            await scoped.GetRequiredService<NetclawProfileService>().PairAsync(17, new("https://netclaw.example", "test-code"), "operator", default);
            var store = scoped.GetRequiredService<InvestigationStore>();
            id = (await store.CreateChatAsync(17, new(Guid.NewGuid(), "Scheduled report"), "operator", default)).Id;
            await store.AcceptMessageAsync(17, id, new(messageId, "Schedule harmless reports"), "operator", null, false, default);
        }
        var lifecycle = new WorkerLifecycle();
        var worker = new NetclawInvestigationWorker(provider.GetRequiredService<IServiceScopeFactory>(), daemon, clock, lifecycle);
        var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await worker.StartAsync(deadline.Token);
            var sent = await daemon.Sent.Reader.ReadAsync(deadline.Token);
            return new(options, clock, daemon, provider, worker, lifecycle, id, messageId, sent.SessionId, deadline);
        }
        catch
        {
            deadline.Dispose();
            await worker.StopAsync(default); worker.Dispose(); await provider.DisposeAsync();
            throw;
        }
    }

    private sealed record ScheduledTurn(DbContextOptions<OrchestratorDbContext> Options, RecoveryClock Clock, Daemon Daemon,
        ServiceProvider Provider, NetclawInvestigationWorker Worker, WorkerLifecycle Lifecycle, Guid Id, Guid MessageId,
        string SessionId, CancellationTokenSource Deadline) : IAsyncDisposable
    {
        public CancellationToken Token => Deadline.Token;
        public Task OutputAsync(object output) => Daemon.Output!(JsonSerializer.SerializeToElement(output));

        public Task RegisterAsync(string id, Guid registrationId, bool recurring = false, int turn = 1) => OutputAsync(new
        {
            type = "tool_result", sessionId = SessionId, toolName = "set_reminder", turnNumber = turn,
            result = $"Reminder 'Fixture' scheduled. Runs once. Next execution: soon. Delivery: reply to this session. ID: {id}",
            scheduledReminder = new { id, messageId = MessageId, registrationId, recurring }
        });

        public async Task ParentAsync(string[]? children = null)
        {
            await OutputAsync(new { type = "text", sessionId = SessionId, text = "Parent ack" });
            await OutputAsync(new { type = "turn_completed", sessionId = SessionId, turnNumber = 1, turnOutcome = "completed",
                openChildRunIds = children ?? [] });
        }

        public Task TerminalAsync(int turn, string id, Guid registrationId, string? finalText, string? sessionId = null, Guid? messageId = null) => OutputAsync(new
        {
            type = "turn_completed", sessionId = sessionId ?? SessionId, turnNumber = turn, turnOutcome = "completed",
            openChildRunIds = Array.Empty<string>(), sourceReminderId = $"{id}:1795007000000",
            scheduledReport = new { messageId = messageId ?? MessageId, registrationId, finalText }
        });

        public async Task WaitForOutputAsync(string detail, string reason)
        {
            string diagnostic;
            do { diagnostic = await Lifecycle.OutputDiagnostics.Reader.ReadAsync(Token); }
            while (!diagnostic.Contains(detail, StringComparison.Ordinal) || !diagnostic.EndsWith(reason, StringComparison.Ordinal));
        }

        public async Task<InvestigationSnapshotDto> LoadAsync()
        {
            await using var db = new OrchestratorDbContext(Options);
            return await new InvestigationStore(db, Clock).LoadAsync(17, Id, 0, Token);
        }

        public async ValueTask DisposeAsync()
        {
            Deadline.Dispose();
            await Worker.StopAsync(default); Worker.Dispose(); await Provider.DisposeAsync();
        }
    }
}
