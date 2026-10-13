using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentUpdateScriptSeedPlacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_startup_preserves_current_canonical_and_legacy_seed_identity(bool legacyPlacement)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var created = await fixture.ReadAsync();
        Assert.Equal(2, created.Count);
        Assert.Contains(created, script => script.FolderPath == "/Windows/NetRatel/");
        Assert.Contains(created, script => script.FolderPath == "/Linux/NetRatel/");

        if (legacyPlacement)
        {
            await using var db = fixture.CreateDatabase();
            foreach (var script in await db.Scripts.ToListAsync())
            {
                script.FolderPath = script.FolderPath.TrimEnd('/').Replace('/', '\\');
                script.Name = script.Name.ToLowerInvariant();
            }
            await db.SaveChangesAsync();
        }

        var before = await fixture.ReadAsync();
        await fixture.StartAsync();
        await fixture.StartAsync();
        AssertUnchanged(before, await fixture.ReadAsync());
    }

    [Fact]
    public async Task Updating_a_legacy_seed_preserves_placement_and_advances_source_once()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var desired = Assert.Single(await fixture.ReadAsync(), script => script.ScriptType == "PowerShell");
        var before = await MakeWindowsSeedStaleAsync(fixture, desired.Id);

        await fixture.StartAsync();
        var after = Assert.Single(await fixture.ReadAsync(), script => script.Id == desired.Id);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.FolderPath, after.FolderPath);
        Assert.Equal(desired.Content, after.Content);
        Assert.Equal(desired.Description, after.Description);
        Assert.Equal(desired.ManifestJson, after.ManifestJson);
        Assert.Equal(before.SourceRevision + 1, after.SourceRevision);

        var current = await fixture.ReadAsync();
        await fixture.StartAsync();
        AssertUnchanged(current, await fixture.ReadAsync());
    }

    [Fact]
    public async Task Concurrent_first_start_reconciles_only_the_exact_committed_seed()
    {
        await using var fixture = new Fixture();
        fixture.Race.Mode = RaceMode.CreateWinner;

        await fixture.StartAsync();

        var scripts = await fixture.ReadAsync();
        Assert.Equal(1, fixture.Race.CreateCollisions);
        Assert.Equal(2, scripts.Count);
        Assert.All(scripts, script => Assert.Equal(1, script.SourceRevision));
        Assert.Contains(scripts, script => script.FolderPath == "/Windows/NetRatel/");
        Assert.Contains(scripts, script => script.FolderPath == "/Linux/NetRatel/");
        await fixture.StartAsync();
        AssertUnchanged(scripts, await fixture.ReadAsync());
    }

    [Fact]
    public async Task A_different_source_created_at_the_seed_path_is_not_overwritten()
    {
        await using var fixture = new Fixture();
        fixture.Race.Mode = RaceMode.CreateDifferentSourceWinner;

        await Assert.ThrowsAsync<ScriptLibraryCollisionException>(fixture.StartAsync);

        Assert.Equal(1, fixture.Race.CreateCollisions);
        var winner = Assert.Single(await fixture.ReadAsync());
        Assert.Equal("/Windows/NetRatel/", winner.FolderPath);
        Assert.Equal("Update Client To Latest", winner.Name);
        Assert.Equal("operator source", winner.Content);
        Assert.Equal("operator description", winner.Description);
        Assert.Equal("Python", winner.ScriptType);
        Assert.Equal(1, winner.SourceRevision);
    }

    [Fact]
    public async Task An_ancestor_file_collision_still_fails_startup_without_overwriting_it()
    {
        await using var fixture = new Fixture();
        await using (var db = fixture.CreateDatabase())
            await new ScriptService(db).CreateAsync(new("Windows", "/", "operator source", "saved", "PowerShell", null));
        var before = await fixture.ReadAsync();

        await Assert.ThrowsAsync<ScriptLibraryCollisionException>(fixture.StartAsync);

        AssertUnchanged(before, await fixture.ReadAsync());
    }

    [Fact]
    public async Task A_reconciled_losing_update_disposes_its_dirty_context_before_the_next_seed_write()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var scripts = await fixture.ReadAsync();
        var desired = Assert.Single(scripts, script => script.ScriptType == "PowerShell");
        var linux = Assert.Single(scripts, script => script.ScriptType == "Bash");
        var before = await MakeWindowsSeedStaleAsync(fixture, desired.Id);
        await using (var db = fixture.CreateDatabase())
            await new ScriptService(db).DeleteAsync(linux.Id);
        fixture.Race.Mode = RaceMode.UpdateWinner;

        await fixture.StartAsync();

        Assert.Equal(1, fixture.Race.UpdateConcurrencyFailures);
        Assert.True(fixture.Race.LosingContextRemainedModified);
        var current = await fixture.ReadAsync();
        Assert.Equal(2, current.Count);
        var windows = Assert.Single(current, script => script.Id == desired.Id);
        Assert.Equal(desired.Content, windows.Content);
        Assert.Equal(desired.Description, windows.Description);
        Assert.Equal(before.Name, windows.Name);
        Assert.Equal(before.FolderPath, windows.FolderPath);
        Assert.Equal(before.SourceRevision + 1, windows.SourceRevision);
        var replacementLinux = Assert.Single(current, script => script.ScriptType == "Bash");
        Assert.NotEqual(linux.Id, replacementLinux.Id);
        Assert.Equal(1, replacementLinux.SourceRevision);
    }

    [Fact]
    public async Task A_deleted_seed_is_not_reported_as_a_successful_update()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var windows = Assert.Single(await fixture.ReadAsync(), script => script.ScriptType == "PowerShell");
        await MakeWindowsSeedStaleAsync(fixture, windows.Id);
        fixture.Race.Mode = RaceMode.DeleteBeforeUpdate;

        await Assert.ThrowsAsync<ScriptSourceConcurrencyException>(fixture.StartAsync);

        Assert.DoesNotContain(await fixture.ReadAsync(), script => script.Id == windows.Id);
    }

    private static async Task<ScriptInfo> MakeWindowsSeedStaleAsync(Fixture fixture, ulong id)
    {
        await using var db = fixture.CreateDatabase();
        var row = await db.Scripts.SingleAsync(script => script.Id == (long)id);
        row.Name = row.Name.ToLowerInvariant();
        row.FolderPath = row.FolderPath.TrimEnd('/');
        row.Content = "old seed";
        row.Description = "old description";
        await db.SaveChangesAsync();
        return (await new ScriptService(db).GetAsync(id))!;
    }

    private static void AssertUnchanged(IReadOnlyList<ScriptInfo> before, IReadOnlyList<ScriptInfo> after)
    {
        Assert.Equal(before.Count, after.Count);
        foreach (var expected in before)
        {
            var actual = Assert.Single(after, script => script.Id == expected.Id);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.FolderPath, actual.FolderPath);
            Assert.Equal(expected.Content, actual.Content);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(expected.ScriptType, actual.ScriptType);
            Assert.Equal(expected.ManifestJson, actual.ManifestJson);
            Assert.Equal(expected.SourceRevision, actual.SourceRevision);
            Assert.Equal(expected.UpdatedAtUtc, actual.UpdatedAtUtc);
        }
    }

    private enum RaceMode { None, CreateWinner, CreateDifferentSourceWinner, UpdateWinner, DeleteBeforeUpdate }

    private sealed class RaceState
    {
        public RaceMode Mode { get; set; }
        public bool Taken { get; set; }
        public int CreateCollisions { get; set; }
        public int UpdateConcurrencyFailures { get; set; }
        public bool LosingContextRemainedModified { get; set; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<OrchestratorDbContext> _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        private readonly ServiceProvider _provider;
        public RaceState Race { get; } = new();

        public Fixture()
        {
            _provider = new ServiceCollection()
                .AddScoped(_ => CreateDatabase())
                .AddScoped<IScriptService>(services => new RacingScriptService(services.GetRequiredService<OrchestratorDbContext>(), _options, Race))
                .BuildServiceProvider();
        }

        public OrchestratorDbContext CreateDatabase() => new(_options);
        public Task StartAsync() => new AgentUpdateScriptSeedService(_provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentUpdateScriptSeedService>.Instance).StartAsync(CancellationToken.None);
        public async Task<IReadOnlyList<ScriptInfo>> ReadAsync()
        {
            await using var db = CreateDatabase();
            return await new ScriptService(db).ListAsync();
        }
        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    // Inject the competing host at a precise read/write boundary while keeping
    // collision, revision validation and failed tracked state in the real service.
    private sealed class RacingScriptService(OrchestratorDbContext db, DbContextOptions<OrchestratorDbContext> options, RaceState race) : IScriptService
    {
        private readonly ScriptService _inner = new(db);
        public Task<IReadOnlyList<ScriptInfo>> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);
        public Task<ScriptInfo?> GetAsync(ulong id, CancellationToken ct = default) => _inner.GetAsync(id, ct);
        public Task<IReadOnlyList<ScriptParamInfo>> GetParamsAsync(ulong id, CancellationToken ct = default) => _inner.GetParamsAsync(id, ct);
        public Task<ScriptInfo?> DeleteAsync(ulong id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
        public Task<ScriptInfo?> ParseManifestAsync(ulong id, string? manifest, CancellationToken ct = default) => _inner.ParseManifestAsync(id, manifest, ct);

        public async Task<ScriptInfo> CreateAsync(CreateScriptCommand command, CancellationToken ct = default)
        {
            if (!race.Taken && race.Mode is RaceMode.CreateWinner or RaceMode.CreateDifferentSourceWinner)
            {
                race.Taken = true;
                await using var winner = new OrchestratorDbContext(options);
                var competingCommand = race.Mode == RaceMode.CreateDifferentSourceWinner
                    ? command with { Content = "operator source", Description = "operator description", ScriptType = "Python" }
                    : command;
                await new ScriptService(winner).CreateAsync(competingCommand, ct);
                try { return await _inner.CreateAsync(command, ct); }
                catch (ScriptLibraryCollisionException) { race.CreateCollisions++; throw; }
            }
            return await _inner.CreateAsync(command, ct);
        }

        public async Task<ScriptInfo?> UpdateAsync(UpdateScriptCommand command, CancellationToken ct = default)
        {
            if (!race.Taken && race.Mode is RaceMode.UpdateWinner or RaceMode.DeleteBeforeUpdate)
            {
                race.Taken = true;
                await using var winner = new OrchestratorDbContext(options);
                if (race.Mode == RaceMode.DeleteBeforeUpdate)
                    await new ScriptService(winner).DeleteAsync(command.ScriptId, ct);
                else
                {
                    await db.Scripts.Include(script => script.Parameters).SingleAsync(script => script.Id == (long)command.ScriptId, ct);
                    await new ScriptService(winner).UpdateAsync(command, ct);
                    try { return await _inner.UpdateAsync(command, ct); }
                    catch (DbUpdateConcurrencyException)
                    {
                        race.UpdateConcurrencyFailures++;
                        race.LosingContextRemainedModified = db.ChangeTracker.Entries<ScriptDefinition>()
                            .Any(entry => entry.State == EntityState.Modified);
                        throw;
                    }
                }
            }
            return await _inner.UpdateAsync(command, ct);
        }
    }
}
