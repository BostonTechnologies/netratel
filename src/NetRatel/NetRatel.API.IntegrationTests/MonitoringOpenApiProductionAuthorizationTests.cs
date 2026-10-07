using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Security.Authorization;
using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class MonitoringOpenApiProductionAuthorizationTests(ApiFactory factory)
{
    private sealed record Operation(string Method, string Path, string Policy);
    private static readonly Operation[] Operations =
    [
        new("GET", "/api/v2/monitoring/tenants", MonitoringAuthorization.DiscoveryPolicy),
        new("GET", "/api/v2/tenants/{tenantId}/monitoring/permissions", MonitoringAuthorization.PermissionsSummaryPolicy),
        new("GET", "/api/v2/tenants/{tenantId}/monitoring/configuration", MonitoringAuthorization.ReadPolicy),
        new("GET", "/api/v2/tenants/{tenantId}/monitoring/summary", MonitoringAuthorization.ReadPolicy),
        new("GET", "/api/v2/tenants/{tenantId}/monitoring/series", MonitoringAuthorization.ReadPolicy),
        new("GET", "/api/v2/tenants/{tenantId}/monitoring/events", MonitoringAuthorization.ReadPolicy),
        new("GET", "/api/v2/tenants/{tenantId}/monitoring/published-flows", MonitoringAuthorization.ReadOrManagePolicy),
        new("GET", "/api/v2/tenants/{tenantId}/monitoring/clients", MonitoringAuthorization.ReadOrManagePolicy),
        new("GET", "/api/v2/tenants/{tenantId}/monitoring/agents/{agentId}/series", MonitoringAuthorization.ReadPolicy),
        new("POST", "/api/v2/tenants/{tenantId}/monitoring/targets/preview", MonitoringAuthorization.ManagePolicy),
        new("PUT", "/api/v2/tenants/{tenantId}/monitoring/rules/{ruleId}", MonitoringAuthorization.ManagePolicy),
        new("PUT", "/api/v2/tenants/{tenantId}/monitoring/groups/{groupId}", MonitoringAuthorization.ManagePolicy),
        new("PUT", "/api/v2/tenants/{tenantId}/monitoring/bypasses/{bypassId}", MonitoringAuthorization.BypassPolicy),
        new("DELETE", "/api/v2/tenants/{tenantId}/monitoring/rules/{ruleId}", MonitoringAuthorization.ManagePolicy),
        new("DELETE", "/api/v2/tenants/{tenantId}/monitoring/groups/{groupId}", MonitoringAuthorization.ManagePolicy),
        new("DELETE", "/api/v2/tenants/{tenantId}/monitoring/bypasses/{bypassId}", MonitoringAuthorization.BypassPolicy),
        new("POST", "/api/v2/tenants/{tenantId}/monitoring/agents/{agentId}/rules/{ruleId}/ack", MonitoringAuthorization.AcknowledgePolicy),
        new("POST", "/api/v2/tenants/{tenantId}/monitoring/agents/{agentId}/rules/{ruleId}/clear", MonitoringAuthorization.ClearPolicy),
    ];

    [Fact]
    public async Task Every_monitoring_operation_has_its_exact_named_runtime_permission_and_resolvable_document_alternatives()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        foreach (var expected in Operations)
        {
            var routePattern = expected.Path.Replace("{tenantId}", "{tenantId:int}")
                .Replace("{agentId}", "{agentId:guid}").Replace("{ruleId}", "{ruleId:guid}")
                .Replace("{groupId}", "{groupId:guid}").Replace("{bypassId}", "{bypassId:guid}");
            var endpoint = endpoints.Single(endpoint =>
                string.Equals(endpoint.RoutePattern.RawText?.TrimStart('/'), routePattern.TrimStart('/'), StringComparison.Ordinal) &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(expected.Method));
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy)
                .Should().Equal(new[] { expected.Policy }, because: "the exposed operation must use its exact current permission gate with no inherited bare policy");
            AssertSecurity(document, expected.Path, expected.Method, ["Bearer", "LocalSession", "IntegrationCredential"]);
        }
        AssertSecurity(document, "/api/v2/access/self", "GET", ["Bearer", "LocalSession"]);

        foreach (var expected in Operations)
        {
            var requestPath = expected.Path.Replace("{tenantId}", "1")
                .Replace("{agentId}", Guid.NewGuid().ToString()).Replace("{ruleId}", Guid.NewGuid().ToString())
                .Replace("{groupId}", Guid.NewGuid().ToString()).Replace("{bypassId}", Guid.NewGuid().ToString());
            if (requestPath.EndsWith("/ack", StringComparison.Ordinal) || requestPath.EndsWith("/clear", StringComparison.Ordinal))
                requestPath += "?resourceKey=cpu";
            using var request = new HttpRequestMessage(new HttpMethod(expected.Method), requestPath);
            if (expected.Method != "GET") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var anonymous = await client.SendAsync(request);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "{0} {1} is a protected account operation", expected.Method, expected.Path);
        }
    }

    private static void AssertSecurity(JsonObject document, string path, string method, string[] expected)
    {
        var requirements = document["paths"]![path]![method.ToLowerInvariant()]!["security"]!.AsArray();
        requirements.Count.Should().Be(expected.Length);
        requirements.Should().OnlyContain(requirement => requirement != null && requirement.AsObject().Count == 1,
            "supported account authentication schemes are separate OR alternatives");
        requirements.SelectMany(requirement => requirement!.AsObject().Select(pair => pair.Key))
            .Should().BeEquivalentTo(expected);
    }
}
