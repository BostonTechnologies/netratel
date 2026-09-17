using System;
using System.Collections.Generic;
using System.Linq;
using MudBlazor;
using NetRatel.Shared.Contracts.Scripts;

namespace NetRatel.Web.Components.ScriptLibrary;

public static class ScriptTreeBuilder
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public static List<ScriptTreeNode> Build(
        IEnumerable<ScriptDto> scripts,
        string? filterByType,
        string? search,
        ISet<string>? expanded = null)
    {
        var expandedSet = expanded ?? new HashSet<string>(Cmp);
        var srch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var filtered = scripts
            .Where(s => string.IsNullOrWhiteSpace(filterByType) || Cmp.Equals(s.ScriptType ?? string.Empty, filterByType))
            .Where(s => srch is null
                        || (!string.IsNullOrWhiteSpace(s.Name) && s.Name.Contains(srch, StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrWhiteSpace(s.FolderPath) && s.FolderPath.Contains(srch, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var folders = new Dictionary<string, ScriptTreeNode>(Cmp)
        {
            ["/"] = new ScriptTreeNode
            {
                Name = "/",
                FullPath = "/",
                IsFolder = true,
                IsExpanded = true
            }
        };

        foreach (var script in filtered)
        {
            var folderPath = NormalizeFolder(script.FolderPath);
            EnsureFolderChain(folders, folderPath, expandedSet);

            var fileName = string.IsNullOrWhiteSpace(script.Name) ? $"Script {script.Id}" : script.Name;
            var fileNode = new ScriptTreeNode
            {
                Name = fileName,
                FullPath = folderPath + fileName,
                Id = script.Id,
                IsFolder = false,
                Icon = IconFor(script.ScriptType),
                IsExpanded = false
            };

            if (folders.TryGetValue(folderPath, out var parent))
            {
                if (!parent.Children.Any(c => !c.IsFolder && c.Id == script.Id))
                {
                    parent.Children.Add(fileNode);
                }
            }
        }

        SortRecursive(folders["/"]);
        return folders["/"].Children;
    }

    private static string NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return normalized;
    }

    private static void EnsureFolderChain(Dictionary<string, ScriptTreeNode> dict, string path, ISet<string> expanded)
    {
        if (dict.ContainsKey(path))
        {
            return;
        }

        var parentPath = "/";
        if (!string.Equals(path, "/", StringComparison.Ordinal))
        {
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            parentPath = idx <= 0 ? "/" : trimmed[..(idx + 1)];
            EnsureFolderChain(dict, parentPath, expanded);
        }

        var name = string.Equals(path, "/", StringComparison.Ordinal)
            ? "/"
            : path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

        var node = new ScriptTreeNode
        {
            Name = name,
            FullPath = path,
            IsFolder = true,
            IsExpanded = expanded.Contains(path)
        };

        dict[path] = node;

        if (dict.TryGetValue(parentPath, out var parent))
        {
            if (!parent.Children.Any(c => c.IsFolder && Cmp.Equals(c.FullPath, path)))
            {
                parent.Children.Add(node);
            }
        }
    }

    private static void SortRecursive(ScriptTreeNode node)
    {
        node.Children.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder)
            {
                return a.IsFolder ? -1 : 1;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var child in node.Children.Where(c => c.IsFolder))
        {
            SortRecursive(child);
        }
    }

    private static string IconFor(string? type) => (type ?? string.Empty).ToLowerInvariant() switch
    {
        "powershell" => Icons.Material.Filled.Terminal,
        "bash" or "shell" => Icons.Material.Filled.Terminal,
        "python" => Icons.Material.Filled.Code,
        "javascript" or "typescript" => Icons.Material.Filled.Code,
        "sql" => Icons.Material.Filled.DataObject,
        "json" => Icons.Material.Filled.DataArray,
        _ => Icons.Material.Filled.InsertDriveFile
    };
}
