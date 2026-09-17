using System.Text.Json.Serialization;
using NetRatel.Shared;

namespace NetRatel.API.Models;

public sealed class ClientDownloadRequest
{
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int TenantId { get; set; }

    public ClientEnvironment Environment { get; set; } = ClientEnvironment.Dev;

    public string RuntimeId { get; set; } = "win-x64";

    public string Version { get; set; } = "latest";

    public bool InjectEnrollment { get; set; }

    public int? ValidForMinutes { get; set; }

    public Guid? EnrollmentCodeId { get; set; }

    public int? MaxUses { get; set; }
}
