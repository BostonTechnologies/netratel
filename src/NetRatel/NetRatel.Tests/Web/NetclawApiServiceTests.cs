using System.Net;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class NetclawApiServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Forbidden")]
    [InlineData("<html>Forbidden</html>")]
    [InlineData("[]")]
    public async Task Empty_or_non_object_error_preserves_permission_message_and_status(string body)
    {
        var service = new NetclawApiService(new Factory(body));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.ConnectionAsync(17));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("permissions", error.Message);
    }

    [Fact]
    public async Task Structured_error_preserves_server_detail_and_status()
    {
        var service = new NetclawApiService(new Factory("{\"message\":\"Tenant access was withdrawn.\"}"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.ConnectionAsync(17));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("Tenant access was withdrawn.", error.Message);
    }

    private sealed class Factory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(body)) { BaseAddress = new("https://api.example.test") };
    }
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(body) });
    }
}
