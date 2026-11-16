using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.ComponentTests;

/// <summary>Old retained-resource UI fixtures have no approved reference projection; only these new reads are handled.</summary>
internal sealed class AutomationRelatedResourcesFixture : HttpMessageHandler, IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://net.example.test/") };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Get || !path.Contains("/automation-resources/", StringComparison.Ordinal))
            throw new InvalidOperationException("Register an owning HTTP fixture for this component request.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = path.EndsWith("/origins", StringComparison.Ordinal)
            ? JsonContent.Create(Array.Empty<AutomationTrace.Contracts.AutomationOriginContext>())
            : JsonContent.Create(new PagedResult<AutomationResourceSearchDto>([], 1, 25, 0)) });
    }
}
