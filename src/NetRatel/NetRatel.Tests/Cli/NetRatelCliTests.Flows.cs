using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace NetRatel.Tests.Cli;

public sealed partial class NetRatelCliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlowDelete_dispatches_exact_graph_free_target_with_explicit_confirmation(bool confirmed)
    {
        var id = Guid.NewGuid();
        var code = await RunCliAsync(["flows", "delete", "--tenant-id", "7", "--flow-id", id.ToString(),
            "--body", "{\"expectedRevision\":4}", .. confirmed ? new[] { "--confirm" } : []], async request =>
        {
            if (request.RequestUri!.Host == "auth.example") return Json(HttpStatusCode.OK, "{\"access_token\":\"minted-token\"}");
            request.RequestUri.AbsolutePath.Should().EndWith("/netratel_flows/delete");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            json.RootElement.GetProperty("confirm").GetBoolean().Should().Be(confirmed);
            var target = json.RootElement.GetProperty("request"); target.GetProperty("flowId").GetGuid().Should().Be(id);
            target.GetProperty("data").EnumerateObject().Select(property => property.Name).Should().Equal("expectedRevision");
            return Json(HttpStatusCode.OK, "{}");
        });
        code.Should().Be(CliExitCodes.Success);
    }
}
