using System.Text.Json.Serialization;
using NetRatel.Shared;

namespace NetRatel.API.Models;

public sealed class ClientScriptRequest
{
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int TenantId { get; set; }

    public string RuntimeId { get; set; } = "win-x64";

    /// <summary>
    /// Optional immutable artifact version. When supplied, the generated script
    /// downloads precisely this version rather than whichever artifact is latest.
    /// </summary>
    public string? ArtifactVersion { get; set; }

    public int ValidForMinutes { get; set; } = 60;

    public int? MaxUses { get; set; } = 1;

    public bool InstallAsService { get; set; } = true;

    public bool SilentInstall { get; set; } = true;
}
