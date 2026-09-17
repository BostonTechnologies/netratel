using System.Collections.Generic;

namespace NetRatel.Shared.Contracts.Requests;

public class UpdateTenantRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public List<string>? Domains { get; set; } = new();
    public string? ContactPerson { get; set; }
    public string? ContactEmail { get; set; }
    public bool? AutoUpdate { get; set; }
    public string? AutoUpdateChannel { get; set; }
    public string? AutoUpdateTargetVersion { get; set; }
}
