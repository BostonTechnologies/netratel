using System.Collections.Generic;

namespace NetRatel.Web.Components.ScriptLibrary;

public class ScriptTreeNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; } // always ends with "/" for folders
    public bool IsFolder { get; init; }
    public ulong? Id { get; init; } // only for files
    public string? Icon { get; init; }
    public bool IsExpanded { get; set; }
    public List<ScriptTreeNode> Children { get; } = new();
}
