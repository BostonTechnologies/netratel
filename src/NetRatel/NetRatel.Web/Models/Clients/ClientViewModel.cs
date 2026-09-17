using NetRatel.Shared.Contracts;
using NetRatel.Shared.Data;

namespace NetRatel.Web.Models.Clients;

public class ClientViewModel
{
    public required ClientDto Client { get; set; }
    public ClientInformation? Info { get; set; }
    public string? TenantName { get; set; }
}
