using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ApiEndpointRegistrationSourceTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void Program_Uses_Provider_Neutral_Oidc_For_Interactive_Bearer_Tokens()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Program.cs"));

        source.Should().Contain(".AddJwtBearer(\"Oidc\"")
            .And.Contain("Authentication:Oidc")
            .And.Contain("Authorization:Oidc:AdminGroupId")
            .And.Contain("AuthenticationType = \"Oidc\"")
            .And.NotContain(".AddJwtBearer(\"Azure\"");
    }

    [Fact]
    public void OpenApi_Uses_Operation_Level_Taxonomy_And_Security_Metadata()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Program.cs"));
        var catalog = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/OpenApi/NetRatelOpenApiCatalog.cs"));

        program.Should().Contain("options.AddOperationTransformer(NetRatelOpenApiCatalog.TransformOperationAsync);")
            .And.Contain("SecuritySchemes[\"IntegrationCredential\"]")
            .And.Contain("SecuritySchemes[\"LocalSession\"]")
            .And.NotContain("document.Security.Add(");
        catalog.Should().Contain("operation.OperationId")
            .And.Contain("IAllowAnonymous")
            .And.Contain("M2MOnly")
            .And.Contain("AgentGatewayAccess")
            .And.Contain("McpLocalDelegationExchange")
            .And.Contain("Integrations/MCP")
            .And.Contain("Remote Support");
    }

    [Fact]
    public void Program_Uses_Only_The_Api_Endpoint_Bootstrap()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Program.cs"));

        source.Should().Contain("app.MapApiEndpoints();");
        source.Should().NotContain("app.MapGet(");
        source.Should().NotContain("app.MapPost(");
        source.Should().NotContain("app.MapPut(");
        source.Should().NotContain("app.MapDelete(");
        source.Should().NotContain("app.MapMethods(");
        source.Should().NotContain("app.MapMcpEndpoints(");
    }

    [Fact]
    public void Program_Uses_Dedicated_H2c_Listener_For_The_Agent_Gateway()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Program.cs"));

        source.Should().Contain("k.ListenAnyIP(httpPort, listen => listen.Protocols = HttpProtocols.Http1);");
        source.Should().Contain("akkaMigration.GatewayGrpcPort");
        source.Should().Contain("listen => listen.Protocols = HttpProtocols.Http2");
        source.Should().Contain("akkaMigration.Enabled && akkaMigration.GatewayEnabled");
    }

    [Fact]
    public void Program_Registers_Development_Mcp_Retention_Services_Only_In_Development()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Program.cs"));

        source.Should().Contain("if (builder.Environment.IsDevelopment())");
        source.Should().Contain("AddHostedService<DevelopmentMcpFileArtifactRetentionService>()");
        source.Should().Contain("AddHostedService<ProductionMcpTerminalExpiryService>()");
    }

    [Fact]
    public void Program_Does_Not_Start_Legacy_Spacetime_Or_Hangfire_Runtimes()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Program.cs"));

        source.Should().NotContain("AddHangfire(");
        source.Should().NotContain("AddHangfireServer(");
        source.Should().NotContain("UseHangfireDashboard(");
        source.Should().NotContain("JobRunReconciliationHostedService");
        source.Should().NotContain("JobScheduleStartupSyncHostedService");
        source.Should().NotContain("SpacetimeDbService");
        source.Should().NotContain("TerminalTransportSessionRegistry");
        source.Should().NotContain("TerminalDirectTunnelRegistry");
        source.Should().NotContain("RemoteDesktopSessionRegistry");
        source.Should().NotContain("RemoteSupportSessionRegistry");
        source.Should().NotContain("RemoteSupportWindowsSessionInventoryCache");
        source.Should().NotContain("IAkkaJobAuthorityService");
    }

    [Fact]
    public void Gateway_Presence_Read_Endpoint_Is_Separate_From_Legacy_Client_Endpoints()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/ClientPresenceReadEndpoints.cs"));

        source.Should().Contain("/api/v2/client-presence");
        source.Should().Contain("IClientPresenceReadModel");
        source.Should().Contain("PresenceReadModelEnabled");
        source.Should().Contain("RequireAuthorization(\"InstanceAdministrator\")");
        source.Should().Contain("[FromQuery] int? limit");
        source.Should().Contain(".Take(boundedLimit)");
        source.Should().NotContain("SpacetimeDbService");
    }

    [Fact]
    public void Global_search_uses_current_postgresql_sources_without_spacetime()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/Search/GlobalSearchEndpoints.cs"));
        var directory = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Services/Client/AgentDirectoryPresentation.cs"));

        source.Should().Contain("/api/v1/global-search");
        source.Should().Contain("GlobalSearchAgentDto");
        source.Should().Contain("AgentDirectorySearch");
        directory.Should().Contain("db.Agents");
        source.Should().Contain("db.JobTaskActivities");
        source.Should().NotContain("SpacetimeDbService");
        source.Should().NotContain("SpacetimeDB");
        source.Should().NotContain("Db.Client");
        source.Should().NotContain("Db.ClientTask");
    }

    [Fact]
    public void Global_search_composes_agent_and_business_matching_before_materialization()
    {
        var search = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/Search/GlobalSearchEndpoints.cs"));
        var directory = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Services/Client/AgentDirectoryPresentation.cs"));
        var presence = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/ClientPresenceReadEndpoints.cs"));

        search.Should().Contain("BuildJobQuery");
        search.Should().Contain("BuildRequestQuery");
        search.Should().Contain("BuildTaskQuery");
        search.Should().Contain("matchingAgents.Any");
        search.Should().Contain("DatabaseCommandCount");
        search.Should().NotContain("SearchJobsForAgentsAsync");
        search.Should().NotContain("LoadByAgentIdsAsync");
        directory.Should().Contain("IQueryable<AgentDirectoryRow> Query");
        directory.Should().Contain("AgentDirectoryPresentation");
        presence.Should().Contain("AgentDirectoryPresentation.Create");
    }

    [Fact]
    public void Endpoint_Bootstrap_Registers_Gateway_Client_Modules_Without_Legacy_Client_Routes()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/ApiEndpointRegistrationExtensions.cs"));
        var mappings = new[]
        {
            "app.MapDefaultEndpoints();",
            "app.MapHealthEndpoints();",
            "app.MapDocumentationEndpoints();",
            "app.MapAgentGatewayEndpoints();",
            "app.MapSecretEndpoints();",
            "app.MapPrimaryClientAgentBindingReadEndpoints();",
            "app.MapPrimaryClientGatewayCardReadEndpoints();",
            "app.MapMcpOperatorPolicyAdministrationEndpoints();",
            "app.MapMcpOperatorAccessEndpoints();",
            "app.MapMcpOperatorAuthenticationStatusEndpoints();",
            "app.MapMcpOperatorPolicyInspectionEndpoints();",
            "app.MapMcpOperatorPolicyMutationEndpoints();",
            "app.MapMcpOperatorTerminalAvailabilityEndpoints();",
            "app.MapMcpOperatorCommandEndpoints();",
            "app.MapMcpOperatorScriptEndpoints();",
            "app.MapMcpOperatorJobEndpoints();",
            "app.MapMcpOperatorTaskEndpoints();",
            "app.MapMcpOperatorRequestEndpoints();",
            "app.MapMcpOperatorTenantEndpoints();",
            "app.MapMcpOperatorOnboardingEndpoints();",
            "app.MapMcpOperatorClientAdministrationEndpoints();",
            "app.MapMcpOperatorNotificationEndpoints();",
            "app.MapMcpOperatorEventEndpoints();",
            "app.MapMcpOperatorConnectivityEndpoints();",
            "app.MapMcpOperatorClientFileEndpoints();",
            "app.MapDevelopmentOperatorTargetEndpoints();",
            "app.MapDevelopmentMcpFileGatewayEndpoints();",
            "app.MapDevelopmentMcpClientObservabilityEndpoints();",
            "app.MapDevelopmentMcpScriptEndpoints();",
            "app.MapClientPresenceReadEndpoints();",
            "app.MapAgentControlEndpoints();",
            "app.MapAgentTelemetryReadEndpoints();",
            "app.MapTelemetryEndpoints();",
            "app.MapAgentFileGatewayEndpoints();",
            "app.MapAgentCommandGatewayEndpoints();",
            "app.MapAgentRemoteSupportGatewayEndpoints();",
            "app.MapAgentTerminalGatewayEndpoints();",
            "app.MapClientArtifactsEndpoints();",
            "app.MapClientUpdatesEndpoints();",
            "app.MapAgentTaskEndpoints();",
            "app.MapRequestEndpoints();",
            "app.MapJobEndpoints();",
            "app.MapJobRunEndpoints();",
            "app.MapGlobalSearchEndpoints();",
            "app.MapNotificationEndpoints();",
            "app.MapAgentAuthEndpoints();",
            "app.MapM2MTokenEndpoints();",
            "app.MapInternalEndpoints();",
            "app.MapAdminConnectivityEndpoints();",
            "app.MapMachineTokenAuthenticationStatusEndpoints();",
            "app.MapAiAgentOpsEndpoints();"
        };

        var positions = mappings.Select(mapping => source.IndexOf(mapping, StringComparison.Ordinal)).ToArray();
        positions.Should().OnlyContain(position => position >= 0);
        positions.Should().BeInAscendingOrder();
        source.Should().NotContain("app.MapClientEndpoints();");
        source.Should().NotContain("app.MapPrimaryClientAgentBindingManagementEndpoints();");
        source.Should().NotContain("app.MapPrimaryClientGatewayCardActionEndpoints();");
        source.Should().Contain("app.MapTelemetryEndpoints();");
        source.Should().NotContain("app.MapClientFileSystemEndpoints();");
        source.Should().NotContain("app.MapTerminalEndpoints();");
        source.Should().NotContain("app.MapRemoteDesktopEndpoints();");
        source.Should().NotContain("app.MapRemoteSupportEndpoints();");
        source.Should().NotContain("app.MapClientTaskEndpoints();");
        source.Should().NotContain("app.MapClientSettingsEndpoints();");
        source.Should().Contain("app.MapTenantEndpoints();");
        source.Should().Contain("app.MapScriptEndpoints();");
        source.Should().Contain("app.MapClientUpdatesEndpoints();");
        source.Should().Contain("app.MapAgentTaskEndpoints();");
        source.Should().Contain("app.MapRequestEndpoints();");
        source.Should().Contain("app.MapJobEndpoints();");
        source.Should().Contain("app.MapJobRunEndpoints();");
        source.Should().Contain("app.MapGlobalSearchEndpoints();");
        source.Should().Contain("app.MapInternalEndpoints();");
        source.Should().NotContain("app.MapMcpEndpoints();");
        source.Should().Contain("app.MapSystemEndpoints();");
    }

    [Fact]
    public void Unregistered_Spacetime_Endpoint_Modules_Are_Deleted()
    {
        var legacySources = new[]
        {
            "src/NetRatel/NetRatel.API/Endpoints/Client/ClientEndpoints.cs",
            "src/NetRatel/NetRatel.API/Endpoints/Client/ClientSettingsEndpoints.cs",
            "src/NetRatel/NetRatel.API/Endpoints/Client/ClientTaskEndpoints.cs",
            "src/NetRatel/NetRatel.API/Endpoints/Client/ClientFileSystemEndpoints.cs",
            "src/NetRatel/NetRatel.API/Endpoints/RemoteAccess/TerminalEndpoints.cs",
            "src/NetRatel/NetRatel.API/Endpoints/RemoteAccess/RemoteDesktopEndpoints.cs"
        };

        legacySources.Should().OnlyContain(path => !File.Exists(Path.Combine(RepoRoot, path)));
    }

    [Fact]
    public void Internal_ExternalService_Contract_Uses_PostgreSql_And_Akka_Without_Spacetime()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/Internal/InternalEndpoints.cs"));

        source.Should().Contain("RequireAuthorization(\"M2MOnly\")");
        source.Should().Contain("IAkkaJobAuthorityService");
        source.Should().Contain("OrchestratorDbContext");
        source.Should().NotContain("SpacetimeDbService");
        source.Should().NotContain("SpacetimeDB");
        source.Should().NotContain("SpacetimeIdentityHelpers");
    }

    [Fact]
    public void Health_And_MachineToken_Status_Endpoints_Preserve_Their_Contracts()
    {
        var health = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/Health/HealthEndpoints.cs"));
        var status = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/Auth/MachineTokenAuthenticationStatusEndpoints.cs"));
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Program.cs"));

        health.Should().Contain("/health/live");
        health.Should().Contain("/health/ready");
        health.Should().Contain("RequireAuthorization(\"HealthRead\")");
        health.Should().Contain("StatusCodes.Status503ServiceUnavailable");
        health.Should().Contain("WithTags(\"Health\")");
        health.Should().Contain("Liveness probe");
        health.Should().Contain("Readiness probe for required NetRatel dependencies");
        health.Should().NotContain("SpacetimeDbService");

        program.Should().Contain("options.AddPolicy(\"HealthRead\"");
        program.Should().Contain("policy.AddAuthenticationSchemes(\"Bearer\", \"M2M\")");

        status.Should().Contain("/api/v1/auth/machine-token/status");
        status.Should().Contain("RequireAuthorization(\"MachineTokenApi\")");
        status.Should().Contain("WithTags(\"Authentication\")");
        status.Should().Contain("identityProvider");
        status.Should().Contain("Distinct(StringComparer.OrdinalIgnoreCase)");
    }
}
