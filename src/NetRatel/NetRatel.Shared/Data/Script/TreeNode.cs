using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace NetRatel.Shared.Data.Script;

public class TreeNode
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty; // Unique identifier for the node (folder path or script full path)
    public bool IsFolder { get; set; }
    public ulong? Id { get; set; } // Store Script ID only if it's a file
    public string Icon { get; set; } // Default icon
    public List<TreeNode> Children { get; set; } = new List<TreeNode>();
    public bool IsExpanded { get; set; }
}
