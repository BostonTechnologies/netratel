using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints.Search;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class GlobalSearchOpsPostgresTests(PostgreSqlPersistenceFixture postgres, ITestOutputHelper output)
{
    [Fact]
    public async Task Persistent_ops_queries_are_bounded_single_commands_with_tenant_and_alert_permission_fences()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var metrics = new DatabaseCommandMetricsInterceptor();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).AddInterceptors(metrics).Options;
        await using var db = new OrchestratorDbContext(options);
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow; var agentA = Guid.NewGuid(); var agentB = Guid.NewGuid();
        db.Tenants.AddRange(new Tenant { Id = 7, Name = "Operations", CreatedAtUtc = now, UpdatedAtUtc = now }, new Tenant { Id = 8, Name = "Hidden tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
        db.Agents.AddRange(new Agent { Id = agentA, TenantId = 7, Name = "sql-host", FriendlyName = "Database host", Tags = ["critical-system"], DeviceInfoJson = """{"hostName":{"privateValue":"PRIVATE-HOST-OBJECT"},"ipAddress":"10.7.0.4","privateInventory":"PRIVATE-INVENTORY"}""", CreatedAtUtc = now },
            new Agent { Id = agentB, TenantId = 8, Name = "hidden-host", Tags = ["critical-system"], CreatedAtUtc = now });
        for (var index = 0; index < 64; index++)
        {
            var tenant = index % 2 == 0 ? 7 : 8; var agent = tenant == 7 ? agentA : agentB; var occurrence = Guid.NewGuid();
            db.MonitoringOccurrences.Add(new MonitoringOccurrenceRecord { OccurrenceId = occurrence, TenantId = tenant, RuleId = Guid.NewGuid(), AgentId = agent, ResourceKey = "cpu", RaisedEventId = Guid.NewGuid(), RaisedAtUtc = now.AddSeconds(index),
                OccurrenceJson = """{"PinnedRule":{"Name":"CPU alert","Severity":2,"ExecutionCredentialId":"PRIVATE-CREDENTIAL"}}""" });
            db.FlowDefinitions.Add(new FlowDefinitionRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = $"Recovery flow {index}", Enabled = true, DraftJson = "{}", CreatedAtUtc = now, UpdatedAtUtc = now });
            var investigation = new AiInvestigationRecord { TenantId = tenant, RequestId = Guid.NewGuid(), Title = $"System investigation {index}", AgentIds = [agent], OccurrenceId = index % 4 == 0 ? occurrence : null, LastActivityUtc = now };
            db.AiInvestigations.Add(investigation);
            db.AiInvestigationEvents.Add(new AiInvestigationEventRecord { InvestigationId = investigation.Id, Sequence = 1, Type = "assistant", Text = "PRIVATE-TRANSCRIPT", CreatedAtUtc = now });
        }
        await db.SaveChangesAsync();
        metrics.Reset();
        var elapsed = Stopwatch.StartNew();
        var alerts = await GlobalSearchEndpoints.BuildAlertQuery(db, "critical-system", [7]).Take(6).ToListAsync();
        output.WriteLine($"Alerts: {elapsed.Elapsed.TotalMilliseconds:F2} ms, {alerts.Count} rows");
        elapsed.Restart();
        var flows = await GlobalSearchEndpoints.BuildFlowQuery(db, "Recovery", [7]).Take(6).ToListAsync();
        output.WriteLine($"Flows: {elapsed.Elapsed.TotalMilliseconds:F2} ms, {flows.Count} rows");
        elapsed.Restart();
        var investigations = await GlobalSearchEndpoints.BuildInvestigationQuery(db, "10.7.0.4", [7], []).Take(6).ToListAsync();
        output.WriteLine($"Investigations: {elapsed.Elapsed.TotalMilliseconds:F2} ms, {investigations.Count} rows");
        Assert.Equal(3, metrics.Snapshot().CommandCount);
        Assert.Equal(6, alerts.Count); Assert.All(alerts, row => { Assert.Equal(7, row.TenantId); Assert.Equal("Database host", row.ClientName); });
        Assert.Equal(6, flows.Count); Assert.All(flows, row => Assert.Equal(7, row.TenantId));
        Assert.Equal(6, investigations.Count); Assert.All(investigations, row => { Assert.Equal(7, row.TenantId); Assert.Null(row.OccurrenceId); });
        Assert.Empty(await GlobalSearchEndpoints.BuildInvestigationQuery(db, "PRIVATE-TRANSCRIPT", [7], [7]).Take(6).ToListAsync());
        Assert.Empty(await GlobalSearchEndpoints.BuildAlertQuery(db, "PRIVATE-CREDENTIAL", [7]).Take(6).ToListAsync());
        Assert.Empty(await GlobalSearchEndpoints.BuildAlertQuery(db, "PRIVATE-INVENTORY", [7]).Take(6).ToListAsync());
        Assert.Empty(await GlobalSearchEndpoints.BuildInvestigationQuery(db, "PRIVATE-INVENTORY", [7], [7]).Take(6).ToListAsync());
        Assert.Empty(await GlobalSearchEndpoints.BuildAlertQuery(db, "PRIVATE-HOST-OBJECT", [7]).Take(6).ToListAsync());
        Assert.Empty(await GlobalSearchEndpoints.BuildInvestigationQuery(db, "PRIVATE-HOST-OBJECT", [7], [7]).Take(6).ToListAsync());
    }
}
