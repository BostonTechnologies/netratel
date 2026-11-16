using System.Net;
using System.Net.Http.Json;
using AutomationTrace.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Web.Components.Pages.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowIncidentTemplateFieldsTests : AsyncBunitContext
{
    [Fact]
    public async Task Custom_selection_requires_a_revision_and_preview_only_calls_the_read_only_route()
    {
        var standard = new IncidentPresentationReference("standard", "system-standard", "1", AutomationTraceContract.ContextVersion, new('a', 64), new('b', 64));
        var custom = standard with { Mode = "template", TemplateId = "published-demo", Revision = "3" };
        var capability = new RatelDeskIncidentPresentationCapability(AutomationTraceContract.IncidentVersion, AutomationTraceContract.ContextVersion,
            standard, "https://desk.example.test/create", "https://desk.example.test/catalogue", "https://desk.example.test/preview", "rateldesk.incident-templates.read", "https://desk.example.test");
        var handler = new Handler(new(capability, [new(custom.TemplateId, "Job report", "demo-org", "netratel-internal-incident", ["job"], custom, false, false)]), custom);
        Services.AddSingleton<IHttpClientFactory>(handler);
        IncidentPresentationReference? selected = null;
        var cut = Render<FlowIncidentTemplateFields>(p => p.Add(x => x.TenantId, 17).Add(x => x.ConnectorId, Guid.NewGuid())
            .Add(x => x.SourceKinds, ["job"]).Add(x => x.Changed, value => selected = value));
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("input[type=checkbox]")));
        cut.Find("input[type=checkbox]").Change(true);
        Assert.Equal(standard, selected);
        cut.Render(p => p.Add(x => x.Presentation, selected));
        cut.FindAll("input[type=checkbox]")[1].Change(true);
        Assert.Equal("template", selected!.Mode); Assert.Empty(selected.TemplateId); Assert.Empty(selected.Revision);
        cut.Render(p => p.Add(x => x.Presentation, selected));
        Assert.Contains("Choose a published revision", cut.Markup);
        cut.Find("select").Change(custom.TemplateId + ":" + custom.Revision + ":" + custom.ContentDigest);
        Assert.Equal(custom, selected);
        cut.Render(p => p.Add(x => x.Presentation, selected));
        cut.Find("button").Click();
        await cut.WaitForAssertionAsync(() => Assert.Equal("<p>Read-only preview</p>", cut.Find("iframe").GetAttribute("srcdoc")));
        Assert.Equal(new[] { "GET", "POST" }, handler.Requests.Select(x => x.Method));
        Assert.EndsWith("/incident-templates/preview", handler.Requests[1].Path);
    }

    private sealed class Handler(RatelDeskIncidentPresentationCatalogue catalogue, IncidentPresentationReference selected) : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Method, string Path)> Requests { get; } = [];
        public HttpClient CreateClient(string name) { Assert.Equal("FlowsApi", name); return new(this, disposeHandler: false) { BaseAddress = new("https://net.example.test/") }; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = request.Method == HttpMethod.Get
                ? JsonContent.Create(catalogue) : JsonContent.Create(new IncidentTemplatePreview("Safe subject", "Safe summary", "<p>Read-only preview</p>", "Read-only preview", selected)) });
        }
    }
}
