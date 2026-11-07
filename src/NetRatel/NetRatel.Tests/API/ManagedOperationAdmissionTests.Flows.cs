using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class ManagedOperationAdmissionTests
{
    [Theory]
    [InlineData("edit-only")]
    [InlineData("no-policy")]
    [InlineData("policy-only")]
    [InlineData("destructive-disabled")]
    [InlineData("foreign-tenant")]
    [InlineData("revoked")]
    [InlineData("allowed")]
    public async Task Flow_delete_requires_current_domain_grant_and_destructive_exact_operation_policy(string mode)
    {
        await using var scenario = await Scenario.CreateAsync("netratel_flows", "delete");
        if (mode is "edit-only" or "policy-only") scenario.Access.Permissions.Remove("flow.delete");
        if (mode == "edit-only") scenario.Access.Permissions.Add("flow.edit");
        var policy = await scenario.Db.McpOperatorPolicies.SingleAsync();
        if (mode == "no-policy") policy.Operation = "netratel_flows/set_enabled";
        if (mode == "destructive-disabled") policy.ConstraintsJson = "{\"destructiveOperationsAllowed\":false}";
        if (mode == "revoked") scenario.Access.Revoked = true;
        await scenario.Db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new { tenantId = mode == "foreign-tenant" ? 8 : 7,
            flowId = Guid.NewGuid(), data = new { expectedRevision = 4 } });
        var result = await scenario.Admission.EvaluateAsync(scenario.Descriptor, payload, scenario.Http, default);
        if (mode == "allowed") result.FailureCode.Should().BeNull(); else result.FailureCode.Should().NotBeNull();
        var descriptor = McpOperatorOperationCatalog.Find("netratel_flows", "delete")!;
        descriptor.OperationFamily.Should().Be(McpOperatorOperationFamily.AutomationWrite);
        descriptor.ConfirmationClass.Should().Be(McpOperatorConfirmationClass.Destructive);
        descriptor.RequiredScope.Should().Be(McpOperationAccessScope.Write);
    }

    [Fact]
    public async Task Delete_preview_is_graph_free_and_authority_revision_and_policy_are_rechecked_on_confirmation()
    {
        await using var scenario = await Scenario.CreateAsync("netratel_flows", "delete");
        var id = Guid.NewGuid();
        scenario.Db.FlowDefinitions.Add(new() { Id = id, TenantId = 7, Name = "Read protected name", Revision = 4,
            Enabled = true, DraftJson = "{\"schemaVersion\":2,\"secretPrompt\":true}" });
        await scenario.Db.SaveChangesAsync();
        var request = JsonSerializer.SerializeToElement(new { tenantId = 7, flowId = id, data = new { expectedRevision = 4 } });
        var preview = await scenario.InvokeAsync(request, false);
        ((IStatusCodeHttpResult)preview).StatusCode.Should().Be(200);
        var json = JsonSerializer.Serialize(((IValueHttpResult)preview).Value);
        json.Should().Contain(id.ToString()).And.Contain("expectedRevision").And.Contain("currentRevision").And.Contain("retention");
        json.Should().NotContain("Read protected name").And.NotContain("secretPrompt");
        (await scenario.Db.FlowDefinitions.SingleAsync()).DeletedAtUtc.Should().BeNull();
        scenario.Access.Revoked = true;
        ((IStatusCodeHttpResult)await scenario.InvokeAsync(request, true)).StatusCode.Should().Be(403);
        scenario.Access.Revoked = false;
        var row = await scenario.Db.FlowDefinitions.SingleAsync(); row.Revision = 5; await scenario.Db.SaveChangesAsync();
        ((IStatusCodeHttpResult)await scenario.InvokeAsync(request, true)).StatusCode.Should().Be(409);
        row.DeletedAtUtc.Should().BeNull();
        row.Revision = 4; await scenario.Db.SaveChangesAsync();
        var policy = await scenario.Db.McpOperatorPolicies.SingleAsync(); policy.Operation = "netratel_flows/publish"; await scenario.Db.SaveChangesAsync();
        ((IStatusCodeHttpResult)await scenario.InvokeAsync(request, true)).StatusCode.Should().Be(403);
        policy.Operation = "netratel_flows/delete"; await scenario.Db.SaveChangesAsync();
        ((IStatusCodeHttpResult)await scenario.InvokeAsync(request, true)).StatusCode.Should().Be(200);
        row.DeletedBy.Should().Be("owner-1"); row.Enabled.Should().BeFalse(); row.Revision.Should().Be(5);
    }
}
