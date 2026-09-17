using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

public class ApiRouteVersioningTests
{
    private static readonly Regex UnversionedPublicApiRoutePattern = new(
        """Map(?:Group|Get|Post|Put|Delete|Patch)\("/api(?!/v[12])""",
        RegexOptions.Compiled);

    [Fact]
    public void PublicApiRoutes_UseTheSupportedV1OrV2Versions()
    {
        var repoRoot = FindRepoRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src", "NetRatel", "NetRatel.API"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var offenders = files
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new { path, line, lineNumber = index + 1 })
                .Where(candidate => UnversionedPublicApiRoutePattern.IsMatch(candidate.line))
                .Select(candidate => $"{Path.GetRelativePath(repoRoot, candidate.path)}:{candidate.lineNumber}: {candidate.line.Trim()}"))
            .ToArray();

        offenders.Should().BeEmpty("public REST API endpoints must use the supported /api/v1 or /api/v2 prefixes");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetRatel.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
