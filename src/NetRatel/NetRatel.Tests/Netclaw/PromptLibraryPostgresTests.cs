using System.Text;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Netclaw;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class PromptLibraryPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Prompt_lifecycle_is_tenant_scoped_and_retains_revisions_after_archive()
    {
        var options = await DatabaseAsync();
        await using var db = new OrchestratorDbContext(options); var library = new PromptLibraryService(db, TimeProvider.System);
        var created = await library.CreateAsync(17, new("Offline client checks", "Check last contact before recommending changes", "# Guidance\nInspect evidence; do not reset credentials.\n"), "author");
        var id = created.Summary.Id;
        Assert.Equal(1, created.Summary.Revision); Assert.Equal("author", created.Summary.CreatedBy);
        var viewed = await library.GetAsync(17, id);
        Assert.Equal(created.Summary.Id, viewed.Summary.Id); Assert.Equal(created.Summary.Name, viewed.Summary.Name); Assert.Equal(created.Body, viewed.Body);
        Assert.Single((await library.ListAsync(17, "offline")).Items); Assert.Empty((await library.ListAsync(18)).Items);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => library.GetAsync(18, id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => library.ResolveAsync(18, new(id, 1)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => library.UpdateAsync(18, id, new("Foreign edit", null, "Foreign guidance", 1), "other"));
        var edited = await library.UpdateAsync(17, id, new("Offline client checks", null, "New guidance", 1), "editor");
        Assert.Equal(2, edited.Summary.Revision);
        Assert.Equal(created.Body, (await library.GetAsync(17, id, 1)).Body);
        var oldPin = await library.ResolveAsync(17, new(id, 1)); Assert.Equal(created.Body, oldPin.Body);
        var archived = await library.ArchiveAsync(17, id, new(2, 0), "editor");
        Assert.True(archived.Summary.Archived); Assert.Equal(3, archived.Summary.Revision);
        Assert.Empty((await library.ListAsync(17)).Items); Assert.True(Assert.Single((await library.ListAsync(17, includeArchived: true)).Items).Archived);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => library.ResolveAsync(17, new(id, 1)));
        Assert.Equal(oldPin, await library.ResolveAsync(17, new(id, 1), allowArchived: true));
        Assert.Equal(created.Body, (await library.GetAsync(17, id, 1)).Body);
        Assert.Equal(3, await db.AiPromptRevisions.CountAsync(x => x.TenantId == 17 && x.PromptId == id));
    }

    [Fact]
    public async Task Default_and_editor_revision_tokens_reject_lost_updates_and_default_archive_requires_explicit_removal()
    {
        var options = await DatabaseAsync();
        await using var firstDb = new OrchestratorDbContext(options); var first = new PromptLibraryService(firstDb, TimeProvider.System);
        var one = await first.CreateAsync(17, new("Tenant guidance", null, "Initial tenant guidance"), "author");
        var two = await first.CreateAsync(17, new("Storage checks", null, "Read storage evidence"), "author");
        Assert.Null(await first.ResolveDefaultAsync(17));
        var state = await first.SetDefaultAsync(17, new(one.Summary.Id, 0, 1));
        Assert.Equal(1, state.Revision); Assert.True(state.Prompt!.IsDefault);
        var original = state.Prompt!;
        await using var staleDb = new OrchestratorDbContext(options); var stale = new PromptLibraryService(staleDb, TimeProvider.System);
        await first.UpdateAsync(17, one.Summary.Id, new("Tenant guidance", null, "Edited tenant guidance", 1), "editor");
        await Assert.ThrowsAsync<AiPromptConflictException>(() => stale.UpdateAsync(17, one.Summary.Id, new("Stale guidance", null, "Overwrite attempt", 1), "other"));
        await Assert.ThrowsAsync<AiPromptConflictException>(() => stale.SetDefaultAsync(17, new(two.Summary.Id, 0, 1)));
        Assert.Equal("Initial tenant guidance", original.Body);
        Assert.Equal("Edited tenant guidance", (await stale.ResolveDefaultAsync(17))!.Body);
        await Assert.ThrowsAsync<AiPromptConflictException>(() => first.ArchiveAsync(17, one.Summary.Id, new(2, 1), "editor"));
        await first.ArchiveAsync(17, one.Summary.Id, new(2, 1, RemoveDefault: true), "editor");
        var empty = await first.ListAsync(17); Assert.Null(empty.Default.Prompt); Assert.Equal(2, empty.Default.Revision);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => first.SetDefaultAsync(17, new(one.Summary.Id, 2, 3)));
        await first.SetDefaultAsync(17, new(two.Summary.Id, 2, 1));
        Assert.Equal(two.Summary.Id, (await first.ResolveDefaultAsync(17))!.PromptId);
        Assert.Single(await firstDb.AiPromptDefaults.Where(x => x.TenantId == 17).ToListAsync());
        Assert.True(Assert.Single((await first.ListAsync(17)).Items).IsDefault);
    }

    [Fact]
    public async Task Prompt_validation_reports_utf8_and_invalid_text_limits_without_truncation()
    {
        var options = await DatabaseAsync(); await using var db = new OrchestratorDbContext(options);
        var library = new PromptLibraryService(db, TimeProvider.System);
        var body = new string('é', AiPromptLimits.MaxBodyUtf8Bytes / 2);
        Assert.Equal(AiPromptLimits.MaxBodyUtf8Bytes, Encoding.UTF8.GetByteCount(body));
        Assert.Equal(body, (await library.CreateAsync(17, new("Byte boundary", null, body), "operator")).Body);
        await Assert.ThrowsAsync<ArgumentException>(() => library.CreateAsync(17, new("Too large", null, body + "é"), "operator"));
        await Assert.ThrowsAsync<ArgumentException>(() => library.CreateAsync(17, new("Bad\nname", null, "Guidance"), "operator"));
        await Assert.ThrowsAsync<ArgumentException>(() => library.CreateAsync(17, new("Invalid surrogate", null, "\ud800"), "operator"));
        Assert.Single((await library.ListAsync(17)).Items);
    }

    private async Task<DbContextOptions<OrchestratorDbContext>> DatabaseAsync()
    {
        var connection = await fixture.CreateDatabaseAsync(); var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        await using var db = new OrchestratorDbContext(options); Assert.False(db.Database.HasPendingModelChanges()); await db.Database.MigrateAsync();
        db.Tenants.AddRange(new Tenant { Id = 17, Name = "Prompt operations" }, new Tenant { Id = 18, Name = "Other tenant" });
        await db.SaveChangesAsync(); return options;
    }
}
