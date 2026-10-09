using System.Net;
using System.Net.Http.Json;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
using Xunit;

namespace NetRatel.Tests.SystemPairing;

public sealed class PairingTransportTests
{
    [Theory]
    [InlineData("not-json synthetic-private-token")]
    [InlineData("[]")]
    [InlineData("{\"access_token\":123}")]
    public async Task Invalid_token_responses_have_a_finite_reason_and_linked_request_reference(string body)
    {
        using var http = new HttpClient(new TokenHandler(body));
        var transport = new PairingTransport(http, new Correlation());
        var peer = new PairingMetadata(PairingProtocol.Contract, "rateldesk", Guid.NewGuid().ToString("D"), "Peer",
            "https://peer.example.test", "https://peer.example.test", null);
        var credential = new PairingBusinessCredential("client", "synthetic-private-secret", "https://peer.example.test/connect/token",
            "audience", "issuer", ["rateldesk.incident-receipts.read"], null, null);
        var error = await Assert.ThrowsAsync<PairingException>(() => transport.TokenAsync(peer, credential,
            "rateldesk.incident-receipts.read", default));
        Assert.Equal("token-response-invalid", error.Code); Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain("synthetic-private", error.Message);
    }

    [Theory]
    [InlineData("ABCD-2345")]
    [InlineData("abcd2345")]
    [InlineData("aB-cD-2-3-4-5")]
    public async Task A_valid_diagnostic_reference_cannot_reflect_the_submitted_pairing_code(string code)
    {
        var diagnostic = new PairingReadinessDiagnostic("receiver-capabilities", "receiver-endpoint-outside-approved-api-base",
            "abcd2345" + Guid.NewGuid().ToString("N")[8..]);
        Assert.True(PairingReadinessDiagnostics.IsValid(diagnostic));
        using var http = new HttpClient(new DiagnosticHandler(diagnostic));
        var transport = new PairingTransport(http);
        var request = new PairingExchangeRequest(code, Guid.NewGuid(), new(PairingProtocol.Contract, "netratel", Guid.NewGuid().ToString("D"),
            "Caller", "https://caller.example.test", "https://caller.example.test", Guid.NewGuid().ToString("D")), "offered-inbound-secret");
        var error = await Assert.ThrowsAsync<PairingException>(() => transport.SendAsync<bool>("https://peer.example.test", HttpMethod.Post,
            "/exchange", request, null, null, default));
        Assert.Null(error.Diagnostic);
        Assert.DoesNotContain(diagnostic.Reference, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_valid_bounded_diagnostics_survive_peer_errors(bool forged)
    {
        const string privateValue = "synthetic-private-token-and-peer-text";
        var diagnostic = new PairingReadinessDiagnostic(forged ? privateValue : "receiver-capabilities",
            "receiver-endpoint-outside-approved-api-base", "67f452da36d541b892c21f50dd7d8f83");
        using var http = new HttpClient(new DiagnosticHandler(diagnostic, privateValue));
        var transport = new PairingTransport(http);
        var error = await Assert.ThrowsAsync<PairingException>(() => transport.SendAsync<bool>("https://peer.example.test", HttpMethod.Post,
            "/mappings/" + Guid.NewGuid().ToString("D") + "/test", null, privateValue, Guid.NewGuid().ToString("D"), default,
            callerSecretHash: new string('a', 64)));
        Assert.DoesNotContain(privateValue, error.Message);
        Assert.Equal(forged ? null : diagnostic, error.Diagnostic);
        if (!forged) Assert.Equal(PairingReadinessDiagnostics.Describe(diagnostic), error.Message);
    }

    [Theory]
    [InlineData("abcdef23")]
    [InlineData("aBcD-eF23")]
    [InlineData("a-b-c-d-e-f-2-3")]
    [InlineData("--aB-cD--eF-2--3--")]
    public async Task Peer_diagnostics_cannot_reflect_equivalent_pairing_codes(string reflected)
    {
        var error = await RejectAsync("ABCD-EF23", reflected, "Rejected " + reflected);
        Assert.Equal("peer-rejected", error.Code);
        Assert.Equal("The peer rejected this operation (HTTP 403).", error.Message);
    }

    [Fact]
    public async Task Every_hyphen_placement_is_redacted_without_losing_unrelated_diagnostics()
    {
        const string compact = "ABCD-EF23";
        const string characters = "aBcDeF23";
        for (var placement = 0; placement < 128; placement++)
        {
            var reflected = "--" + string.Concat(characters.Select((character, index) =>
                character + (index < 7 && (placement & (1 << index)) != 0 ? "--" : ""))) + "--";
            var error = await RejectAsync(compact, reflected, "Peer rejected " + reflected);
            Assert.Equal("peer-rejected", error.Code);
            Assert.Equal("The peer rejected this operation (HTTP 403).", error.Message);
        }
        var unrelated = await RejectAsync(compact, "customer-unavailable", "Select a current customer.");
        Assert.Equal("customer-unavailable", unrelated.Code);
        Assert.Equal("Select a current customer.", unrelated.Message);
    }

    [Fact]
    public async Task Code_normalization_is_independent_of_entered_case_and_separators()
    {
        var error = await RejectAsync(" --aB-cD--eF-2--3-- ", "ABCD-EF23", "Rejected abcdef23");
        Assert.Equal("peer-rejected", error.Code);
        Assert.Equal("The peer rejected this operation (HTTP 403).", error.Message);
    }

    [Fact]
    public async Task Invalid_codes_keep_exact_redaction_and_ordinary_secrets_remain_case_sensitive()
    {
        var invalid = await RejectAsync("bad[code", "rejected", "Rejected bad[code");
        Assert.Equal("rejected", invalid.Code);
        Assert.Equal("The peer rejected this operation (HTTP 403).", invalid.Message);
        const string secret = "CaseSensitiveInboundSecret";
        var exact = await RejectAsync("ABCD-EF23", secret, "Rejected " + secret, secret);
        Assert.Equal("peer-rejected", exact.Code);
        Assert.Equal("The peer rejected this operation (HTTP 403).", exact.Message);
        var changed = await RejectAsync("ABCD-EF23", "rejected", "Rejected " + secret.ToLowerInvariant(), secret);
        Assert.Equal("rejected", changed.Code);
        Assert.Equal("Rejected " + secret.ToLowerInvariant(), changed.Message);
    }

    private static async Task<PairingException> RejectAsync(string code, string reflectedCode, string message,
        string inboundSecret = "offered-inbound-secret")
    {
        using var http = new HttpClient(new RejectionHandler(reflectedCode, message));
        var transport = new PairingTransport(http);
        var peer = new PairingMetadata(PairingProtocol.Contract, "netratel", Guid.NewGuid().ToString("D"),
            "Caller", "https://caller.example.test", "https://api.caller.example.test", Guid.NewGuid().ToString("D"));
        var request = new PairingExchangeRequest(code, Guid.NewGuid(), peer, inboundSecret);
        return await Assert.ThrowsAsync<PairingException>(() => transport.SendAsync<bool>("https://peer.example.test",
            HttpMethod.Post, "/exchange", request, null, null, default));
    }

    private sealed class RejectionHandler(string code, string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { code, message }) });
    }
    private sealed class DiagnosticHandler(PairingReadinessDiagnostic diagnostic, string message = "Peer rejected access") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            { Content = JsonContent.Create(new { code = "saved-access-unavailable", message, diagnostic }) });
    }
    private sealed class TokenHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("67f452da36d541b892c21f50dd7d8f83", request.Headers.GetValues("X-Correlation-Id").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private sealed class Correlation : NetRatel.Application.Events.ICorrelationContext
    {
        public string? Current => "corr-67f452da36d541b892c21f50dd7d8f83";
        public string GetOrCreate() => Current!;
    }
}
