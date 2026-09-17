using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorScriptStoreTests
{
    [Fact]
    public async Task Shared_source_edit_prevents_reviewed_replacement_from_silently_overwriting_it()
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow);
        var store = new McpOperatorScriptStore(db);
        var created = await store.CreateAsync(request, CancellationToken.None);
        var source = await db.Scripts.SingleAsync();
        source.Content = "external UI edit";
        await db.SaveChangesAsync();
        source.SourceRevision.Should().Be(2);
        var replace = () => store.ReplaceAsync(new(created.ScriptId, created.Version, request.Decision,
            request.AcceptedAudit, request.Draft, DateTimeOffset.UtcNow), CancellationToken.None);
        await replace.Should().ThrowAsync<McpOperatorScriptIntegrityException>();
        (await db.Scripts.SingleAsync()).Content.Should().Be("external UI edit");
        (await db.McpOperatorScriptVersions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_RequiresExplicitCallerOwnershipAndCreatesImmutableRevisionEvidence()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now);
        var store = new McpOperatorScriptStore(db);

        var created = await store.CreateAsync(request, CancellationToken.None);
        var other = request.Decision.Request.Principal with { Subject = "other@example.test" };
        var hidden = await store.GetOwnedAsync(created.ScriptId, 42, other, "https://mcp.prod.example/mcp", "prod", CancellationToken.None);

        created.TenantId.Should().Be(42);
        created.Version.Should().Be(1);
        created.ContentHash.Should().Be(Hash(request.Draft.Content));
        hidden.Should().BeNull();
        (await db.McpOperatorScriptVersions.SingleAsync()).Should().Match<McpOperatorScriptVersionRecord>(row =>
            row.Action == "created" && row.ScriptVersion == 1 && row.ContentHash == created.ContentHash);
    }

    [Fact]
    public async Task ReplaceAsync_RejectsStaleEtagAndPreservesThePreviousRevisionHash()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now);
        var store = new McpOperatorScriptStore(db);
        var created = await store.CreateAsync(request, CancellationToken.None);
        var updatedDraft = request.Draft with { Content = "Write-Output 'updated'", ContentHash = Hash("Write-Output 'updated'") };
        var replace = new McpOperatorScriptReplaceRequest(created.ScriptId, created.Version, request.Decision, request.AcceptedAudit, updatedDraft, now.AddMinutes(1));

        var updated = await store.ReplaceAsync(replace, CancellationToken.None);
        var stale = () => store.ReplaceAsync(replace, CancellationToken.None);
        var versions = await db.McpOperatorScriptVersions.OrderBy(row => row.ScriptVersion).ToArrayAsync();

        updated.Should().NotBeNull();
        updated!.Version.Should().Be(2);
        await stale.Should().ThrowAsync<McpOperatorScriptConcurrencyException>();
        versions.Select(row => row.ContentHash).Should().Equal(Hash(request.Draft.Content), Hash(updatedDraft.Content));
    }

    [Fact]
    public async Task CreateAsync_RejectsASecretParameterDefaultValue()
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow) with
        {
            Draft = CreateRequest(DateTimeOffset.UtcNow).Draft with
            {
                Parameters = [new McpOperatorScriptParameter("token", "secret_reference", true, DefaultValue: "not-allowed", SecretReference: "API_TOKEN")]
            }
        };

        var create = () => new McpOperatorScriptStore(db).CreateAsync(request, CancellationToken.None);

        await create.Should().ThrowAsync<ArgumentException>();
        db.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOwnedAsync_RejectsBackingContentThatNoLongerMatchesTheApprovedRevision()
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow);
        var store = new McpOperatorScriptStore(db);
        var created = await store.CreateAsync(request, CancellationToken.None);
        var backing = await db.Scripts.SingleAsync();
        backing.Content = "Write-Output 'changed outside the operator revision'";
        await db.SaveChangesAsync();

        var read = () => store.GetOwnedAsync(created.ScriptId, 42, request.Decision.Request.Principal,
            "https://mcp.prod.example/mcp", "prod", CancellationToken.None);

        await read.Should().ThrowAsync<McpOperatorScriptIntegrityException>();
    }

    [Fact]
    public async Task CreateAsync_RejectsObviousSecretLiteralsInScriptContent()
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow);
        const string content = "apiKey = 'not-a-secret-fixture'";
        request = request with { Draft = request.Draft with { Content = content, ContentHash = Hash(content) } };

        var create = () => new McpOperatorScriptStore(db).CreateAsync(request, CancellationToken.None);

        await create.Should().ThrowAsync<ArgumentException>();
        db.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task OidcAzpOnlyIdentity_CanReadItsCreatedScriptWhileOtherSubjectsRemainHidden()
    {
        await using var db = CreateDb();
        var request = CreateRequest(DateTimeOffset.UtcNow);
        var principal = request.Decision.Request.Principal with { ClientId = null, AuthorizedParty = "https://mcp.example/mcp" };
        request = request with
        {
            Decision = request.Decision with { Request = request.Decision.Request with { Principal = principal } },
            AcceptedAudit = request.AcceptedAudit with { ClientId = null, AuthorizedParty = principal.AuthorizedParty }
        };
        var store = new McpOperatorScriptStore(db);
        var created = await store.CreateAsync(request, CancellationToken.None);
        var read = await store.GetOwnedAsync(created.ScriptId, 42, principal, "https://mcp.prod.example/mcp", "prod", CancellationToken.None);
        read.Should().NotBeNull();
        read!.Value.Content.Should().Be(request.Draft.Content);
        (await store.ListOwnedAsync(42, principal, "https://mcp.prod.example/mcp", "prod", CancellationToken.None))
            .Should().ContainSingle(script => script.ScriptId == created.ScriptId);
        (await store.GetOwnedAsync(created.ScriptId, 42, principal with { Subject = "another-user" },
            "https://mcp.prod.example/mcp", "prod", CancellationToken.None)).Should().BeNull();
        (await store.GetOwnedAsync(created.ScriptId, 42, principal,
            "https://another.example/mcp", "prod", CancellationToken.None)).Should().BeNull();
    }

    private static McpOperatorScriptCreateRequest CreateRequest(DateTimeOffset now)
    {
        var agentId = Guid.Parse("bb69ba7b-1e56-4ea8-bc36-533950b50610");
        var policyId = Guid.Parse("9252da81-08cf-4f62-a546-241428329d2b");
        var auditId = Guid.Parse("3252da81-08cf-4f62-a546-241428329d2b");
        var principal = new McpOperatorPrincipal("operator@example.test", "operator-client", "operator-client", Set(), Set(), Set("netratel.mcp.write"));
        var access = new McpOperatorAccessRequest(McpOperatorEnvironment.Production, principal, 42, agentId,
            McpOperatorTargetClassification.ManagedStandard, McpOperatorOperationFamily.ScriptsWrite, "netratel_scripts/create",
            Set("netratel.mcp.write"), McpOperatorConfirmationClass.StandardMutation, "corr-42", "request-42",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", McpResource: "https://mcp.prod.example/mcp", McpInstance: "prod", Tool: "netratel_scripts");
        var constraints = new McpOperatorConstraints(AllowedShells: ["powershell"], WorkingDirectories: ["C:\\NetRatel"],
            MaxCommandDurationSeconds: 300, MaxScriptBytes: 64 * 1024, MaxTaskTargetCount: 1, MaxFanOut: 1);
        var decision = new McpOperatorDecision(true, null, null, [policyId], constraints, access.TargetSetDigest, access, 4);
        var audit = new McpOperatorAcceptedAudit(auditId, policyId, access.Environment, "netratel-mcp-http-prod", principal.Subject,
            principal.ClientId, principal.AuthorizedParty, [], [], principal.Scopes.Order(StringComparer.Ordinal).ToArray(), access.McpResource,
            access.McpInstance, access.Tool, access.TenantId, access.AgentId, access.OperationFamily, access.Operation, access.CorrelationId,
            access.RequestId, now);
        const string content = "Write-Output 'safe'";
        var draft = new McpOperatorScriptDraft("safe-script", "Safe test script", "powershell", content, Hash(content), [], 60,
            "C:\\NetRatel", [McpOperatorScriptSideEffect.ReadOnly]);
        return new McpOperatorScriptCreateRequest(decision, audit, draft, now);
    }

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
