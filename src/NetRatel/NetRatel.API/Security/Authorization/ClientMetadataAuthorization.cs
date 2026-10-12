using Microsoft.AspNetCore.Authorization;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Security.Authorization;

public static class ClientMetadataAuthorization
{
    public const string ReadPolicy = "ClientMetadataRead";
    public const string WritePolicy = "ClientMetadataWrite";

    public static void AddPolicies(AuthorizationOptions options)
    {
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, ReadPolicy, NetRatelPermissions.TelemetryRead);
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, WritePolicy, NetRatelPermissions.ClientManagement);
    }
}
