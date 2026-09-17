using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorCommandStoreTests
{
    [Fact]
    public async Task CreateOrGetAsync_BindsTheCommandToItsFrozenPolicyAndEnforcesConcurrency()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now, maximumConcurrent: 1);
        var store = new McpOperatorCommandStore(db);

        var lease = await store.CreateOrGetAsync(request, CancellationToken.None);
        var retry = await store.CreateOrGetAsync(request, CancellationToken.None);
        var second = request with { CommandId = Guid.NewGuid().ToString("N"), IdempotencyId = Guid.NewGuid() };
        var rejected = () => store.CreateOrGetAsync(second, CancellationToken.None);

        lease.CommandHash.Should().Be(request.CommandHash);
        lease.EnvironmentReferences.Should().Equal("NETRATEL_TOKEN");
        lease.EffectiveConstraints.MaxConcurrentCommands.Should().Be(1);
        retry.CommandId.Should().Be(lease.CommandId);
        await rejected.Should().ThrowAsync<McpOperatorCommandLimitException>()
            .Where(exception => exception.Code == "command_concurrency_limit_reached");
    }

    [Fact]
    public async Task GetOwnedAsync_HidesCommandsFromAnotherDelegatedCaller()
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow, maximumConcurrent: 2);
        var store = new McpOperatorCommandStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);
        var other = request.Decision.Request.Principal with { Subject = "other@example.test" };

        var owned = await store.GetOwnedAsync(request.CommandId, 42, request.Decision.Request.AgentId!.Value, other,
            "https://mcp.prod.example/mcp", "prod", CancellationToken.None);

        owned.Should().BeNull();
    }

    [Fact]
    public async Task RecordLifecycleAsync_DoesNotReviveACancelRequestedCommand()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now, maximumConcurrent: 2);
        var store = new McpOperatorCommandStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);
        await store.RequestCancelAsync(request.CommandId, now.AddSeconds(1), CancellationToken.None);

        await store.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId!.Value,
            McpOperatorCommandState.Started, now.AddSeconds(2), null, CancellationToken.None);
        var lease = await store.GetAsync(request.CommandId, CancellationToken.None);

        lease.Should().NotBeNull();
        lease!.State.Should().Be(McpOperatorCommandState.CancelRequested);
    }

    [Theory]
    [InlineData("/tmp/new-feature-workspace")]
    [InlineData("C:/NetRatel/FeatureTesting")]
    public async Task ExplicitAnyDirectoryGrant_PersistsACommandInANewWorkspace(string directory)
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow, maximumConcurrent: 1);
        request = request with
        {
            WorkingDirectory = directory,
            Decision = request.Decision with
            {
                EffectiveConstraints = request.Decision.EffectiveConstraints! with { WorkingDirectories = ["*"] }
            }
        };
        var store = new McpOperatorCommandStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);
        (await store.GetAsync(request.CommandId, CancellationToken.None))!.WorkingDirectory.Should().Be(directory);
    }

    [Fact]
    public async Task CancelledLifecycle_RefreshesTheGatewayContextAfterHttpCancellation()
    {
        var database = Guid.NewGuid().ToString("N");
        await using var gatewayDb = CreateDb(database);
        await using var httpDb = CreateDb(database);
        var request = CreateRequest(DateTimeOffset.UtcNow, 2);
        var gateway = new McpOperatorCommandStore(gatewayDb);
        await gateway.CreateOrGetAsync(request, CancellationToken.None);
        await gateway.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId!.Value,
            McpOperatorCommandState.Started, DateTimeOffset.UtcNow, null, CancellationToken.None);
        await new McpOperatorCommandStore(httpDb).RequestCancelAsync(request.CommandId, DateTimeOffset.UtcNow, CancellationToken.None);

        await gateway.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId.Value,
            McpOperatorCommandState.Cancelled, DateTimeOffset.UtcNow, null, CancellationToken.None);

        await using var verificationDb = CreateDb(database);
        var saved = await new McpOperatorCommandStore(verificationDb).GetAsync(request.CommandId, CancellationToken.None);
        saved!.State.Should().Be(McpOperatorCommandState.Cancelled);
        saved.Output.Should().NotBeNull();
        saved.Output!.UnavailableReason.Should().Be("agent_result_missing");
    }

    [Fact]
    public async Task LateStartedLifecycle_RefreshesTheGatewayContextAndPreservesHttpCancellation()
    {
        var database = Guid.NewGuid().ToString("N");
        await using var gatewayDb = CreateDb(database);
        await using var httpDb = CreateDb(database);
        var request = CreateRequest(DateTimeOffset.UtcNow, 2);
        var gateway = new McpOperatorCommandStore(gatewayDb);
        await gateway.CreateOrGetAsync(request, CancellationToken.None);
        await new McpOperatorCommandStore(httpDb).RequestCancelAsync(request.CommandId, DateTimeOffset.UtcNow, CancellationToken.None);

        await gateway.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId!.Value,
            McpOperatorCommandState.Started, DateTimeOffset.UtcNow, null, CancellationToken.None);

        await using var verificationDb = CreateDb(database);
        (await new McpOperatorCommandStore(verificationDb).GetAsync(request.CommandId, CancellationToken.None))!
            .State.Should().Be(McpOperatorCommandState.CancelRequested);
    }

    [Fact]
    public async Task TerminalLifecycle_RetriesWhenHttpCancellationCommitsBetweenReadAndSave()
    {
        var database = Guid.NewGuid().ToString("N");
        var race = new BeforeSaveInterceptor();
        await using var gatewayDb = CreateDb(database, race);
        await using var httpDb = CreateDb(database);
        var request = CreateRequest(DateTimeOffset.UtcNow, 2);
        var gateway = new McpOperatorCommandStore(gatewayDb);
        await gateway.CreateOrGetAsync(request, CancellationToken.None);
        race.BeforeNextSave = async token =>
            await new McpOperatorCommandStore(httpDb).RequestCancelAsync(request.CommandId, DateTimeOffset.UtcNow, token);

        await gateway.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId!.Value,
            McpOperatorCommandState.Cancelled, DateTimeOffset.UtcNow, null, CancellationToken.None);

        race.Executions.Should().Be(1);
        await using var verificationDb = CreateDb(database);
        (await new McpOperatorCommandStore(verificationDb).GetAsync(request.CommandId, CancellationToken.None))!
            .State.Should().Be(McpOperatorCommandState.Cancelled);
    }

    [Fact]
    public async Task HttpCancellation_RetriesAndPreservesTerminalResultCommittedBetweenReadAndSave()
    {
        var database = Guid.NewGuid().ToString("N");
        var race = new BeforeSaveInterceptor();
        await using var httpDb = CreateDb(database, race);
        await using var gatewayDb = CreateDb(database);
        var request = CreateRequest(DateTimeOffset.UtcNow, 2);
        var http = new McpOperatorCommandStore(httpDb);
        await http.CreateOrGetAsync(request, CancellationToken.None);
        race.BeforeNextSave = token => new McpOperatorCommandStore(gatewayDb).RecordLifecycleAsync(
            request.CommandId, 42, request.Decision.Request.AgentId!.Value, McpOperatorCommandState.Completed,
            DateTimeOffset.UtcNow, null, token, """{"stdout":["finished-before-cancel"]}""", 0);

        var receipt = await http.RequestCancelAsync(request.CommandId, DateTimeOffset.UtcNow, CancellationToken.None);

        race.Executions.Should().Be(1);
        receipt!.State.Should().Be(McpOperatorCommandState.Completed);
        receipt.Output!.Stdout.Should().Equal("finished-before-cancel");
        await using var verificationDb = CreateDb(database);
        (await new McpOperatorCommandStore(verificationDb).GetAsync(request.CommandId, CancellationToken.None))!
            .State.Should().Be(McpOperatorCommandState.Completed);
    }

    [Fact]
    public async Task TerminalOutput_IsPersistedRedactedWithoutRawDiagnosticsAndSurvivesReload()
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow, 2);
        var store = new McpOperatorCommandStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);
        var json = """{"stdout":["certification-result"],"stderr":["password=do-not-persist"],"diagnostics":{"arguments":"raw-command-sentinel"}}""";
        await store.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId!.Value,
            McpOperatorCommandState.Completed, DateTimeOffset.UtcNow, null, CancellationToken.None, json, 0);
        db.ChangeTracker.Clear();
        var output = (await store.GetAsync(request.CommandId, CancellationToken.None))!.Output!;
        output.ExitCode.Should().Be(0);
        output.Stdout.Should().Equal("certification-result");
        output.Stderr.Should().NotContain(line => line.Contains("do-not-persist", StringComparison.Ordinal));
        output.UnavailableReason.Should().BeNull();
        var stored = (await db.McpOperatorCommands.SingleAsync()).OutputJson!;
        stored.Should().NotContain("raw-command-sentinel").And.NotContain("do-not-persist");
        var other = request.Decision.Request.Principal with { Subject = "another-operator" };
        (await store.GetOwnedAsync(request.CommandId, 42, request.Decision.Request.AgentId.Value, other,
            request.Decision.Request.McpResource!, request.Decision.Request.McpInstance!, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TerminalOutput_RespectsUtf8ByteBudgetAndCannotBeOverwrittenByALateFrame()
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow, 2) with { MaximumOutputBytes = 5 };
        var store = new McpOperatorCommandStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);
        await store.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId!.Value,
            McpOperatorCommandState.Completed, DateTimeOffset.UtcNow, null, CancellationToken.None,
            """{"stdout":["éééé"],"stderr":["later"]}""", 0);
        await store.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId.Value,
            McpOperatorCommandState.Started, DateTimeOffset.UtcNow.AddSeconds(1), null, CancellationToken.None,
            """{"stdout":["overwrite"]}""", 7);
        var output = (await store.GetAsync(request.CommandId, CancellationToken.None))!.Output!;
        output.Stdout.Should().Equal("éé");
        output.Stderr.Should().BeEmpty();
        output.OutputTruncated.Should().BeTrue();
        output.ExitCode.Should().Be(0);
    }

    [Theory]
    [InlineData(null, "agent_result_missing")]
    [InlineData("{incomplete", "agent_result_invalid")]
    public async Task MissingOrMalformedTerminalOutput_IsReportedExplicitly(string? result, string reason)
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow, 2);
        var store = new McpOperatorCommandStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);
        await store.RecordLifecycleAsync(request.CommandId, 42, request.Decision.Request.AgentId!.Value,
            McpOperatorCommandState.Failed, DateTimeOffset.UtcNow, "agent_command_failed", CancellationToken.None, result, 1);
        (await store.GetAsync(request.CommandId, CancellationToken.None))!.Output!.UnavailableReason.Should().Be(reason);
    }

    private static McpOperatorCommandCreateRequest CreateRequest(DateTimeOffset createdAtUtc, int maximumConcurrent)
    {
        var agentId = Guid.Parse("bb69ba7b-1e56-4ea8-bc36-533950b50610");
        var policyId = Guid.Parse("9252da81-08cf-4f62-a546-241428329d2b");
        var auditId = Guid.Parse("3252da81-08cf-4f62-a546-241428329d2b");
        var principal = new McpOperatorPrincipal("operator@example.test", "operator-client", "operator-client", Set(), Set(), Set("netratel.mcp.execute"));
        var access = new McpOperatorAccessRequest(
            McpOperatorEnvironment.Production,
            principal,
            42,
            agentId,
            McpOperatorTargetClassification.ManagedStandard,
            McpOperatorOperationFamily.TaskExecution,
            "netratel_commands/execute",
            Set("netratel.mcp.execute"),
            McpOperatorConfirmationClass.Destructive,
            "corr-42",
            "request-42",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            McpResource: "https://mcp.prod.example/mcp",
            McpInstance: "prod",
            Tool: "netratel_commands");
        var constraints = new McpOperatorConstraints(
            AllowedShells: ["bash"],
            WorkingDirectories: ["/srv/netratel"],
            MaxCommandDurationSeconds: 300,
            MaxConcurrentCommands: maximumConcurrent,
            MaxOutputBytes: 16 * 1024,
            MaxTaskTargetCount: 1,
            MaxFanOut: 1,
            DestructiveOperationsAllowed: true);
        var decision = new McpOperatorDecision(true, null, null, [policyId], constraints, access.TargetSetDigest, access, 4);
        var audit = new McpOperatorAcceptedAudit(
            auditId, policyId, access.Environment, "netratel-mcp-http-prod", principal.Subject, principal.ClientId, principal.AuthorizedParty,
            [], [], principal.Scopes.Order(StringComparer.Ordinal).ToArray(), access.McpResource, access.McpInstance, access.Tool,
            access.TenantId, access.AgentId, access.OperationFamily, access.Operation, access.CorrelationId, access.RequestId, createdAtUtc);
        return new McpOperatorCommandCreateRequest(
            Guid.NewGuid().ToString("N"), decision, audit, Guid.NewGuid(), "corr-42", "bash", "/srv/netratel",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 12, ["NETRATEL_TOKEN"], 60, 8 * 1024, createdAtUtc);
    }

    private static OrchestratorDbContext CreateDb(string? database = null, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(database ?? Guid.NewGuid().ToString("N"));
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private sealed class BeforeSaveInterceptor : SaveChangesInterceptor
    {
        public Func<CancellationToken, Task>? BeforeNextSave { get; set; }
        public int Executions { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var action = BeforeNextSave;
            BeforeNextSave = null;
            if (action is not null)
            {
                Executions++;
                await action(cancellationToken);
            }
            return result;
        }
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
