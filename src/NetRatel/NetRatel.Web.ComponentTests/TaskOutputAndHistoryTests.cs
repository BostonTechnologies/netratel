using System.Net;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Web.Components.Pages.Tasks;
using NetRatel.Web.Services.ClientTasks;
using Xunit;
using TaskDetailPage = NetRatel.Web.Components.Pages.Tasks.TaskDetail;

namespace NetRatel.Web.ComponentTests;

public sealed class TaskOutputAndHistoryTests : AsyncBunitContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public TaskOutputAndHistoryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
    }

    [Fact]
    public void CompletedTask_RendersEveryOutputView_AndContainsLongLines()
    {
        ConfigureTaskApi(CreateTask("Completed", "completed successfully", ResultJson));

        var cut = Render<TaskDetailPage>(parameters => parameters.Add(x => x.TaskId, 101));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Console");
            cut.Markup.Should().Contain("Stdout");
            cut.Markup.Should().Contain("Stderr");
            cut.Markup.Should().Contain("Diagnostics");
            cut.Markup.Should().Contain("Plain");
            cut.Markup.Should().Contain("Raw JSON");
            cut.Markup.Should().Contain("first stdout line");
            cut.Markup.Should().Contain("Exit 17");
            cut.Find(".task-output-scroll").GetAttribute("class").Should().Contain("task-output-scroll");
            cut.Markup.Should().Contain("max-height: min(46dvh, 34rem)");
            cut.Markup.Should().Contain("overflow-wrap: anywhere");
        });
    }

    [Fact]
    public void FailedTask_RendersConciseStatusAndStructuredResultWithoutLosingStderr()
    {
        ConfigureTaskApi(CreateTask("Failed", "The update command failed.", ResultJson));

        var cut = Render<TaskDetailPage>(parameters => parameters.Add(x => x.TaskId, 101));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("The update command failed.");
            cut.Markup.Should().Contain("Write-Error:");
            cut.Markup.Should().Contain("Access denied to fixture path");
            cut.Markup.Should().Contain("Exit 17");
        });

        cut.FindAll(".mud-tab").Single(tab => tab.TextContent == "Diagnostics").Click();
        cut.WaitForAssertion(() => cut.Find(".task-stream-panel").TextContent.Should().Contain("attempt: 3"));
    }

    [Fact]
    public void TerminalTaskWithoutResult_RendersCompactExplicitEmptyState()
    {
        ConfigureTaskApi(CreateTask("Cancelled", "Cancelled by operator", null));

        var cut = Render<TaskDetailPage>(parameters => parameters.Add(x => x.TaskId, 101));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No execution output was captured.");
            cut.Markup.Should().NotContain("mud-expansion-panels");
        });
    }

    [Fact]
    public void TaskHistory_RequestsTheFirstTenRowsAndNeverRendersRawResultInTargetColumn()
    {
        var rawPayload = new string('x', 64 * 1024);
        var history = new TaskHistoryPageDto(
            [new TaskHistoryItemDto(101, "request-101", 7, "Update", "Failed", "A concise failure", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Fixture agent", "fixture-host", "Fixture agent", Guid.NewGuid())],
            27,
            0,
            10);
        var factory = new TaskHttpClientFactory((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/v2/tasks/history");
            return JsonResponse(history);
        });
        Services.AddSingleton<IHttpClientFactory>(factory);
        Services.AddSingleton<TaskApiService>();

        var cut = Render<TaskHistory>();

        cut.WaitForAssertion(() =>
        {
            factory.Requests.Should().ContainSingle();
            factory.Requests[0].Query.Should().Contain("page=0");
            factory.Requests[0].Query.Should().Contain("pageSize=10");
            cut.Markup.Should().Contain("fixture-host");
            cut.Markup.Should().Contain("1-10 of 27");
            cut.Markup.Should().NotContain(rawPayload);
            cut.Markup.Should().Contain("task-history-target-column");
        });

        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/Components/Pages/Tasks/TaskHistory.razor"));
        File.ReadAllText(sourcePath).Should().Contain("PageSizeOptions=\"new int[] { 10, 20, 50, 100 }\"");
    }

    private void ConfigureTaskApi(TaskDto task)
    {
        Services.AddSingleton<IHttpClientFactory>(new TaskHttpClientFactory((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/tasks/101")
            {
                return JsonResponse(task);
            }

            return JsonResponse<IReadOnlyList<TaskLogDto>>([]);
        }));
        Services.AddSingleton<TaskApiService>();
    }

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions)) };

    private static TaskDto CreateTask(string status, string statusMessage, string? returnData) =>
        new(
            101,
            "request-101",
            "agent-routing-identity",
            7,
            ClientEnvironment.Dev,
            "Update",
            status,
            statusMessage,
            returnData,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            17,
            "Fixture agent",
            "fixture-host",
            "Fixture agent",
            Guid.Parse("fe4c9a9d-dbae-4c03-9fed-4f2e4f10d5b1"));

    private const string ResultJson = """
        {"stdout":["first stdout line","second stdout line"],"stderr":["Write-Error:","Access denied to fixture path"],"diagnostics":{"attempt":"3","source":"fixture"},"exitCode":17}
        """;

    private sealed class TaskHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : IHttpClientFactory
    {
        public List<Uri> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, response))
        {
            BaseAddress = new Uri("https://netratel.test")
        };

        private sealed class Handler(TaskHttpClientFactory owner, Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(request.RequestUri!);
                return Task.FromResult(response(request, cancellationToken));
            }
        }
    }
}
