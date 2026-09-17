namespace NetRatel.API.Models;

public sealed class ClientArtifactSummaryDto
{
    public string Rid { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class ClientArtifactListDto
{
    public int Total { get; set; }
    public IReadOnlyList<ClientArtifactSummaryDto> Items { get; set; } = Array.Empty<ClientArtifactSummaryDto>();
}

public sealed class ClientArtifactUploadResultDto
{
    public ClientArtifactSummaryDto Artifact { get; set; } = new();
    public bool Created { get; set; }
}
