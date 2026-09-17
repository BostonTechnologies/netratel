using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NetRatel.Shared.Contracts.Requests;

public class CreateTenantRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public List<string>? Domains { get; set; } = new();
    public string? ContactPerson { get; set; }
    public string? ContactEmail { get; set; }
    public bool AutoUpdate { get; set; }
    public string AutoUpdateChannel { get; set; } = "stable";
    public string? AutoUpdateTargetVersion { get; set; }
}
