using System.Net;
using System.Globalization;
using System.Text.Json;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Web.Components.RequestsUi;
using NetRatel.Web.Models.Requests;
using NetRatel.Web.Services.Requests;
using RequestsPage = NetRatel.Web.Components.Pages.Requests;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public class RequestsPageTests : AsyncBunitContext
{
    public RequestsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddScoped<RequestApiService>();
    }

    [Fact]
    public void RequestsPage_RendersRunAsIconLink()
    {
        RenderRequestsWith(SampleRequestsJson());

        var cut = RenderRequestsPage();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain(">Open run<");
            var link = cut.Find("a[href='/jobs/9/runs/19']");
            link.GetAttribute("aria-label").Should().Be("Open JobRun 19");
            link.ClassList.Should().Contain(x => x.Contains("mud-icon-button", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void RequestsPage_RendersEventLinesWithTimestampAndMessage()
    {
        RenderRequestsWith(SampleRequestsJson());

        var cut = RenderRequestsPage();
        cut.WaitForElement(".mud-expand-panel-header").Click();

        cut.WaitForAssertion(() =>
        {
            var lines = cut.FindAll(".request-log-line");
            lines.Should().HaveCount(3);
            var expectedTs = DateTimeOffset.Parse("2026-05-13T13:21:47Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            lines[0].QuerySelector(".request-log-time")!.TextContent.Should().Contain(expectedTs);
            lines[0].QuerySelector(".request-log-message")!.TextContent.Should().Contain("Request submitted by external-service.api.");
        });
    }

    [Fact]
    public void RequestsPage_RendersInlinePayloadsAndReadableRoutingWhenExpanded()
    {
        RenderRequestsWith(SampleRequestsJson());

        var cut = RenderRequestsPage();
        cut.WaitForElement(".mud-expand-panel-header").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[aria-label='Inputs payload preview']").TextContent.Should().Contain("meta");
            cut.Find("[aria-label='Output payload preview']").TextContent.Should().Contain("netratelRunId");
            cut.FindAll("button[aria-label='Expand Inputs']").Should().ContainSingle();
            cut.FindAll("button[aria-label='Expand Output']").Should().ContainSingle();
            var routing = cut.Find("section[aria-label='Routing']");
            routing.QuerySelector("dd")!.TextContent.Should().Contain("#9").And.Contain("Linux System Snapshot");
            routing.QuerySelector("a")!.GetAttribute("href").Should().Be("/jobs/9/runs/19");
            routing.TextContent.Should().Contain("Created (UTC)").And.Contain("2026-05-13 13:21:47");
        });
    }

    [Fact]
    public async Task RequestJsonPanel_ExpandOpensFullPayloadDialog()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<RequestJsonPanel>(1);
            builder.AddAttribute(2, "Title", "Output");
            builder.AddAttribute(3, "Json", "{\"completed\":true}");
            builder.CloseComponent();
        });

        await cut.Find("button[aria-label='Expand Output']").ClickAsync();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".mud-dialog .request-json-payload.is-dialog").TextContent.Should().Contain("completed");
        });
        await cut.Find(".mud-dialog-actions button").ClickAsync();
        cut.WaitForAssertion(() => cut.FindAll(".mud-dialog").Should().BeEmpty());
    }

    [Fact]
    public void RequestJsonPanel_ShowsMissingPayloadWithoutExpandAction()
    {
        var cut = Render<RequestJsonPanel>(parameters => parameters.Add(x => x.Title, "Output"));

        cut.Find("section[aria-label='Output']").TextContent.Should().Contain("No output captured.");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void RequestsPage_AddsStreamedRequestWithoutRefresh()
    {
        RenderRequestsWith("[]", StreamRequestUpsertJson(SampleRequestJson(21, "New", "Live ExternalService Intake", updated: "2026-05-13T13:25:00Z")));

        var cut = RenderRequestsPage();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("#21 - Live ExternalService Intake");
            cut.Markup.Should().Contain(">1 live<");
        });
    }

    [Fact]
    public void RequestsPage_UpdatesStreamedCompletionAndLiveCount()
    {
        RenderRequestsWith(
            JsonSerializer.Serialize(new[] { SampleRequest(22, "Processing", "Live Completion", updated: "2026-05-13T13:25:00Z") }),
            StreamRequestUpsertJson(SampleRequestJson(22, "Success", "Live Completion", updated: "2026-05-13T13:26:00Z")));

        var cut = RenderRequestsPage();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Success");
            cut.Markup.Should().Contain(">0 live<");
            cut.Markup.Should().NotContain("Processing");
        });
    }

    [Fact]
    public void RequestsPage_IgnoresStaleStreamedUpdates()
    {
        RenderRequestsWith(
            JsonSerializer.Serialize(new[] { SampleRequest(23, "Success", "Stale Guard", updated: "2026-05-13T13:30:00Z") }),
            StreamRequestUpsertJson(SampleRequestJson(23, "Processing", "Stale Guard", updated: "2026-05-13T13:29:00Z")));

        var cut = RenderRequestsPage();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Success");
            cut.Markup.Should().NotContain("Processing");
        });
    }

    [Fact]
    public async Task RequestsPage_KeepsTheSameRequestExpandedWhenLiveUpdatesReorderRows()
    {
        var stream = RenderRequestsWith(JsonSerializer.Serialize(new[]
        {
            SampleRequest(21, "Processing", "First request", "2026-05-13T13:30:00Z"),
            SampleRequest(22, "Processing", "Second request", "2026-05-13T13:29:00Z")
        }));
        var cut = RenderRequestsPage();
        cut.WaitForElement(".mud-expand-panel-header").Click();
        var updated = JsonSerializer.Deserialize<RequestDto>(
            SampleRequestJson(22, "Success", "Second request", "2026-05-13T13:31:00Z"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        await cut.InvokeAsync(() => stream.UpsertAsync(updated));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".request-row");
            rows[0].TextContent.Should().Contain("Second request");
            rows[0].QuerySelector(".request-details").Should().BeNull();
            rows[1].QuerySelector(".request-details-heading")!.TextContent.Should().Contain("#21");
        });
    }

    [Fact]
    public void RequestJsonPayloadView_FallsBackForInvalidJson()
    {
        var cut = Render<RequestJsonPayloadView>(parameters => parameters
            .Add(x => x.Json, "{not valid json")
            .Add(x => x.Preview, true));

        cut.Markup.Should().Contain("Payload is not valid JSON.");
        cut.Markup.Should().Contain("{not valid json");
    }

    private StubRequestStreamService RenderRequestsWith(string json, string? stream = null)
    {
        Services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(json, stream ?? ": connected\n\n"));
        var service = new StubRequestStreamService(stream);
        Services.AddSingleton<IRequestStreamService>(service);
        return service;
    }

    private IRenderedComponent<IComponent> RenderRequestsPage()
        => Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<RequestsPage>(1);
            builder.CloseComponent();
        });

    private static string SampleRequestsJson()
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                id = 17,
                sourceSystem = "external-service.api",
                targetClientIdentity = "C200B586ABCB73D63D632306A6FB59F510D6DD5340A8BB023F2BAF60F282B3DB",
                jobDefinitionId = "9",
                executionId = (string?)null,
                status = "Success",
                resultMessage = "Callback status 'succeeded' for JobRun 19.",
                resultData = "{\"netratelRequestId\":\"17\",\"netratelRunId\":\"19\"}",
                jobInputs = "{\"meta\":{\"requestId\":\"019e21ee03ef7145a8e0e17f391938f5\"},\"input\":{}}",
                logs = new[]
                {
                    "[2026-05-13T13:21:47Z] Request submitted by external-service.api.",
                    "[2026-05-13T13:21:47Z] Callback status 'running' for JobRun 19.",
                    "[2026-05-13T13:21:50Z] Callback status 'succeeded' for JobRun 19."
                },
                created = "2026-05-13T13:21:47Z",
                updated = "2026-05-13T13:21:50Z",
                jobRunId = 19,
                jobId = 9,
                jobName = "Linux System Snapshot - Example Development"
            }
        });

    private static object SampleRequest(int id, string status, string jobName, string updated)
        => new
        {
            id,
            sourceSystem = "external-service.api",
            targetClientIdentity = "C200B586ABCB73D63D632306A6FB59F510D6DD5340A8BB023F2BAF60F282B3DB",
            jobDefinitionId = "9",
            executionId = (string?)null,
            status,
            resultMessage = status == "Success" ? "Completed." : null,
            resultData = status == "Success" ? "{\"ok\":true}" : null,
            jobInputs = "{\"input\":{}}",
            logs = new[]
            {
                "[2026-05-13T13:21:47Z] Request submitted by external-service.api.",
                $"[{updated}] Status -> {status}."
            },
            created = "2026-05-13T13:21:47Z",
            updated,
            jobRunId = 19,
            jobId = 9,
            jobName
        };

    private static string SampleRequestJson(int id, string status, string jobName, string updated)
        => JsonSerializer.Serialize(SampleRequest(id, status, jobName, updated));

    private static string StreamRequestUpsertJson(string requestJson)
        => $"event: request-upsert\ndata: {requestJson}\n\n";

    private sealed class StubHttpClientFactory(string responseJson, string streamResponse) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(responseJson, streamResponse))
            {
                BaseAddress = new Uri("https://netratel.test")
            };
    }

    private sealed class StubHandler(string responseJson, string streamResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri?.AbsolutePath == "/api/v1/requests/stream"
                ? streamResponse
                : responseJson;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }

    private sealed class StubRequestStreamService(string? streamResponse) : IRequestStreamService
    {
        private readonly List<string> _events = ParseStreamEvents(streamResponse);
        private Func<RequestDto, Task>? _handler;

        public bool IsConnected { get; private set; }
        public event Action<bool>? ConnectionChanged;

        public async Task StartAsync(Func<RequestDto, Task> handler, CancellationToken ct = default)
        {
            _handler = handler;
            IsConnected = true;
            ConnectionChanged?.Invoke(true);

            foreach (var payload in _events)
            {
                var request = JsonSerializer.Deserialize<RequestDto>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (request is not null)
                {
                    await handler(request);
                }
            }
        }

        public Task UpsertAsync(RequestDto request)
            => _handler?.Invoke(request) ?? throw new InvalidOperationException("Stream has not started.");

        public Task StopAsync()
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
            => new(StopAsync());

        private static List<string> ParseStreamEvents(string? stream)
        {
            if (string.IsNullOrWhiteSpace(stream))
            {
                return [];
            }

            return stream
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(block => block.Split('\n'))
                .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[5..].Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }
    }
}
