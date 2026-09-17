using System.Net;
using System.Globalization;
using System.Text.Json;
using Bunit;
using FluentAssertions;
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
    public void RequestsPage_RendersPayloadActionsOnlyWhenPayloadsExist()
    {
        RenderRequestsWith(SampleRequestsJson());

        var cut = RenderRequestsPage();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button[aria-label='View Inputs']").Should().ContainSingle();
            cut.FindAll("button[aria-label='View Output']").Should().ContainSingle();
            cut.Markup.Should().NotContain("No payloads");
        });
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
    public void RequestJsonPayloadView_FallsBackForInvalidJson()
    {
        var cut = Render<RequestJsonPayloadView>(parameters => parameters
            .Add(x => x.Json, "{not valid json")
            .Add(x => x.Preview, true));

        cut.Markup.Should().Contain("Payload is not valid JSON.");
        cut.Markup.Should().Contain("{not valid json");
    }

    private void RenderRequestsWith(string json, string? stream = null)
    {
        Services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(json, stream ?? ": connected\n\n"));
        Services.AddSingleton<IRequestStreamService>(new StubRequestStreamService(stream));
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
                rundeckJobDefinitionId = "9",
                rundeckExecutionId = (string?)null,
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
            rundeckJobDefinitionId = "9",
            rundeckExecutionId = (string?)null,
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

        public bool IsConnected { get; private set; }
        public event Action<bool>? ConnectionChanged;

        public async Task StartAsync(Func<RequestDto, Task> handler, CancellationToken ct = default)
        {
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
