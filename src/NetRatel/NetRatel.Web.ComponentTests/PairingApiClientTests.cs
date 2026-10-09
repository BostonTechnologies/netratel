using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Shared.SystemPairing;
using NetRatel.Web.Services.Pairing;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class PairingApiClientTests
{
    [Fact]
    public async Task Code_and_operation_identity_stay_in_pair_body_and_mapping_delete_preserves_other_pair_mappings()
    {
        var peer = new PairingMetadata(PairingProtocol.Contract, "rateldesk", Guid.NewGuid().ToString("D"), "Desk", "https://desk.test", "https://api.desk.test", null);
        var mapping = new PairingMapping(Guid.NewGuid(), new string('a', 64), "One connection", "4", "org-one", "customer-one", true, false);
        var card = new PairingConnectionDto(mapping.Id.ToString("D"), mapping.PairId, mapping, peer, "Connected");
        using var transport = new Transport(request => request.Method == HttpMethod.Delete
            ? new(HttpStatusCode.NotFound)
            : new(HttpStatusCode.OK) { Content = JsonContent.Create(card) });
        var client = new PairingApiClient(transport);
        var operation = Guid.NewGuid();
        await client.ConnectAsync(new("http://desk.internal:8080", "ABCD-EFGH", operation));
        var pairRequest = transport.Requests.Single();
        pairRequest.Path.Should().Be("/api/v1/admin/system-connections/pair");
        pairRequest.Path.Should().NotContain("ABCD");
        using var payload = JsonDocument.Parse(pairRequest.Body!);
        payload.RootElement.GetProperty("operationId").GetGuid().Should().Be(operation);
        payload.RootElement.GetProperty("pairingCode").GetString().Should().Be("ABCD-EFGH");
        await client.SaveAsync(mapping);
        await client.DeleteAsync(card);
        await client.DeleteAsync(card);
        transport.Requests.Skip(1).Should().OnlyContain(x => x.Path.EndsWith($"/{mapping.PairId}/mappings/{mapping.Id:D}", StringComparison.Ordinal));
        transport.Requests.Skip(2).Should().OnlyContain(x => x.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Operational_failure_exposes_actionable_safe_reason_and_reference_without_echoing_peer_secrets()
    {
        using var transport = new Transport(_ => new(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { code = "pairing-code-invalid", correlationId = "safe-ref-17", message = "Pairing ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghi private-token" })
        });
        var client = new PairingApiClient(transport);
        var error = await Assert.ThrowsAsync<PairingApiException>(() => client.GenerateCodeAsync());
        error.Message.Should().Contain("Generate a new code").And.NotContain("private-token");
        error.Reference.Should().Be("safe-ref-17");
    }

    [Fact]
    public async Task Oversized_administration_response_fails_with_a_bounded_read()
    {
        using var transport = new Transport(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('a', 131073), Encoding.UTF8) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new PairingApiClient(transport).ListAsync());
    }

    private sealed record Request(HttpMethod Method, string Path, string? Body);
    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public List<Request> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://api.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }
}
