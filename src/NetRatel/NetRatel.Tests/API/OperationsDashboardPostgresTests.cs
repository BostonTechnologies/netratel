using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Services.Dashboard;
using NetRatel.API.Services.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class OperationsDashboardPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Tenant_totals_include_acknowledged_alerts_beyond_the_page_and_presence_obeys_authority_expiry()
    {
        var connection = await fixture.CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var clock = new DashboardClock(now);
        await using var provider = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(clock)
            .AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)))
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection))
            .AddNetRatelClientServicesPersistence().BuildServiceProvider();
        var ids = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToArray();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.MigrateAsync();
            db.Agents.AddRange(ids.Select((id, index) => new Agent { Id = id, TenantId = 1, Name = $"Client {index}", CreatedAtUtc = now,
                DeviceInfoJson = JsonSerializer.Serialize(new { hostName = $"host-{index}", ipAddress = $"192.0.2.{index + 1}" }) }));
            db.Agents.Add(new() { Id = Guid.NewGuid(), TenantId = 1, Name = "Deleted client", CreatedAtUtc = now, DeletedAtUtc = now });
            db.Agents.Add(new() { Id = Guid.NewGuid(), TenantId = 2, Name = "Other tenant", CreatedAtUtc = now });
            for (var index = 0; index < 28; index++) db.MonitoringSeries.Add(Series(1, ids[index], now,
                index == 0 ? MonitoringSeverity.Critical : MonitoringSeverity.Warning,
                index == 27 ? MonitoringPhase.Recovering : MonitoringPhase.Firing,
                acknowledged: index is 0 or 1, suppressed: index == 2, unknown: index == 27));
            db.MonitoringSeries.Add(Series(1, ids[0], now, MonitoringSeverity.Information));
            db.MonitoringSeries.Add(Series(1, ids[28], now, MonitoringSeverity.Warning, MonitoringPhase.Pending, active: false));
            db.MonitoringSeries.Add(Series(1, ids[29], now, MonitoringSeverity.Warning, MonitoringPhase.Healthy, active: false, unknown: true));
            db.MonitoringSeries.Add(Series(2, Guid.NewGuid(), now, MonitoringSeverity.Critical));
            var job = new JobDefinition { Id = 1, TenantId = 1, Name = "Inventory", CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Jobs.Add(job);
            db.JobRuns.AddRange(Enumerable.Range(1, 8).Select(index => new JobRunRecord { Id = index, JobId = job.Id, TenantId = 1,
                AgentId = ids[0], CreatedAtUtc = now.AddSeconds(index), Status = 2 }));
            db.JobRuns.Add(new() { Id = 9, JobId = job.Id, TenantId = 2, AgentId = ids[0], CreatedAtUtc = now.AddMinutes(1), Status = 2 });
            await db.SaveChangesAsync();
        }
        var ownership = provider.GetRequiredService<IClientConnectionEpochStore>();
        for (var index = 0; index < 3; index++)
        {
            var admission = await ownership.ReserveAsync(new(new(1, ids[index]), Guid.NewGuid(), Guid.NewGuid(), 0,
                now, now.AddSeconds(30), now.AddMinutes(index == 2 ? 1 : 10), new("dashboard-fixture", ["presence"], null)), default);
            admission.Disposition.Should().Be(OwnershipDisposition.Accepted);
            var reservation = admission.Reservation!;
            (await ownership.CommitAsync(reservation, new(reservation.Owner, 1, now), default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        var service = new OperationsDashboardService(new TenantAuthorizer(), provider.GetRequiredService<IServiceScopeFactory>(), clock);
        var first = await service.ReadAsync(1, 0, 25, new(), default);
        var second = await service.ReadAsync(1, 1, 25, new(), default);
        first.OnlineClients.Should().Be(2); first.OfflineClients.Should().Be(28);
        first.ActiveOccurrences.Should().Be(29); first.FiringClients.Should().Be(27); first.AlertingClients.Should().Be(28);
        first.PendingSeries.Should().Be(1); first.UnknownSeries.Should().Be(2); first.SuppressedOccurrences.Should().Be(1);
        first.Clients.Should().HaveCount(25); second.Clients.Should().HaveCount(3);
        second.ActiveOccurrences.Should().Be(first.ActiveOccurrences); second.FiringClients.Should().Be(first.FiringClients);
        first.Clients.Select(client => client.AgentId).Intersect(second.Clients.Select(client => client.AgentId)).Should().BeEmpty();
        var highest = first.Clients[0];
        highest.AgentId.Should().Be(ids[0]); highest.HighestSeverity.Should().Be(MonitoringSeverity.Critical);
        highest.ActiveOccurrences.Should().Be(2); highest.AcknowledgedOccurrences.Should().Be(1);
        highest.Identity.DisplayName.Should().Be("Client 0"); highest.Identity.Hostname.Should().Be("host-0");
        first.Clients.Concat(second.Clients).Sum(client => client.ActiveOccurrences).Should().Be(first.ActiveOccurrences);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.MonitoringSeries.Add(Series(1, Guid.Empty, now, MonitoringSeverity.Critical,
                offlineClients: [ids[0], ids[29]]));
            await db.SaveChangesAsync();
        }
        var grouped = await service.ReadAsync(1, 0, 50, new(), default);
        grouped.ActiveOccurrences.Should().Be(first.ActiveOccurrences + 1);
        grouped.FiringClients.Should().Be(first.FiringClients + 1); // Existing alerting client is counted once across both kinds.
        grouped.AlertingClients.Should().Be(first.AlertingClients + 1);
        grouped.Clients.Should().NotContain(client => client.AgentId == Guid.Empty);
        grouped.Clients.Single(client => client.AgentId == ids[0]).ActiveOccurrences.Should().Be(3);
        grouped.Clients.Single(client => client.AgentId == ids[29]).ActiveOccurrences.Should().Be(1);
        var jobs = await new OperationsRecentJobsService(new JobsAccess(), provider.GetRequiredService<IServiceScopeFactory>()).ReadAsync(1, new(), default);
        jobs.Should().HaveCount(6); jobs.Select(job => job.RunId).Should().Equal(8, 7, 6, 5, 4, 3);
        jobs.Should().OnlyContain(job => job.JobName == "Inventory" && job.ClientIdentity!.DisplayName == "Client 0");
    }

    [Fact]
    public async Task Recent_runs_keep_retired_job_names_only_in_the_original_tenant()
    {
        var connection = await fixture.CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        await using var provider = new ServiceCollection()
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection)).BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.MigrateAsync();
            db.Jobs.AddRange(
                new JobDefinition { Id = 1, TenantId = 1, Name = "Retained inventory", CreatedAtUtc = now, UpdatedAtUtc = now },
                new JobDefinition { Id = 2, TenantId = 2, Name = "Private other tenant job", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.JobRuns.AddRange(
                new JobRunRecord { Id = 1, JobId = 1, TenantId = 1, CreatedAtUtc = now, Status = 2 },
                new JobRunRecord { Id = 2, JobId = 2, TenantId = 1, CreatedAtUtc = now.AddSeconds(1), Status = 2 },
                new JobRunRecord { Id = 3, JobId = 1, TenantId = 2, CreatedAtUtc = now.AddSeconds(2), Status = 2 });
            await db.SaveChangesAsync();
            (await new JobDefinitionService(db).DeleteAsync(1)).Should().NotBeNull();
        }

        var rows = await new OperationsRecentJobsService(new JobsAccess(), provider.GetRequiredService<IServiceScopeFactory>())
            .ReadAsync(1, new(), default);
        rows.Select(row => row.RunId).Should().Equal(2, 1);
        rows.Single(row => row.RunId == 1).JobName.Should().Be("Retained inventory");
        rows.Single(row => row.RunId == 2).JobName.Should().Be("Job unavailable");
    }

    [Fact]
    public async Task Unauthorized_tenant_and_unbounded_page_are_rejected_before_database_access()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new OperationsDashboardService(new TenantAuthorizer(), provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var denied = await Assert.ThrowsAsync<MonitoringApiException>(() => service.ReadAsync(2, 0, 25, new(), default));
        denied.StatusCode.Should().Be(403);
        var invalid = await Assert.ThrowsAsync<MonitoringApiException>(() => service.ReadAsync(1, 0, 51, new(), default));
        invalid.StatusCode.Should().Be(400);
        var jobs = new OperationsRecentJobsService(new JobsAccess(), provider.GetRequiredService<IServiceScopeFactory>());
        var jobsDenied = await Assert.ThrowsAsync<MonitoringApiException>(() => jobs.ReadAsync(2, new(), default));
        jobsDenied.StatusCode.Should().Be(403);
    }

    private static MonitoringSeriesRecord Series(int tenantId, Guid agentId, DateTimeOffset now,
        MonitoringSeverity severity, MonitoringPhase phase = MonitoringPhase.Firing, bool acknowledged = false,
        bool suppressed = false, bool unknown = false, bool active = true, IReadOnlyList<Guid>? offlineClients = null)
    {
        var ruleId = Guid.NewGuid(); var occurrenceId = Guid.NewGuid();
        var resource = offlineClients is null ? "cpu" : MonitoringOfflinePresentation.ResourceKey;
        var key = new MonitoringSeriesKey(tenantId, ruleId, agentId, resource);
        var rule = new MonitoringRuleDto(tenantId, ruleId, 1, 1, "Controlled CPU", true, severity,
            new(MonitoringTargetMode.Selected, offlineClients?.ToImmutableArray() ?? [agentId], []), new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 80, 70, null, null, []),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), AlertIfOffline: offlineClients is not null);
        var evidence = new MonitoringEvidenceDto(new(1, 1), Guid.NewGuid(), now, now,
            unknown ? MonitoringEvidenceQuality.Unknown : MonitoringEvidenceQuality.Fresh, MonitoringClassification.Breach, offlineClients?.Count ?? 90, 1, null,
            OfflineSummary: offlineClients is null ? null : new(offlineClients.Count, offlineClients.Count, string.Join(", ", offlineClients), false));
        var occurrence = new MonitoringOccurrenceDto(occurrenceId, Guid.NewGuid(), now, now, rule, evidence,
            MonitoringFlowDispatchDisposition.NoFlowSelected, acknowledged ? Guid.NewGuid() : null, acknowledged ? now : null);
        var state = new MonitoringSeriesState(key, 1, 1, phase, evidence.Quality, LatestEvidence: evidence,
            Occurrence: active ? occurrence : null, Suppressed: suppressed, ApplicableBypassIds: [], OfflineAgentIds: offlineClients?.ToImmutableArray() ?? default);
        return new() { TenantId = tenantId, RuleId = ruleId, AgentId = agentId, ResourceKey = resource, StateRevision = 1,
            Phase = phase, EvidenceQuality = evidence.Quality, ActiveOccurrenceId = active ? occurrenceId : null,
            Acknowledged = acknowledged, Suppressed = suppressed, StateJson = JsonSerializer.Serialize(state), UpdatedAtUtc = now };
    }

    private sealed class DashboardClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class TenantAuthorizer : IMonitoringResourceAuthorizer
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, MonitoringResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(resource.TenantId == 1 && permission == NetRatelPermissions.MonitoringRead);
    }

    private sealed class JobsAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantId == 1 && permission is NetRatelPermissions.MonitoringRead or NetRatelPermissions.JobManagement);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
