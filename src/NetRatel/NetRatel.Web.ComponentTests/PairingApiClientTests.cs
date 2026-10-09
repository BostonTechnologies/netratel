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

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "pairing_code_rejected", "Generate a new code")]
    [InlineData(HttpStatusCode.Conflict, "pairing_code_used", "Generate a new code")]
    [InlineData(HttpStatusCode.Conflict, "pairing_operation_changed", "Generate a fresh code")]
    [InlineData(HttpStatusCode.Unauthorized, "pairing_generation_changed", "pair the systems again")]
    [InlineData(HttpStatusCode.Forbidden, "customer_not_authorized", "Choose an authorized RatelDesk customer")]
    [InlineData(HttpStatusCode.Conflict, "mapping_revision_changed", "Reopen its current configuration")]
    [InlineData(HttpStatusCode.BadGateway, "identity_mismatch", "identity could not be verified")]
    [InlineData(HttpStatusCode.BadGateway, "peer_unavailable", "certificate")]
    public async Task Structured_peer_failures_use_known_recovery_actions_instead_of_interactive_sign_in_expiry(
        HttpStatusCode status, string code, string action)
    {
        using var transport = new Transport(_ => new(status)
        {
            Content = JsonContent.Create(new { code, correlationId = "peer-safe-ref", message = "Untrusted private peer details" })
        });
        var error = await Assert.ThrowsAsync<PairingApiException>(() => new PairingApiClient(transport)
            .ConnectAsync(new("https://desk.test", "ABCD-EFGH", Guid.NewGuid())));
        error.Code.Should().Be(code);
        error.Message.Should().Contain(action).And.NotContain("Sign in again").And.NotContain("private peer details");
        error.Reference.Should().Be("peer-safe-ref");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("authentication_required")]
    public async Task Missing_or_unknown_unauthorized_response_still_requires_interactive_sign_in(string? code)
    {
        using var transport = new Transport(_ => new(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new
            {
                code, correlationId = "local-auth-ref",
                diagnostic = new PairingReadinessDiagnostic("receipt-token", "business-access-rejected", Guid.NewGuid().ToString("N"), 401)
            })
        });
        var error = await Assert.ThrowsAsync<PairingApiException>(() => new PairingApiClient(transport).ListAsync());
        error.Message.Should().Contain("Sign in again").And.NotContain("Generate a");
        error.Reference.Should().Be("local-auth-ref");
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "receiver-readiness-unverified", "receiver-targets", "receiver-target-rejected", 403, "incident target validation")]
    [InlineData(HttpStatusCode.Unauthorized, "business-access-rejected", "receipt-token", "business-access-rejected", 401, "receipt access token")]
    public async Task Save_readiness_failure_uses_validated_stage_and_linked_reference_without_peer_text(
        HttpStatusCode status, string code, string stage, string reason, int peerStatus, string expectedStage)
    {
        var reference = Guid.NewGuid().ToString("N");
        using var transport = new Transport(_ => new(status)
        {
            Content = JsonContent.Create(new
            {
                code, correlationId = "outer-safe-ref", message = "Bearer PRIVATE-PEER-TOKEN",
                diagnostic = new PairingReadinessDiagnostic(stage, reason, reference, peerStatus)
            })
        });
        var mapping = new PairingMapping(Guid.NewGuid(), new string('a', 64), "Incident connection", "4", "org-one", "customer-one", true, false);
        var error = await Assert.ThrowsAsync<PairingApiException>(() => new PairingApiClient(transport).SaveAsync(mapping));
        error.Code.Should().Be(code);
        error.Reference.Should().Be(reference);
        error.Message.Should().Contain(expectedStage).And.Contain($"HTTP {peerStatus}").And.NotContain("PRIVATE-PEER-TOKEN").And.NotContain("Sign in again");
    }

    [Theory]
    [InlineData("PRIVATE-PEER-STAGE", "receiver-target-rejected", "d9816845e68c47b8b85960bff27bc87c")]
    [InlineData("receiver-targets", "PRIVATE-PEER-CODE", "d9816845e68c47b8b85960bff27bc87c")]
    [InlineData("receiver-targets", "receiver-target-rejected", "PRIVATE-PEER-REFERENCE")]
    public async Task Forged_diagnostic_fields_are_ignored_and_never_exposed(string stage, string reason, string reference)
    {
        using var transport = new Transport(_ => new(HttpStatusCode.UnprocessableEntity)
        {
            Content = JsonContent.Create(new
            {
                code = "receiver-readiness-unverified", correlationId = "outer-safe-ref", message = "PRIVATE-PEER-MESSAGE",
                diagnostic = new PairingReadinessDiagnostic(stage, reason, reference, 403)
            })
        });
        var error = await Assert.ThrowsAsync<PairingApiException>(() => new PairingApiClient(transport).GenerateCodeAsync());
        error.Reference.Should().Be("outer-safe-ref");
        error.Message.Should().Contain("Receiver readiness could not be verified").And.NotContain("PRIVATE-PEER").And.NotContain("HTTP 403");
    }

    [Fact]
    public async Task Test_and_refreshed_last_test_messages_use_local_diagnostics_and_drop_forged_peer_details()
    {
        var reference = Guid.NewGuid().ToString("N");
        var testedAt = DateTimeOffset.UtcNow;
        var mapping = new PairingMapping(Guid.NewGuid(), new string('a', 64), "Incident connection", "4", "org-one", "customer-one", true, false);
        var peer = new PairingMetadata(PairingProtocol.Contract, "rateldesk", Guid.NewGuid().ToString("D"), "Desk", "https://desk.test", "https://api.desk.test", null);
        var card = new PairingConnectionDto(mapping.Id.ToString("D"), mapping.PairId, mapping, peer, "Connected");
        using var transport = new Transport(request => new(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith("/test", StringComparison.Ordinal)
                ? JsonContent.Create(new PairingTestResult(false, "PRIVATE-PEER-MESSAGE", testedAt,
                    new("receiver-capabilities", "receiver-authentication-rejected", reference, 401)))
                : JsonContent.Create(new[] { card with { LastTest = new(false, "PRIVATE-PEER-MESSAGE", testedAt,
                    new("receiver-targets", "receiver-target-rejected", "PRIVATE-PEER-REFERENCE", 403)) } })
        });
        var client = new PairingApiClient(transport);
        var test = await client.TestAsync(card);
        test.Success.Should().BeFalse(); test.TestedAtUtc.Should().Be(testedAt);
        test.Message.Should().Contain("receiver capabilities").And.Contain("HTTP 401").And.Contain(reference).And.NotContain("PRIVATE-PEER");
        test.Diagnostic!.Reference.Should().Be(reference);
        var lastTest = (await client.ListAsync()).Single().LastTest!;
        lastTest.Success.Should().BeFalse(); lastTest.TestedAtUtc.Should().Be(testedAt);
        lastTest.Diagnostic.Should().BeNull();
        lastTest.Message.Should().Contain("could not be verified").And.NotContain("PRIVATE-PEER").And.NotContain("HTTP 403");
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
