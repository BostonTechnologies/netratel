using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Infrastructure.Secrets;

internal static class SecretFolderPage
{
    public const int EntryLimit = 1000;

    public static SecretBrowsePage Create(IEnumerable<SecretEntryMetadata> entries, SecretBrowseQuery query, string searchScope)
    {
        var folder = entries.Take(EntryLimit + 1).ToArray();
        var filtered = folder.Take(EntryLimit)
            .Where(entry => query.Search is null || entry.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => Key(entry), StringComparer.Ordinal)
            .Where(entry => query.Cursor is null || StringComparer.Ordinal.Compare(Key(entry), query.Cursor) > 0)
            .Take(query.Limit + 1).ToArray();
        return new(filtered.Take(query.Limit).ToArray(), filtered.Length > query.Limit ? Key(filtered[query.Limit - 1]) : null,
            folder.Length > EntryLimit, searchScope);
    }

    private static string Key(SecretEntryMetadata entry) => entry.IsFolder ? entry.Locator + "/" : entry.Locator;
}
