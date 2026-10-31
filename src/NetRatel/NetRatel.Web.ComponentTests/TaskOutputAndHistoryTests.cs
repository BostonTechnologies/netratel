using System.Net;
using System.Text.Json;
using Bunit;
using AwesomeAssertions;
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
        Services.AddSingleton<ITaskExecutionStream>(new FixtureTaskStream());
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
        cut.WaitForAssertion(() => cut.FindAll(".task-stream-panel").Should().Contain(panel => panel.TextContent.Contains("attempt: 3")));
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
            cut.Find("[data-testid='task-history-count']").TextContent.Should().Be("27 matching runs · 1 on this page");
            cut.Markup.Should().Contain("Start not recorded");
        });

        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/Components/Pages/Tasks/TaskHistory.razor"));
        File.ReadAllText(sourcePath).Should().Contain("PageSizeOptions=\"new int[] { 10, 20, 50, 100 }\"");
    }

    [Fact]
    public void TaskHistory_LoadsPreviewOnlyWhenExpanded_BoundsOutput_AndKeepsFullTaskLink()
    {
        var task = CreateTask("Completed", "Finished", JsonSerializer.Serialize(new
        {
            stdout = new[] { "<script>display-as-text</script>" }.Concat(Enumerable.Range(1, 80).Select(i => $"output-line-{i}")),
            stderr = Array.Empty<string>(), exitCode = 17
        }));
        var factory = new TaskHttpClientFactory((request, _) =>
            request.RequestUri!.AbsolutePath == "/api/v2/tasks/history"
                ? JsonResponse(History(task))
                : JsonResponse(task));
        Services.AddSingleton<IHttpClientFactory>(factory);
        Services.AddSingleton<TaskApiService>();
        var cut = Render<TaskHistory>();

        cut.WaitForAssertion(() => cut.Find("[aria-label='Expand task summary']").Should().NotBeNull());
        factory.Requests.Should().ContainSingle();
        cut.FindAll("[data-testid='task-history-preview']").Should().BeEmpty();
        cut.Find("[aria-label='Open task details']").GetAttribute("href").Should().Be("/tasks/request/request-101");

        cut.Find("[aria-label='Expand task summary']").Click();
        cut.WaitForAssertion(() =>
        {
            factory.Requests.Should().HaveCount(2);
            factory.Requests.Last().AbsolutePath.Should().Be("/api/v2/tasks/101");
            var output = cut.Find(".task-history-preview-console").TextContent;
            output.Should().Contain("<script>display-as-text</script>").And.Contain("output-line-59").And.NotContain("output-line-60");
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(60);
            cut.Markup.Should().NotContain("<script>").And.Contain("Preview shortened");
            cut.Find("[aria-label='Collapse task summary']").GetAttribute("aria-expanded").Should().Be("true");
        });

        cut.Find("[aria-label='Collapse task summary']").Click();
        cut.FindAll("[data-testid='task-history-preview']").Should().BeEmpty();
        factory.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void TaskHistoryPreview_HoldsLargeResultsOutsideTheTable_AndRefreshesExplicitly()
    {
        var task = CreateTask("Completed", "Finished", new string('x', 65 * 1024));
        var reads = 0;
        Services.AddSingleton<IHttpClientFactory>(new TaskHttpClientFactory((_, _) =>
        {
            reads++;
            return JsonResponse(reads == 1 ? task : task with { ReturnData = ResultJson });
        }));
        Services.AddSingleton<TaskApiService>();
        var cut = Render<TaskHistoryPreview>(parameters => parameters.Add(x => x.Item, History(task).Items[0]));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("too large for a quick preview"));
        cut.FindAll(".task-history-preview-console").Should().BeEmpty();
        cut.Markup.Should().NotContain(new string('x', 100));
        cut.Find("[aria-label='Refresh task preview']").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".task-history-preview-console").TextContent.Should().Contain("first stdout line").And.Contain("Access denied to fixture path");
            reads.Should().Be(2);
        });
    }

    [Fact]
    public void TaskHistoryPreview_KeepsSummaryAfterLoadFailure_AndRetriesToExplicitEmptyState()
    {
        var task = CreateTask("Cancelled", "Cancelled by operator", null);
        var reads = 0;
        Services.AddSingleton<IHttpClientFactory>(new TaskHttpClientFactory((_, _) =>
            ++reads == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : JsonResponse(task)));
        Services.AddSingleton<TaskApiService>();
        var cut = Render<TaskHistoryPreview>(parameters => parameters.Add(x => x.Item, History(task).Items[0]));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Task details could not be loaded").And.Contain("fixture-host").And.Contain("request-101");
            cut.Find("a").GetAttribute("href").Should().Be("/tasks/request/request-101");
        });
        cut.Find("[aria-label='Refresh task preview']").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No execution output was captured.").And.NotContain("Task details could not be loaded"));
        reads.Should().Be(2);
    }

    [Fact]
    public async Task TaskHistory_CollapseDisposesPendingPreview_WithoutRestoringLateOutput()
    {
        var task = CreateTask("Completed", "Finished", ResultJson);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<IHttpClientFactory>(new TaskHttpClientFactory((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/tasks/history") return Task.FromResult(JsonResponse(History(task)));
            requested.TrySetResult(token);
            return response.Task;
        }));
        Services.AddSingleton<TaskApiService>();
        var cut = Render<TaskHistory>();
        cut.WaitForAssertion(() => cut.Find("[aria-label='Expand task summary']").Should().NotBeNull());
        var opening = cut.Find("[aria-label='Expand task summary']").ClickAsync(new());
        try
        {
            var cancellation = await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cut.WaitForAssertion(() => cut.Find("[aria-label='Collapse task summary']").Should().NotBeNull());

            cut.Find("[aria-label='Collapse task summary']").Click();
            cancellation.IsCancellationRequested.Should().BeTrue();
            response.SetResult(JsonResponse(task));
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(() =>
            {
                cut.FindAll("[data-testid='task-history-preview']").Should().BeEmpty();
                cut.Markup.Should().NotContain("first stdout line");
            });
        }
        finally
        {
            response.TrySetResult(JsonResponse(task));
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void PendingLibraryTask_ShowsRecordedNameAndTenant_WithoutInventingStartOrRuntime()
    {
        ConfigureTaskApi(CreateTask("Pending", "Waiting for execution", null) with
        {
            TaskType = TaskKinds.ExecLibraryScript, ScriptName = "List drive contents", ScriptType = "PowerShell", TenantName = "Camelot-Estate",
            StartedAt = null, CompletedAt = null, ExitCode = null, Environment = null
        });
        var cut = Render<TaskDetailPage>(parameters => parameters.Add(x => x.TaskId, 101));
        cut.WaitForAssertion(() =>
        {
            cut.Find(".task-detail-title").TextContent.Should().Be("List drive contents");
            cut.Find("[data-testid='task-target']").TextContent.Should().Contain("fixture-host · Camelot-Estate");
            cut.Find("[data-testid='task-started']").TextContent.Should().Be("Started: Not started");
            cut.Find("[data-testid='task-runtime']").TextContent.Should().Be("Runtime: Not started");
            cut.Find("[data-testid='task-connection']").TextContent.Should().Be("Connecting");
            cut.Markup.Should().Contain("Environment: Not recorded");
            cut.Find("[aria-label='Copy request ID']").Should().NotBeNull();
        });
    }

    [Fact]
    public void UnknownOutcome_DoesNotRenderReconciliationDeadlineAsMeasuredRuntime()
    {
        ConfigureTaskApi(CreateTask("Unknown", "No terminal execution receipt was received.", null) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30), CompletedAt = DateTimeOffset.UtcNow, ExitCode = null
        });
        var cut = Render<TaskDetailPage>(parameters => parameters.Add(x => x.TaskId, 101));
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Outcome unknown").And.Contain("Reconciled:");
            cut.Find("[data-testid='task-runtime']").TextContent.Should().Be("Runtime: Not recorded");
            cut.Markup.Should().NotContain("Exit 0");
        });
    }

    [Fact]
    public async Task TaskSnapshot_RecoversLogFailure_DeduplicatesOrdersAndBoundsOutput_AndKeepsSelectedTab()
    {
        var stream = new FixtureTaskStream();
        Services.AddSingleton<ITaskExecutionStream>(stream);
        var logAttempts = 0;
        var task = CreateTask("Processing", "Running", null) with { CompletedAt = null, ExitCode = null };
        Services.AddSingleton<IHttpClientFactory>(new TaskHttpClientFactory((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/tasks/101") return JsonResponse(task);
            logAttempts++;
            if (logAttempts == 2) return new(HttpStatusCode.ServiceUnavailable);
            if (logAttempts == 1) return JsonResponse<IReadOnlyList<TaskLogDto>>([
                new(1, task.RequestId, "", DateTimeOffset.UtcNow, "stdout", "initial line", 1)]);
            return JsonResponse(Enumerable.Range(2, 2001).Reverse()
                .Select(id => new TaskLogDto(id, task.RequestId, "", DateTimeOffset.UtcNow, "stdout", $"line-{id}", id))
                .Append(new(2, task.RequestId, "", DateTimeOffset.UtcNow, "stdout", "duplicate-must-not-appear", 2)).ToArray());
        }));
        Services.AddSingleton<TaskApiService>();
        var cut = Render<TaskDetailPage>(parameters => parameters.Add(x => x.TaskId, 101));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("initial line"));
        cut.FindAll(".mud-tab").Single(tab => tab.TextContent == "Stdout").Click();
        await cut.InvokeAsync(() => stream.RefreshAsync());
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='task-connection']").TextContent.Should().Be("Recovering");
            cut.Markup.Should().Contain("initial line");
        });
        await cut.InvokeAsync(() => stream.RefreshAsync());
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='task-connection']").TextContent.Should().Be("Live");
            var output = cut.Find(".task-output-console").TextContent;
            output.Should().NotContain("initial line");
            output.Should().NotContain("duplicate-must-not-appear");
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.Should().Be(2000);
            output.IndexOf("line-3", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("line-2002", StringComparison.Ordinal));
            cut.FindAll(".mud-tab").Single(tab => tab.TextContent == "Stdout").ClassList.Should().Contain("mud-tab-active");
        });
    }

    [Fact]
    public async Task TaskNavigation_DiscardsAResponseFromThePreviousRoute()
    {
        var stream = new FixtureTaskStream();
        Services.AddSingleton<ITaskExecutionStream>(stream);
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequests = 0;
        var oldTask = CreateTask("Processing", "Old task", null) with { ScriptName = "Old execution" };
        var newTask = oldTask with { Id = 202, RequestId = "request-202", ScriptName = "New execution", ClientHostName = "new-host" };
        Services.AddSingleton<IHttpClientFactory>(new TaskHttpClientFactory((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/tasks/101")
            {
                if (++oldRequests == 2) { delayedRequest.TrySetResult(); return oldResponse.Task; }
                return Task.FromResult(JsonResponse(oldTask));
            }
            if (request.RequestUri.AbsolutePath == "/api/v2/tasks/202") return Task.FromResult(JsonResponse(newTask));
            return Task.FromResult(JsonResponse<IReadOnlyList<TaskLogDto>>([]));
        }));
        Services.AddSingleton<TaskApiService>();
        var cut = Render<TaskDetailPage>(parameters => parameters.Add(x => x.TaskId, 101));
        cut.WaitForAssertion(() => cut.Find(".task-detail-title").TextContent.Should().Be("Old execution"));
        var refresh = cut.InvokeAsync(() => stream.RefreshAsync());
        await delayedRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cut.Render(parameters => parameters.Add(x => x.TaskId, 202));
        cut.WaitForAssertion(() => cut.Find(".task-detail-title").TextContent.Should().Be("New execution"));
        oldResponse.SetResult(JsonResponse(oldTask));
        await refresh;
        cut.Find(".task-detail-title").TextContent.Should().Be("New execution");
        cut.Find("[data-testid='task-target']").TextContent.Should().Contain("new-host");
    }

    private sealed class FixtureTaskStream : ITaskExecutionStream
    {
        private Func<CancellationToken, Task<bool>>? _refresh;
        private Action<string>? _onStatus;
        public Task StartAsync(string requestId, Func<CancellationToken, Task<bool>> refresh, Action<string> onStatus, CancellationToken ct = default)
        { _refresh = refresh; _onStatus = onStatus; return Task.CompletedTask; }
        public async Task RefreshAsync() => _onStatus?.Invoke(await _refresh!(CancellationToken.None) ? "Live" : "Recovering");
        public Task StopAsync() { _refresh = null; _onStatus = null; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private static TaskHistoryPageDto History(TaskDto task) => new(
        [new TaskHistoryItemDto(task.Id, task.RequestId, task.TenantId, task.TaskType, task.Status,
            task.StatusMessage, task.Created, task.CompletedAt, task.ClientDisplayName, task.ClientHostName,
            task.ClientName, task.AgentId, task.Environment, task.StartedAt, task.ExitCode,
            task.ScriptName, task.ScriptType, task.CommandLabel, task.TenantName)], 1, 0, 10);

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

    private sealed class TaskHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;
        public TaskHttpClientFactory(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
            => _response = (request, cancellation) => Task.FromResult(response(request, cancellation));
        public TaskHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
            => _response = response;
        public List<Uri> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, _response))
        {
            BaseAddress = new Uri("https://netratel.test")
        };

        private sealed class Handler(TaskHttpClientFactory owner, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(request.RequestUri!);
                return response(request, cancellationToken);
            }
        }
    }
}
