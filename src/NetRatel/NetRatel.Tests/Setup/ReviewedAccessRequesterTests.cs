using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.AgentClient;
using NetRatel.Setup;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.Setup;

public sealed class ReviewedAccessRequesterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Command_keeps_requester_and_ambient_credentials_out_of_the_reviewed_destination(bool dedicatedRequester)
    {
        using var directory = new PrivateDirectory();
        var requester = Path.Combine(directory.Path, "requester.json");
        var destination = Path.Combine(directory.Path, "auxiliary.json");
        var state = Path.Combine(directory.Path, "pending.json");
        var proposalPath = Path.Combine(directory.Path, "proposal.json");
        const string primary = "nrt_ic_synthetic-primary";
        const string auxiliary = "nrt_ic_synthetic-reviewed-auxiliary";
        const string device = "nrt_pair_synthetic-private-capability";
        var profile = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "IntegrationCredential", CredentialPurpose = "HttpMcp",
            IntegrationCredential = dedicatedRequester ? null : primary
        };
        if (dedicatedRequester) ConfigurationFileStorage.WriteAtomic(requester, JsonSerializer.Serialize(profile, AgentClientConfiguration.JsonOptions));
        else new NetRatelConfigurationService().SaveProfile(requester, profile, ConfigurationSurface.HttpMcpClient);
        var requesterHash = ConfigurationFileStorage.Hash(requester);
        var protectedFile = NetRatelConfigurationService.ReadConfiguration(requester).IntegrationCredentialFile;
        var protectedHash = protectedFile is null ? null : ConfigurationFileStorage.Hash(protectedFile);
        var expiry = DateTimeOffset.Parse("2030-04-01T10:11:12.123456Z");
        var proposal = new ReviewedMcpAccessProposal(Guid.NewGuid().ToString("N"), "Restricted auxiliary", "create", expiry,
            [new(17, "flow.read")], ["mcp.discovery.read"], [new(17, "netratel_flows", "list")]);
        ConfigurationFileStorage.WriteAtomic(proposalPath, JsonSerializer.Serialize(proposal, AgentClientConfiguration.JsonOptions));
        var starts = 0; var acknowledgements = 0;
        using var handler = new Handler(async request =>
        {
            Assert.Equal("instance.example", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath.EndsWith("capabilities", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization);
                return Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", pairingSupported = true,
                    verificationUri = "https://instance.example/account/integration-credentials", httpMcpResource = "https://instance.example/mcp" });
            }
            if (request.RequestUri.AbsolutePath == "/mcp/setup/access/start")
            {
                starts++;
                Assert.Equal("Bearer " + primary, request.Headers.Authorization!.ToString());
                Assert.False(File.Exists(destination));
                return Json(new IntegrationPairingStartResponse(proposal.RequestId, "ABCD-EFGH", device,
                    "https://instance.example/account/integration-credentials", "https://instance.example", DateTimeOffset.UtcNow.AddMinutes(10), 5));
            }
            Assert.Null(request.Headers.Authorization);
            using var content = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(device, content.RootElement.GetProperty("deviceCapability").GetString());
            if (request.RequestUri.AbsolutePath.EndsWith("/poll", StringComparison.Ordinal))
                return Json(new IntegrationPairingPollResponse("issued", Delivery: new("a1111111111141118111111111111111", auxiliary, proposal.Name,
                    PairingCredentialPurpose.HttpMcp, "https://instance.example/mcp", expiry, proposal.Grants, proposal.InstancePermissions)));
            if (request.RequestUri.AbsolutePath.EndsWith("/acknowledge", StringComparison.Ordinal))
            {
                acknowledgements++;
                return Json(new { status = "consumed" });
            }
            throw new InvalidOperationException("Unexpected reviewed setup request.");
        });
        string? EnvironmentValue(string key) => key switch
        {
            ReviewedAccessRequesterConfiguration.CredentialEnvironmentVariable => dedicatedRequester ? primary : null,
            "NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN" or "NetRatel__Mcp__Client__IntegrationCredential" => "nrt_ic_wrong-ambient-primary",
            "NETRATEL_MCP_CLIENT_API_BASE_URL" or "NetRatel__Mcp__Client__ApiBaseUrl" => "https://wrong-destination.example/mcp",
            _ => null
        };
        var options = new Dictionary<string, string>
        {
            ["--state-file"] = state, ["--config"] = destination, ["--profile"] = "auxiliary",
            ["--instance"] = "https://instance.example/mcp", ["--request-file"] = proposalPath, ["--requester-config"] = requester
        };
        using var output = new StringWriter();
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(0, await SetupCommand.RunReviewedAccessAsync(["access", "prepare"], options, output, default, handler, EnvironmentValue));
        Assert.Equal(1, starts);
        Assert.False(File.Exists(destination));
        Assert.DoesNotContain(primary, File.ReadAllText(state));
        var changedProfile = new Dictionary<string, string>(options) { ["--profile"] = "changed" };
        await Assert.ThrowsAsync<AgentClientValidationException>(() => SetupCommand.RunReviewedAccessAsync(["access", "prepare"], changedProfile, output, default, handler, EnvironmentValue));
        Assert.Equal(1, starts);

        Assert.Equal(0, await SetupCommand.RunReviewedAccessAsync(["access", "resume"], options, output, default, handler, EnvironmentValue));
        Assert.False(File.Exists(state));
        Assert.Equal(1, acknowledgements);
        var saved = new NetRatelConfigurationService().Load(new(ConfigurationSurface.HttpMcpClient, destination, "auxiliary", GetEnvironmentVariable: _ => null));
        Assert.Equal(auxiliary, saved.Configuration.IntegrationCredential);
        Assert.Equal("https://instance.example/mcp", saved.Configuration.ApiBaseUrl);
        Assert.Equal(requesterHash, ConfigurationFileStorage.Hash(requester));
        if (protectedFile is not null) Assert.Equal(protectedHash, ConfigurationFileStorage.Hash(protectedFile));
        Assert.DoesNotContain(primary, File.ReadAllText(destination));
        Assert.DoesNotContain(auxiliary, File.ReadAllText(destination));
        foreach (var secret in new[] { primary, auxiliary, device, "nrt_ic_wrong-ambient-primary" }) Assert.DoesNotContain(secret, output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("synthetic\r\nheader")]
    [InlineData("synthetic\0credential")]
    public void Invalid_dedicated_requester_value_is_rejected_without_falling_back_or_echoing_it(string value)
    {
        var error = Assert.Throws<AgentClientValidationException>(() => ReviewedAccessRequesterConfiguration.Load("not-opened.json", _ => value));
        Assert.Equal("The protected reviewed-access requester credential is invalid.", error.Message);
    }

    [Fact]
    public void Dedicated_requester_value_is_bounded_before_opening_configuration()
        => Assert.Throws<AgentClientValidationException>(() => ReviewedAccessRequesterConfiguration.Load("not-opened.json", _ => new string('x', 16_385)));

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
    private sealed class PrivateDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netratel-reviewed-requester-" + Guid.NewGuid().ToString("N"));
        public PrivateDirectory()
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Path);
            else Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
