namespace NetRatel.Web.Components.Shared.Netclaw;

public sealed class ChatDisplayOptions
{
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowCallIds { get; set; }
    public bool ExpandDetails { get; set; }
    public bool ShowUsage { get; set; }
    public bool ShowProcessing { get; set; }
    public bool ShowCompaction { get; set; }
    public bool ShowSubagents { get; set; }
}
