using NetRatel.API.Endpoints.Client;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Endpoints.Systems;
using NetRatel.API.Endpoints.RatelDesk;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.API.Realtime.Operations;
using NetRatel.API.Endpoints.SystemPairing;
using NetRatel.API.Endpoints.Notifications;

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
        app.MapAgentRemoteSupportV2Endpoints();
        app.MapSignalRAuthorityEndpoints();
        app.MapOperationsLogEndpoints();

        app.MapTenantEndpoints();
        app.MapSecretEndpoints();
        app.MapRatelDeskConnectorEndpoints();
        app.MapNotificationConnectorEndpoints();
        app.MapScriptEndpoints();
        app.MapPrimaryClientAgentBindingReadEndpoints();
        app.MapMcpOperatorPolicyAdministrationEndpoints();
        app.MapMcpOperatorAccountPolicyMutationEndpoints();
        app.MapMcpOperatorAccessEndpoints();
        app.MapMcpOperatorAuthenticationStatusEndpoints();
        app.MapMcpOperatorClientSearchEndpoints();
        app.MapMcpOperatorPolicyInspectionEndpoints();
        app.MapMcpOperatorPolicyMutationEndpoints();
        app.MapMcpOperatorTerminalAvailabilityEndpoints();
        app.MapMcpOperatorRemoteSupportEndpoints();
        app.MapMcpOperatorComputerUseEndpoints();
        app.MapComputerUseOperatorEndpoints();
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
        app.MapClientServicesEndpoints();
        app.MapClientMetadataEndpoints();
        app.MapClientOffboardingEndpoints();
        NetRatel.API.Endpoints.Monitoring.MonitoringEndpoints.MapMonitoringEndpoints(app);
        NetRatel.API.Endpoints.Monitoring.OperationsDashboardEndpoints.MapOperationsDashboardEndpoints(app);
        app.MapFlowEndpoints();
        app.MapNetclawEndpoints();
        app.MapAiPromptEndpoints();
        app.MapTelemetryEndpoints();
        app.MapAgentFileGatewayEndpoints();
        app.MapAgentLogGatewayEndpoints();
        app.MapAgentCommandGatewayEndpoints();
        app.MapAgentTerminalGatewayEndpoints();

        app.MapClientArtifactsEndpoints();
        app.MapClientInstallLinkEndpoints();
        app.MapClientUpdatesEndpoints();
        app.MapAgentTaskEndpoints();
        app.MapRequestEndpoints();
        app.MapJobEndpoints();
        app.MapJobRunEndpoints();
        app.MapGlobalSearchEndpoints();
        app.MapNotificationEndpoints();
        app.MapAgentAuthEndpoints();
        app.MapLocalAuthenticationEndpoints();
        app.MapIntegrationCredentialEndpoints();
        app.MapIntegrationPairingEndpoints();
        app.MapMcpLocalDelegationEndpoints();
        app.MapDeploymentBrandingEndpoints();
        app.MapAccessAdministrationEndpoints();
        app.MapM2MTokenEndpoints();
        app.MapServiceIdentityMetadataEndpoints();
        app.MapPairingEndpoints();
        app.MapInternalEndpoints();

        app.MapSystemEndpoints();
        app.MapMachineTokenAuthenticationStatusEndpoints();
        app.MapAiAgentSelfStatusEndpoints();
        app.MapAiAgentOpsEndpoints();

        return app;
    }
}
