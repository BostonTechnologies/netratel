using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorFileArtifactStoreTests
{
    [Fact]
    public async Task CreateOrGetAsync_BindsTheArtifactToItsCallerAndWipesBytesOnCleanup()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now);
        var store = new McpOperatorFileArtifactStore(db);

        var created = await store.CreateOrGetAsync(request, CancellationToken.None);
        var replay = await store.CreateOrGetAsync(request, CancellationToken.None);
        var owned = await store.GetOwnedAsync(created.ArtifactId, 42, request.Access.AgentId, request.Access.Principal,
            request.Access.McpResource, request.Access.McpInstance, includeContent: true, CancellationToken.None);
        var otherCaller = request.Access.Principal with { Subject = "other@example.test" };
        var hidden = await store.GetOwnedAsync(created.ArtifactId, 42, request.Access.AgentId, otherCaller,
            request.Access.McpResource, request.Access.McpInstance, includeContent: true, CancellationToken.None);
        var conflictingReplay = request with { ReadRootFingerprint = new string('B', 64) };
        Func<Task> createConflictingReplay = () => store.CreateOrGetAsync(conflictingReplay, CancellationToken.None);

        created.Content.Should().BeNull();
        replay.ArtifactId.Should().Be(created.ArtifactId);
        owned!.Content.Should().Equal(request.Content);
        hidden.Should().BeNull();
        await createConflictingReplay.Should().ThrowAsync<InvalidOperationException>();

        var cleaned = await store.CleanupOwnedAsync(created.ArtifactId, 42, request.Access.AgentId, request.Access.Principal,
            request.Access.McpResource, request.Access.McpInstance, now.AddMinutes(1), CancellationToken.None);

        cleaned!.Content.Should().BeNull();
        cleaned.DeletedAtUtc.Should().Be(now.AddMinutes(1));
        (await db.McpOperatorFileArtifacts.SingleAsync()).Content.Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeExpiredAsync_WipesOnlyExpiredArtifactBytes()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var expired = CreateRequest(now.AddMinutes(-2)) with { ExpiresAtUtc = now.AddMinutes(-1) };
        var current = CreateRequest(now) with { IdempotencyId = Guid.NewGuid() };
        var store = new McpOperatorFileArtifactStore(db);

        await store.CreateOrGetAsync(expired, CancellationToken.None);
        await store.CreateOrGetAsync(current, CancellationToken.None);

        var purged = await store.PurgeExpiredAsync(now, 100, CancellationToken.None);
        var records = await db.McpOperatorFileArtifacts.OrderBy(record => record.CreatedAtUtc).ToListAsync();

        purged.Should().Be(1);
        records.Should().ContainSingle(record => record.IdempotencyId == expired.IdempotencyId)
            .Which.Content.Should().BeEmpty();
        records.Should().ContainSingle(record => record.IdempotencyId == current.IdempotencyId)
            .Which.Content.Should().NotBeEmpty();
    }

    private static McpOperatorFileArtifactCreateRequest CreateRequest(DateTimeOffset createdAtUtc)
    {
        var principal = new McpOperatorPrincipal(
            "operator@example.test",
            "operator-client",
            "operator-client",
            Set(),
            Set(),
            Set("netratel.mcp.files"));
        var access = new McpOperatorRouteAccessRequest(
            McpOperatorEnvironment.Production,
            principal,
            "netratel-mcp-http-prod",
            "https://mcp.prod.example/mcp",
            "prod",
            "netratel_files",
            "collect",
            42,
            Guid.Parse("bb69ba7b-1e56-4ea8-bc36-533950b50610"),
            Set("netratel.mcp.files"),
            "corr-42",
            "request-42",
            TargetOnline: true,
            CapabilityAvailable: true);
        return new McpOperatorFileArtifactCreateRequest(
            access,
            Guid.Parse("3252da81-08cf-4f62-a546-241428329d2b"),
            Guid.Parse("6e5bd941-9209-4bb4-a432-2e3d1c2ddcd9"),
            new string('A', 64),
            "report.txt",
            "text/plain",
            "bounded artifact"u8.ToArray(),
            createdAtUtc,
            createdAtUtc.AddMinutes(15));
    }

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
