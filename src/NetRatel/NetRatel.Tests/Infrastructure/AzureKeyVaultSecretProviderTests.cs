using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Secrets;
using NetRatel.Shared.Contracts.Secrets;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class AzureKeyVaultSecretProviderTests
{
    private const string Vault = "https://netratel-fixture.vault.azure.net";
    private const string DirectoryId = "6055b963-646b-4a79-9f8d-fdeaacb2d5e5";
    private const string ApplicationId = "75bd6252-1b35-47ee-ac84-e24961242e3b";
    private const string Bootstrap = "synthetic-azure-bootstrap";

    [Fact]
    public async Task Azure_SDK_preserves_versions_metadata_and_recoverable_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new ConfigurationBuilder().Build();
        using var host = new HostBuilder().Build();
        using var httpClient = new HttpClient();
        var network = new SecretProviderNetworkPolicy(settings, host.Services.GetRequiredService<IHostEnvironment>());
        var fixture = new VaultFixture();
        var http = new SecretProviderHttp(httpClient, network, () => new ProtocolHandler(fixture));
        using var provider = new AzureKeyVaultSecretProvider(http, network, TimeProvider.System);
        var context = new SecretProviderContext(7, Guid.NewGuid(), 1,
            network.Normalize(SecretProviderKind.AzureKeyVault, new(Vault, DirectoryTenantId: DirectoryId, ClientId: ApplicationId)),
            () => new ProviderAuthentication { ClientSecret = Bootstrap });

        Assert.Empty((await provider.BrowseAsync(context, new(), ct)).Entries);
        var first = await provider.WriteAsync(context, new() { Locator = "Canary", Value = "synthetic first value", Description = "Initial note" }, ct);
        var listed = Assert.Single((await provider.BrowseAsync(context, new(), ct)).Entries);
        Assert.Equal(first.Locator, listed.Locator);
        Assert.Null(listed.Version);
        Assert.DoesNotContain("synthetic first value", JsonSerializer.Serialize(listed));
        Assert.Equal("Initial note", listed.Description);

        var next = await provider.WriteAsync(context, new()
        { Locator = "canary", Value = "synthetic next value", ExpectedRevision = first.Revision, ExpectedVersion = first.Version }, ct);
        Assert.NotEqual(first.Version, next.Version);
        Assert.Equal(first.Description, next.Description);
        Assert.Equal("synthetic first value", Assert.Single((await provider.ResolveAsync(context, "canary", first.Version, [null], ct)).Values));
        Assert.Equal("synthetic next value", Assert.Single((await provider.ResolveAsync(context, "canary", null, [null], ct)).Values));
        var annotated = await provider.WriteAsync(context, new()
        { Locator = "canary", Description = "Revised note", ExpectedRevision = next.Revision, ExpectedVersion = next.Version }, ct);
        Assert.Equal(next.Version, annotated.Version);
        Assert.NotEqual(next.Revision, annotated.Revision);
        Assert.Equal("Revised note", annotated.Description);
        Assert.Equal(new[] { next.Version, first.Version }, (await provider.GetMetadataAsync(context, "canary", null, ct)).Versions);
        var stale = await Assert.ThrowsAsync<SecretProviderException>(() => provider.DeleteAsync(context,
            new("canary", next.Revision!, next.Version!), ct));
        Assert.Equal(SecretProviderFailure.Conflict, stale.Reason);
        var removal = await provider.DeleteAsync(context, new("canary", annotated.Revision!, annotated.Version!), ct);
        Assert.Equal(SecretDeletionMode.RecoverableEntry, removal.Mode);
        Assert.Empty((await provider.BrowseAsync(context, new(), ct)).Entries);
        Assert.True(fixture.Deleted);
        Assert.True(fixture.Authenticated);
    }

    private sealed class ProtocolHandler(VaultFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => fixture.RespondAsync(request, ct);
    }

    private sealed class VaultFixture
    {
        private readonly List<Version> _versions = [];
        public bool Deleted { get; private set; }
        public bool Authenticated { get; private set; }

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (uri.Host == "login.microsoftonline.com")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal($"/{DirectoryId}/oauth2/v2.0/token", path);
                var content = await request.Content!.ReadAsStringAsync(ct);
                Assert.Contains($"client_id={ApplicationId}", content, StringComparison.Ordinal);
                Assert.Contains($"client_secret={Bootstrap}", content, StringComparison.Ordinal);
                Authenticated = true;
                return Respond(new { token_type = "Bearer", expires_in = 3600, access_token = "synthetic-azure-token" });
            }

            Assert.Equal(Vault, uri.GetLeftPart(UriPartial.Authority));
            if (request.Headers.Authorization is null)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.ParseAdd($"Bearer authorization=\"https://login.microsoftonline.com/{DirectoryId}\", resource=\"https://vault.azure.net\"");
                return challenge;
            }
            Assert.Equal("Bearer synthetic-azure-token", request.Headers.Authorization.ToString());

            if (request.Method == HttpMethod.Get && path == "/secrets")
                return Respond(new { value = _versions.Count == 0 || Deleted ? [] : new[] { Listed(_versions[^1]) } });
            if (request.Method == HttpMethod.Get && path == "/secrets/canary/versions")
                return Respond(new { value = _versions.Select(Metadata).ToArray() });
            if (request.Method == HttpMethod.Put && path == "/secrets/canary")
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var created = new Version((_versions.Count + 1).ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
                    1760000000 + _versions.Count * 2, body.GetProperty("value").GetString()!, Description(body));
                _versions.Add(created);
                return Respond(Entry(created));
            }
            if (request.Method == HttpMethod.Patch && path == $"/secrets/canary/{_versions[^1].Id}")
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                _versions[^1] = _versions[^1] with { Description = Description(body), Updated = _versions[^1].Created + 1 };
                return Respond(Metadata(_versions[^1]));
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/secrets/canary", StringComparison.Ordinal))
            {
                var version = path == "/secrets/canary" ? _versions[^1] : _versions.Single(item => path.EndsWith(item.Id, StringComparison.Ordinal));
                return Respond(Entry(version));
            }
            if (request.Method == HttpMethod.Delete && path == "/secrets/canary")
            {
                Deleted = true;
                return Respond(DeletedEntry());
            }
            if (request.Method == HttpMethod.Get && path == "/deletedsecrets/canary" && Deleted)
                return Respond(DeletedEntry());
            throw new InvalidOperationException("Unexpected Azure Key Vault fixture operation.");
        }

        private static string? Description(JsonElement body) => body.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("netratel-description", out var description) ? description.GetString() : null;

        private static object Attributes(Version version) => new { enabled = true, created = version.Created, updated = version.Updated ?? version.Created };
        private static object Tags(Version version) => version.Description is null ? new Dictionary<string, string>() :
            new Dictionary<string, string> { ["netratel-description"] = version.Description };
        private static string SecretId(Version version) => $"{Vault}/secrets/canary/{version.Id}";
        private static object Listed(Version version) => new { id = $"{Vault}/secrets/canary", attributes = Attributes(version), tags = Tags(version) };
        private static object Metadata(Version version) => new { id = SecretId(version), attributes = Attributes(version), tags = Tags(version) };
        private static object Entry(Version version) => new { id = SecretId(version), value = version.Value, attributes = Attributes(version), tags = Tags(version) };
        private object DeletedEntry() => new
        {
            id = SecretId(_versions[^1]), recoveryId = $"{Vault}/deletedsecrets/canary",
            deletedDate = 1760000050, scheduledPurgeDate = 1765000050, attributes = Attributes(_versions[^1]), tags = Tags(_versions[^1])
        };
        private static HttpResponseMessage Respond(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

        private sealed record Version(string Id, long Created, string Value, string? Description, long? Updated = null);
    }
}
