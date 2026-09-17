using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class PrimaryClientAgentBindingServiceTests
{
    [Fact]
    public async Task IssueForPrimaryClientBindingAsync_CreatesSingleUseCodeAndPendingBindingTogether()
    {
        await using var db = CreateDb();
        var service = new EnrollmentCodeIssueService(db);

        var issued = await service.IssueForPrimaryClientBindingAsync(
            new(7, "c0ffee", 60, "operator@example.test", "canary pairing"),
            CancellationToken.None);

        issued.EnrollmentCode.TenantId.Should().Be(7);
        issued.EnrollmentCode.Code.Should().StartWith("ENR-");
        issued.Binding.Status.Should().Be(PrimaryClientAgentBindingStatus.Pending);
        issued.Binding.EnrollmentCodeId.Should().Be(issued.EnrollmentCode.EnrollmentCodeId);
        issued.Binding.PrimaryClientIdentity.Should().Be("C0FFEE");
        issued.Binding.BindingSource.Should().Be("atomic-server-issued-enrollment");
        (await db.EnrollmentCodes.SingleAsync()).MaxUses.Should().Be(1);
        (await db.PrimaryClientAgentBindings.SingleAsync()).EnrollmentCodeId.Should().Be(issued.EnrollmentCode.EnrollmentCodeId);
    }

    [Fact]
    public async Task IssueForPrimaryClientBindingAsync_RejectsAnExistingActiveBinding()
    {
        await using var db = CreateDb();
        var agent = NewAgent(tenantId: 7);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        var bindings = new PrimaryClientAgentBindingService(db);
        await bindings.CreateManualRepairAsync(
            new(7, agent.Id, "c0ffee", "operator@example.test", "existing canary"),
            CancellationToken.None);

        var issue = new EnrollmentCodeIssueService(db);
        var act = () => issue.IssueForPrimaryClientBindingAsync(
            new(7, "c0ffee", 60, "operator@example.test", null),
            CancellationToken.None);

        await act.Should().ThrowAsync<AgentAuthException>()
            .Where(exception => exception.StatusCode == 409);
        (await db.EnrollmentCodes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreatePendingAsync_BindsOnlyToAServerIssuedSingleUseEnrollmentCode()
    {
        await using var db = CreateDb();
        var code = NewEnrollmentCode(tenantId: 7, maxUses: 1);
        db.EnrollmentCodes.Add(code);
        await db.SaveChangesAsync();

        var service = new PrimaryClientAgentBindingService(db);
        var result = await service.CreatePendingAsync(
            new(7, code.Id, "c0ffee", "operator@example.test", "canary pairing"),
            CancellationToken.None);

        result.Disposition.Should().Be(PrimaryClientAgentBindingDisposition.Created);
        result.Binding.Should().NotBeNull();
        result.Binding!.Status.Should().Be(PrimaryClientAgentBindingStatus.Pending);
        result.Binding.EnrollmentCodeId.Should().Be(code.Id);
        result.Binding.AgentId.Should().BeNull();
        result.Binding.PrimaryClientIdentity.Should().Be("C0FFEE");
        result.Binding.BindingSource.Should().Be("server-issued-enrollment");
    }

    [Fact]
    public async Task CreatePendingAsync_RejectsMultiUseEnrollmentCodes()
    {
        await using var db = CreateDb();
        var code = NewEnrollmentCode(tenantId: 7, maxUses: 2);
        db.EnrollmentCodes.Add(code);
        await db.SaveChangesAsync();

        var result = await new PrimaryClientAgentBindingService(db).CreatePendingAsync(
            new(7, code.Id, "c0ffee", "operator@example.test", null),
            CancellationToken.None);

        result.Disposition.Should().Be(PrimaryClientAgentBindingDisposition.EnrollmentCodeMustBeSingleUse);
        (await db.PrimaryClientAgentBindings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BindEnrollmentAsync_TransitionsThePendingServerIssuedBinding()
    {
        await using var db = CreateDb();
        var agent = NewAgent(tenantId: 7);
        var code = NewEnrollmentCode(tenantId: 7, maxUses: 1);
        db.AddRange(agent, code);
        await db.SaveChangesAsync();
        var service = new PrimaryClientAgentBindingService(db);

        var pending = await service.CreatePendingAsync(
            new(7, code.Id, "c0ffee", "operator@example.test", null),
            CancellationToken.None);
        var result = await service.BindEnrollmentAsync(7, code.Id, agent.Id, CancellationToken.None);

        pending.Binding.Should().NotBeNull();
        result.Disposition.Should().Be(PrimaryClientAgentBindingDisposition.Created);
        result.Binding.Should().NotBeNull();
        result.Binding!.Status.Should().Be(PrimaryClientAgentBindingStatus.Bound);
        result.Binding.AgentId.Should().Be(agent.Id);
        result.Binding.BoundBy.Should().Be("enrollment");
        (await service.GetByAgentAsync(7, agent.Id, CancellationToken.None))!.BindingId.Should().Be(pending.Binding!.BindingId);
    }

    [Fact]
    public async Task ListBoundAsync_ExcludesPendingAndRevokedBindings()
    {
        await using var db = CreateDb();
        var boundAgent = NewAgent(tenantId: 7);
        var pendingCode = NewEnrollmentCode(tenantId: 7, maxUses: 1);
        db.AddRange(boundAgent, pendingCode);
        await db.SaveChangesAsync();
        var service = new PrimaryClientAgentBindingService(db);

        var bound = await service.CreateManualRepairAsync(
            new(7, boundAgent.Id, "bound", "operator@example.test", "canary"),
            CancellationToken.None);
        await service.CreatePendingAsync(
            new(7, pendingCode.Id, "pending", "operator@example.test", null),
            CancellationToken.None);
        await service.RevokeAsync(7, bound.Binding!.BindingId, "operator@example.test", "rollback", CancellationToken.None);

        (await service.ListBoundAsync(7, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateManualRepairAsync_RejectsConflictingOneToOneMappings()
    {
        await using var db = CreateDb();
        var firstAgent = NewAgent(tenantId: 7);
        var secondAgent = NewAgent(tenantId: 7);
        db.AddRange(firstAgent, secondAgent);
        await db.SaveChangesAsync();
        var service = new PrimaryClientAgentBindingService(db);

        var created = await service.CreateManualRepairAsync(
            new(7, firstAgent.Id, "c0ffee", "operator@example.test", "existing canary"),
            CancellationToken.None);
        var sameAgent = await service.CreateManualRepairAsync(
            new(7, firstAgent.Id, "bead", "operator@example.test", "incorrect rebind"),
            CancellationToken.None);
        var samePrimary = await service.CreateManualRepairAsync(
            new(7, secondAgent.Id, "c0ffee", "operator@example.test", "incorrect conflict"),
            CancellationToken.None);

        created.Disposition.Should().Be(PrimaryClientAgentBindingDisposition.Created);
        sameAgent.Disposition.Should().Be(PrimaryClientAgentBindingDisposition.AgentAlreadyBound);
        sameAgent.Binding!.PrimaryClientIdentity.Should().Be("C0FFEE");
        samePrimary.Disposition.Should().Be(PrimaryClientAgentBindingDisposition.PrimaryClientAlreadyBound);
        samePrimary.Binding!.AgentId.Should().Be(firstAgent.Id);
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ReportsUnboundAgentsWithoutInventingAMapping()
    {
        await using var db = CreateDb();
        var bound = NewAgent(tenantId: 7);
        var unbound = NewAgent(tenantId: 7);
        db.AddRange(bound, unbound);
        await db.SaveChangesAsync();
        var service = new PrimaryClientAgentBindingService(db);
        await service.CreateManualRepairAsync(
            new(7, bound.Id, "c0ffee", "operator@example.test", "existing canary"),
            CancellationToken.None);

        var diagnostics = await service.GetDiagnosticsAsync(7, CancellationToken.None);

        diagnostics.TotalAgents.Should().Be(2);
        diagnostics.BoundAgents.Should().Be(1);
        diagnostics.UnboundAgentIds.Should().ContainSingle().Which.Should().Be(unbound.Id);
        diagnostics.AmbiguousAgentBindings.Should().BeEmpty();
        diagnostics.ConflictingPrimaryClientBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeAsync_AllowsAReplacementBindingButPreservesTheAuditRecord()
    {
        await using var db = CreateDb();
        var oldAgent = NewAgent(tenantId: 7);
        var replacementAgent = NewAgent(tenantId: 7);
        db.AddRange(oldAgent, replacementAgent);
        await db.SaveChangesAsync();
        var service = new PrimaryClientAgentBindingService(db);
        var original = await service.CreateManualRepairAsync(
            new(7, oldAgent.Id, "c0ffee", "operator@example.test", "initial mapping"),
            CancellationToken.None);

        var revoked = await service.RevokeAsync(7, original.Binding!.BindingId, "operator@example.test", "agent replaced", CancellationToken.None);
        var replacement = await service.CreateManualRepairAsync(
            new(7, replacementAgent.Id, "c0ffee", "operator@example.test", "replacement mapping"),
            CancellationToken.None);

        revoked.Should().BeTrue();
        replacement.Disposition.Should().Be(PrimaryClientAgentBindingDisposition.Created);
        (await db.PrimaryClientAgentBindings.CountAsync()).Should().Be(2);
        (await db.PrimaryClientAgentBindings.SingleAsync(binding => binding.Id == original.Binding.BindingId)).Status
            .Should().Be(PrimaryClientAgentBindingStatus.Revoked);
    }

    private static OrchestratorDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static Agent NewAgent(int tenantId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Status = AgentStatus.Active,
        IsEnabled = true,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static EnrollmentCode NewEnrollmentCode(int tenantId, int? maxUses) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Code = $"ENR-{Guid.NewGuid():N}",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ValidFromUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        ValidToUtc = DateTimeOffset.UtcNow.AddHours(1),
        MaxUses = maxUses
    };
}
