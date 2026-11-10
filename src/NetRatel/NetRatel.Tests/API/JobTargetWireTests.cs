using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services.Jobs;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using NetRatel.Application.Operations;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobTargetWireTests
{
    [Theory]
    [InlineData("4")]
    [InlineData("RuntimeInput, ExplicitNodes")]
    [InlineData(" RuntimeInput")]
    public void Managed_target_data_uses_the_same_strict_enum_boundary(string invalidSource)
    {
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(new { target = new { source = invalidSource,
            runtimeInput = new { kind = "Tags", name = "Tags" } } }));
        Assert.Throws<JsonException>(() => JobTargetHttpJson.Read<JobTargetPreviewRequest>(body.RootElement));
    }

    [Fact]
    public async Task Named_tooling_inputs_round_trip_through_HTTP_without_changing_numeric_Web_or_pin_formats()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(JobTargetHttpJson.Configure);
        await using var app = builder.Build();
        app.MapPost("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/preview/create",
            (McpOperatorJobMutationRequest request) => request.Job);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var outbound = new WireClient(http);
        var target = new JobTargetDefinition { Source = JobTargetSource.RuntimeInput,
            RuntimeInput = new(JobRuntimeTargetKind.ClientId, "ClientId") };
        var policy = new JobExecutionPolicy(2, JobNodeFailurePolicy.ContinueRemaining);
        var draft = new McpOperatorJobDraftV2("Investigate low disk space", "/") { Target = target, ExecutionPolicy = policy };
        var result = await new McpOperatorJobV2Client(outbound).PreviewMutationAsync(new(7, Guid.NewGuid()), "create", new(Job: draft));
        outbound.Body!["job"]!["target"]!["source"]!.GetValue<string>().Should().Be("RuntimeInput");
        result!["target"]!["source"]!.GetValue<int>().Should().Be((int)JobTargetSource.RuntimeInput);
        result["target"]!["runtimeInput"]!["kind"]!.GetValue<int>().Should().Be((int)JobRuntimeTargetKind.ClientId);
        var web = result.Deserialize<McpOperatorJobDraft>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        web.Target!.Source.Should().Be(target.Source);
        web.ExecutionPolicy.Should().Be(policy);
        var canonical = JsonSerializer.SerializeToElement(target);
        canonical.GetProperty("Source").GetInt32().Should().Be(4);
        var pinned = JsonSerializer.SerializeToElement(target, JobTargetJsonContext.Default.JobTargetDefinition);
        pinned.GetProperty("RuntimeInput").GetProperty("Kind").GetInt32().Should().Be(1);

        foreach (var bad in new[] { "4", "ExecutionGroup,ExplicitNodes", " RuntimeInput " })
        {
            var body = new JsonObject { ["job"] = new JsonObject { ["name"] = "Invalid target", ["folderPath"] = "/",
                ["target"] = new JsonObject { ["source"] = bad } } };
            using var rejected = await http.PostAsJsonAsync($"/api/v2/mcp/operator/agents/7/{Guid.NewGuid():D}/jobs/preview/create", body);
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    private sealed class WireClient(HttpClient http) : INetRatelMcpOutboundClient
    {
        public NetRatelMcpTarget Target { get; } = new("prod", new Uri("https://api.example.test"), new Uri("https://mcp.example.test/mcp"), "test");
        public JsonNode? Body { get; private set; }
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            Body = body?.DeepClone();
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        }
    }
}
