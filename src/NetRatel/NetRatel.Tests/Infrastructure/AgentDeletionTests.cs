using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Application.Agents;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class AgentDeletionTests
{
    [Fact]
    public async Task Removal_is_tenant_scoped_idempotent_and_preserves_the_original_audit()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var agent = new Agent { Id = Guid.NewGuid(), TenantId = 42, IsEnabled = true, Status = AgentStatus.Active };
        var owner = new ClientConnectionOwnerRecord
        {
            TenantId = 42, AgentId = agent.Id, ConnectionEpoch = 1, ConnectionId = Guid.NewGuid(), Active = true, OwnerRevision = 4
        };
        var admission = new ClientConnectionAdmissionRecord
        {
            TenantId = 42, AgentId = agent.Id, ConnectionId = Guid.NewGuid(), ConnectionEpoch = 2, Status = 1
        };
        db.Agents.Add(agent);
        db.ClientConnectionOwners.Add(owner);
        db.ClientConnectionAdmissions.Add(admission);
        db.AgentCredentials.Add(new AgentCredential { Id = Guid.NewGuid(), AgentId = agent.Id, RefreshTokenHash = "old-credential" });
        db.AgentRefreshTokens.Add(new AgentRefreshToken
        {
            Id = Guid.NewGuid(), AgentId = agent.Id, TokenHash = "old-token", ProtectedSuccessorToken = "protected-successor"
        });
        await db.SaveChangesAsync();
        var service = new AgentManagementService(db, NullLogger<AgentManagementService>.Instance);

        Func<Task> foreignRemoval = () => service.DeleteAsync(43, agent.Id, "Wrong tenant", "other-admin", default);
        (await foreignRemoval.Should().ThrowAsync<AgentAuthException>()).Which.StatusCode.Should().Be(404);
        agent.DeletedAtUtc.Should().BeNull();
        owner.Active.Should().BeTrue();

        await service.DeleteAsync(42, agent.Id, "Duplicate installation", "admin", default);
        var deletedAt = agent.DeletedAtUtc;
        await service.DeleteAsync(42, agent.Id, "Retry with different text", "other-admin", default);

        agent.DeletedAtUtc.Should().Be(deletedAt);
        agent.DeletedBy.Should().Be("admin");
        owner.Active.Should().BeFalse();
        owner.OwnerRevision.Should().Be(5);
        admission.Status.Should().Be(2);
        (await db.AgentCredentials.SingleAsync()).RevokedAtUtc.Should().NotBeNull();
        var token = await db.AgentRefreshTokens.SingleAsync();
        token.RevokedAtUtc.Should().NotBeNull();
        token.ProtectedSuccessorToken.Should().BeNull();
        var audit = await db.OutboxMessages.ToListAsync();
        audit.Should().HaveCount(2);
        foreach (var entry in audit)
        {
            var payload = JsonSerializer.Deserialize<AgentLifecyclePayload>(entry.PayloadJson)!;
            payload.Actor.Should().Be("admin");
            payload.Reason.Should().Be("Duplicate installation");
        }
        (await service.GetAsync(42, agent.Id, default)).Should().BeNull();
    }
}
