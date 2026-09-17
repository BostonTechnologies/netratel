using FluentAssertions;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorOperationCatalogTests
{
    [Fact]
    public void Every_transport_catalog_operation_has_one_policy_family_and_access_classification()
    {
        McpOperatorOperationCatalog.Operations
            .Select(entry => (entry.ToolName, entry.OperationName))
            .Should().OnlyHaveUniqueItems();

        foreach (var access in McpOperationAccessCatalog.Operations)
        {
            var operation = McpOperatorOperationCatalog.Find(access.ToolName, access.OperationName);
            operation.Should().NotBeNull($"{access.ToolName}/{access.OperationName} is a checked-in MCP operation");
            operation!.OperationFamily.Should().NotBe(McpOperatorOperationFamily.None);
            operation.RequiredScope.Should().Be(access.RequiredScope);
            operation.MinimumRole.Should().Be(access.MinimumRole);
            if (operation.ConfirmationClass != McpOperatorConfirmationClass.None)
                operation.RequiresIdempotency.Should().BeTrue();
        }

        McpOperatorOperationCatalog.Operations
            .Where(operation => operation.ToolName == "netratel_terminal" && operation.OperationName is "send_input" or "resize" or "close")
            .Should().OnlyContain(operation => operation.RequiresIdempotency && operation.ConfirmationClass == McpOperatorConfirmationClass.None);
    }

    [Fact]
    public void V2_role_gated_scopes_accept_only_the_documented_role_bundles()
    {
        McpOperationRoleRequirements.IsSatisfied(McpOperationAccessScope.Observe, Set("Observer")).Should().BeTrue();
        McpOperationRoleRequirements.IsSatisfied(McpOperationAccessScope.Files, Set("Operator")).Should().BeTrue();
        McpOperationRoleRequirements.IsSatisfied(McpOperationAccessScope.Execute, Set("AutomationOperator")).Should().BeTrue();
        McpOperationRoleRequirements.IsSatisfied(McpOperationAccessScope.Admin, Set("PolicyAdministrator")).Should().BeTrue();
    }

    [Fact]
    public void V2_role_gated_scopes_reject_a_role_from_a_different_bundle()
    {
        McpOperationRoleRequirements.IsSatisfied(McpOperationAccessScope.Observe, Set("OnboardingOperator")).Should().BeFalse();
        McpOperationRoleRequirements.IsSatisfied(McpOperationAccessScope.Files, Set("OnboardingOperator")).Should().BeFalse();
        McpOperationRoleRequirements.IsSatisfied(McpOperationAccessScope.Execute, Set("Operator")).Should().BeFalse();
        McpOperationRoleRequirements.IsSatisfied(McpOperationAccessScope.Admin, Set("AutomationOperator")).Should().BeFalse();
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
