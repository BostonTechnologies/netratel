using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.Application.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorPolicyAdministrationEndpointTests
{
    [Fact]
    public void Routes_AreVersionedAndAvailableWithoutTheDevelopmentOnlyMarkerSurface()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IMcpOperatorPolicyAdministration>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorPolicyAdministrationEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        routes.Should().Contain("/api/v2/mcp-operator/policies");
        routes.Should().Contain("/api/v2/mcp-operator/audits");
        routes.Should().Contain("/api/v2/mcp-operator/accepted-audits");
        routes.Should().Contain("/api/v2/mcp-operator/targets/{tenantId:int}/{agentId:guid}");
        routes.Should().NotContain("/api/v2/dev-operator-targets");

        var policyRoute = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .First(endpoint => endpoint.RoutePattern.RawText == "/api/v2/mcp-operator/policies");
        policyRoute.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .Should().Contain("McpOperatorPolicyAdmin");
    }

    [Fact]
    public void WouldEscalateCaller_RejectsAllowPoliciesForTheCallersExistingGroupOrRole()
    {
        var user = Principal(
            new Claim("sub", "operator@example.test"),
            new Claim("groups", "netratel-operators"),
            new Claim(ClaimTypes.Role, "Operator"));

        McpOperatorPolicyAdministrationEndpoints.WouldEscalateCaller(
            Draft(McpOperatorPolicyEffect.Allow, McpOperatorPrincipalSelectorKind.OidcGroup, "netratel-operators"),
            user,
            "operator@example.test").Should().BeTrue();
        McpOperatorPolicyAdministrationEndpoints.WouldEscalateCaller(
            Draft(McpOperatorPolicyEffect.Allow, McpOperatorPrincipalSelectorKind.MappedRole, "Operator"),
            user,
            "operator@example.test").Should().BeTrue();
        McpOperatorPolicyAdministrationEndpoints.WouldEscalateCaller(
            Draft(McpOperatorPolicyEffect.Deny, McpOperatorPrincipalSelectorKind.OidcGroup, "netratel-operators"),
            user,
            "operator@example.test").Should().BeFalse();
        McpOperatorPolicyAdministrationEndpoints.WouldEscalateCaller(
            Draft(McpOperatorPolicyEffect.Allow, McpOperatorPrincipalSelectorKind.OidcGroup, "incident-responders"),
            user,
            "operator@example.test").Should().BeFalse();
    }

    [Fact]
    public void PolicyAdministration_UsesTheDedicatedAdminScopeAndGroupPolicy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var endpoints = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Endpoints/Client/McpOperatorPolicyAdministrationEndpoints.cs"));
        var program = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Program.cs"));

        endpoints.Should().Contain("RequireAuthorization(\"McpOperatorPolicyAdmin\")");
        program.Should().Contain("options.AddPolicy(\"McpOperatorPolicyAdmin\"")
            .And.Contain("EffectiveAccessRequirement")
            .And.Contain("legacyRequiredScope: \"netratel.mcp.admin\"");
    }

    private static McpOperatorPolicyDraft Draft(
        McpOperatorPolicyEffect effect,
        McpOperatorPrincipalSelectorKind selectorKind,
        string selectorValue) => new(
        "test policy",
        McpOperatorEnvironment.Development,
        effect,
        0,
        new McpOperatorPrincipalSelector(selectorKind, selectorValue),
        new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.Tenant, 42),
        null,
        McpOperatorOperationFamily.Observability,
        null,
        new McpOperatorConstraints(),
        null,
        null);

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "NetRatel.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the NetRatel repository root.");
    }
}
