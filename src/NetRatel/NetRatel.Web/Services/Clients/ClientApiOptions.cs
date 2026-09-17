namespace NetRatel.Web.Services.Clients;

public sealed class ClientApiOptions
{
    public string GetClientsByTenantPath { get; set; } = "/api/v1/tenants/{tenantId}/clients";

    public string SearchClientsPath { get; set; } = "/api/v1/clients";
}
