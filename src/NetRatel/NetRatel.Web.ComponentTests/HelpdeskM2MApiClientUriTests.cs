using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using NetRatel.Web.Services.ServiceLinks;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class HelpdeskM2MApiClientUriTests
{
    [Theory]
    [InlineData("https://api.example.test/", "https://api.example.test/api/v2/account/service-clients/authority", "https://api.example.test/api/v1/admin/service-links/identity")]
    [InlineData("https://api.example.test/nr/", "https://api.example.test/nr/api/v2/account/service-clients/authority", "https://api.example.test/nr/api/v1/admin/service-links/identity")]
    public async Task Both_administration_families_preserve_the_configured_upstream_base_path(
        string baseAddress, string expectedAuthority, string expectedIdentity)
    {
        using var transport = new AdministrationTransport(baseAddress, expectedAuthority, expectedIdentity);
        var client = new HelpdeskM2MApiClient(transport);

        var authority = await client.GetAuthorityAsync();
        var identity = await client.GetIdentityAsync();

        Assert.True(authority.CanManage);
        var tenant = Assert.Single(authority.Tenants);
        Assert.Equal(7, tenant.TenantId);
        Assert.Equal("Permitted tenant", tenant.Name);
        Assert.Equal("netratel.orchestration.read", Assert.Single(tenant.Scopes));
        Assert.Equal("5ba7f148-0e77-4fb5-98c9-dbf76c4a71bd", identity.InstanceId);
        Assert.Equal("f048227d-cf86-4d43-a21f-1a07b40d9a14", identity.SourceInstanceId);
        Assert.Equal(3, identity.Revision);
        Assert.Equal(new[] { "ServiceLinkApi", "ServiceLinkApi" }, transport.ClientNames);
        Assert.Equal(new[] { expectedAuthority, expectedIdentity }, transport.RequestUris);
    }

    // This fixture tests the production client's URI resolution and real DTO
    // deserialization. It supplies no authentication or protocol acceptance proof.
    private sealed class AdministrationTransport(string baseAddress, string expectedAuthority, string expectedIdentity)
        : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> ClientNames { get; } = [];
        public List<string> RequestUris { get; } = [];

        public HttpClient CreateClient(string name)
        {
            ClientNames.Add(name);
            return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(baseAddress) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri!.AbsoluteUri;
            RequestUris.Add(uri);
            object? payload = uri == expectedAuthority
                ? new ServiceClientManagementAuthority(true,
                    [new ServiceClientTenantAuthority(7, "Permitted tenant", ["netratel.orchestration.read"], [], [])])
                : uri == expectedIdentity
                    ? new ServiceLinkIdentityDto("5ba7f148-0e77-4fb5-98c9-dbf76c4a71bd", "f048227d-cf86-4d43-a21f-1a07b40d9a14", 3)
                    : null;
            return Task.FromResult(payload is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload, payload.GetType()) });
        }
    }
}
