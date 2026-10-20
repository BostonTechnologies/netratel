using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class McpOperatorResultPersistencePostgresTests(PostgreSqlPersistenceFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<OrchestratorDbContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Policy_first_result_matches_the_persisted_projection_at_exact_timestamp_precision()
    {
        var preciseNow = SubMicrosecondTime();
        await using var writer = new OrchestratorDbContext(_options);
        // Force a nonzero seventh digit so this provider regression cannot pass
        // accidentally when the system clock happens to return whole microseconds.
        writer.SavingChanges += (_, _) =>
        {
            foreach (var entry in writer.ChangeTracker.Entries<McpOperatorPolicyRecord>()
                         .Where(entry => entry.State == EntityState.Added))
                entry.Entity.CreatedAtUtc = preciseNow;
        };
        var draft = Draft(preciseNow);
        var first = await new McpOperatorPolicyAdministration(writer)
            .CreateAsync(draft, "policy-admin@example.test", CancellationToken.None);

        await using var reader = new OrchestratorDbContext(_options);
        var replayProjection = await new McpOperatorPolicyAdministration(reader)
            .GetAsync(first.PolicyId, CancellationToken.None);

        replayProjection.Should().NotBeNull();
        JsonSerializer.Serialize(first).Should().Be(JsonSerializer.Serialize(replayProjection));
        (first.CreatedAtUtc.UtcTicks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
        (first.ExpiresAtUtc!.Value.UtcTicks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
        (first.ReviewByUtc!.Value.UtcTicks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
        (await reader.McpOperatorPolicies.CountAsync(policy => policy.Id == first.PolicyId)).Should().Be(1);
        (await reader.McpOperatorPolicyChangeAudits.CountAsync(audit => audit.PolicyId == first.PolicyId)).Should().Be(1);
    }

    [Fact]
    public async Task Artifact_first_result_and_exact_replay_match_across_database_contexts()
    {
        var preciseNow = SubMicrosecondTime();
        var agentId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var idempotencyId = Guid.NewGuid();
        var principal = new McpOperatorPrincipal("operator@example.test", "file-client", "file-client",
            Set(), Set(), Set("netratel.mcp.files"));
        var access = new McpOperatorRouteAccessRequest(McpOperatorEnvironment.Production, principal,
            "mcp-service", "https://mcp.example.test/mcp", "prod", "netratel_files", "collect", 42, agentId,
            principal.Scopes, "file-correlation", "file-request", TargetOnline: true, CapabilityAvailable: true);
        var request = new McpOperatorFileArtifactCreateRequest(access, auditId, idempotencyId,
            new string('A', 64), "bounded.txt", "text/plain", "bounded artifact"u8.ToArray(),
            preciseNow, preciseNow.AddMinutes(15));
        await using var writer = new OrchestratorDbContext(_options);
        var policy = await new McpOperatorPolicyAdministration(writer)
            .CreateAsync(Draft(preciseNow), "policy-admin@example.test", CancellationToken.None);
        writer.Agents.Add(new Agent
        {
            Id = agentId, TenantId = 42, IsEnabled = true,
            Status = AgentStatus.Active, CreatedAtUtc = preciseNow
        });
        writer.McpOperatorAcceptedAudits.Add(new McpOperatorAcceptedAuditRecord
        {
            Id = auditId, PolicyId = policy.PolicyId, Environment = access.Environment,
            ServicePrincipal = access.ServicePrincipal, Subject = principal.Subject, ClientId = principal.ClientId,
            AuthorizedParty = principal.AuthorizedParty, McpResource = access.McpResource, McpInstance = access.McpInstance,
            Tool = access.Tool, TenantId = access.TenantId, AgentId = agentId,
            OperationFamily = McpOperatorOperationFamily.FileRead, Operation = "netratel_files/collect",
            CorrelationId = access.CorrelationId, RequestId = access.RequestId, OccurredAtUtc = preciseNow
        });
        writer.McpOperatorIdempotencyRecords.Add(new McpOperatorIdempotencyRecord
        {
            Id = idempotencyId, PolicyId = policy.PolicyId, PolicyVersion = policy.Version,
            Environment = access.Environment, Subject = principal.Subject, ClientId = principal.ClientId!,
            TenantId = access.TenantId, AgentId = agentId, OperationFamily = McpOperatorOperationFamily.FileRead,
            Operation = "netratel_files/collect", IdempotencyKey = "file-collection-key",
            PayloadHash = new string('B', 64), TargetSetDigest = new string('C', 64),
            Outcome = McpOperatorIdempotencyOutcome.Pending, CreatedAtUtc = preciseNow, Version = 1
        });
        await writer.SaveChangesAsync();
        var first = await new McpOperatorFileArtifactStore(writer).CreateOrGetAsync(request, CancellationToken.None);

        await using var reader = new OrchestratorDbContext(_options);
        var store = new McpOperatorFileArtifactStore(reader);
        var replay = await store.CreateOrGetAsync(request, CancellationToken.None);
        var readback = await store.GetOwnedAsync(first.ArtifactId, access.TenantId, agentId, principal,
            access.McpResource!, access.McpInstance!, includeContent: false, CancellationToken.None);

        JsonSerializer.Serialize(first).Should().Be(JsonSerializer.Serialize(replay));
        JsonSerializer.Serialize(first).Should().Be(JsonSerializer.Serialize(readback));
        (first.CreatedAtUtc.UtcTicks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
        (first.ExpiresAtUtc.UtcTicks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
        first.Content.Should().BeNull();
        (await reader.McpOperatorFileArtifacts.CountAsync()).Should().Be(1);
        (await reader.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
    }

    private static McpOperatorPolicyDraft Draft(DateTimeOffset preciseNow) => new(
        "bounded file policy", McpOperatorEnvironment.Production, McpOperatorPolicyEffect.Allow, 10,
        new(McpOperatorPrincipalSelectorKind.OidcGroup, "file-observers"),
        new(McpOperatorTargetSelectorKind.Tenant, 42), null, McpOperatorOperationFamily.FileRead,
        "netratel_files/collect", new(), preciseNow.AddHours(1), preciseNow.AddMinutes(30));

    private static DateTimeOffset SubMicrosecondTime()
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerMicrosecond + 7, TimeSpan.Zero);
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
