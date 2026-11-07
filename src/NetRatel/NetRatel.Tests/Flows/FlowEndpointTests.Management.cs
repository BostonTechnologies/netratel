using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed partial class FlowEndpointTests
{
    [Fact]
    public async Task Delete_only_authority_gets_no_draft_and_edit_only_authority_cannot_delete()
    {
        using var host = await HostAsync(); var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var created = await http.PostAsJsonAsync(Path, new FlowCreateRequest("Synthetic protected draft"));
        var flow = (await created.Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var row = await db.FlowDefinitions.SingleAsync();
        row.DraftJson = "{\"schemaVersion\":2,\"privatePrompt\":\"must never appear\"}"; await db.SaveChangesAsync();
        http.DefaultRequestHeaders.Authorization = new("Test", "edit");
        using var edit = await http.SendAsync(new(HttpMethod.Delete, $"{Path}/{flow.Id}") { Content = JsonContent.Create(new FlowDeleteRequest(flow.Revision)) });
        edit.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        http.DefaultRequestHeaders.Authorization = new("Test", "delete");
        (await http.GetAsync($"{Path}/{flow.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var foreign = await http.SendAsync(new(HttpMethod.Delete, $"{Path.Replace("/17/", "/18/")}/{flow.Id}") { Content = JsonContent.Create(new FlowDeleteRequest(flow.Revision)) });
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var deleted = await http.SendAsync(new(HttpMethod.Delete, $"{Path}/{flow.Id}") { Content = JsonContent.Create(new FlowDeleteRequest(flow.Revision)) });
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await deleted.Content.ReadAsStringAsync();
        json.Should().NotContain("draft").And.NotContain("privatePrompt").And.NotContain("must never appear").And.NotContain(flow.Name);
        var result = (await deleted.Content.ReadFromJsonAsync<FlowDeleteResultDto>())!;
        result.Disposition.Should().Be(FlowDeleteDisposition.Deleted);
        using var repeat = await http.SendAsync(new(HttpMethod.Delete, $"{Path}/{flow.Id}") { Content = JsonContent.Create(new FlowDeleteRequest(flow.Revision)) });
        (await repeat.Content.ReadFromJsonAsync<FlowDeleteResultDto>())!.Disposition.Should().Be(FlowDeleteDisposition.AlreadyDeleted);
        http.DefaultRequestHeaders.Authorization = new("Test", "read");
        (await http.GetAsync($"{Path}/{flow.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.GetFromJsonAsync<FlowSummaryDto>($"{Path}/{flow.Id}/metadata"))!.DeletedAtUtc.Should().NotBeNull();
        (await http.GetAsync($"{Path}/{flow.Id}/versions")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetAsync($"{Path}/{flow.Id}/runs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetFromJsonAsync<FlowDefinitionDto[]>(Path)).Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_reads_are_additive_metadata_only_and_validate_bounded_query_values()
    {
        using var host = await HostAsync(); var http = host.GetTestClient(); http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var created = await http.PostAsJsonAsync(Path, new FlowCreateRequest("Literal %_ search"));
        var flow = (await created.Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var row = await db.FlowDefinitions.SingleAsync(); row.DraftJson = "{\"privatePrompt\":\"do not parse\"}"; await db.SaveChangesAsync();
        http.DefaultRequestHeaders.Authorization = new("Test", "read");
        var response = await http.GetAsync(Path + "/summary?search=%25_&pageSize=20&sort=Name");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<FlowPageDto>())!;
        page.Items.Single().Id.Should().Be(flow.Id); page.Total.Should().Be(1);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("privatePrompt").And.NotContain("do not parse");
        foreach (var query in new[] { "pageSize=0", "pageSize=101", "pageIndex=-1", "pageIndex=2147483647", "sort=42", "enabled=42", "search=" + new string('x',129) })
            (await http.GetAsync(Path + "/summary?" + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
