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
    [Fact]
    public async Task Execution_Catalogue_Uses_Tenant_Search_Cursor_And_Decimal_String_Ids()
    {
        Uri? uri = null;
        var selected = new FlowExecutionDefinitionOptionDto(FlowExecutionSourceKind.Job,
            new("9007199254740993", "Synthetic maintenance", "7", new string('a', 64)), new(Guid.NewGuid(), "Synthetic bound endpoint"));
        var factory = new Factory(request => { uri = request.RequestUri; return new(HttpStatusCode.OK) { Content = JsonContent.Create(new FlowExecutionDefinitionsPageDto([selected], "9007199254740995")) }; });
        var api = new FlowApiService(factory);
        var page = await api.SearchExecutionDefinitionsAsync(17, FlowExecutionSourceKind.Job, "Server & CPU", "9007199254740993", 50);
        Assert.Equal("9007199254740993", Assert.Single(page.Items).Definition.Id);
        Assert.Equal("9007199254740995", page.NextAfterId);
        Assert.Equal("/api/v1/tenants/17/flows/execution-definitions", uri!.AbsolutePath);
        Assert.Contains("sourceKind=Job", uri.Query, StringComparison.Ordinal);
        Assert.Contains("search=Server%20%26%20CPU", uri.Query, StringComparison.Ordinal);
        Assert.Contains("afterId=9007199254740993", uri.Query, StringComparison.Ordinal);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task Target_Metadata_Lookup_Preserves_Selected_Unavailable_Ids_And_Native_Cursor()
    {
        var agent = Guid.NewGuid(); var missing = Guid.NewGuid(); Uri? uri = null;
        var factory = new Factory(request => { uri = request.RequestUri; return new(HttpStatusCode.OK) { Content = JsonContent.Create(
            new FlowExecutionTargetSelectionDto[] { new(new(agent, "Disabled synthetic endpoint"), false), new(new(missing), false) }) }; });
        var api = new FlowApiService(factory);
        var selections = await api.GetSelectedExecutionTargetsAsync(17, FlowExecutionSourceKind.Script, [agent, missing]);
        Assert.Equal(2, selections.Count); Assert.All(selections, item => Assert.False(item.Eligible));
        Assert.Equal(missing, selections[1].Target.AgentId); Assert.Null(selections[1].Target.DisplayName);
        Assert.Equal("/api/v1/tenants/17/flows/execution-definitions/targets/selected", uri!.AbsolutePath);
        Assert.Contains("ids=" + agent.ToString("D"), uri.Query, StringComparison.Ordinal);
        Assert.Contains("ids=" + missing.ToString("D"), uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execution_Search_Denial_Is_An_Error_And_Never_An_Empty_Selection()
    {
        var factory = new Factory(_ => new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { detail = "protected source" }) });
        var api = new FlowApiService(factory);
        var error = await Assert.ThrowsAsync<FlowApiException>(() => api.SearchExecutionDefinitionsAsync(17, FlowExecutionSourceKind.Script));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("protected source", error.Message, StringComparison.Ordinal); Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task Template_Creation_Uses_Separate_Disabled_Draft_Command_Without_Changing_Legacy_Wire_Shape()
    {
        var flow = FlowEditorTests.Definition() with { Enabled = false };
        var factory = new Factory(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(flow) });
        var api = new FlowApiService(factory);
        var created = await api.CreateFromTemplateAsync(17, new("Synthetic recovery", FlowOverhaulTemplates.ScriptRecovery));
        Assert.False(created.Enabled);
        Assert.Equal("POST /api/v1/tenants/17/flows/from-template", factory.Requests[0].Route);
        using var body = JsonDocument.Parse(factory.Requests[0].Body!);
        Assert.Equal("Synthetic recovery", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("script-recovery", body.RootElement.GetProperty("templateId").GetString());
        Assert.False(body.RootElement.TryGetProperty("useTemplate", out _));
        Assert.DoesNotContain(factory.Requests, request => request.Route.Contains("/publish", StringComparison.Ordinal) || request.Route.Contains("/execute", StringComparison.Ordinal));
        var legacy = JsonSerializer.SerializeToElement(new FlowCreateRequest("Old template"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(legacy.GetProperty("useTemplate").GetBoolean()); Assert.False(legacy.TryGetProperty("templateId", out _));
    }

    [Fact]
    public async Task Schema_Two_Save_And_Validate_Explicitly_Request_Their_Bounded_Graph_Budget_Only()
    {
        var headers = new List<string?>();
        var flow = FlowEditorTests.Definition();
        var factory = new Factory(request =>
        {
            headers.Add(request.Headers.TryGetValues("X-NetRatel-Flow-Schema", out var values) ? Assert.Single(values) : null);
            return new(HttpStatusCode.OK) { Content = request.Method == HttpMethod.Put ? JsonContent.Create(flow) : JsonContent.Create(new FlowValidationResultDto(true, [])) };
        });
        var api = new FlowApiService(factory);
        await api.SaveAsync(17, flow.Id, new(flow.Revision, flow.Name, flow.Draft));
        var upgraded = FlowDraftMigration.UpgradeToSchemaTwo(flow.Draft);
        await api.SaveAsync(17, flow.Id, new(flow.Revision, flow.Name, upgraded));
        await api.ValidateAsync(17, upgraded);
        Assert.Null(headers[0]); Assert.Equal("2", headers[1]); Assert.Equal("2", headers[2]);
    }

    [Fact]
    public async Task Native_Definition_And_Trigger_Errors_Have_Actionable_Messages_Without_Server_Details()
    {
        var factory = new Factory(_ => new(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new
        { code = "execution-definition-name-limit", detail = "protected native definition detail" }) });
        var api = new FlowApiService(factory);
        var error = await Assert.ThrowsAsync<FlowApiException>(() => api.GetExecutionDefinitionAsync(17, FlowExecutionSourceKind.Job, "42"));
        Assert.Equal("This definition name exceeds the supported 512-character limit.", error.Message);
        Assert.DoesNotContain("protected native definition", error.Message, StringComparison.Ordinal);
        Assert.Single(factory.Requests);

        var publishFactory = new Factory(_ => new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new
        { code = "execution-trigger-source-unavailable", detail = "protected native source detail" }) });
        var publishApi = new FlowApiService(publishFactory);
        var sourceError = await Assert.ThrowsAsync<FlowApiException>(() => publishApi.PublishAsync(17, Guid.NewGuid(), new(1)));
        Assert.Equal(HttpStatusCode.BadRequest, sourceError.StatusCode);
        Assert.Equal("execution-trigger-source-unavailable", sourceError.Code);
        Assert.Equal("Request events currently support job-backed definitions. Select Script execution events for library scripts, or remove the unavailable filter. Your draft is preserved.", sourceError.Message);
        Assert.DoesNotContain("protected native source", sourceError.Message, StringComparison.Ordinal);
        Assert.Single(publishFactory.Requests);
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
