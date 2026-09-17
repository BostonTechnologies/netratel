using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class AgentManagementServiceTests
{
    [Fact]
    public async Task AgentDirectory_QueryFilter_ReturnsOnlyCanonicalAgent()
    {
        await using var db = CreateDb();
        var canonicalId = Guid.NewGuid();
        db.Agents.AddRange(
            new Agent
            {
                Id = canonicalId,
                TenantId = 42,
                IsEnabled = true,
                Status = AgentStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                PublicKeyFingerprint = "stable-key"
            },
            new Agent
            {
                Id = Guid.NewGuid(),
                TenantId = 42,
                IsEnabled = false,
                Status = AgentStatus.Disabled,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                PublicKeyFingerprint = "stable-key",
                SupersededByAgentId = canonicalId,
                SupersededAtUtc = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var visible = await db.Agents.AsNoTracking().ToArrayAsync();

        visible.Should().ContainSingle().Which.Id.Should().Be(canonicalId);
        (await db.Agents.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DisableAsync_SetsDisabledFields()
    {
        await using var db = CreateDb();
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            TenantId = 42,
            IsEnabled = true,
            Status = AgentStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var svc = new AgentManagementService(db, NullLogger<AgentManagementService>.Instance);
        await svc.DisableAsync(42, agent.Id, "lost", "admin", CancellationToken.None);

        var updated = await db.Agents.SingleAsync(a => a.Id == agent.Id);
        updated.IsEnabled.Should().BeFalse();
        updated.Status.Should().Be(AgentStatus.Disabled);
        updated.DisabledReason.Should().Be("lost");
        updated.RevokedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_TombstonesAgent_RevokesCredentials_AndRetainsOnlySafeEvidence()
    {
        await using var db = CreateDb();
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            TenantId = 42,
            IsEnabled = true,
            Status = AgentStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        db.AgentCredentials.Add(new AgentCredential
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            RefreshTokenHash = "active-credential",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        db.AgentRefreshTokens.Add(new AgentRefreshToken
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            TokenHash = "active-refresh-token",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var svc = new AgentManagementService(db, NullLogger<AgentManagementService>.Instance);
        await svc.DeleteAsync(42, agent.Id, "Machine retired", "admin", CancellationToken.None);

        (await svc.GetAsync(42, agent.Id, CancellationToken.None)).Should().BeNull();
        (await db.Agents.ToListAsync()).Should().BeEmpty();
        var tombstone = await db.Agents.IgnoreQueryFilters().SingleAsync(a => a.Id == agent.Id);
        tombstone.IsEnabled.Should().BeFalse();
        tombstone.Status.Should().Be(AgentStatus.Disabled);
        tombstone.DisabledReason.Should().Be("decommissioned");
        tombstone.DeletedAtUtc.Should().NotBeNull();
        tombstone.DeletedBy.Should().Be("admin");
        (await db.AgentCredentials.SingleAsync()).RevokedAtUtc.Should().NotBeNull();
        (await db.AgentRefreshTokens.SingleAsync()).RevokedAtUtc.Should().NotBeNull();
        var evidence = await db.OutboxMessages.ToListAsync();
        evidence.Should().HaveCount(2);
        evidence.Select(message => message.PayloadJson).Should().OnlyContain(payload =>
            payload.Contains("decommissioned", StringComparison.Ordinal) &&
            !payload.Contains("Machine retired", StringComparison.Ordinal));
    }

    private static OrchestratorDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new OrchestratorDbContext(opts);
    }
}
