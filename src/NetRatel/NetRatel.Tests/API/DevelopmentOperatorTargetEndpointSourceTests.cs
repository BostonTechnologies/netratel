using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class DevelopmentOperatorTargetEndpointSourceTests
{
    [Fact]
    public void TargetAdministration_IsDevelopmentOnlyAndUsesPersistedAgentIds()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var registration = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/ApiEndpointRegistrationExtensions.cs"));
        var endpoints = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/DevelopmentOperatorTargetEndpoints.cs"));
        var files = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/DevelopmentMcpFileGatewayEndpoints.cs"));
        var observability = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/DevelopmentMcpClientObservabilityEndpoints.cs"));
        var scripts = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/DevelopmentMcpScriptEndpoints.cs"));

        registration.Should().Contain("if (app.Environment.IsDevelopment())");
        registration.Should().Contain("app.MapDevelopmentOperatorTargetEndpoints();");
        registration.Should().Contain("app.MapDevelopmentMcpFileGatewayEndpoints();");
        registration.Should().Contain("app.MapDevelopmentMcpClientObservabilityEndpoints();");
        registration.Should().Contain("app.MapDevelopmentMcpScriptEndpoints();");
        endpoints.Should().Contain("RequireAuthorization(\"Operator\")");
        endpoints.Should().Contain("Guid agentId");
        endpoints.Should().Contain("FileFixtureRoot");
        endpoints.Should().NotContain("request.Host");
        endpoints.Should().NotContain("developmentOnly");
        files.Should().Contain("RequireAuthorization(\"Operator\")");
        files.Should().Contain("DevelopmentFileFixture.Contains");
        files.Should().Contain("DevelopmentOperatorOperation.FileCollect");
        files.Should().Contain("DevelopmentOperatorOperation.FileArtifactCleanup");
        files.Should().NotContain("request.Host");
        observability.Should().Contain("RequireAuthorization(\"Operator\")");
        observability.Should().Contain("DevelopmentOperatorOperation.ClientLogRead");
        observability.Should().Contain("DevelopmentOperatorOperation.ClientTelemetryRead");
        observability.Should().Contain("DevelopmentOperatorTargetGate.RequireAcceptedAsync");
        observability.Should().NotContain("request.Host");
        scripts.Should().Contain("RequireAuthorization(\"Operator\")");
        scripts.Should().Contain("DevelopmentOperatorOperation.ScriptMutation");
        scripts.Should().Contain("DevelopmentMcpScripts");
        scripts.Should().Contain("server-generated harmless scripts");
        scripts.Should().NotContain("request.Host");
    }

    [Fact]
    public void LegacyJobRunMutations_RemainOnTheExternalServiceOperatorSurface()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Jobs/JobRunEndpoints.cs"));

        source.Should().Contain("RequireAuthorization()");
        source.Should().Contain("CanManageAsync");
        source.Should().Contain("authority.StartAsync(jobId, request, ct)");
        source.Should().NotContain("IDevelopmentOperatorTargetAuthority");
        source.Should().NotContain("DevelopmentOperatorTargetGate");
    }

    [Fact]
    public void LegacyExternalServiceGatewayOperations_AreNotSubjectToTheMcpTargetGate()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var tasks = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/AgentTaskEndpoints.cs"));
        var commands = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/AgentCommandGatewayEndpoints.cs"));
        var files = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/AgentFileGatewayEndpoints.cs"));
        var control = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/AgentControlEndpoints.cs"));

        tasks.Should().Contain("RequireAuthorization()").And.Contain("CanExecuteAsync");
        commands.Should().Contain("RequireAuthorization(\"CommandOperator\")");
        files.Should().Contain("RequireAuthorization(\"FileReader\")");
        control.Should().Contain("RequireAuthorization(\"ClientManager\")");
        tasks.Should().NotContain("DevelopmentOperatorTargetGate");
        commands.Should().NotContain("DevelopmentOperatorTargetGate");
        files.Should().NotContain("DevelopmentOperatorTargetGate");
        control.Should().NotContain("DevelopmentOperatorTargetGate");
    }

    [Fact]
    public void LegacyRequestSubmission_RemainsOnTheExternalServiceOperatorSurface()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var requests = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Requests/RequestEndpoints.cs"));

        requests.Should().Contain("RequireAuthorization()");
        requests.Should().Contain("CanManageAsync");
        requests.Should().NotContain("IDevelopmentOperatorTargetAuthority");
        requests.Should().NotContain("DevelopmentOperatorTargetGate");
    }

}
