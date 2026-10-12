using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Netclaw;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class NetclawPersistencePostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Migration_preserves_tenant_context_and_message_identity_across_scopes_and_rejects_stale_approvals()
    {
        var options = await DatabaseAsync(); var agent = Guid.NewGuid(); var retired = Guid.NewGuid();
        var request = new InvestigationCreateRequest(Guid.NewGuid(), "Investigate worker health", [agent]);
        var message = new InvestigationMessageRequest(Guid.NewGuid(), "Find the cause without changing systems."); Guid id;
        await using (var db = new OrchestratorDbContext(options))
        {
            db.Agents.AddRange(new Agent { Id = agent, TenantId = 17, Name = "linux-worker", Tags = ["production"], CreatedAtUtc = DateTimeOffset.UtcNow },
                new Agent { Id = retired, TenantId = 17, Name = "retired", CreatedAtUtc = DateTimeOffset.UtcNow, DeletedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(); var store = new InvestigationStore(db, TimeProvider.System);
            Assert.Single(await store.ClientsAsync(17, null, default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.CreateAsync(17, request with { RequestId = Guid.NewGuid(), AgentIds = [retired] }, "operator", default));
            id = (await store.CreateAsync(17, request, "operator", default)).Id;
            Assert.True(await store.AcceptMessageAsync(17, id, message, "operator", null, false, default));
        }
        await using (var db = new OrchestratorDbContext(options))
        {
            var store = new InvestigationStore(db, TimeProvider.System);
            Assert.Equal(id, (await store.CreateAsync(17, request, "operator", default)).Id);
            Assert.False(await store.AcceptMessageAsync(17, id, message, "operator", null, false, default));
            await Assert.ThrowsAsync<InvestigationConflictException>(() => store.AcceptMessageAsync(17, id, message with { Text = "Changed request" }, "operator", null, false, default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.LoadAsync(18, id, 0, default));
            var snapshot = await store.LoadAsync(17, id, 0, default);
            Assert.Equal(InvestigationState.Queued, snapshot.Investigation.State); Assert.Equal(message.MessageId, snapshot.ActiveMessageId);
            Assert.Single(snapshot.Events, item => item.Type == "operator"); Assert.Equal("production", Assert.Single(snapshot.Clients).Tags.Single());
            var row = await db.AiInvestigations.SingleAsync(x => x.Id == id); row.State = InvestigationState.AwaitingApproval;
            var optionsJson = JsonSerializer.Serialize(new[] { new InvestigationOptionDto("deny", "Deny"), new InvestigationOptionDto("approve_once", "Allow once") });
            db.AiInvestigationApprovals.AddRange(new() { InvestigationId = id, MessageId = Guid.NewGuid(), CallId = "old-turn", OptionsJson = optionsJson },
                new() { InvestigationId = id, MessageId = message.MessageId, CallId = "current-turn", OptionsJson = optionsJson });
            await db.SaveChangesAsync();
            Assert.Equal("current-turn", Assert.Single((await store.LoadAsync(17, id, 0, default)).PendingApprovals).CallId);
            await Assert.ThrowsAsync<InvestigationConflictException>(() => store.AcceptApprovalAsync(17, id, "old-turn", "approve_once", "operator", default));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AcceptApprovalAsync(17, id, "current-turn", "approve_everywhere", "operator", default));
            Assert.Equal(message.MessageId, await store.AcceptApprovalAsync(17, id, "current-turn", "deny", "operator", default));
            await Assert.ThrowsAsync<InvestigationConflictException>(() => store.AcceptApprovalAsync(17, id, "current-turn", "deny", "operator", default));
            await Assert.ThrowsAsync<InvestigationConflictException>(() => store.ArchiveAsync(17, id, new(Guid.NewGuid(), true), "operator", default));
            await store.ArchiveAsync(17, id, new(message.MessageId, true), "operator", default);
        }
        await using var check = new OrchestratorDbContext(options);
        var final = await new InvestigationStore(check, TimeProvider.System).LoadAsync(17, id, 0, default);
        Assert.Equal(InvestigationState.Archived, final.Investigation.State); Assert.Empty(final.PendingApprovals);
        Assert.Single(final.Events, item => item.Type == "approval_response");
        Assert.Equal(final.Events.Select(x => x.Sequence).Distinct().Count(), final.Events.Count);
    }

    [Fact]
    public async Task Pair_commits_protected_device_token_before_a_failed_probe_and_stale_pair_does_not_consume_another_code()
    {
        var options = await DatabaseAsync(); var protection = new EphemeralDataProtectionProvider(); var pairing = new Pairing(); var daemon = new Daemon { FailConnection = true };
        await using var db = new OrchestratorDbContext(options);
        var profiles = new NetclawProfileService(db, protection, pairing, daemon, TimeProvider.System);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => profiles.PairAsync(18, new("https://netclaw.example", "unused-test-code"), "operator", default));
        Assert.Equal(0, pairing.Calls);
        var result = await profiles.PairAsync(17, new("https://netclaw.example", "single-use-test-code"), "operator", default);
        Assert.False(result.Success); Assert.True(result.Connection.HasDeviceToken); Assert.Equal(1, result.Connection.Revision);
        Assert.DoesNotContain(Pairing.Token, JsonSerializer.Serialize(result));
        var persisted = await db.NetclawConnections.AsNoTracking().SingleAsync();
        Assert.Equal(17, persisted.TenantId); Assert.DoesNotContain(Pairing.Token, persisted.ProtectedDeviceToken);
        await Assert.ThrowsAsync<InvestigationConflictException>(() => profiles.PairAsync(17, new("https://netclaw.example", "unused-test-code", 0), "operator", default));
        Assert.Equal(1, pairing.Calls);
        await using var restartedDb = new OrchestratorDbContext(options);
        var restarted = new NetclawProfileService(restartedDb, protection, pairing, daemon, TimeProvider.System);
        Assert.Equal(Pairing.Token, (await restarted.ResolveAsync(17))!.DeviceToken);
        Assert.False((await restarted.GetAsync(18)).HasDeviceToken);
        await restarted.SetEnabledAsync(17, new(1, false), default); Assert.Null(await restarted.ResolveAsync(17));
    }

    [Fact]
    public async Task Restart_marks_processing_unknown_and_explicit_history_recovery_never_resends_the_operator_message()
    {
        var options = await DatabaseAsync(); var daemon = new Daemon(); var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(options); services.AddScoped<OrchestratorDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<INetclawPairingService, Pairing>(); services.AddSingleton<IAiAssistantChatClientFactory>(daemon);
        services.AddScoped<NetclawProfileService>(); services.AddScoped<InvestigationStore>();
        await using var provider = services.BuildServiceProvider(); var id = Guid.NewGuid(); var messageId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var profile = await scope.ServiceProvider.GetRequiredService<NetclawProfileService>().PairAsync(17, new("https://netclaw.example", "test-code"), "operator", default);
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = new AiInvestigationRecord { Id = id, TenantId = 17, RequestId = Guid.NewGuid(), Title = "Recover existing turn", CreatedBy = "operator", State = InvestigationState.Processing,
                ActiveMessageId = messageId, RemoteSessionId = "saved-session", ProfileFingerprint = NetclawProfileService.Fingerprint(17, "https://netclaw.example/hub/session"), LastActivityUtc = DateTimeOffset.UtcNow };
            db.Add(row); InvestigationStore.Append(db, row, "operator", "Investigate", "operator", messageId); await db.SaveChangesAsync();
            Assert.True(profile.Success);
        }
        daemon.Marker = NetclawInvestigationWorker.MessageMarker(id, messageId);
        var lifecycle = new WorkerLifecycle();
        using var worker = new NetclawInvestigationWorker(provider.GetRequiredService<IServiceScopeFactory>(), daemon, TimeProvider.System, lifecycle);
        await worker.StartAsync(default);
        try
        {
            await lifecycle.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var persisted = new OrchestratorDbContext(options))
                Assert.Equal(InvestigationState.DeliveryUnknown, (await persisted.AiInvestigations.AsNoTracking().SingleAsync(x => x.Id == id)).State);
            await worker.ReconcileAsync(17, id, "operator", default);
            Assert.Equal(0, daemon.Sends); Assert.Contains("saved-session", daemon.Joins);
            await using (var scope = provider.CreateAsyncScope())
            {
                var snapshot = await scope.ServiceProvider.GetRequiredService<InvestigationStore>().LoadAsync(17, id, 0, default);
                Assert.Equal(InvestigationState.DeliveryUnknown, snapshot.Investigation.State);
                Assert.Single(snapshot.Events, item => item.Type == "recovery_admission_confirmed");
            }
            await daemon.Output!(JsonSerializer.SerializeToElement(new { type = "turn_completed", sessionId = "saved-session" }));
            await daemon.ResumedSessionClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var completed = new OrchestratorDbContext(options);
            Assert.Equal(InvestigationState.Idle, (await completed.AiInvestigations.AsNoTracking().SingleAsync(x => x.Id == id)).State);
            Assert.Equal(0, daemon.Sends);
        }
        finally { await worker.StopAsync(default); }
    }

    private async Task<DbContextOptions<OrchestratorDbContext>> DatabaseAsync()
    {
        var connection = await fixture.CreateDatabaseAsync(); var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        await using var db = new OrchestratorDbContext(options); Assert.False(db.Database.HasPendingModelChanges()); await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = 17, Name = "Netclaw fixture" }); await db.SaveChangesAsync(); return options;
    }
    private sealed class WorkerLifecycle : ILogger<NetclawInvestigationWorker>
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "TransportOwnershipAcquired") Ready.TrySetResult();
            else if (logLevel >= LogLevel.Warning) Ready.TrySetException(new InvalidOperationException(formatter(state, exception)));
        }
    }
    private sealed class Pairing : INetclawPairingService
    {
        public const string Token = "synthetic-netclaw-device-token"; public int Calls;
        public Task<string> ExchangeCodeAsync(NetclawPairingTarget target, string pairingCode, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Token); }
    }
    private sealed class Daemon : IAiAssistantChatClientFactory
    {
        public bool FailConnection; public int Sends; public string Marker = "";
        public List<string?> Joins { get; } = []; public Func<JsonElement, Task>? Output;
        public TaskCompletionSource ResumedSessionClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAiAssistantChatClient Create(NetclawRuntimeProfile profile) => new Client(this);
        private sealed class Client(Daemon daemon) : IAiAssistantChatClient
        {
            public bool IsConnected { get; private set; }
            private bool resumedSession;
            public async Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken ct)
            {
                if (daemon.FailConnection) throw new HttpRequestException("Synthetic probe failure.");
                IsConnected = true; resumedSession = sessionId is not null; daemon.Joins.Add(sessionId); daemon.Output = output;
                if (sessionId is not null) await output(JsonSerializer.SerializeToElement(new { type = "session_joined", sessionId, recentMessages = new[] { new { role = "user", content = daemon.Marker + "Investigate" } } }));
                return new(sessionId ?? "probe-session", sessionId is null);
            }
            public Task SendAsync(string sessionId, string text, CancellationToken ct) { daemon.Sends++; return Task.CompletedTask; }
            public Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct) => throw new InvalidOperationException("Recovery must not submit an approval.");
            public ValueTask DisposeAsync() { IsConnected = false; if (resumedSession) daemon.ResumedSessionClosed.TrySetResult(); return ValueTask.CompletedTask; }
        }
    }
}
