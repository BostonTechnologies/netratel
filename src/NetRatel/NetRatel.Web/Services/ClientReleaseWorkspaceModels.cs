namespace NetRatel.Web.Services;

public sealed class ClientReleaseWorkspacePageModel
{
    public List<ClientReleaseWorkspaceRowModel> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; } = 20;
    public bool CanManageCleanup { get; set; } = true;
    public int Total { get; set; }
    public DateTimeOffset RefreshedAtUtc { get; set; }
    public string? Warning { get; set; }
    public string[] SelectableVersions { get; set; } = [];
}
public sealed class ClientReleaseWorkspaceRowModel
{
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string SourceState { get; set; } = "Unknown";
    public GitHubClientReleaseModel? Remote { get; set; }
    public List<ClientArtifactSummaryModel> Packages { get; set; } = [];
    public List<ClientReleaseImportModel> Imports { get; set; } = [];
    public List<ClientUpdateReleaseModel> Releases { get; set; } = [];
    public ClientReleaseDeletionStatusModel? Cleanup { get; set; }
    public bool HasStaging { get; set; }
    public bool HasLocal => HasStaging || Packages.Count != 0 || Imports.Count != 0 || Releases.Count != 0;
    public long ArchiveBytes => Packages.Sum(x => x.Size);
}
public sealed class ClientReleaseDeletionBlockerModel
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string Action { get; set; } = "";
    public string? RuntimeId { get; set; }
    public Guid? ReferenceId { get; set; }
    public int? TenantId { get; set; }
    public Guid? AgentId { get; set; }
    public string? State { get; set; }
    public DateTimeOffset? EvidenceAtUtc { get; set; }
    public string? VersionRole { get; set; }
    public bool CanResolve { get; set; }
}
public sealed class ClientReleaseBatchResultModel
{
    public string Version { get; set; } = "";
    public string State { get; set; } = "";
    public string? Error { get; set; }
    public ClientReleaseDeletionStatusModel? Receipt { get; set; }
}
