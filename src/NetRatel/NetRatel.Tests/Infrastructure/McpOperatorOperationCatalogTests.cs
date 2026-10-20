using AwesomeAssertions;
using NetRatel.Application.Operations;
using NetRatel.Mcp.Core;
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
            if (ManagedOperationCatalog.Find(access.ToolName, access.OperationName) is { } managed)
            {
                // Managed adapters use the typed operation's business identity
                // or revision, not the legacy opaque idempotency admission.
                var transport = NetRatelMcpCatalog.Tools.Single(tool => tool.Name == managed.Tool)
                    .Operations.Single(entry => entry.Name == managed.Operation);
                transport.RequiresConfirmation.Should().Be(managed.Mutation);
                (operation.ConfirmationClass != McpOperatorConfirmationClass.None).Should().Be(managed.Mutation);
                operation.RequiresIdempotency.Should().BeFalse();
                transport.RequiresIdempotency.Should().Be(operation.RequiresIdempotency);
            }
            else if (operation.ConfirmationClass != McpOperatorConfirmationClass.None)
                operation.RequiresIdempotency.Should().BeTrue();
        }

        McpOperatorOperationCatalog.Operations
            .Where(operation => operation.ToolName == "netratel_terminal" && operation.OperationName is "send_input" or "resize" or "close")
            .Should().OnlyContain(operation => operation.RequiresIdempotency && operation.ConfirmationClass == McpOperatorConfirmationClass.None);
    }

    [Fact]
    public void Owner_direct_access_is_an_administrator_mutation_with_exact_confirmation_schema()
    {
        var access = McpOperationAccessCatalog.Find("netratel_access", "owner_apply")!;
        access.RequiredScope.Should().Be(McpOperationAccessScope.Admin);
        access.MinimumRole.Should().Be(McpOperationMinimumRole.Administrator);
        McpLocalOperationAuthorizationCatalog.RequiredPermission("netratel_access", "owner_apply").Should().Be("mcp.policy.admin");
        var operation = NetRatelMcpCatalog.FindOperation("netratel_access", "owner_apply")!;
        operation.Safety.Should().Be(NetRatelMcpOperationSafety.OperatorMutation);
        operation.RequiresIdempotency.Should().BeTrue();
        var schema = NetRatelMcpToolDefinitions.CreateForHttp("prod")
            .Single(definition => definition.ProtocolTool.Name == "netratel_access").ProtocolTool.InputSchema;
        var alternative = schema.GetProperty("oneOf").EnumerateArray().Single(item =>
            item.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "owner_apply");
        var request = alternative.GetProperty("properties").GetProperty("request");
        request.GetProperty("properties").GetProperty("confirm").GetProperty("const").GetBoolean().Should().BeTrue();
        var required = request.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        required.Should().Contain("requestId");
        required.Should().Contain("proposalHash");
        required.Should().Contain("confirm");
    }

    [Theory]
    [InlineData("netratel_services", "control", McpOperatorConfirmationClass.RemoteExecution)]
    [InlineData("netratel_ai", "send", McpOperatorConfirmationClass.RemoteExecution)]
    [InlineData("netratel_ai", "respond", McpOperatorConfirmationClass.RemoteExecution)]
    [InlineData("netratel_ai", "reconcile", McpOperatorConfirmationClass.RemoteExecution)]
    [InlineData("netratel_ai", "pair", McpOperatorConfirmationClass.CredentialIssuance)]
    [InlineData("netratel_monitoring", "delete_rule", McpOperatorConfirmationClass.Destructive)]
    [InlineData("netratel_monitoring", "delete_group", McpOperatorConfirmationClass.Destructive)]
    [InlineData("netratel_monitoring", "delete_silence", McpOperatorConfirmationClass.Destructive)]
    [InlineData("netratel_monitoring", "save_group", McpOperatorConfirmationClass.StandardMutation)]
    [InlineData("netratel_flows", "save_draft", McpOperatorConfirmationClass.StandardMutation)]
    [InlineData("netratel_services", "inventory", McpOperatorConfirmationClass.None)]
    public void Managed_risk_classification_preserves_execution_credential_and_destructive_policy_gates(
        string tool, string operation, McpOperatorConfirmationClass expected)
    {
        McpOperatorOperationCatalog.Find(tool, operation)!.ConfirmationClass.Should().Be(expected);
    }

    [Theory]
    [InlineData("netratel_services", "control", "operationId")]
    [InlineData("netratel_ai", "create", "requestId")]
    [InlineData("netratel_ai", "send", "messageId")]
    [InlineData("netratel_monitoring", "save_group", "expectedConfigurationRevision")]
    [InlineData("netratel_flows", "save_draft", "expectedRevision")]
    public void Managed_schemas_require_the_typed_business_identity_or_revision(
        string tool, string operation, string property)
    {
        var schema = NetRatelMcpToolDefinitions.CreateForHttp("prod")
            .Single(definition => definition.ProtocolTool.Name == tool).ProtocolTool.InputSchema;
        var alternative = schema.GetProperty("oneOf").EnumerateArray().Single(item =>
            item.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == operation);
        var request = alternative.GetProperty("properties").GetProperty("request");
        var data = request.GetProperty("properties").GetProperty("data");
        data.GetProperty("required").EnumerateArray().Select(item => item.GetString()).Should().Contain(property);
        request.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        request.GetProperty("properties").TryGetProperty("idempotencyKey", out _).Should().BeFalse();
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
