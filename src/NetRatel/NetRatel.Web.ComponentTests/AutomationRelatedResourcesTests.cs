using System.Net;
using System.Net.Http.Json;
using AutomationTrace.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Shared;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class AutomationRelatedResourcesTests : AsyncBunitContext
{
    [Fact]
    public async Task Repeated_incident_associations_keep_each_native_destination_and_one_external_ticket_link()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        using var api = new Api();
        Services.AddSingleton<IHttpClientFactory>(api);
        var cut = Render<AutomationRelatedResources>(p => p.Add(x => x.TenantId, 17)
            .Add(x => x.Kind, "job-run").Add(x => x.ResourceId, "639300000000000001"));
        await cut.WaitForAssertionAsync(() =>
        {
            Assert.Single(cut.FindAll("a[href='https://desk.example.test/incidents/demo']"));
            Assert.Single(cut.FindAll("a[href='https://desk.example.test/requests/source']"));
            var nativeLinks = cut.FindAll("nav[aria-label='Related native resources'] a");
            Assert.Equal(new[] { "/requests?search=130", "/jobs/67/runs/639300000000000001", "/flows?tenantId=17&run=demo" },
                nativeLinks.Select(link => link.GetAttribute("href")));
            Assert.Contains("Sam Demo", cut.Markup);
            Assert.Contains("Alex Example", cut.Markup);
        });
        Assert.Equal(2, api.Reads);
    }

    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public int Reads { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://net.example.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith("/api/v1/tenants/17/automation-resources/job-run/639300000000000001", request.RequestUri!.AbsolutePath);
            Reads++;
            var origin = new AutomationOriginContext(AutomationTraceContract.OriginVersion, "demo-desk",
                new("org", "Demo organization"), new("source", "REQ-DEMO-001", "Directory check", DateTimeOffset.UnixEpoch),
                new("service", "Directory service"), null, new("task", "List directory"), "attempt", "correlation",
                new("sam", "Sam Demo"), new("alex", "Alex Example"), [new("source-request", "REQ-DEMO-001", "https://desk.example.test/requests/source")]);
            var rows = new[] { ("request", "130", "/requests?search=130"), ("job-run", "639300000000000001", "/jobs/67/runs/639300000000000001"),
                ("flow-run", "demo", "/flows?tenantId=17&run=demo") }.Select(item => new AutomationResourceSearchDto(
                    17, item.Item1, item.Item2, "Directory check", "INC-DEMO-001", "incident", "demo-desk", "org", item.Item3,
                    "https://desk.example.test/incidents/demo")).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri.AbsolutePath.EndsWith("/origins", StringComparison.Ordinal)
                ? JsonContent.Create(new[] { origin }) : JsonContent.Create(new PagedResult<AutomationResourceSearchDto>(rows, 1, 25, rows.Length)) });
        }
    }
}
