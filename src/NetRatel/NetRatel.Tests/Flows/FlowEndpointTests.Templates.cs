using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed partial class FlowEndpointTests
{
    [Theory]
    [MemberData(nameof(FlowOverhaulTemplateTests.TemplateIds), MemberType = typeof(FlowOverhaulTemplateTests))]
    public async Task Every_template_creates_only_a_disabled_unpublished_draft_with_no_execution(string templateId)
    {
        using var host = await HostAsync(); var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var response = await http.PostAsJsonAsync(Path + "/from-template",
            new FlowCreateFromTemplateRequest("  Synthetic template  ", templateId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flow = (await response.Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        Assert.Equal(17, flow.TenantId); Assert.Equal("Synthetic template", flow.Name);
        Assert.False(flow.Enabled); Assert.Null(flow.PublishedVersionId); Assert.Equal(0, flow.PublishedVersionNumber);
        Assert.Equal(1, flow.Revision);
        var expected = FlowOverhaulTemplates.Descriptors.Single(template => template.Id == templateId);
        Assert.Equal(expected.SchemaVersion, flow.Draft.SchemaVersion);
        Assert.All(flow.Draft.Nodes, node => Assert.Null(node.ConnectorId));
        var reload = (await http.GetFromJsonAsync<FlowDefinitionDto>($"{Path}/{flow.Id}"))!;
        Assert.False(reload.Enabled); Assert.Equal(flow.Draft.SchemaVersion, reload.Draft.SchemaVersion);
        Assert.Equal(flow.Draft.Nodes.Select(node => node.Id), reload.Draft.Nodes.Select(node => node.Id));
        Assert.Equal(0, host.Services.GetRequiredService<ExecutionSentinel>().Calls);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(0, await db.FlowVersions.CountAsync()); Assert.Equal(0, await db.FlowRuns.CountAsync());
        Assert.Equal(0, await db.FlowActions.CountAsync());
        var audit = await db.FlowAudits.SingleAsync();
        Assert.Equal("created-from-template", audit.Operation); Assert.Equal(flow.Id, audit.FlowId);
        Assert.Equal(FlowTestData.Authority.PrincipalId, audit.ActorId);
    }

    [Fact]
    public async Task Template_catalog_and_creation_enforce_tenant_permissions_before_disclosure_or_writes()
    {
        using var host = await HostAsync(); var http = host.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(Path + "/templates")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Test", "read");
        var catalog = (await http.GetFromJsonAsync<FlowTemplateDescriptorDto[]>(Path + "/templates"))!;
        Assert.Equal(FlowOverhaulTemplates.Descriptors.Count, catalog.Length);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await http.GetAsync(Path.Replace("/17/", "/18/", StringComparison.Ordinal) + "/templates")).StatusCode);
        var request = new FlowCreateFromTemplateRequest("Synthetic template", FlowOverhaulTemplates.ScriptRecovery);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(Path + "/from-template", request)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Test", "all");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await http.PostAsJsonAsync(Path.Replace("/17/", "/18/", StringComparison.Ordinal) + "/from-template", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(Path + "/from-template",
            request with { TemplateId = "unsupported-template" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(Path + "/from-template",
            request with { Name = " " })).StatusCode);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(0, await db.FlowDefinitions.CountAsync()); Assert.Equal(0, await db.FlowAudits.CountAsync());
        Assert.Equal(0, host.Services.GetRequiredService<ExecutionSentinel>().Calls);
        var old = (await http.GetFromJsonAsync<FlowGraphDto>(Path + "/template"))!;
        Assert.Equal(1, old.SchemaVersion);
    }
}
