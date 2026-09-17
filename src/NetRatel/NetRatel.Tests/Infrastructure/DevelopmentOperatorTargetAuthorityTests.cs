using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class DevelopmentOperatorTargetAuthorityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task EvaluateAsync_RequiresAnExplicitPersistedDevelopmentSafeGrant()
    {
        await using var db = CreateDb();
        var agent = AddAgent(db);
        var authority = CreateAuthority(db, Environments.Development);
        var request = Request(agent, DevelopmentOperatorOperation.JobRunStart);

        var beforeGrant = await authority.EvaluateAsync(request, Ct);

        beforeGrant.IsAllowed.Should().BeFalse();
        beforeGrant.RejectionCode.Should().Be("target_not_authorized");

        await authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.JobRuns), Ct);
        var decision = await authority.EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeTrue();
        decision.GrantId.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_UsesTheOperationScopeInsteadOfCallerSuppliedEnvironment()
    {
        await using var db = CreateDb();
        var agent = AddAgent(db);
        var authority = CreateAuthority(db, Environments.Development);
        await authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.JobRuns), Ct);

        var decision = await authority.EvaluateAsync(
            Request(agent, DevelopmentOperatorOperation.TaskCreate),
            Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.RejectionCode.Should().Be("target_operation_not_authorized");
    }

    [Fact]
    public async Task EvaluateAsync_RequiresTheDedicatedObservabilityScopeForClientLogAndTelemetryReads()
    {
        await using var db = CreateDb();
        var agent = AddAgent(db);
        var authority = CreateAuthority(db, Environments.Development);
        await authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.JobRuns), Ct);

        var logDenied = await authority.EvaluateAsync(Request(agent, DevelopmentOperatorOperation.ClientLogRead), Ct);
        var telemetryDenied = await authority.EvaluateAsync(Request(agent, DevelopmentOperatorOperation.ClientTelemetryRead), Ct);
        logDenied.RejectionCode.Should().Be("target_operation_not_authorized");
        telemetryDenied.RejectionCode.Should().Be("target_operation_not_authorized");

        await authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.Observability), Ct);
        (await authority.EvaluateAsync(Request(agent, DevelopmentOperatorOperation.ClientLogRead), Ct)).IsAllowed.Should().BeTrue();
        (await authority.EvaluateAsync(Request(agent, DevelopmentOperatorOperation.ClientTelemetryRead), Ct)).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task RecordAcceptedAsync_AppendsAContentFreeImmutableAuditRow()
    {
        await using var db = CreateDb();
        var agent = AddAgent(db);
        var authority = CreateAuthority(db, Environments.Development);
        await authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.JobRuns), Ct);
        var decision = await authority.EvaluateAsync(
            Request(agent, DevelopmentOperatorOperation.JobRunStart),
            Ct);

        var audit = await authority.RecordAcceptedAsync(decision, Ct);
        var row = await db.DevelopmentOperatorAcceptedAudits.SingleAsync(Ct);

        audit.AuditId.Should().Be(row.Id);
        row.TenantId.Should().Be(agent.TenantId);
        row.AgentId.Should().Be(agent.Id);
        row.Operation.Should().Be(DevelopmentOperatorOperation.JobRunStart);
        row.ActorId.Should().Be("operator-42");
        row.CorrelationId.Should().Be("corr-42");
        typeof(DevelopmentOperatorAcceptedAuditRecord).GetProperties().Select(property => property.Name)
            .Should().NotContain(["Command", "Payload", "FilePath", "Secret"]);
    }

    [Fact]
    public async Task GrantAsync_SupersedesThePriorGrantWithoutErasingItsApprovalEvidence()
    {
        await using var db = CreateDb();
        var agent = AddAgent(db);
        var authority = CreateAuthority(db, Environments.Development);

        var first = await authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.JobRuns), Ct);
        var second = await authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.Tasks), Ct);
        var grants = await db.DevelopmentOperatorTargetGrants.OrderBy(grant => grant.GrantedAtUtc).ToListAsync(Ct);

        grants.Should().HaveCount(2);
        grants.Single(grant => grant.Id == first.GrantId).RevokedAtUtc.Should().NotBeNull();
        grants.Single(grant => grant.Id == second.GrantId).RevokedAtUtc.Should().BeNull();
        (await authority.GetActiveGrantAsync(agent.TenantId, agent.Id, Ct))!
            .AllowedOperations.Should().Be(DevelopmentOperatorOperationScope.Tasks);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsTheSameGrantOutsideDevelopment()
    {
        await using var db = CreateDb();
        var agent = AddAgent(db);
        var developmentAuthority = CreateAuthority(db, Environments.Development);
        await developmentAuthority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.JobRuns), Ct);
        var productionAuthority = CreateAuthority(db, Environments.Production);

        var decision = await productionAuthority.EvaluateAsync(
            Request(agent, DevelopmentOperatorOperation.JobRunStart),
            Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.RejectionCode.Should().Be("development_environment_required");
    }

    [Fact]
    public async Task GrantAsync_RequiresACanonicalFixtureRootForFileSystemOperations()
    {
        await using var db = CreateDb();
        var agent = AddAgent(db);
        var authority = CreateAuthority(db, Environments.Development);

        var missingRoot = () => authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.FileSystem), Ct);
        await missingRoot.Should().ThrowAsync<ArgumentException>();

        var granted = await authority.GrantAsync(Grant(agent, DevelopmentOperatorOperationScope.FileSystem, "/tmp/netratel-mcp-qa/"), Ct);

        granted.FileFixtureRoot.Should().Be("/tmp/netratel-mcp-qa");
        DevelopmentFileFixture.Contains(granted.FileFixtureRoot, "/tmp/netratel-mcp-qa/marker.txt").Should().BeTrue();
        DevelopmentFileFixture.Contains(granted.FileFixtureRoot, "/tmp/netratel-mcp-qa-other/marker.txt").Should().BeFalse();
        DevelopmentFileFixture.Contains(granted.FileFixtureRoot, "/tmp/netratel-mcp-qa/../secrets").Should().BeFalse();
    }

    private static OrchestratorDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new OrchestratorDbContext(options);
    }

    private static Agent AddAgent(OrchestratorDbContext db)
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            TenantId = 42,
            Name = "qa-target",
            Status = AgentStatus.Active,
            IsEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Agents.Add(agent);
        db.SaveChanges();
        return agent;
    }

    private static DevelopmentOperatorTargetAuthority CreateAuthority(OrchestratorDbContext db, string environmentName) =>
        new(db, new TestHostEnvironment { EnvironmentName = environmentName });

    private static DevelopmentOperatorTargetGrantRequest Grant(Agent agent, DevelopmentOperatorOperationScope scope, string? fileFixtureRoot = null) =>
        new(
            agent.TenantId,
            agent.Id,
            DevelopmentOperatorTargetClassification.DedicatedQa,
            scope,
            DateTimeOffset.UtcNow.AddDays(1),
            "issue-861",
            "operator-42",
            "corr-42",
            fileFixtureRoot);

    private static DevelopmentOperatorTargetRequest Request(Agent agent, DevelopmentOperatorOperation operation) =>
        new(agent.TenantId, agent.Id, operation, "operator-42", "corr-42");

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NetRatel.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
