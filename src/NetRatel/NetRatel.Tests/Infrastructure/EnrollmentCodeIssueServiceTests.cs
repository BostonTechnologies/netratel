using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class EnrollmentCodeIssueServiceTests
{
    [Fact]
    public async Task IssueAsync_EnforcesValidityBounds()
    {
        await using var db = CreateDb();
        var service = new EnrollmentCodeIssueService(db);

        var act = () => service.IssueAsync(new EnrollmentCodeIssueRequest(7, 3, 1, null, null), CancellationToken.None);
        await act.Should().ThrowAsync<AgentAuthException>().WithMessage("*ValidForMinutes*");
    }

    [Fact]
    public async Task IssueAsync_CreatesCode_WithRequestedMaxUses_AndZeroUses()
    {
        await using var db = CreateDb();
        var service = new EnrollmentCodeIssueService(db);

        var result = await service.IssueAsync(new EnrollmentCodeIssueRequest(42, 60, 1, "test", "n"), CancellationToken.None);
        var row = await db.EnrollmentCodes.SingleAsync(x => x.Id == result.EnrollmentCodeId);

        row.MaxUses.Should().Be(1);
        row.Uses.Should().Be(0);
        row.TenantId.Should().Be(42);
    }

    [Fact]
    public async Task DevelopmentMcpOwnership_IsRecoveredFromPersistence_AfterServiceRecreation()
    {
        var options = CreateOptions();
        var targetAgentId = Guid.NewGuid();
        EnrollmentCodeIssueResult issued;

        await using (var issuingDb = new OrchestratorDbContext(options))
        {
            issued = await new EnrollmentCodeIssueService(issuingDb).IssueAsync(
                new EnrollmentCodeIssueRequest(
                    TenantId: 42,
                    ValidForMinutes: 5,
                    MaxUses: 1,
                    CreatedBy: "test",
                    Notes: "MCP-QA-onboarding-persistence",
                    DevelopmentMcpTargetAgentId: targetAgentId,
                    DevelopmentMcpMarker: "MCP-QA-onboarding-persistence"),
                CancellationToken.None);
        }

        await using var recoveryDb = new OrchestratorDbContext(options);
        var recovered = await new EnrollmentCodeIssueService(recoveryDb).GetDevelopmentMcpOwnershipAsync(
            issued.EnrollmentCodeId,
            42,
            targetAgentId,
            CancellationToken.None);

        recovered.Should().NotBeNull();
        recovered!.Marker.Should().Be("MCP-QA-onboarding-persistence");
        recovered.TargetAgentId.Should().Be(targetAgentId);
        recovered.MaxUses.Should().Be(1);
        recovered.Uses.Should().Be(0);
    }

    private static OrchestratorDbContext CreateDb() => new(CreateOptions());

    private static DbContextOptions<OrchestratorDbContext> CreateOptions()
        => new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
}
