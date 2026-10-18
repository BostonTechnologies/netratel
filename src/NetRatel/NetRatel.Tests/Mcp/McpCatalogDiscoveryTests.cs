using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Tools;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class McpCatalogDiscoveryTests
{
    [Theory]
    [InlineData(NetRatelMcpTransport.StreamableHttp, "dev", false)]
    [InlineData(NetRatelMcpTransport.StreamableHttp, "dev", true)]
    [InlineData(NetRatelMcpTransport.StreamableHttp, "prod", true)]
    [InlineData(NetRatelMcpTransport.Stdio, "dev", false)]
    [InlineData(NetRatelMcpTransport.Stdio, "dev", true)]
    [InlineData(NetRatelMcpTransport.Stdio, "prod", true)]
    public void Exact_schemas_and_counts_match_published_tools_for_each_selected_surface_and_fit_the_budget(
        NetRatelMcpTransport transport, string instance, bool operatorSurfaceEnabled)
    {
        var host = Host(transport, instance, operatorSurfaceEnabled);
        var tools = transport == NetRatelMcpTransport.StreamableHttp
            ? NetRatelMcpToolDefinitions.CreateForHttp(host)
            : NetRatelMcpToolDefinitions.CreateForStdio(new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                ["netratel_config"] = typeof(ReadOnlyTools),
                ["netratel_remote_support_v2"] = typeof(MutationTools)
            }, instance);
        var handler = new NetRatelMcpCatalogTools(host);
        var status = handler.netratel_capabilities("status");
        Assert.True(status.Success);
        Assert.Equal(tools.Count, status.Data!["catalog"]!["toolCount"]!.GetValue<int>());
        Assert.Equal(tools.Sum(tool => tool.ProtocolTool.InputSchema.GetProperty("oneOf").GetArrayLength()),
            status.Data["catalog"]!["operationCount"]!.GetValue<int>());

        foreach (var tool in tools)
        foreach (var expected in tool.ProtocolTool.InputSchema.GetProperty("oneOf").EnumerateArray())
        {
            var operation = expected.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString()!;
            var response = handler.netratel_capabilities("schema", JsonSerializer.SerializeToElement(new { tool = tool.ProtocolTool.Name, operation }));
            Assert.True(response.Success, $"{tool.ProtocolTool.Name}/{operation}: {response.Summary}");
            var input = JsonSerializer.SerializeToElement(response.Data!["inputSchema"]);
            Assert.True(JsonElement.DeepEquals(expected, input), $"{tool.ProtocolTool.Name}/{operation} differs from tools/list.");
            Assert.False(input.GetProperty("additionalProperties").GetBoolean());
            Assert.False(input.TryGetProperty("oneOf", out _));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input))),
                response.Data["schemaDigest"]!.GetValue<string>());
            Assert.Equal(status.Data["catalog"]!["digest"]!.GetValue<string>(), response.Data["catalogDigest"]!.GetValue<string>());
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(response, JsonSerializerOptions.Web).Length, 1, NetRatelMcpDiscovery.MaxStructuredResponseBytes);
            Assert.False(response.Data["accessCheckPerformed"]!.GetValue<bool>());
            Assert.False(response.Data["inspectedOperationDispatched"]!.GetValue<bool>());
            var descriptor = NetRatelMcpCatalog.Tools.Single(candidate => candidate.Name == tool.ProtocolTool.Name)
                .Operations.Single(candidate => candidate.Name == operation);
            var requirements = response.Data["requirements"]!;
            Assert.Equal(descriptor.RequiresConfirmation, requirements["requiresConfirmation"]!.GetValue<bool>());
            Assert.Equal(descriptor.RequiresIdempotency, requirements["requiresIdempotency"]!.GetValue<bool>());
            Assert.Equal(McpOperationAccessCatalog.Find(tool.ProtocolTool.Name, operation)!.RequiredScope.ToString(), requirements["requiredScope"]!.GetValue<string>());
            Assert.Equal(McpOperationTargetCatalog.Find(tool.ProtocolTool.Name, operation)!.Model.ToString(), requirements["targetModel"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Status_is_compact_has_no_topology_and_does_not_claim_to_observe_native_metadata()
    {
        var host = Host();
        var otherDestination = new NetRatelMcpHostContext(new NetRatelMcpTarget("dev", new Uri("https://different-api.example/"),
            new Uri("https://different-mcp.example/mcp"), NetRatelMcpCatalog.Revision), NetRatelMcpTransport.StreamableHttp, "Synthetic.Mcp");
        var status = new NetRatelMcpCatalogTools(host).netratel_capabilities("status");
        var sameContract = new NetRatelMcpCatalogTools(otherDestination).netratel_capabilities("status");
        var operatorContract = new NetRatelMcpCatalogTools(Host(operatorSurfaceEnabled: true)).netratel_capabilities("status");

        Assert.True(status.Success);
        Assert.Null(status.Data!["hostRelease"]);
        Assert.False(status.Data["sourceIdentityAvailable"]!.GetValue<bool>());
        Assert.Equal("mcp_host", status.Data["identityScope"]!.GetValue<string>());
        Assert.Equal("not_observed", status.Data["nativeLoadedMetadata"]!.GetValue<string>());
        Assert.Equal(NetRatelMcpCatalog.Revision, status.Data["catalog"]!["revision"]!.GetValue<string>());
        Assert.Equal(status.Data["catalog"]!["digest"]!.GetValue<string>(), sameContract.Data!["catalog"]!["digest"]!.GetValue<string>());
        Assert.NotEqual(status.Data["catalog"]!["digest"]!.GetValue<string>(), operatorContract.Data!["catalog"]!["digest"]!.GetValue<string>());
        var json = JsonSerializer.Serialize(status, JsonSerializerOptions.Web);
        Assert.DoesNotContain("api.example", json);
        Assert.DoesNotContain("https://", json);
        Assert.DoesNotContain("apiBaseUrl", json);
        Assert.DoesNotContain("inputSchema", status.Data.ToJsonString());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(json), 1, 4096);

        // A caller can edit its returned JsonNode; the cached contract stays immutable.
        status.Data["catalog"]!["digest"] = "caller-edited";
        Assert.Equal(sameContract.Data["catalog"]!["digest"]!.GetValue<string>(),
            new NetRatelMcpCatalogTools(host).netratel_capabilities("status").Data!["catalog"]!["digest"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("get")]
    [InlineData("status")]
    [InlineData("schema")]
    public void Discovery_operations_remain_read_only_with_no_business_target(string operation)
    {
        var descriptor = NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_capabilities").Operations.Single(candidate => candidate.Name == operation);
        Assert.Equal(NetRatelMcpOperationSafety.Read, descriptor.Safety);
        Assert.False(descriptor.RequiresConfirmation);
        Assert.False(descriptor.RequiresIdempotency);
        Assert.Equal(McpOperationAccessScope.Read, McpOperationAccessCatalog.Find("netratel_capabilities", operation)!.RequiredScope);
        Assert.Equal(McpOperationTargetModel.NoBusinessTarget, McpOperationTargetCatalog.Find("netratel_capabilities", operation)!.Model);
        Assert.Equal("mcp.discovery.read", McpLocalOperationAuthorizationCatalog.RequiredPermission("netratel_capabilities", operation));
        Assert.Empty(McpLocalOperationAuthorizationCatalog.AdditionalPermissions("netratel_capabilities", operation));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"tool\":\"netratel_clients\"}")]
    [InlineData("{\"tool\":\"netratel_clients\",\"operation\":5}")]
    [InlineData("{\"tool\":\"netratel_clients\",\"operation\":\"presence\",\"tenantId\":17}")]
    [InlineData("{\"tool\":\"netratel_clients\",\"operation\":\"\"}")]
    [InlineData("{\"tool\":\"netratel_clients\",\"operation\":\"presence/secret-marker\"}")]
    public void Direct_schema_handler_rejects_incomplete_open_and_unbounded_requests(string json)
    {
        var response = new NetRatelMcpCatalogTools(Host()).netratel_capabilities("schema", JsonSerializer.Deserialize<JsonElement>(json));
        Assert.False(response.Success);
        Assert.Equal("validation_error", response.Error!.Code);
        Assert.Equal("input", response.Failure!.Layer);
        Assert.DoesNotContain("secret-marker", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void Discovery_input_validator_closes_both_envelopes_before_dispatch()
    {
        var validator = new NetRatelMcpInputValidator(NetRatelMcpToolDefinitions.CreateForHttp(Host()));
        foreach (var json in new[]
        {
            "{\"operation\":\"status\",\"request\":{}}",
            "{\"operation\":\"status\",\"confirm\":true}",
            "{\"operation\":\"schema\"}",
            "{\"operation\":\"schema\",\"request\":{\"tool\":\"netratel_clients\",\"operation\":\"presence\",\"extra\":true}}",
            JsonSerializer.Serialize(new { operation = "schema", request = new { tool = new string('a', 129), operation = "presence" } })
        })
        {
            var response = validator.Validate("netratel_capabilities", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
            Assert.NotNull(response);
            Assert.Equal("validation_error", response.Error!.Code);
        }
        foreach (var json in new[] { "{\"operation\":\"status\"}", "{\"operation\":\"schema\",\"request\":{\"tool\":\"netratel_clients\",\"operation\":\"presence\"}}" })
            Assert.Null(validator.Validate("netratel_capabilities", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)));

        var handler = new NetRatelMcpCatalogTools(Host());
        Assert.Equal("validation_error", handler.netratel_capabilities("status", JsonSerializer.SerializeToElement(new { })).Error!.Code);
        Assert.Equal("unsupported_operation", handler.netratel_capabilities("schema", JsonSerializer.SerializeToElement(new { tool = "netratel_config", operation = "get" })).Error!.Code);
        Assert.Equal("unsupported_operation", handler.netratel_capabilities("schema", JsonSerializer.SerializeToElement(new { tool = "netratel_clients", operation = "not_published" })).Error!.Code);
    }

    [Theory]
    [InlineData("0.1.1-beta.24+0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0.1.1-beta.24+not-a-source-revision")]
    [InlineData("0.1.1-beta.24")]
    public void Own_assembly_identity_reports_product_and_information_without_inventing_public_source(string informationalVersion)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Synthetic.Mcp") { Version = new Version(0, 1, 1, 0) }, AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informationalVersion]));
        var identity = NetRatelMcpReleaseIdentity.ReadAssemblyMetadata(assembly);
        Assert.Equal("Synthetic.Mcp", identity.ServiceName);
        Assert.Equal("0.1.1-beta.24", identity.ProductVersion);
        Assert.Equal(informationalVersion, identity.InformationalVersion);
        Assert.Equal("0.1.1.0", identity.AssemblyVersion);
        Assert.Null(identity.SourceRevision);

        var host = Host() with { ReleaseIdentity = identity };
        var status = new NetRatelMcpCatalogTools(host).netratel_capabilities("status");
        using var resource = JsonDocument.Parse(new NetRatelMcpCatalogResources(host).ServerStatus());
        Assert.False(status.Data!["sourceIdentityAvailable"]!.GetValue<bool>());
        Assert.Equal(informationalVersion, status.Data["hostRelease"]!["informationalVersion"]!.GetValue<string>());
        Assert.Equal(informationalVersion, resource.RootElement.GetProperty("discovery").GetProperty("hostRelease").GetProperty("informationalVersion").GetString());
        Assert.DoesNotContain("NetRatel.API", JsonSerializer.Serialize(status));
    }

    [Theory]
    [InlineData("FEDCBA9876543210FEDCBA9876543210FEDCBA98", "fedcba9876543210fedcba9876543210fedcba98")]
    [InlineData("not-a-public-source-revision", null)]
    [InlineData("", null)]
    public void Public_source_identity_requires_valid_explicit_build_metadata(string publicSourceRevision, string? expectedSourceRevision)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Synthetic.Mcp.Public"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
            ["0.1.1.0+0123456789abcdef0123456789abcdef01234567"]));
        var metadataConstructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, ["ProductVersion", "0.1.1-beta.24"]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, ["SourceRevisionId", publicSourceRevision]));

        var identity = NetRatelMcpReleaseIdentity.ReadAssemblyMetadata(assembly);
        Assert.Equal("0.1.1-beta.24", identity.ProductVersion);
        Assert.Equal(expectedSourceRevision, identity.SourceRevision);
        var status = new NetRatelMcpCatalogTools(Host() with { ReleaseIdentity = identity }).netratel_capabilities("status");
        Assert.Equal(expectedSourceRevision is not null, status.Data!["sourceIdentityAvailable"]!.GetValue<bool>());
    }

    private static NetRatelMcpHostContext Host(NetRatelMcpTransport transport = NetRatelMcpTransport.StreamableHttp,
        string instance = "dev", bool operatorSurfaceEnabled = false)
        => new(new NetRatelMcpTarget(instance, new Uri("https://api.example/"), new Uri("https://mcp.example/mcp"), NetRatelMcpCatalog.Revision),
            transport, "Synthetic.Mcp", operatorSurfaceEnabled);
}
