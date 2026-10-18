using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class McpOperatorPolicyMutationGuardPostgresTests(PostgreSqlPersistenceFixture postgres) : IAsyncLifetime
{
    private const int TenantId = 42;
    private const string Administrator = "policy-admin@example.test";
    private const string ServicePrincipal = "mcp-service";
    private readonly Guid _agentId = Guid.NewGuid();
    private string _connectionString = null!;
    private DbContextOptions<OrchestratorDbContext> _options = null!;
    private McpOperatorTargetProfile _reviewedProfile = null!;
    private McpOperatorDecision _decision = null!;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(_connectionString).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
        db.Agents.Add(new Agent
        {
            Id = _agentId, TenantId = TenantId, IsEnabled = true,
            Status = AgentStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var administration = new McpOperatorPolicyAdministration(db);
        _reviewedProfile = await administration.UpsertTargetProfileAsync(TenantId, _agentId,
            McpOperatorTargetClassification.ManagedStandard, ["reviewed"], null, Administrator, CancellationToken.None);
        await administration.CreateAsync(Draft() with
        {
            Name = "classified policy administration",
            Effect = McpOperatorPolicyEffect.Allow,
            PrincipalSelector = new(McpOperatorPrincipalSelectorKind.OidcGroup, "policy-administrators"),
            OperationFamily = McpOperatorOperationFamily.PolicyAdministration,
            Operation = "netratel_policy/create",
            Constraints = new(RequiredConfirmationClass: McpOperatorConfirmationClass.PolicyAdministration)
        }, "independent-admin@example.test", CancellationToken.None);
        var principal = new McpOperatorPrincipal(Administrator, "policy-client", "policy-client",
            new HashSet<string> { "policy-administrators" }, new HashSet<string> { "PolicyAdministrator" },
            new HashSet<string> { "netratel.mcp.admin" }, ServicePrincipal);
        _decision = await new McpOperatorAuthorization(db).EvaluateAsync(new(
            McpOperatorEnvironment.Development, principal, TenantId, _agentId, _reviewedProfile.Classification,
            McpOperatorOperationFamily.PolicyAdministration, "netratel_policy/create",
            new HashSet<string> { "netratel.mcp.admin" }, McpOperatorConfirmationClass.PolicyAdministration,
            "policy-mutation-test", "policy-mutation-request", TargetTags: _reviewedProfile.Tags.ToHashSet(),
            McpResource: "https://mcp.example.test/", McpInstance: "dev", Tool: "netratel_policy"), CancellationToken.None);
        _decision.IsAllowed.Should().BeTrue();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Profile_change_committed_before_the_guard_rejects_without_audit_or_policy_mutation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var profileWriter = new OrchestratorDbContext(_options);
        await using var policyWriter = new OrchestratorDbContext(_options);
        await policyWriter.Database.OpenConnectionAsync(ct);
        var policyWriterPid = ((NpgsqlConnection)policyWriter.Database.GetDbConnection()).ProcessID;
        await using var transaction = await profileWriter.Database.BeginTransactionAsync(ct);
        await ChangeProfileAsync(profileWriter, ct);
        var administration = new McpOperatorPolicyAdministration(policyWriter);
        var pending = administration.ExecuteWithTargetProfileAsync(TenantId, _agentId, _reviewedProfile,
            mutationToken => AcceptAndCreateAsync(policyWriter, administration, mutationToken), ct);

        await WaitForDatabaseBlockAsync(policyWriterPid, pending, ct);
        await transaction.CommitAsync(ct);

        var rejection = await Assert.ThrowsAsync<McpOperatorAdmissionRejectedException>(() => pending);
        rejection.FailureCode.Should().Be("confirmation_plan_stale");
        await AssertMutationCountsAsync(0, ct);
    }

    [Fact]
    public async Task Profile_change_after_accepted_audit_waits_until_the_policy_mutation_commits()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var policyWriter = new OrchestratorDbContext(_options);
        await using var profileWriter = new OrchestratorDbContext(_options);
        await profileWriter.Database.OpenConnectionAsync(ct);
        var profileWriterPid = ((NpgsqlConnection)profileWriter.Database.GetDbConnection()).ProcessID;
        var administration = new McpOperatorPolicyAdministration(policyWriter);
        Task<McpOperatorTargetProfile>? profileChange = null;

        await administration.ExecuteWithTargetProfileAsync(TenantId, _agentId, _reviewedProfile, async mutationToken =>
        {
            await new McpOperatorAuthorization(policyWriter).RecordAcceptedAsync(_decision, ServicePrincipal, mutationToken);
            profileChange = ChangeProfileAsync(profileWriter, mutationToken);
            // This is the gap between accepted audit and the actual policy write.
            // An implementation releasing its guard after audit cannot pass.
            await WaitForDatabaseBlockAsync(profileWriterPid, profileChange, mutationToken);
            return await administration.CreateAsync(Draft(), Administrator, mutationToken);
        }, ct);

        var changed = await profileChange!;
        changed.Classification.Should().Be(McpOperatorTargetClassification.Restricted);
        changed.Version.Should().Be(_reviewedProfile.Version + 1);
        await AssertMutationCountsAsync(1, ct);
    }

    [Fact]
    public async Task Failed_guarded_mutation_rolls_back_both_accepted_audit_and_policy_write()
    {
        await using var db = new OrchestratorDbContext(_options);
        var administration = new McpOperatorPolicyAdministration(db);
        var mutate = () => administration.ExecuteWithTargetProfileAsync<McpOperatorPolicy>(TenantId, _agentId, _reviewedProfile,
            async ct =>
            {
                await AcceptAndCreateAsync(db, administration, ct);
                throw new InvalidOperationException("mutation-failed");
            }, CancellationToken.None);

        await mutate.Should().ThrowAsync<InvalidOperationException>().WithMessage("mutation-failed");

        await AssertMutationCountsAsync(0, CancellationToken.None);
    }

    private async Task<McpOperatorPolicy> AcceptAndCreateAsync(OrchestratorDbContext db,
        McpOperatorPolicyAdministration administration, CancellationToken ct)
    {
        await new McpOperatorAuthorization(db).RecordAcceptedAsync(_decision, ServicePrincipal, ct);
        return await administration.CreateAsync(Draft(), Administrator, ct);
    }

    private Task<McpOperatorTargetProfile> ChangeProfileAsync(OrchestratorDbContext db, CancellationToken ct) =>
        new McpOperatorPolicyAdministration(db).UpsertTargetProfileAsync(TenantId, _agentId,
            McpOperatorTargetClassification.Restricted, ["changed"], _reviewedProfile.Version,
            "independent-admin@example.test", ct);

    private async Task AssertMutationCountsAsync(int count, CancellationToken ct)
    {
        await using var verify = new OrchestratorDbContext(_options);
        (await verify.McpOperatorAcceptedAudits.CountAsync(row => row.TenantId == TenantId && row.AgentId == _agentId, ct)).Should().Be(count);
        (await verify.McpOperatorPolicies.CountAsync(row => row.Name == "reviewed deny rule", ct)).Should().Be(count);
        (await verify.McpOperatorPolicyChangeAudits.CountAsync(row => row.Action == "policy_created" && row.ActorId == Administrator, ct)).Should().Be(count);
    }

    private async Task WaitForDatabaseBlockAsync(int processId, Task pending, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", connection);
            command.Parameters.AddWithValue("pid", processId);
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return;
            pending.IsCompleted.Should().BeFalse("the competing profile operation must wait for the policy transaction");
        } while (await timer.WaitForNextTickAsync(ct));
        throw new InvalidOperationException("The database lock observation ended before a fence was acquired.");
    }

    private McpOperatorPolicyDraft Draft() => new(
        "reviewed deny rule", McpOperatorEnvironment.Development, McpOperatorPolicyEffect.Deny, 20,
        new(McpOperatorPrincipalSelectorKind.OidcGroup, "ordinary-observers"),
        new(McpOperatorTargetSelectorKind.ExactAgent, TenantId, _agentId), McpOperatorTargetClassification.ManagedStandard,
        McpOperatorOperationFamily.Observability, null, new(), DateTimeOffset.UtcNow.AddHours(1), null);
}
