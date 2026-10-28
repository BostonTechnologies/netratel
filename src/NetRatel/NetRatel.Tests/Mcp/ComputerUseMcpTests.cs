using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Http;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class ComputerUseMcpTests
{
    [Theory]
    [InlineData("netratel.mcp.execute", "act", false)]
    [InlineData("netratel.mcp.observe", "observe", false)]
    [InlineData("netratel.mcp.cua.view", "act", false)]
    [InlineData("netratel.mcp.cua.view", "observe", true)]
    [InlineData("netratel.mcp.cua.view", "open_view", true)]
    [InlineData("netratel.mcp.cua.control", "act", true)]
    public void Desktop_access_uses_separate_view_and_control_scopes(string scope, string operation, bool allowed)
    {
        var authorizer = new McpOperationScopeAuthorization(Host(), new NetRatelMcpHttpOptions());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", scope)], "test"));
        Assert.Equal(allowed, authorizer.Evaluate(principal, "netratel_computer_use",
            new Dictionary<string, JsonElement> { ["operation"] = JsonSerializer.SerializeToElement(operation) }).Allowed);
        Assert.Contains("remote-support.access", McpLocalOperationAuthorizationCatalog.AdditionalPermissions("netratel_computer_use", operation));
    }

    [Theory]
    [InlineData(NetRatelMcpHttpOptions.DefaultExecuteScope)]
    [InlineData(NetRatelMcpHttpOptions.DefaultCuaViewScope)]
    [InlineData(NetRatelMcpHttpOptions.OpenIdScope)]
    [InlineData(NetRatelMcpHttpOptions.ProfileScope)]
    [InlineData(NetRatelMcpHttpOptions.OfflineAccessScope)]
    public void Dedicated_scope_options_cannot_alias_other_permissions_or_identity_scopes(string aliasedScope)
    {
        var options = new NetRatelMcpHttpOptions
        {
            Instance = "prod", ProdApiBaseUrl = "https://api.example", PublicResourceUri = "https://api.example/mcp",
            LocalCredentialMode = true, CuaControlScope = aliasedScope
        };
        var result = new NetRatelMcpHttpOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("distinct", StringComparison.Ordinal));
    }

    [Fact]
    public void Published_schema_rejects_proxy_fields_and_requires_exact_window_before_open()
    {
        var validator = new NetRatelMcpInputValidator(NetRatelMcpToolDefinitions.CreateForHttp(Host()));
        var target = TargetRequest(window: true);
        var arguments = Arguments("preview_open", target);
        Assert.Null(validator.Validate("netratel_computer_use", arguments));
        target["driverTool"] = "run_shell";
        Assert.NotNull(validator.Validate("netratel_computer_use", Arguments("preview_open", target)));
        target.Remove("driverTool");
        target["target"]!.AsObject().Remove("windowId");
        Assert.NotNull(validator.Validate("netratel_computer_use", Arguments("preview_open", target)));

        Assert.DoesNotContain(NetRatelMcpToolDefinitions.CreateForHttp("dev"), tool => tool.ProtocolTool.Name == "netratel_computer_use");
    }

    [Fact]
    public async Task Invalid_direct_target_does_not_reach_the_api()
    {
        var api = new RecordingApi();
        var handler = new NetRatelMcpOperationalTools(api, Host());
        var request = TargetRequest();
        request["target"]!["operatorId"] = "someone-else";
        var result = await handler.netratel_computer_use("capabilities", Element(request));
        Assert.True(result.IsError);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Mutation_requires_confirmation_and_preserves_exact_text_and_observation()
    {
        var api = new RecordingApi { Response = Result("applied_unverified") };
        var handler = new NetRatelMcpOperationalTools(api, Host());
        var request = SessionRequest();
        request["observationId"] = "observed_window_1";
        request["action"] = new JsonObject { ["kind"] = "type", ["elementId"] = "element_4", ["text"] = "  first\nsecond  " };
        request["planToken"] = new string('p', 32);
        request["idempotencyKey"] = new string('i', 32);
        var unconfirmed = await handler.netratel_computer_use("act", Element(request));
        Assert.True(unconfirmed.IsError);
        Assert.Empty(api.Calls);
        var confirmed = await handler.netratel_computer_use("act", Element(request), true);
        Assert.True(confirmed.IsError);
        var call = Assert.Single(api.Calls);
        Assert.EndsWith($"/sessions/{request["sessionId"]}/actions/confirm", call.Path);
        Assert.Equal("  first\nsecond  ", call.Body!["action"]!["text"]!.GetValue<string>());
        Assert.Equal("observed_window_1", call.Body["observationId"]!.GetValue<string>());
        Assert.Null(call.Body["tenantId"]);
    }

    [Theory]
    [InlineData("refused", false)]
    [InlineData("cancelled", false)]
    [InlineData("unknown", false)]
    [InlineData("applied_unverified", false)]
    [InlineData("completed", false)]
    [InlineData("verified", true)]
    public async Task Http_success_does_not_mislabel_an_unverified_action(string status, bool success)
    {
        var api = new RecordingApi { Response = Result(status) };
        var request = SessionRequest();
        request["observationId"] = "fresh_1";
        request["action"] = new JsonObject { ["kind"] = "invoke", ["elementId"] = "button_1" };
        request["planToken"] = new string('p', 32);
        request["idempotencyKey"] = new string('i', 32);
        var result = await new NetRatelMcpOperationalTools(api, Host()).netratel_computer_use("act", Element(request), true);
        Assert.Equal(success, result.StructuredContent!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(!success, result.IsError);
        Assert.Equal(status, result.StructuredContent.Value.GetProperty("data").GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Screenshot_is_mcp_image_content_without_duplicate_base64_text()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aA1cAAAAASUVORK5CYII=";
        var response = Result("observed");
        response["result"]!["observation"] = new JsonObject
        {
            ["observationId"] = "fresh_1",
            ["screenshot"] = new JsonObject { ["mimeType"] = "image/png", ["base64"] = png, ["width"] = 1, ["height"] = 1 }
        };
        var request = SessionRequest();
        request["includeScreenshot"] = true;
        var result = await new NetRatelMcpOperationalTools(new RecordingApi { Response = response }, Host())
            .netratel_computer_use("observe", Element(request));
        Assert.False(result.IsError);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Convert.FromBase64String(png), image.DecodedData.ToArray());
        Assert.DoesNotContain(png, result.StructuredContent!.Value.GetRawText());
        Assert.DoesNotContain(png, Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.Equal(1, result.StructuredContent.Value.GetProperty("data").GetProperty("result")
            .GetProperty("observation").GetProperty("screenshot").GetProperty("contentBlockIndex").GetInt32());
    }

    private static NetRatelMcpHostContext Host() => new(
        new NetRatelMcpTarget("prod", new Uri("https://api.example"), new Uri("https://api.example/mcp"), NetRatelMcpCatalog.Revision),
        NetRatelMcpTransport.StreamableHttp, "test");

    private static JsonObject TargetRequest(bool window = false)
    {
        var target = new JsonObject { ["windowsSessionId"] = 2, ["userSidHash"] = new string('a', 64) };
        if (window) { target["processId"] = 812; target["windowId"] = "window_1"; }
        return new JsonObject { ["tenantId"] = 17, ["agentId"] = Guid.NewGuid().ToString("D"), ["target"] = target, ["provider"] = "cua_driver" };
    }

    private static JsonObject SessionRequest() => new()
    { ["tenantId"] = 17, ["agentId"] = Guid.NewGuid().ToString("D"), ["sessionId"] = Guid.NewGuid().ToString("D") };

    private static JsonObject Result(string status) => new() { ["result"] = new JsonObject { ["status"] = status } };
    private static JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);
    private static Dictionary<string, JsonElement> Arguments(string operation, JsonObject request) => new()
    { ["operation"] = JsonSerializer.SerializeToElement(operation), ["request"] = Element(request) };

    private sealed class RecordingApi : INetRatelMcpApiClient
    {
        public List<(string Path, JsonNode? Body)> Calls { get; } = [];
        public JsonObject Response { get; init; } = Result("observed");
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        { Calls.Add((path, null)); return Task.FromResult<JsonNode?>(Response.DeepClone()); }
        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        { Calls.Add((path, body?.DeepClone())); return Task.FromResult<JsonNode?>(Response.DeepClone()); }
    }
}
