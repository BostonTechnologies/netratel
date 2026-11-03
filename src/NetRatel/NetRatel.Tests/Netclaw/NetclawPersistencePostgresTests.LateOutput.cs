using System.Runtime.CompilerServices;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Healthy_silent_turn_retains_receiver_and_persists_exact_late_answer_once(bool deliveryUnknown)
    {
        var options = await DatabaseAsync(); var daemon = new Daemon { RemoteTurnCount = 0, TextAdmissionVersion = 1 };
        var clock = new RecoveryClock(); var services = new ServiceCollection();
        await using var source = new OrchestratorDbContext(options);
        services.AddLogging(); services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(options); services.AddScoped<OrchestratorDbContext>();
        services.AddSingleton(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(source.Database.GetConnectionString()).Options);
        services.AddScoped<NetRatelIdentityDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<INetclawPairingService, Pairing>(); services.AddSingleton<IAiAssistantChatClientFactory>(daemon);
        services.AddScoped<NetclawProfileService>(); services.AddScoped<InvestigationStore>();
        services.AddScoped<NetclawAuthority>(); services.AddSingleton<IEffectiveAccessService>(new ContextAccess());
        await using var provider = services.BuildServiceProvider(); Guid id; var message = Guid.NewGuid(); NetclawConnectionDto connection;
        await using (var scope = provider.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            await identity.Database.MigrateAsync(); identity.ApplicationPrincipals.Add(new() { Id = "operator" }); await identity.SaveChangesAsync();
            connection = (await scope.ServiceProvider.GetRequiredService<NetclawProfileService>().PairAsync(17, new("https://netclaw.example", "test-code"), "operator", default)).Connection;
            var store = scope.ServiceProvider.GetRequiredService<InvestigationStore>();
            id = (await store.CreateChatAsync(17, new(Guid.NewGuid(), "Long model call"), "operator", default)).Id;
            await store.AcceptMessageAsync(17, id, new(message, "Read the monitoring series."), "operator", null, false, default);
        }
        var lifecycle = new WorkerLifecycle();
        using var worker = new NetclawInvestigationWorker(provider.GetRequiredService<IServiceScopeFactory>(), daemon, clock, lifecycle);
        await worker.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await lifecycle.Ready.Task.WaitAsync(deadline.Token);
            var sent = await daemon.Sent.Reader.ReadAsync(deadline.Token);
            clock.Advance(TimeSpan.FromMinutes(8));
            await WorkerTickAsync(worker, deadline.Token);
            await using (var db = new OrchestratorDbContext(options))
            {
                var row = await db.AiInvestigations.SingleAsync(x => x.Id == id);
                Assert.Equal(InvestigationState.Processing, row.State);
                Assert.Single(worker.ConnectionDiagnostics(connection).Sessions);
            }
            if (deliveryUnknown) await daemon.Output!(JsonSerializer.SerializeToElement(new { type = "error", sessionId = sent.SessionId }));
            await daemon.Output!(JsonSerializer.SerializeToElement(new { type = "text", sessionId = "another-session", text = "Wrong session" }));
            await daemon.Output!(JsonSerializer.SerializeToElement(new { type = "text", sessionId = sent.SessionId, turnNumber = 2, text = "Later turn" }));
            await daemon.Output!(JsonSerializer.SerializeToElement(new { type = "turn_completed", sessionId = sent.SessionId, turnNumber = 2, turnOutcome = "completed" }));
            await daemon.Output!(JsonSerializer.SerializeToElement(new { type = "text_delta", sessionId = sent.SessionId, turnNumber = 2, text = "Wrong later draft" }));
            await daemon.Output!(JsonSerializer.SerializeToElement(new { type = deliveryUnknown ? "text_delta" : "text", sessionId = sent.SessionId, turnNumber = 1, text = "Monitoring denied; no operational check succeeded." }));
            var terminal = JsonSerializer.SerializeToElement(new { type = "turn_completed", sessionId = sent.SessionId, turnNumber = 1, turnOutcome = "completed" });
            await daemon.Output!(terminal); await daemon.Output!(terminal);
            await daemon.TurnClosed.Reader.ReadAsync(deadline.Token);
            await using var check = new OrchestratorDbContext(options);
            var snapshot = await new InvestigationStore(check, clock).LoadAsync(17, id, 0, default);
            Assert.Equal(InvestigationState.Idle, snapshot.Investigation.State);
            Assert.Equal(message, snapshot.ActiveMessageId);
            Assert.Equal("Monitoring denied; no operational check succeeded.", Assert.Single(snapshot.Events, x => x.Type == "assistant").Text);
            Assert.Single(snapshot.Events, x => x.Type == "turn_completed");
            Assert.DoesNotContain(snapshot.Events, x => x.Text is "Wrong session" or "Later turn");
            Assert.Equal(1, daemon.Sends);
        }
        finally { await worker.StopAsync(default); }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TickAsync")]
    private static extern Task WorkerTickAsync(NetclawInvestigationWorker worker, CancellationToken ct);
}
