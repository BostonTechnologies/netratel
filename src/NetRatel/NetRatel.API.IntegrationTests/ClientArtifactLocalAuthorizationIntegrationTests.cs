using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Models;
using NetRatel.API.Services;
using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class ClientArtifactLocalAuthorizationIntegrationTests(ApiFactory factory)
{
    [Fact]
    public async Task ArtifactUploadRequiresEffectiveInstanceAuthorityAndOnlyAuthorizedUploadsAreStored()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var administrator = await factory.CreateLocalAdministratorClientAsync(TimeSpan.FromSeconds(45));
        var tenantId = await ReadTenantIdAsync(administrator, timeout.Token);

        var anonymousVersion = NewVersion();
        using (var anonymous = factory.CreateClient())
        using (var response = await PostUploadAsync(
            anonymous, anonymousVersion, BuildArchive(anonymousVersion), timeout.Token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "artifact upload requires an authenticated caller");
        }

        var unprivilegedVersion = NewVersion();
        using (var unprivileged = await factory.CreateLocalUserClientAsync(
                   $"artifact-reader-{Guid.NewGuid():N}@example.test", "A1! unprivileged integration passphrase"))
        using (var response = await PostUploadAsync(
                   unprivileged, unprivilegedVersion, BuildArchive(unprivilegedVersion), timeout.Token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a real Local account without effective instance artifact authority must remain denied");
        }

        var agentVersion = NewVersion();
        using (var agent = factory.CreateClient())
        {
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", factory.CreateAgentBearerToken(tenantId, Guid.NewGuid()));
            using var response = await PostUploadAsync(
                agent, agentVersion, BuildArchive(agentVersion), timeout.Token);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a valid signed Agent token is not operator artifact authority");
        }

        var invalidMixedVersion = NewVersion();
        administrator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-token");
        using (var response = await PostUploadAsync(
                   administrator, invalidMixedVersion, BuildArchive(invalidMixedVersion), timeout.Token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "an explicit invalid bearer token must not fall back to the administrator cookie");
        }

        var agentMixedVersion = NewVersion();
        administrator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateAgentBearerToken(tenantId, Guid.NewGuid()));
        using (var response = await PostUploadAsync(
                   administrator, agentMixedVersion, BuildArchive(agentMixedVersion), timeout.Token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "an explicit valid Agent bearer token must not borrow authority from a Local administrator cookie");
        }

        administrator.DefaultRequestHeaders.Authorization = null;
        foreach (var version in new[]
                 {
                     anonymousVersion,
                     unprivilegedVersion,
                     agentVersion,
                     invalidMixedVersion,
                     agentMixedVersion
                 })
        {
            (await ReadArtifactMetadataAsync(version, timeout.Token)).Should().BeNull(
                "a rejected upload must not leave artifact metadata or bytes behind");
        }

        var acceptedVersion = NewVersion();
        var archive = BuildArchive(acceptedVersion);
        using (var response = await PostUploadAsync(
                   administrator, acceptedVersion, archive, timeout.Token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created,
                "the persisted Local instance administrator is authorized by the registered effective-access policy");
            var upload = await response.Content.ReadFromJsonAsync<ClientArtifactUploadResultDto>(timeout.Token);
            upload.Should().NotBeNull();
            upload!.Created.Should().BeTrue();
            upload.Artifact.Rid.Should().Be("win-x64");
            upload.Artifact.Version.Should().Be(acceptedVersion);
            upload.Artifact.Size.Should().Be(archive.Length);
            upload.Artifact.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant());
        }

        using var download = await administrator.GetAsync(
            $"/api/v1/client-artifacts/win-x64/{Uri.EscapeDataString(acceptedVersion)}/raw-download",
            timeout.Token);
        download.StatusCode.Should().Be(HttpStatusCode.OK,
            "the same administrator can retrieve the artifact through the protected raw-download route");
        (await download.Content.ReadAsByteArrayAsync(timeout.Token)).Should().Equal(archive);
    }

    private static async Task<int> ReadTenantIdAsync(HttpClient administrator, CancellationToken cancellationToken)
    {
        using var response = await administrator.GetAsync("/api/v2/access/tenants", cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the fixture administrator must use the real Local cookie and effective-access tenant API");

        using var tenants = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
        tenants.Should().NotBeNull();
        var tenant = tenants!.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "OpenAPI tenant");
        return tenant.GetProperty("tenantId").GetInt32();
    }

    private async Task<ClientArtifactSummaryDto?> ReadArtifactMetadataAsync(
        string version,
        CancellationToken cancellationToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var artifacts = scope.ServiceProvider.GetRequiredService<IClientArtifactsService>();
        return await artifacts.GetMetadataAsync("win-x64", version, cancellationToken);
    }

    private static async Task<HttpResponseMessage> PostUploadAsync(
        HttpClient client,
        string version,
        byte[] archive,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("win-x64"), "rid");
        content.Add(new StringContent(version), "version");
        content.Add(new StringContent("Production Local authorization integration test"), "notes");

        var file = new ByteArrayContent(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", $"NetRatel.Client-win-x64-{version}.zip");
        return await client.PostAsync("/api/v1/client-artifacts/upload", content, cancellationToken);
    }

    private static string NewVersion() => $"1.0.0-it.{Guid.NewGuid():N}";

    private static byte[] BuildArchive(string version)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var executable = archive.CreateEntry("NetRatel.Client.exe");
            using (var writer = new StreamWriter(executable.Open(), Encoding.UTF8))
            {
                writer.Write("integration fixture executable");
            }

            var manifest = archive.CreateEntry("netratel-client-manifest.json");
            using var manifestWriter = new StreamWriter(manifest.Open(), Encoding.UTF8);
            manifestWriter.Write(JsonSerializer.Serialize(new
            {
                schema = "netratel.client.manifest.v1",
                product = "NetRatel.Client",
                version,
                runtimeId = "win-x64",
                commitSha = "0123456789abcdef0123456789abcdef01234567",
                executable = "NetRatel.Client.exe"
            }));
        }

        return stream.ToArray();
    }
}
