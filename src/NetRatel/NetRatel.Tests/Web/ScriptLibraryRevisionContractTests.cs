using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Shared.Contracts.Scripts;
using NetRatel.Web.Services.ScriptLibrary;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class ScriptLibraryRevisionContractTests
{
    [Fact]
    public async Task UI_client_sends_observed_revision_and_returns_the_committed_revision_for_subsequent_edits()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example") };
        var client = new ScriptLibraryClient(new Factory(http));
        var revision = await client.UpdateAsync(901, new UpdateScriptRequest(null, null, null, "edited", null, 7), CancellationToken.None);
        revision.Should().Be(8);
        handler.Revision.Should().Be(7);
        await client.DeleteAsync(901, revision, CancellationToken.None);
        handler.DeleteQuery.Should().Be("?expectedSourceRevision=8");
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public long Revision { get; private set; }
        public string? DeleteQuery { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Revision = body.RootElement.GetProperty("expectedSourceRevision").GetInt64();
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":901,\"sourceRevision\":8}", Encoding.UTF8, "application/json") };
            }
            DeleteQuery = request.RequestUri!.Query;
            return new(HttpStatusCode.Accepted);
        }
    }
}
