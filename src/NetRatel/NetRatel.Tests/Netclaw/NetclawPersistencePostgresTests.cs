using System.Text.Json;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Netclaw;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class NetclawPersistencePostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Worker_sends_and_records_exact_canonical_context_on_first_and_later_turns_with_saved_guidance()
    {
        var options = await DatabaseAsync(); var daemon = new Daemon(); var services = new ServiceCollection();
        await using var connectionSource = new OrchestratorDbContext(options);
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionSource.Database.GetConnectionString()).Options;
        services.AddLogging(); services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(options); services.AddScoped<OrchestratorDbContext>();
        services.AddSingleton(identityOptions); services.AddScoped<NetRatelIdentityDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<INetclawPairingService, Pairing>(); services.AddSingleton<IAiAssistantChatClientFactory>(daemon);
        services.AddScoped<NetclawProfileService>(); services.AddScoped<InvestigationStore>();
        services.AddScoped<NetclawAuthority>(); services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider(); var agent = Guid.NewGuid(); Guid id; Guid promptId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            await identity.Database.MigrateAsync();
            identity.ApplicationPrincipals.Add(new() { Id = "operator", ExternalIssuer = "https://issuer.example.test", ExternalSubject = "sso-operator" });
            await identity.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sso = new ClaimsPrincipal(new ClaimsIdentity([new("iss", "https://issuer.example.test"), new("sub", "sso-operator"),
                new("roles", "Operator"), new("iat", (now - 60).ToString()), new("exp", (now + 3600).ToString())], "Oidc"));
            Assert.Equal("operator", await new ApplicationPrincipalResolver(identity).ResolveExternalAsync(sso));
            Assert.Empty(await identity.PrincipalRoleAssignments.ToListAsync());
            Assert.True(await scope.ServiceProvider.GetRequiredService<NetclawAuthority>().CanInvokeAsync(17, "operator", null, false, default));
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Agents.Add(new Agent { Id = agent, TenantId = 17, Name = agent.ToString(), FriendlyName = "Friendly worker",
                Tags = ["production"], CreatedAtUtc = DateTimeOffset.UtcNow, LastSeenUtc = DateTimeOffset.UtcNow,
                DeviceInfoJson = "{\"hostName\":\"real-worker\",\"os\":\"Linux\",\"architecture\":\"arm64\",\"reportedAddress\":\"10.0.0.17\"}" });
            await db.SaveChangesAsync();
            var library = new PromptLibraryService(db, TimeProvider.System);
            var guide = await library.CreateAsync(17, new("Tenant guide", "Saved guidance", "Original tenant guidance."), "operator");
            promptId = guide.Summary.Id; await library.SetDefaultAsync(17, new(promptId, 0, 1));
            await scope.ServiceProvider.GetRequiredService<NetclawProfileService>().PairAsync(17, new("https://netclaw.example", "test-code"), "operator", default);
            var store = scope.ServiceProvider.GetRequiredService<InvestigationStore>();
            var row = await store.CreateAsync(17, new(Guid.NewGuid(), "Canonical context", [agent]), "operator", default);
            id = row.Id; Assert.Equal(0, daemon.Sends);
            var picker = Assert.Single(await store.ClientsAsync(17, [agent], default));
            Assert.Equal("real-worker", picker.HostName); Assert.Equal("10.0.0.17", picker.PrimaryIpAddress);
            await store.AcceptMessageAsync(17, id, new(Guid.NewGuid(), "First question"), "operator", null, false, default);
        }
        var lifecycle = new WorkerLifecycle();
        using var worker = new NetclawInvestigationWorker(provider.GetRequiredService<IServiceScopeFactory>(), daemon, TimeProvider.System, lifecycle);
        await worker.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await lifecycle.Ready.Task.WaitAsync(deadline.Token);
            var first = await daemon.Sent.Reader.ReadAsync(deadline.Token);
            await using (var db = new OrchestratorDbContext(options))
            {
                var saved = Assert.Single(await db.Set<AiInvestigationContextRecord>().AsNoTracking().ToListAsync());
                Assert.Equal(first.Text, saved.Request); Assert.NotNull(saved.SendAttemptedAtUtc);
                Assert.Equal(saved.PayloadBytes, JsonSerializer.SerializeToUtf8Bytes(new[] { first.SessionId, first.Text }).Length);
                Assert.Contains("Netclaw fixture", first.Text); Assert.Contains("real-worker", first.Text);
                Assert.Contains("10.0.0.17", first.Text); Assert.Contains("Original tenant guidance", first.Text);
            }
            await daemon.Output!(JsonSerializer.SerializeToElement(new { type = "turn_completed", sessionId = first.SessionId }));
            await daemon.TurnClosed.Reader.ReadAsync(deadline.Token);
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var record = await db.Agents.SingleAsync(x => x.Id == agent);
                record.DeviceInfoJson = "{\"hostName\":\"real-worker\",\"os\":\"Linux\",\"architecture\":\"arm64\",\"reportedAddress\":\"10.0.0.18\"}";
                record.LastSeenUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
                await new PromptLibraryService(db, TimeProvider.System).UpdateAsync(17, promptId, new("Edited guide", null, "New default for future sessions.", 1), "operator");
                await scope.ServiceProvider.GetRequiredService<InvestigationStore>().AcceptMessageAsync(17, id,
                    new(Guid.NewGuid(), "Later question"), "operator", null, false, default);
            }
            var later = await daemon.Sent.Reader.ReadAsync(deadline.Token);
            Assert.Contains("10.0.0.18", later.Text); Assert.Contains("Original tenant guidance", later.Text);
            Assert.DoesNotContain("New default for future sessions", later.Text);
            Assert.Equal(first.SessionId, later.SessionId);
            await using var check = new OrchestratorDbContext(options);
            var contexts = await check.Set<AiInvestigationContextRecord>().AsNoTracking().OrderBy(x => x.PreparedAtUtc).ToListAsync();
            Assert.Equal(2, contexts.Count); Assert.Equal(first.Text, contexts[0].Request); Assert.Equal(later.Text, contexts[1].Request);
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task Paged_system_selection_and_authorized_search_reach_the_estate_beyond_the_initial_rows()
    {
        var options = await DatabaseAsync();
        await using var db = new OrchestratorDbContext(options);
        var target = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        db.Agents.AddRange(Enumerable.Range(1, 501).Select(index => new Agent
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{index:D12}"), TenantId = 17, CreatedAtUtc = DateTimeOffset.UtcNow,
            DeviceInfoJson = "{\"hostName\":\"other-worker\",\"os\":\"Linux\"}"
        }));
        db.Agents.Add(new Agent { Id = target, TenantId = 17, Name = target.ToString(), FriendlyName = "Late estate worker",
            Tags = ["database"], CreatedAtUtc = DateTimeOffset.UtcNow,
            DeviceInfoJson = "{\"hostName\":\"last-fleet-\\u0430\",\"reportedAddress\":\"10.20.30.40\"}" });
        await db.SaveChangesAsync();
        var store = new InvestigationStore(db, TimeProvider.System);
        var seen = new List<Guid>(); int? offset = 0;
        while (offset is { } current)
        {
            var page = await store.SearchClientsAsync(17, null, current, default);
            Assert.InRange(page.Items.Count, 0, 100); seen.AddRange(page.Items.Select(x => x.AgentId)); offset = page.NextOffset;
        }
        Assert.Equal(502, seen.Count); Assert.Contains(target, seen);
        foreach (var term in new[] { "last-fleet-а", "10.20.30.40", "Late estate", "database" })
            Assert.Equal(target, Assert.Single((await store.SearchClientsAsync(17, term, 0, default)).Items).AgentId);
        Assert.Empty((await store.SearchClientsAsync(17, "not-present", 0, default)).Items);
    }

    [Fact]
    public async Task Investigation_pins_default_and_ordered_optional_revisions_and_retries_keep_the_exact_snapshot()
    {
        var options = await DatabaseAsync(); var agent = Guid.NewGuid();
        await using var db = new OrchestratorDbContext(options);
        db.Tenants.Add(new Tenant { Id = 18, Name = "Other tenant" });
        db.Agents.Add(new Agent { Id = agent, TenantId = 17, Name = agent.ToString(), CreatedAtUtc = DateTimeOffset.UtcNow,
            DeviceInfoJson = "{\"hostName\":\"real-worker\",\"os\":\"Linux\",\"architecture\":\"x64\",\"reportedAddress\":\"10.0.0.17\"}" });
        await db.SaveChangesAsync();
        var library = new PromptLibraryService(db, TimeProvider.System);
        var guide = await library.CreateAsync(17, new("Tenant guide", null, "Original tenant default."), "owner");
        var first = await library.CreateAsync(17, new("First optional", "First selected", "Original optional first."), "owner");
        var second = await library.CreateAsync(17, new("Second optional", null, "Original optional second."), "owner");
        var foreign = await library.CreateAsync(18, new("Foreign", null, "Foreign guidance."), "owner");
        await library.SetDefaultAsync(17, new(guide.Summary.Id, 0, guide.Summary.Revision));
        var refs = new[] { new AiPromptReference(second.Summary.Id, 1), new AiPromptReference(first.Summary.Id, 1), new AiPromptReference(guide.Summary.Id, 1) };
        var create = new InvestigationCreateRequest(Guid.NewGuid(), "Evidence", [agent], SelectedPrompts: refs);
        var store = new InvestigationStore(db, TimeProvider.System, library);
        var row = await store.CreateAsync(17, create, "owner", default);
        var saved = InvestigationContextComposer.Prompts(row).ToArray();
        Assert.Equal([guide.Summary.Id, second.Summary.Id, first.Summary.Id], saved.Select(x => x.PromptId));
        Assert.True(saved[0].IsDefault); Assert.Equal(3, saved.Length);
        await library.UpdateAsync(17, guide.Summary.Id, new("Edited guide", null, "Edited default body.", 1), "owner");
        await library.UpdateAsync(17, first.Summary.Id, new("Edited optional", null, "Edited optional body.", 1), "owner");
        db.ChangeTracker.Clear();
        var retried = await store.CreateAsync(17, create, "owner", default);
        Assert.Equal(row.Id, retried.Id); Assert.Equal(saved, InvestigationContextComposer.Prompts(retried));
        await Assert.ThrowsAsync<InvestigationConflictException>(() => store.CreateAsync(17, create with { SelectedPrompts = refs.Reverse().ToArray() }, "owner", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.CreateAsync(17, create with { RequestId = Guid.NewGuid(), SelectedPrompts = [new(foreign.Summary.Id, 1)] }, "owner", default));
        var later = await store.ComposeAsync(retried, Guid.NewGuid(), "Later question", "session", default);
        Assert.Contains("Original tenant default", later.Request); Assert.Contains("Original optional first", later.Request);
        Assert.DoesNotContain("Edited default body", later.Request); Assert.DoesNotContain("Edited optional body", later.Request);
        var newSession = await store.CreateAsync(17, create with { RequestId = Guid.NewGuid(), SelectedPrompts = [new(guide.Summary.Id, 1)] }, "owner", default);
        var differentRevisions = InvestigationContextComposer.Prompts(newSession);
        Assert.Equal([2L, 1L], differentRevisions.Select(x => x.Revision));
        Assert.True(differentRevisions[0].IsDefault); Assert.False(differentRevisions[1].IsDefault);
        var tooLarge = new InvestigationMessageRequest(Guid.NewGuid(), new string('界', 8000));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcceptMessageAsync(17, newSession.Id, tooLarge, "owner", null, false, default));
        Assert.Equal(InvestigationState.Idle, (await store.LoadAsync(17, newSession.Id, 0, default)).Investigation.State);
        Assert.False(await db.AiInvestigationEvents.AnyAsync(x => x.MessageId == tooLarge.MessageId));
        var currentClient = Assert.Single(await store.ClientsAsync(17, [agent], default));
        Assert.Equal("real-worker", currentClient.HostName); Assert.Equal("10.0.0.17", currentClient.PrimaryIpAddress);
        Assert.Single((await store.SearchClientsAsync(17, "10.0.0.17", 0, default)).Items);
        Assert.Empty((await store.SearchClientsAsync(18, "real-worker", 0, default)).Items);
    }

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
        NetclawConnectionDto connection;
        await using (var scope = provider.CreateAsyncScope())
        {
            var profile = await scope.ServiceProvider.GetRequiredService<NetclawProfileService>().PairAsync(17, new("https://netclaw.example", "test-code"), "operator", default);
            connection = profile.Connection;
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = new AiInvestigationRecord { Id = id, TenantId = 17, RequestId = Guid.NewGuid(), Title = "Recover existing turn", CreatedBy = "operator", State = InvestigationState.Processing,
                ActiveMessageId = messageId, RemoteSessionId = "saved-session", ProfileFingerprint = NetclawProfileService.Fingerprint(17, "https://netclaw.example/hub/session"), LastActivityUtc = DateTimeOffset.UtcNow };
            db.Add(row); InvestigationStore.Append(db, row, "operator", "Investigate", "operator", messageId); await db.SaveChangesAsync();
            Assert.True(profile.Success);
        }
        daemon.Marker = NetclawInvestigationWorker.MessageMarker(id, messageId);
        var lifecycle = new WorkerLifecycle();
        using var worker = new NetclawInvestigationWorker(provider.GetRequiredService<IServiceScopeFactory>(), daemon, TimeProvider.System, lifecycle);
        Assert.False(worker.ConnectionDiagnostics(connection).ObservationAvailable);
        await worker.StartAsync(default);
        try
        {
            await lifecycle.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var persisted = new OrchestratorDbContext(options))
                Assert.Equal(InvestigationState.DeliveryUnknown, (await persisted.AiInvestigations.AsNoTracking().SingleAsync(x => x.Id == id)).State);
            await worker.ReconcileAsync(17, id, "operator", default);
            Assert.Equal(0, daemon.Sends); Assert.Contains("saved-session", daemon.Joins);
            var joins = daemon.Joins.Count;
            var observation = worker.ConnectionDiagnostics(connection);
            Assert.True(observation.ObservationAvailable);
            var session = Assert.Single(observation.Sessions);
            Assert.Equal(id, session.InvestigationId); Assert.Equal(NetclawSignalRState.Connected, session.SignalRState);
            Assert.True(session.AuthenticatedAtUtc <= observation.ObservedAtUtc);
            Assert.True(session.LastActivityUtc <= observation.ObservedAtUtc);
            Assert.Empty(worker.ConnectionDiagnostics(connection with { TenantId = 18 }).Sessions);
            Assert.Empty(worker.ConnectionDiagnostics(connection with { Id = Guid.NewGuid() }).Sessions);
            Assert.Empty(worker.ConnectionDiagnostics(connection with { Revision = connection.Revision + 1 }).Sessions);
            Assert.Empty(worker.ConnectionDiagnostics(connection with { Enabled = false }).Sessions);
            Assert.DoesNotContain(Pairing.Token, JsonSerializer.Serialize(observation));
            Assert.Equal(joins, daemon.Joins.Count); Assert.Equal(0, daemon.Sends);
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
        public Channel<(string SessionId, string Text)> Sent { get; } = Channel.CreateUnbounded<(string SessionId, string Text)>();
        public Channel<bool> TurnClosed { get; } = Channel.CreateUnbounded<bool>();
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
            public Task SendAsync(string sessionId, string text, CancellationToken ct) { daemon.Sends++; daemon.Sent.Writer.TryWrite((sessionId, text)); return Task.CompletedTask; }
            public Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct) => throw new InvalidOperationException("Recovery must not submit an approval.");
            public ValueTask DisposeAsync() { IsConnected = false; if (resumedSession) daemon.ResumedSessionClosed.TrySetResult(); if (daemon.Sends > 0) daemon.TurnClosed.Writer.TryWrite(true); return ValueTask.CompletedTask; }
        }
    }
    private sealed class ContextAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(tenantId == 17);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
