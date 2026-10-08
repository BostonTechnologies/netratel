namespace NetRatel.Shared.Service.Shells
{
    public sealed class ShellCapability
    {
        public string Keyword { get; set; } = "";
        public string? Path { get; set; }
        public string? Version { get; set; }
    }

    public sealed class ShellInventory
    {
        // Ensure we always have a list to mutate:
        public List<ShellCapability> Shells { get; init; } = new();

        // Resolve missing inventory entries with the same search used for reporting.
        public ShellCapability? Find(string keyword)
        {
            var match = Shells.FirstOrDefault(shell =>
                string.Equals(shell.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match?.Path) && File.Exists(match.Path)) return match;
            var path = ShellExecutableResolver.Resolve(keyword);
            return path is null ? null : new ShellCapability { Keyword = keyword, Path = path };
        }
    }

    public static class ClientRuntime
    {
        // Singleton instance; don't replace it elsewhere
        public static ShellInventory Shells { get; } = new ShellInventory();
    }
}
