using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.Jobs;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorJobEndpointTests
{
    [Fact]
    public void Gateway_terminal_result_supplies_bounded_redacted_job_output()
    {
        var activity = Activity("""{"stdout":["job-marker","token=synthetic-value"],"stderr":"diagnostic","unrelatedSecret":"never expose"}""");

        var logs = McpOperatorJobEndpoints.ProjectActivityLogs(activity, [], 100);

        logs.Select(log => log.Stream).Should().Equal("stdout", "stdout", "stderr");
        logs.Select(log => log.Message).Should().Equal("job-marker", "token=[REDACTED]", "diagnostic");
        logs.Select(log => log.Sequence).Should().Equal(1, 2, 3);
        logs.Should().OnlyContain(log => log.RequestId == activity.RequestId && log.TimestampUtc == activity.CompletedAtUtc);
    }

    [Fact]
    public void Streamed_logs_take_precedence_over_terminal_snapshot()
    {
        var activity = Activity("""{"stdout":["duplicate snapshot"]}""");
        var persisted = new JobTaskLogInfo(1, activity.RequestId, activity.Id, "client", 42,
            "stdout", "streamed marker", 7, activity.CreatedAtUtc);

        McpOperatorJobEndpoints.ProjectActivityLogs(activity, [persisted], 100)
            .Should().ContainSingle().Which.Message.Should().Be("streamed marker");
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("[]")]
    [InlineData("{\"stdout\":[null,123,{}],\"stderr\":false}")]
    public void Malformed_or_nontext_results_do_not_become_log_output(string result)
        => McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 100).Should().BeEmpty();

    [Fact]
    public void Output_respects_remaining_line_budget_and_message_bound()
    {
        var result = System.Text.Json.JsonSerializer.Serialize(new { stdout = new[] { new string('x', 5000), "second", "third" } });
        var logs = McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 2);
        logs.Should().HaveCount(2);
        logs[0].Message.Should().HaveLength(4096);
        logs[1].Message.Should().Be("second");
        McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 0).Should().BeEmpty();
    }

    private static JobTaskActivityInfo Activity(string result) => new(1, "job-request", 2, 3,
        "client", 42, "exec-library-script", "Completed", null,
        DateTimeOffset.Parse("2026-09-05T00:00:00Z"), DateTimeOffset.Parse("2026-09-05T00:00:01Z"), ResultJson: result);

    [Fact]
    public void Production_job_routes_are_versioned_owned_and_m2m_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IClientPresenceRouter>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorJobStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IJobRunService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IAkkaJobAuthorityService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<NetRatelAkkaMigrationOptions>();
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorJobEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/details",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/params",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/steps",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/validate",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/preview/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/confirm/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/query",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/steps",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/logs",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/runs/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/runs/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/cancel/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/cancel/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/delete/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/delete/confirm");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}
