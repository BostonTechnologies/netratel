using System.Net;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Cli;

public sealed partial class NetRatelCliTests
{
    [Fact]
    public async Task Vault_write_reads_private_input_and_excludes_request_material_from_error_output()
    {
        var connection = Guid.NewGuid();
        const string secret = "synthetic multiline secret\n◆";
        using var stdin = new StringReader(JsonSerializer.Serialize(new { name = "Canary", value = secret }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var status = await RunCliAsync(["secrets", "vault", "write-entry", "--tenant-id", "7", "--connection-id", connection.ToString(), "--stdin"],
            async request =>
            {
                if (request.RequestUri!.Host == "auth.example") return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal($"/api/v1/tenants/7/secret-vault/connections/{connection:D}/entry", request.RequestUri.AbsolutePath);
                Assert.DoesNotContain("value", request.RequestUri.Query);
                using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal(secret, sent.RootElement.GetProperty("value").GetString());
                return Json(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = secret }));
            }, stdout, stderr, stdin);
        Assert.Equal(CliExitCodes.RemoteError, status);
        Assert.DoesNotContain("synthetic multiline secret", stdout.ToString() + stderr.ToString());
        Assert.Contains("vault_request_failed", stderr.ToString());
    }
}
