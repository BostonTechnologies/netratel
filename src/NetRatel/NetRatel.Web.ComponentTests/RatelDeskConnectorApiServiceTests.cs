using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Web.Services.RatelDesk;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RatelDeskConnectorApiServiceTests
{
    [Theory]
    [InlineData("[]", "connector-conflict")]
    [InlineData("null", "connector-conflict")]
    [InlineData("42", "connector-conflict")]
    [InlineData("\"rdk_never_display\"", "connector-conflict")]
    [InlineData("{\"code\":\"source-identity-conflict\",\"details\":\"rdk_never_display\"}", "source-identity-conflict")]
    [InlineData("{\"code\":\"identity-revision-conflict\"}", "identity-revision-conflict")]
    [InlineData("{\"code\":\"rdk_untrusted_code\"}", "connector-conflict")]
    public async Task Source_adoption_conflicts_only_expose_allowlisted_codes_from_JSON_objects(string response, string expectedCode)
    {
        using var transport = new Transport(HttpStatusCode.Conflict, new { })
        { OverrideContent = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        Func<Task> action = () => new RatelDeskConnectorApiService(transport)
            .AdoptFlowSourceAsync(4, new(1), CancellationToken.None);

        var error = (await action.Should().ThrowAsync<RatelDeskConnectorApiException>()).Which;
        error.Code.Should().Be(expectedCode);
        error.ToString().Should().NotContain("rdk_");
        transport.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Rotation_uses_exact_tenant_connector_revision_and_dedicated_client_once()
    {
        var id = Guid.NewGuid(); using var transport = new Transport(HttpStatusCode.OK, new RatelDeskConnectorDto(id, 4, 2,
            new("Desk", "https://desk.example", "org", "customer", null, [], new(), true), true, 5, false, RatelDeskConnectorLimits.ReceiverUnavailableCode));
        var service = new RatelDeskConnectorApiService(transport);
        var result = await service.RotateAsync(4, id, new(4, "rdk_synthetic_password_only_in_explicit_post"), CancellationToken.None);
        Assert.Equal(RatelDeskConnectorApiService.ClientName, transport.ClientName);
        Assert.Equal($"/api/v2/tenants/4/connectors/rateldesk/{id:D}/credential", transport.Path);
        Assert.Equal(HttpMethod.Post, transport.Method); Assert.True(transport.AccountHeader); Assert.Equal(1, transport.Calls);
        using var body = JsonDocument.Parse(transport.Body!);
        Assert.Equal(4, body.RootElement.GetProperty("expectedCredentialRevision").GetInt64());
        Assert.Equal("rdk_synthetic_password_only_in_explicit_post", body.RootElement.GetProperty("credential").GetString());
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("rdk_", json); Assert.DoesNotContain("ProtectedCredential", json); Assert.True(result.HasCredential);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "connector-unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "connector-unavailable")]
    [InlineData(HttpStatusCode.Conflict, "connector-conflict")]
    [InlineData(HttpStatusCode.Forbidden, "access-denied")]
    [InlineData(HttpStatusCode.TooManyRequests, "connector-busy")]
    public async Task Remote_error_body_cannot_disclose_a_credential_or_trigger_a_replay(HttpStatusCode status, string code)
    {
        using var transport = new Transport(status, new { error = "rdk_do_not_render_server_error_details" });
        var service = new RatelDeskConnectorApiService(transport);
        var error = await Assert.ThrowsAsync<RatelDeskConnectorApiException>(() => service.RotateAsync(4, Guid.NewGuid(), new(2, "rdk_password"), CancellationToken.None));
        Assert.Equal(code, error.Code); Assert.DoesNotContain("rdk_", error.ToString()); Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Only_the_fixed_capacity_code_is_exposed_from_a_bounded_error_body()
    {
        using var transport = new Transport(HttpStatusCode.TooManyRequests,
            new { code = "connector-capacity-exhausted", details = "rdk_never_render_remote_details" });
        var error = await Assert.ThrowsAsync<RatelDeskConnectorApiException>(() =>
            new RatelDeskConnectorApiService(transport).ListAsync(4, CancellationToken.None));
        Assert.Equal("connector-capacity-exhausted", error.Code);
        Assert.DoesNotContain("rdk_", error.ToString()); Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Invalid_tenant_is_rejected_before_an_HTTP_request()
    {
        using var transport = new Transport(HttpStatusCode.OK, Array.Empty<object>());
        await Assert.ThrowsAsync<ArgumentException>(() => new RatelDeskConnectorApiService(transport).ListAsync(0, CancellationToken.None));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.TooManyRequests)]
    [InlineData(false, HttpStatusCode.TooManyRequests)]
    public async Task Oversized_responses_are_rejected_with_a_stream_budget_before_JSON_parsing(bool advertisedLength, HttpStatusCode status)
    {
        var size = RatelDeskConnectorApiService.MaximumResponseBytes + 65536;
        using var stream = new CountingStream(size);
        using var transport = new Transport(status, Array.Empty<object>())
        { OverrideContent = advertisedLength ? new ByteArrayContent(new byte[size]) : new StreamContent(stream) };
        var error = await Assert.ThrowsAsync<RatelDeskConnectorApiException>(() => new RatelDeskConnectorApiService(transport).ListAsync(4, CancellationToken.None));
        Assert.Equal("response-too-large", error.Code);
        Assert.InRange(stream.BytesRead, 0, RatelDeskConnectorApiService.MaximumResponseBytes + 8192);
        if (!advertisedLength) Assert.True(stream.BytesRead > 0);
    }

    private sealed class CountingStream(int size) : Stream
    {
        public int BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var length = Math.Min(count, size - BytesRead); Array.Clear(buffer, offset, length); BytesRead += length; return length; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); var length = Math.Min(buffer.Length, size - BytesRead); buffer.Span[..length].Clear(); BytesRead += length; return ValueTask.FromResult(length); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Transport(HttpStatusCode status, object response) : HttpMessageHandler, IHttpClientFactory
    {
        public string? ClientName, Path, Body; public HttpMethod? Method; public bool AccountHeader; public int Calls;
        public HttpContent? OverrideContent;
        public HttpClient CreateClient(string name) { ClientName = name; return new(this, false) { BaseAddress = new("https://netratel.example") }; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Method = request.Method; Path = request.RequestUri!.AbsolutePath; AccountHeader = request.Headers.Contains("X-NetRatel-Account-Request");
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new(status) { Content = OverrideContent ?? JsonContent.Create(response, response.GetType()) };
        }
    }
}
