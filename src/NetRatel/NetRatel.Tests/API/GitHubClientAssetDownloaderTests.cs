using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GitHubClientAssetDownloaderTests
{
    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://127.0.0.1/file")]
    [InlineData("https://metadata.google.internal/file")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/file")]
    public async Task RejectsUnsafeRedirectBeforeRequestingIt(string location)
    {
        var calls = 0;
        using var client = Client(request =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri(location) }
            };
        });
        var path = Path.Combine(Path.GetTempPath(), $"netratel-asset-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(client).DownloadAsync(
            5, 7, null, path, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RedirectDropsCredentialAndVerifiesStreamedBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var calls = 0;
        using var client = Client(request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("api.github.com", request.RequestUri!.Host);
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://release-assets.githubusercontent.com/signed?secret=example") }
                };
            }
            Assert.Equal("release-assets.githubusercontent.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var path = Path.Combine(Path.GetTempPath(), $"netratel-asset-{Guid.NewGuid():N}");
        try
        {
            var result = await Downloader(client).DownloadAsync(5, bytes.Length, "sha256:" + digest,
                path, TestContext.Current.CancellationToken);
            Assert.Equal(bytes.Length, result.SizeBytes);
            Assert.Equal(digest, result.Sha256);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConflictingExistingPathIsPreserved()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("payload"))
        });
        var path = Path.Combine(Path.GetTempPath(), $"netratel-asset-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "existing");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => Downloader(client).DownloadAsync(
                5, 7, null, path, TestContext.Current.CancellationToken));
            Assert.Equal("existing", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static GitHubClientAssetDownloader Downloader(HttpClient client) => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GitHubReleases:ReadOnlyToken"] = "test-token"
        }).Build());

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> callback) =>
        new(new StubHandler(callback));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
