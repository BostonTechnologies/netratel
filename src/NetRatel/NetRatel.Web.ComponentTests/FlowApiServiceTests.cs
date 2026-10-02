using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowApiServiceTests
{
    [Fact]
    public async Task Explicit_Save_Uses_Paired_Tenant_Flow_And_Expected_Revision_With_Canonical_Data()
    {
        var flow = FlowEditorTests.Definition(); var factory = new Factory(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(flow with { Revision = 8 }) });
        var api = new FlowApiService(factory);
        await api.GetAsync(flow.TenantId, flow.Id);
        await api.SaveAsync(flow.TenantId, flow.Id, new(7, flow.Name, flow.Draft));
        Assert.Equal($"GET /api/v1/tenants/17/flows/{flow.Id:D}", factory.Requests[0].Route);
        Assert.Equal($"PUT /api/v1/tenants/17/flows/{flow.Id:D}/draft", factory.Requests[1].Route);
        using var body = JsonDocument.Parse(factory.Requests[1].Body!);
        Assert.Equal(7, body.RootElement.GetProperty("expectedRevision").GetInt64());
        Assert.DoesNotContain("$type", factory.Requests[1].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Requests, r => r.Route.Contains("execute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Receiver_Capability_Rejection_Is_Actionable_Without_Disclosing_Server_Detail()
    {
        var factory = new Factory(_ => new(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new { code = "receiver-idempotency-unverified", detail = "protected credential must never be shown" }) });
        var api = new FlowApiService(factory);
        var error = await Assert.ThrowsAsync<FlowApiException>(() => api.PublishAsync(17, Guid.NewGuid(), new(7)));
        Assert.Contains("verified incident deduplication", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("protected credential", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Admission_And_Revision_Failures_Are_Preserved_Without_Retry(HttpStatusCode status)
    {
        var factory = new Factory(_ => new(status) { Content = new StringContent("not-json sensitive detail") });
        var api = new FlowApiService(factory);
        var error = await Assert.ThrowsAsync<FlowApiException>(() => api.SaveAsync(17, Guid.NewGuid(), new(7, "Name", FlowGraphTemplates.IncidentFromAlert())));
        Assert.Equal(status, error.StatusCode); Assert.Single(factory.Requests);
        Assert.DoesNotContain("sensitive detail", error.Message, StringComparison.Ordinal);
    }
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public List<(string Route, string? Body)> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this, respond)) { BaseAddress = new("https://fixture.test") };
        private sealed class Handler(Factory factory, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { factory.Requests.Add(($"{request.Method} {request.RequestUri!.AbsolutePath}", request.Content is null ? null : await request.Content.ReadAsStringAsync(token))); return respond(request); }
        }
    }
}
