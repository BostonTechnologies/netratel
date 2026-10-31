using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Tools;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class ManagedOperationsMcpTests
{
    [Fact]
    public void Both_transports_expose_the_same_complete_typed_managed_schemas()
    {
        var http = NetRatelMcpToolDefinitions.CreateForHttp("prod").ToDictionary(tool => tool.ProtocolTool.Name);
        var stdio = NetRatelMcpToolDefinitions.CreateForStdio(new Dictionary<string, Type>
        {
            ["netratel_config"] = typeof(ReadOnlyTools), ["netratel_remote_support_v2"] = typeof(MutationTools)
        }, "prod").ToDictionary(tool => tool.ProtocolTool.Name);
        foreach (var name in new[] { "netratel_services", "netratel_monitoring", "netratel_flows", "netratel_ai", "netratel_software" })
        {
            var schema = http[name].ProtocolTool.InputSchema;
            schema.GetRawText().Should().Be(stdio[name].ProtocolTool.InputSchema.GetRawText());
            schema.GetRawText().Should().NotContain("\"$ref\"", "embedded DTO schemas must not retain references to the wrong document root");
            var alternatives = schema.GetProperty("oneOf").EnumerateArray().ToArray();
            alternatives.Select(item => item.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString())
                .Should().BeEquivalentTo(ManagedOperationCatalog.Operations.Where(item => item.Tool == name).Select(item => item.Operation));
        }
        http["netratel_flows"].ProtocolTool.InputSchema.GetRawText().Should().Contain("InvestigateWithAi").And.Contain("expectedRevision");
        http["netratel_monitoring"].ProtocolTool.InputSchema.GetRawText().Should().Contain("expectedConfigurationRevision");
        http["netratel_ai"].ProtocolTool.InputSchema.GetRawText().Should().Contain("selectedKey").And.Contain("callId");
    }

    [Theory]
    [InlineData("{\"tenantId\":\"7\"}")]
    [InlineData("{\"tenantId\":null}")]
    [InlineData("{\"tenantId\":7,\"tenantId\":8}")]
    [InlineData("{\"tenantId\":7,\"limit\":\"50\"}")]
    [InlineData("{\"tenantId\":7,\"limit\":101}")]
    [InlineData("{\"tenantId\":7,\"operatorId\":\"admin\"}")]
    public void Invalid_or_impersonating_request_shapes_fail_without_throwing(string json)
    {
        using var request = JsonDocument.Parse(json);
        ManagedOperationCatalog.TryValidate(ManagedOperationCatalog.Find("netratel_monitoring", "series")!, request.RootElement, out _).Should().BeFalse();
    }

    [Fact]
    public void Required_DTO_members_and_unknown_nested_authority_are_rejected()
    {
        var descriptor = ManagedOperationCatalog.Find("netratel_flows", "create")!;
        foreach (var data in new[] { "{}", "{\"name\":null}", "{\"name\":\"draft\",\"publishedBy\":\"admin\"}" })
        {
            using var request = JsonDocument.Parse("{\"tenantId\":7,\"data\":" + data + "}");
            ManagedOperationCatalog.TryValidate(descriptor, request.RootElement, out _).Should().BeFalse();
        }
        using var valid = JsonDocument.Parse("{\"tenantId\":7,\"data\":{\"name\":\"draft\"}}");
        ManagedOperationCatalog.TryValidate(descriptor, valid.RootElement, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(NetRatelMcpTransport.StreamableHttp)]
    [InlineData(NetRatelMcpTransport.Stdio)]
    public async Task Service_control_requires_confirmation_and_preserves_exact_operation_identity(NetRatelMcpTransport transport)
    {
        var client = new RecordingClient();
        var tools = Tools(client, transport);
        var command = new ClientServiceControlRequest(Guid.NewGuid(), "worker@instance.service", ClientServicePlatform.LinuxSystemd, ClientServiceControlAction.Restart, "approved recovery");
        var payload = JsonSerializer.SerializeToElement(new { tenantId = 7, agentId = Guid.NewGuid(), data = command }, ManagedOperationCatalog.JsonOptions);
        var preview = await tools.netratel_services("control", payload);
        preview.RequiresConfirmation.Should().BeTrue();
        client.Calls.Should().ContainSingle();
        client.Calls[0].Body!["confirm"]!.GetValue<bool>().Should().BeFalse("the API must authorize a preview without applying the operation");
        (await tools.netratel_services("control", payload, true)).Success.Should().BeTrue();
        client.Calls.Should().HaveCount(2);
        var call = client.Calls[1];
        call.Path.Should().Be("/api/v2/mcp/operator/application/netratel_services/control");
        call.Body!["request"]!["data"]!["operationId"]!.GetValue<string>().Should().Be(command.OperationId.ToString("D"));
        call.Body["request"]!["data"]!["serviceName"]!.GetValue<string>().Should().Be(command.ServiceName);
        call.Body["confirm"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(NetRatelMcpTransport.StreamableHttp, "acknowledge_offline")]
    [InlineData(NetRatelMcpTransport.StreamableHttp, "clear_offline")]
    [InlineData(NetRatelMcpTransport.Stdio, "acknowledge_offline")]
    [InlineData(NetRatelMcpTransport.Stdio, "clear_offline")]
    public async Task Offline_alert_actions_keep_tenant_authority_and_explicit_confirmation(NetRatelMcpTransport transport, string operation)
    {
        var client = new RecordingClient();
        var tools = Tools(client, transport);
        var ruleId = Guid.NewGuid();
        var action = new MonitoringOperatorActionDto(Guid.NewGuid(), "Reviewed offline alert", ExpectedConfigurationRevision: 17, ExpectedOperatorRevision: 3);
        var request = JsonSerializer.SerializeToElement(new { tenantId = 7, ruleId, data = action }, ManagedOperationCatalog.JsonOptions);

        (await tools.netratel_monitoring(operation, request)).RequiresConfirmation.Should().BeTrue();
        (await tools.netratel_monitoring(operation, request, true)).Success.Should().BeTrue();

        client.Calls.Should().HaveCount(2);
        client.Calls[0].Body!["confirm"]!.GetValue<bool>().Should().BeFalse();
        var confirmed = client.Calls[1];
        confirmed.Path.Should().Be($"/api/v2/mcp/operator/application/netratel_monitoring/{operation}");
        confirmed.Body!["confirm"]!.GetValue<bool>().Should().BeTrue();
        confirmed.Body["request"]!.AsObject().Select(property => property.Key).Should().BeEquivalentTo("tenantId", "ruleId", "data");
        confirmed.Body["request"]!["data"]!["occurrenceId"]!.GetValue<string>().Should().Be(action.OccurrenceId.ToString("D"));
        var descriptor = McpOperationTargetCatalog.Find("netratel_monitoring", operation)!;
        descriptor.Model.Should().Be(McpOperationTargetModel.Tenant);
    }

    [Fact]
    public void Aggregate_actions_forbid_client_resource_overrides_and_keep_normal_client_UUIDs_strict()
    {
        var action = new MonitoringOperatorActionDto(Guid.NewGuid(), "Reviewed offline alert");
        var request = JsonSerializer.SerializeToNode(new { tenantId = 7, ruleId = Guid.NewGuid(), data = action }, ManagedOperationCatalog.JsonOptions)!.AsObject();
        var aggregate = ManagedOperationCatalog.Find("netratel_monitoring", "acknowledge_offline")!;
        ManagedOperationCatalog.TryValidate(aggregate, JsonSerializer.SerializeToElement(request), out _).Should().BeTrue();
        request["agentId"] = Guid.Empty;
        ManagedOperationCatalog.TryValidate(aggregate, JsonSerializer.SerializeToElement(request), out _).Should().BeFalse();
        request.Remove("agentId");
        request["resourceKey"] = MonitoringOfflinePresentation.ResourceKey;
        ManagedOperationCatalog.TryValidate(aggregate, JsonSerializer.SerializeToElement(request), out _).Should().BeFalse();
        request["agentId"] = Guid.Empty;
        var normal = ManagedOperationCatalog.Find("netratel_monitoring", "acknowledge")!;
        ManagedOperationCatalog.TryValidate(normal, JsonSerializer.SerializeToElement(request), out _).Should().BeFalse();

        var schema = NetRatelMcpToolDefinitions.CreateForHttp("prod").Single(tool => tool.ProtocolTool.Name == "netratel_monitoring")
            .ProtocolTool.InputSchema.GetProperty("oneOf").EnumerateArray()
            .Single(operation => operation.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "acknowledge_offline")
            .GetProperty("properties").GetProperty("request");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("tenantId", "ruleId", "data");
    }

    [Theory]
    [InlineData(NetRatelMcpTransport.StreamableHttp)]
    [InlineData(NetRatelMcpTransport.Stdio)]
    public async Task Denied_preview_never_offers_confirmation_or_retries(NetRatelMcpTransport transport)
    {
        var client = new RecordingClient { Deny = true };
        var tools = Tools(client, transport);
        var create = JsonSerializer.SerializeToElement(new { tenantId = 7, data = new FlowCreateRequest("inspect disks") }, ManagedOperationCatalog.JsonOptions);
        var result = await tools.netratel_flows("create", create);
        result.Success.Should().BeFalse();
        result.RequiresConfirmation.Should().BeFalse();
        result.Error!.Code.Should().Be("permission_denied");
        client.Calls.Should().ContainSingle();
        client.Calls[0].Body!["confirm"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Flow_dry_run_and_draft_edit_remain_distinct_from_publication()
    {
        var client = new RecordingClient();
        var tools = Tools(client, NetRatelMcpTransport.Stdio);
        var create = JsonSerializer.SerializeToElement(new { tenantId = 7, data = new FlowCreateRequest("inspect disks") }, ManagedOperationCatalog.JsonOptions);
        (await tools.netratel_flows("create", create, true)).Success.Should().BeTrue();
        client.Calls.Single().Path.Should().EndWith("/netratel_flows/create");
        client.Calls.Should().NotContain(call => call.Path.Contains("publish", StringComparison.Ordinal));
        ManagedOperationCatalog.Find("netratel_flows", "save_draft")!.Permission.Should().Be("flow.edit");
        ManagedOperationCatalog.Find("netratel_flows", "publish")!.Permission.Should().Be("flow.publish");
        ManagedOperationCatalog.Find("netratel_flows", "dry_run")!.Mutation.Should().BeFalse();
    }

    [Fact]
    public void Delegation_keeps_the_external_issuer_separate_from_the_gateway_issuer()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim("sub", "external-subject"), new Claim("iss", "https://id.example/realm"), new Claim("scope", "netratel.mcp.observe")], "Oidc"));
        McpOperatorDelegationIdentityFactory.TryCreate(principal, out var identity).Should().BeTrue();
        var tokens = new McpOperatorDelegationTokenService(new McpOperatorDelegationOptions
        {
            Enabled = true, Issuer = "netratel-test-gateway", Audience = "netratel-test-api", KeyId = "test",
            SharedKeyBase64 = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        });
        var signed = tokens.Create(identity!, new McpOperatorDelegationRequest("netratel_flows", "list", "request-1",
            "https://mcp.example/mcp", "prod", 7));
        tokens.TryValidate(signed, out var restored).Should().BeTrue();
        restored!.Identity.ExternalIssuer.Should().Be("https://id.example/realm");
        restored.Identity.Subject.Should().Be("external-subject");
    }

    [Theory]
    [InlineData(NetRatelMcpTransport.StreamableHttp)]
    [InlineData(NetRatelMcpTransport.Stdio)]
    public async Task Software_reads_are_inert_and_refresh_is_explicitly_confirmed(NetRatelMcpTransport transport)
    {
        var client = new RecordingClient();
        var tools = Tools(client, transport);
        var agentId = Guid.NewGuid();
        var read = JsonSerializer.SerializeToElement(new { tenantId = 7, agentId, data = new NetRatel.Shared.Contracts.SoftwareInventory.SoftwareInventoryQuery() }, ManagedOperationCatalog.JsonOptions);
        (await tools.netratel_software("snapshot", read)).Success.Should().BeTrue();
        client.Calls.Should().ContainSingle().Which.Path.Should().EndWith("/netratel_software/snapshot");
        var refresh = JsonSerializer.SerializeToElement(new { tenantId = 7, agentId, data = new NetRatel.Shared.Contracts.SoftwareInventory.SoftwareRefreshRequest() }, ManagedOperationCatalog.JsonOptions);
        (await tools.netratel_software("refresh", refresh)).RequiresConfirmation.Should().BeTrue();
        (await tools.netratel_software("refresh", refresh, true)).Success.Should().BeTrue();
        client.Calls.Last().Body!["confirm"]!.GetValue<bool>().Should().BeTrue();
        ManagedOperationCatalog.Find("netratel_software", "refresh")!.Permission.Should().Be("client.manage");
        ManagedOperationCatalog.Find("netratel_software", "set_policy")!.Permission.Should().Be("tenant.admin");
    }

    private static NetRatelMcpOperationalTools Tools(RecordingClient client, NetRatelMcpTransport transport) => new(client,
        new NetRatelMcpHostContext(new NetRatelMcpTarget("prod", new Uri("https://api.example"), new Uri("https://mcp.example/mcp"),
            NetRatelMcpCatalog.Revision), transport, "test"));

    private sealed class RecordingClient : INetRatelMcpApiClient
    {
        public bool Deny { get; init; }
        public List<(string Path, JsonNode? Body)> Calls { get; } = [];
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Managed operations must use their dedicated adapter.");
        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            method.Should().Be(HttpMethod.Post);
            Calls.Add((path, body?.DeepClone()));
            if (Deny) throw new NetRatelMcpApiException("permission_denied", "Permission denied.", 403, false);
            return Task.FromResult<JsonNode?>(body?["confirm"]?.GetValue<bool>() == false
                ? new JsonObject { ["requiresConfirm"] = true }
                : new JsonObject { ["accepted"] = true });
        }
    }
}
