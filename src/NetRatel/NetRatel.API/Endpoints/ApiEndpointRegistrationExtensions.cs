using NetRatel.API.Endpoints.Client;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Endpoints.Systems;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime.Shadow;
using NetRatel.API.Realtime.Operations;

namespace NetRatel.API.Endpoints;

/// <summary>Registers the complete HTTP endpoint surface for the NetRatel API.</summary>
public static class ApiEndpointRegistrationExtensions
{
    public static WebApplication MapApiEndpoints(this WebApplication app)
    {
        app.MapDefaultEndpoints();

        app.MapHealthEndpoints();
        app.MapDocumentationEndpoints();
        app.MapAgentGatewayEndpoints();
        app.MapSignalRShadowEndpoints();
        app.MapSignalRAuthorityEndpoints();
        app.MapOperationsLogEndpoints();

        app.MapTenantEndpoints();
        app.MapSecretEndpoints();
        app.MapScriptEndpoints();
        app.MapPrimaryClientAgentBindingReadEndpoints();
        app.MapPrimaryClientGatewayCardReadEndpoints();
        app.MapMcpOperatorPolicyAdministrationEndpoints();
        app.MapMcpOperatorAccessEndpoints();
        app.MapMcpOperatorAuthenticationStatusEndpoints();
        app.MapMcpOperatorClientSearchEndpoints();
        app.MapMcpOperatorPolicyInspectionEndpoints();
        app.MapMcpOperatorPolicyMutationEndpoints();
        app.MapMcpOperatorTerminalAvailabilityEndpoints();
        app.MapMcpOperatorRemoteSupportEndpoints();
        app.MapMcpOperatorTerminalSessionEndpoints();
        app.MapMcpOperatorCommandEndpoints();
        app.MapMcpOperatorScriptEndpoints();
        app.MapMcpOperatorJobEndpoints();
        app.MapMcpOperatorTaskEndpoints();
        app.MapMcpOperatorRequestEndpoints();
        app.MapMcpOperatorTenantEndpoints();
        app.MapMcpOperatorOnboardingEndpoints();
        app.MapMcpOperatorClientAdministrationEndpoints();
        app.MapMcpOperatorNotificationEndpoints();
        app.MapMcpOperatorEventEndpoints();
        app.MapMcpOperatorConnectivityEndpoints();
        app.MapMcpOperatorSystemObservabilityEndpoints();
        app.MapMcpOperatorClientObservabilityEndpoints();
        app.MapMcpOperatorClientFileEndpoints();
        if (app.Environment.IsDevelopment())
        {
            app.MapDevelopmentOperatorTargetEndpoints();
            app.MapDevelopmentMcpFileGatewayEndpoints();
            app.MapDevelopmentMcpClientObservabilityEndpoints();
            app.MapDevelopmentMcpScriptEndpoints();
            app.MapDevelopmentMcpMarkerJobEndpoints();
            app.MapDevelopmentMcpOnboardingEndpoints();
        }
        app.MapClientPresenceReadEndpoints();
        app.MapAgentControlEndpoints();
        app.MapAgentTelemetryReadEndpoints();
        app.MapTelemetryEndpoints();
        app.MapAgentFileGatewayEndpoints();
        app.MapAgentLogGatewayEndpoints();
        app.MapAgentCommandGatewayEndpoints();
        app.MapAgentRemoteSupportGatewayEndpoints();
        app.MapAgentTerminalGatewayEndpoints();

        app.MapClientArtifactsEndpoints();
        app.MapClientUpdatesEndpoints();
        app.MapAgentTaskEndpoints();
        app.MapRequestEndpoints();
        app.MapJobEndpoints();
        app.MapJobRunEndpoints();
        app.MapGlobalSearchEndpoints();
        app.MapNotificationEndpoints();
        app.MapAgentAuthEndpoints();
        app.MapLocalAuthenticationEndpoints();
        app.MapM2MTokenEndpoints();
        app.MapInternalEndpoints();

        app.MapAdminConnectivityEndpoints();
        app.MapSystemEndpoints();
        app.MapMachineTokenAuthenticationStatusEndpoints();
        app.MapAiAgentOpsEndpoints();

        return app;
    }
}
