using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace NetRatel.Tests.Cli;

public sealed partial class NetRatelCliTests
{
    [Fact]
    public async Task Node_log_pages_forward_exact_cursor_and_budget_without_rounding_ids()
    {
        var anchor = Guid.NewGuid(); var node = Guid.NewGuid();
        var code = await RunCliAsync(["operator-job-runs", "node-logs", "--tenant-id", "7", "--agent-id", anchor.ToString(), "--body",
            JsonSerializer.Serialize(new { jobRunId = "9007199254740993", nodeAgentId = node, stepRunId = "9007199254740995",
                cursor = "9007199254740997:9007199254740999", limit = 700, maximumBytes = 32000 })], request =>
        {
            if (request.RequestUri!.Host == "auth.example") return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            request.RequestUri.AbsolutePath.Should().EndWith($"/runs/9007199254740993/nodes/{node:D}/steps/9007199254740995/logs");
            Uri.UnescapeDataString(request.RequestUri.Query).Should().Contain("cursor=9007199254740997:9007199254740999")
                .And.Contain("limit=700").And.Contain("maximumBytes=32000");
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        code.Should().Be(CliExitCodes.Success);
    }

    [Fact]
    public async Task Shared_target_preview_and_reviewed_launch_preserve_typed_runtime_values()
    {
        var agent = Guid.NewGuid();
        foreach (var (arguments, expectedPath) in new[]
        {
            (new[] { "job-targets", "preview", "--tenant-id", "7", "--body", """{"filter":"tags: TestSystem","page":2,"pageSize":2}""" },
                "/api/v2/mcp/operator/application/netratel_job_targets/preview"),
            (new[] { "operator-job-runs", "start", "--tenant-id", "7", "--agent-id", agent.ToString(), "--body", """{"jobId":"9007199254740993","runtimeTarget":{"kind":"ClientId","values":["11111111-1111-4111-8111-111111111111"]}}""" },
                $"/api/v2/mcp/operator/agents/7/{agent:D}/jobs/9007199254740993/runs/preview")
        })
        {
            var code = await RunCliAsync(arguments, async request =>
            {
                if (request.RequestUri!.Host == "auth.example") return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");
                request.RequestUri.AbsolutePath.Should().Be(expectedPath);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                if (expectedPath.Contains("application", StringComparison.Ordinal))
                    body.RootElement.GetProperty("request").GetProperty("data").GetProperty("page").GetInt32().Should().Be(2);
                else
                    body.RootElement.GetProperty("runtimeTarget").GetProperty("kind").GetString().Should().Be("ClientId");
                return Json(HttpStatusCode.OK, "{}");
            });
            code.Should().Be(CliExitCodes.Success);
        }
    }
}
