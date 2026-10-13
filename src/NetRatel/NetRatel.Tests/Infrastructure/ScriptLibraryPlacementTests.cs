using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Scripts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ScriptLibraryPlacementTests
{
    [Theory]
    [InlineData("/operations/../windows/")]
    [InlineData("/operations/./windows/")]
    [InlineData("/operations/\n/windows/")]
    [InlineData("C:\\Windows")]
    public async Task Service_rejects_invalid_logical_paths_without_creating_records(string folder)
    {
        await using var db = CreateDatabase();
        await Assert.ThrowsAsync<ArgumentException>(() => new ScriptService(db).CreateAsync(Command("sample", folder)));
        Assert.Empty(await db.Scripts.ToListAsync());
    }

    [Fact]
    public async Task Service_normalizes_nested_paths_and_rejects_file_folder_and_case_collisions()
    {
        await using var db = CreateDatabase();
        var service = new ScriptService(db);
        var created = await service.CreateAsync(Command(" sample.ps1 ", "operations\\nested//"));
        Assert.Equal("/operations/nested/", created.FolderPath);
        Assert.Equal("sample.ps1", created.Name);
        await Assert.ThrowsAsync<ScriptLibraryCollisionException>(() => service.CreateAsync(Command("SAMPLE.ps1", "/OPERATIONS/nested/")));
        await Assert.ThrowsAsync<ScriptLibraryCollisionException>(() => service.CreateAsync(Command("operations", "/")));
        await service.CreateAsync(Command("reserved", "/"));
        await Assert.ThrowsAsync<ScriptLibraryCollisionException>(() => service.CreateAsync(Command("sample", "/reserved/nested/")));
        Assert.Equal(2, await db.Scripts.CountAsync());
    }

    [Fact]
    public async Task Invalid_or_conflicting_move_preserves_committed_source_content_and_revision()
    {
        await using var db = CreateDatabase();
        var service = new ScriptService(db);
        var first = await service.CreateAsync(Command("one", "/"));
        await service.CreateAsync(Command("two", "/"));
        await Assert.ThrowsAsync<ScriptLibraryCollisionException>(() => service.UpdateAsync(new(first.Id, "two", null, null, "replacement", null, null, first.SourceRevision)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(new(first.Id, null, "/../", null, "replacement", null, null, first.SourceRevision)));
        db.ChangeTracker.Clear();
        var saved = await service.GetAsync(first.Id);
        Assert.Equal("one", saved!.Name);
        Assert.Equal("saved", saved.Content);
        Assert.Equal(first.SourceRevision, saved.SourceRevision);
    }

    [Fact]
    public void Length_name_control_and_required_type_validation_are_bounded()
    {
        Assert.Throws<ArgumentException>(() => ScriptLibraryPlacement.NormalizeName("\nsample"));
        Assert.Throws<ArgumentException>(() => ScriptLibraryPlacement.NormalizeName("../sample"));
        Assert.Throws<ArgumentException>(() => ScriptLibraryPlacement.NormalizeName(new string('a', 201)));
        Assert.Throws<ArgumentException>(() => ScriptLibraryPlacement.NormalizeFolder(new string('a', 512)));
        Assert.Equal("/", ScriptLibraryPlacement.NormalizeFolder(null));
    }

    private static CreateScriptCommand Command(string name, string folder) => new(name, folder, "", "saved", "PowerShell", null);
    private static OrchestratorDbContext CreateDatabase() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
