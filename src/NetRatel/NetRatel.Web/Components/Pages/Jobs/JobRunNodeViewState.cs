namespace NetRatel.Web.Components.Pages.Jobs;

/// <summary>Small UI state retained while a node panel is closed; output is fetched only while open.</summary>
public sealed class JobRunNodeViewState
{
    public HashSet<ulong> ExpandedSteps { get; } = [];
    public Dictionary<ulong, int> ActiveTabs { get; } = [];
    public bool Initialized { get; set; }
}
