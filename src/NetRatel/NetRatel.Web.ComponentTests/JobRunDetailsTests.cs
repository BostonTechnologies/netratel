using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Web.Components.Pages.Jobs;
using NetRatel.Web.Services.Jobs;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class JobRunDetailsTests : AsyncBunitContext
{
    private readonly Transport _transport = new();
    private readonly LiveStream _stream = new();

    public JobRunDetailsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory>(_transport);
        Services.AddScoped<IJobApiClient, JobApiClient>();
        Services.AddSingleton<IJobRunStreamService>(_stream);
    }

    [Fact]
    public async Task Live_replacement_orders_deduplicates_output_and_preserves_expansion_tab_and_context()
    {
        var cut = Render<JobRunDetails>(parameters => parameters.Add(page => page.RunId, 9).Add(page => page.JobId, 42));
        cut.WaitForElement(".job-run-output");
        cut.Markup.Should().Contain("Frozen agent (frozen-host)").And.Contain("Prod");
        cut.Markup.Should().Contain("Frozen script").And.Contain("Bash");
        cut.Markup.Should().NotContain("Execution Trace").And.NotContain("Run Profile").And.NotContain("Step Telemetry");
        var step = cut.FindComponent<MudExpansionPanel>();
        await cut.InvokeAsync(() => step.Instance.ExpandedChanged.InvokeAsync(false));
        var rows = new[] { Line(2, "second"), Line(1, "first"), Line(2, "second duplicate") };
        await _stream.SendAsync(_transport.Run, _transport.Steps, rows);
        cut.WaitForAssertion(() => cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse());
        await cut.InvokeAsync(() => step.Instance.ExpandedChanged.InvokeAsync(true));
        cut.WaitForAssertion(() => cut.Find(".job-run-output").TextContent.Should().Be("first\nsecond"));
        cut.FindAll(".mud-tab").Single(tab => tab.TextContent == "Diagnostics").Click();
        cut.WaitForAssertion(() => cut.Find(".mud-tab-active").TextContent.Should().Be("Diagnostics"));
        await _stream.SendAsync(_transport.Run, _transport.Steps, rows.Append(Line(3, "third")).ToArray());
        cut.WaitForAssertion(() => cut.Find(".job-run-output").TextContent.Should().Be("first\nsecond\nthird"));
        cut.Find(".mud-tab-active").TextContent.Should().Be("Diagnostics");
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();
        _transport.StepReads.Should().Be(1); // Live delivery does not poll step state.
        _transport.LogReads.Should().Be(1);
    }

    [Theory]
    [InlineData(JobRunStatusDto.Pending, JobStepRunStatusDto.Pending, null, null, "Waiting for step execution evidence.")]
    [InlineData(JobRunStatusDto.Running, JobStepRunStatusDto.Running, 100L, null, "Waiting for output. No output has been captured yet.")]
    [InlineData(JobRunStatusDto.Succeeded, JobStepRunStatusDto.Succeeded, 100L, 200L, "Execution finished without captured output.")]
    [InlineData(JobRunStatusDto.Failed, JobStepRunStatusDto.Failed, null, 1801000000L, "Start was not captured. No output was captured.")]
    [InlineData(JobRunStatusDto.Unknown, JobStepRunStatusDto.Unknown, 1000000L, 1801000000L, "No further output was captured. The execution outcome is unknown.")]
    public void Empty_output_uses_execution_evidence_and_never_invents_an_exit_code(
        JobRunStatusDto runStatus, JobStepRunStatusDto stepStatus, long? started, long? completed, string expected)
    {
        _transport.Run = _transport.Run with { Status = runStatus, StartedAtMicros = started, CompletedAtMicros = completed, Environment = null, TargetDisplayName = null, TargetHostName = null,
            TargetName = null, ClientDisplayName = null, ClientIdentity = "legacy-technical-identity" };
        _transport.Steps = [_transport.Steps[0] with { Status = stepStatus, StartedAtMicros = started, CompletedAtMicros = completed }];
        _transport.Log = new("task-1", null, null, null, []);
        var cut = Render<JobRunDetails>(parameters => parameters.Add(page => page.RunId, 9).Add(page => page.JobId, 42));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(expected));
        cut.Markup.Should().Contain("Environment not recorded").And.Contain("Target unknown");
        cut.FindAll(".mud-tab").Single(tab => tab.TextContent == "Diagnostics").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Exit code was not captured."));
        cut.Markup.Should().NotContain("Exit code: 0");
        if (runStatus == JobRunStatusDto.Failed && started is null)
        {
            cut.Find(".job-run-step-time").TextContent.Should().Be("Start not captured");
            cut.Markup.Should().NotContain("This step did not start");
        }
        if (runStatus == JobRunStatusDto.Unknown)
        {
            cut.Markup.Should().Contain("Execution outcome is unknown. No terminal receipt was captured.");
            cut.Find(".job-run-context").TextContent.Should().Contain("Duration unknown").And.NotContain("30m");
            cut.Find(".job-run-step-time").TextContent.Should().Be("Outcome unknown");
            cut.Find(".job-run-diagnostics").TextContent.Should().Contain("Reconciled").And.NotContain("Completed");
            cut.Find(".job-run-details").TextContent.Should().Contain("Reconciled").And.NotContain("Completed");
            cut.FindAll("button").Should().NotContain(button => button.TextContent.Contains("Cancel run"));
            _stream.Subscriptions.Should().Be(0);
        }
    }

    [Fact]
    public void Diagnostics_prefers_the_durable_native_exit_receipt_over_legacy_log_defaults()
    {
        _transport.Run = _transport.Run with { Status = JobRunStatusDto.Failed, Environment = ClientEnvironment.Both };
        _transport.Steps = [_transport.Steps[0] with { Status = JobStepRunStatusDto.Failed, ExitCode = 7, CompletedAtMicros = 200 }];
        _transport.Log = _transport.Log with { ExitCode = 0 };
        var cut = Render<JobRunDetails>(parameters => parameters.Add(page => page.RunId, 9).Add(page => page.JobId, 42));
        cut.WaitForElement(".job-run-output");
        cut.FindAll(".mud-tab").Single(tab => tab.TextContent == "Diagnostics").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Exit code: 7"));
        cut.Markup.Should().NotContain("Exit code: 0");
        cut.Find(".job-run-context").TextContent.Should().Contain("Dev + Prod");
        _stream.Subscriptions.Should().Be(0);
    }

    [Fact]
    public async Task Terminal_snapshot_stops_subscription_and_bounds_large_output_without_repeating_failure()
    {
        var cut = Render<JobRunDetails>(parameters => parameters.Add(page => page.RunId, 9).Add(page => page.JobId, 42));
        cut.WaitForElement(".job-run-output");
        const string failure = "specific native failure";
        var failed = _transport.Run with { Status = JobRunStatusDto.Failed, Error = failure };
        var steps = new[] { _transport.Steps[0] with { Status = JobStepRunStatusDto.Failed, Error = failure, CompletedAtMicros = 200 } };
        await _stream.SendAsync(failed, steps, [Line(1, new string('x', 300_000))]);
        cut.WaitForAssertion(() => cut.Find(".job-run-output").TextContent.Length.Should().Be(256 * 1024));
        cut.Markup.Should().Contain("Some output was omitted from this view.");
        cut.Markup.Should().NotContain("Run error:").And.NotContain("Step error:").And.NotContain("mud-alert-filled-error");
        cut.FindAll("button").Should().NotContain(button => button.TextContent.Contains("Cancel run"));
        await cut.WaitForAssertionAsync(() => _stream.Ended.Should().BeTrue());
    }

    [Fact]
    public async Task Live_three_step_run_follows_once_per_transition_and_manual_inspection_pauses_until_resume()
    {
        var first = _transport.Steps[0];
        var second = first with { Id = 12, Ordinal = 2, Status = JobStepRunStatusDto.Pending, StartedAtMicros = null, TaskRequestId = null, ScriptName = "Second step" };
        var third = second with { Id = 13, Ordinal = 3, ScriptName = "Third step" };
        _transport.Steps = [first, second, third];
        var cut = Render<JobRunDetails>(parameters => parameters.Add(page => page.RunId, 9).Add(page => page.JobId, 42));
        cut.WaitForElement(".job-run-output");
        cut.FindComponents<MudExpansionPanel>().Select(panel => panel.Instance.Expanded).Should().Equal(true, false, false);
        first = first with { Status = JobStepRunStatusDto.Succeeded, CompletedAtMicros = 200 };
        second = second with { Status = JobStepRunStatusDto.Running, StartedAtMicros = 210, TaskRequestId = "task-2" };
        await _stream.SendAsync(_transport.Run, [first, second, third], [Line(1, "second output")], 2);
        cut.WaitForAssertion(() => cut.FindComponents<MudExpansionPanel>().Select(panel => panel.Instance.Expanded).Should().Equal(false, true, false));
        cut.WaitForAssertion(() => cut.FindAll(".job-run-output").Should().Contain(output => output.TextContent == "second output"));
        var secondPanel = cut.FindComponents<MudExpansionPanel>()[1];
        await cut.InvokeAsync(() => secondPanel.Instance.ExpandedChanged.InvokeAsync(true));
        cut.FindComponents<MudSwitch<bool>>().Single().Instance.Value.Should().BeTrue(); // Programmatic echo does not pause.
        await cut.InvokeAsync(() => cut.FindComponents<MudExpansionPanel>()[0].Instance.ExpandedChanged.InvokeAsync(true));
        cut.Markup.Should().Contain("Follow paused");
        third = third with { Status = JobStepRunStatusDto.Running, StartedAtMicros = 310, TaskRequestId = "task-3" };
        second = second with { Status = JobStepRunStatusDto.Succeeded, CompletedAtMicros = 300 };
        await _stream.SendAsync(_transport.Run, [first, second, third], [Line(1, "third output")], 3);
        cut.WaitForAssertion(() => cut.FindComponents<MudExpansionPanel>()[2].Find(".job-run-step-header").TextContent.Should().Contain("Running"));
        cut.FindComponents<MudExpansionPanel>().Select(panel => panel.Instance.Expanded).Should().Equal(true, true, false);
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent == "Resume").Click());
        cut.WaitForAssertion(() => cut.FindComponents<MudExpansionPanel>().Select(panel => panel.Instance.Expanded).Should().Equal(false, false, true));
        cut.WaitForAssertion(() => cut.FindComponents<MudExpansionPanel>()[2].Find(".job-run-output").TextContent.Should().Be("third output"));
        await _stream.SendAsync(_transport.Run with { Status = JobRunStatusDto.Succeeded }, [first, second, third with { Status = JobStepRunStatusDto.Succeeded, CompletedAtMicros = 400 }], [Line(1, "third output")], 3);
        cut.WaitForAssertion(() => cut.Find(".job-run-summary").TextContent.Should().Contain("Execution completed successfully."));
        cut.FindComponents<MudExpansionPanel>().Select(panel => panel.Instance.Expanded).Should().Equal(false, false, true);
        _transport.LogReads.Should().Be(1); // Stream logs satisfy newly opened panels.
    }

    [Fact]
    public async Task Late_http_output_does_not_replace_newer_captured_stream_output()
    {
        var cut = Render<JobRunDetails>(parameters => parameters.Add(page => page.RunId, 9).Add(page => page.JobId, 42));
        cut.WaitForElement(".job-run-output");
        var first = _transport.Steps[0] with { Status = JobStepRunStatusDto.Succeeded, CompletedAtMicros = 200 };
        var second = first with { Id = 12, Ordinal = 2, Status = JobStepRunStatusDto.Running, TaskRequestId = "task-2", StartedAtMicros = 210, CompletedAtMicros = null };
        _transport.PendingSecondLog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await _stream.SendWithoutLogsAsync(_transport.Run, [first, second]);
        cut.WaitForAssertion(() => _transport.LogReads.Should().Be(2));
        await _stream.SendAsync(_transport.Run, [first, second], [Line(2, "new streamed output")], 2);
        cut.WaitForAssertion(() => cut.FindAll(".job-run-output").Should().Contain(output => output.TextContent == "new streamed output"));
        var renderCount = cut.RenderCount;
        _transport.PendingSecondLog.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new ExecutionLogDto("task-2", null, null, null, [Line(1, "old HTTP output")])) });
        await cut.WaitForAssertionAsync(() => cut.RenderCount.Should().BeGreaterThan(renderCount));
        cut.FindAll(".job-run-output").Should().Contain(output => output.TextContent == "new streamed output");
        cut.Markup.Should().NotContain("old HTTP output");
    }

    [Fact]
    public async Task Route_change_resets_follow_and_ignores_old_run_snapshots_without_duplicate_subscriptions()
    {
        var cut = Render<JobRunDetails>(parameters => parameters.Add(page => page.RunId, 9).Add(page => page.JobId, 42));
        cut.WaitForElement(".job-run-output");
        await cut.InvokeAsync(() => cut.FindComponent<MudExpansionPanel>().Instance.ExpandedChanged.InvokeAsync(false));
        cut.Render(parameters => parameters.Add(page => page.RunId, 10).Add(page => page.JobId, 42));
        cut.WaitForAssertion(() => cut.Find(".job-run-context").TextContent.Should().Contain("Run #10"));
        await _stream.SendAsync(_transport.Run, _transport.Steps, [Line(1, "stale output")]);
        var nextRun = _transport.Run with { Id = 10, JobName = "Next route" };
        var nextSteps = _transport.Steps.Select(step => step with { Id = step.Id + 10, JobRunId = 10 }).ToList();
        await _stream.SendAsync(nextRun, nextSteps, [Line(1, "new route output")]);
        cut.WaitForAssertion(() => cut.Find(".job-run-output").TextContent.Should().Be("new route output"));
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();
        cut.FindComponents<MudSwitch<bool>>().Single().Instance.Value.Should().BeTrue();
        cut.Markup.Should().NotContain("stale output");
        _stream.Subscriptions.Should().Be(2);
    }

    private static TaskLogDto Line(long sequence, string message) => new(sequence, "task-1", "agent", DateTimeOffset.UnixEpoch, "stdout", message, sequence);

    private sealed class LiveStream : IJobRunStreamService
    {
        private readonly Channel<JobRunLiveUpdate> _updates = Channel.CreateUnbounded<JobRunLiveUpdate>();
        public int Subscriptions { get; private set; }
        public bool Ended { get; private set; }
        public async IAsyncEnumerable<JobRunLiveUpdate> SubscribeAsync(ulong runId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Subscriptions++;
            try
            {
                await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken)) yield return update;
            }
            finally { Ended = true; }
        }
        public ValueTask SendWithoutLogsAsync(JobRunDto run, IReadOnlyList<JobStepRunDto> steps) =>
            _updates.Writer.WriteAsync(new("Live", new(run, steps, [])));
        public ValueTask SendAsync(JobRunDto run, IReadOnlyList<JobStepRunDto> steps, IReadOnlyList<TaskLogDto> lines, int ordinal = 1) =>
            _updates.Writer.WriteAsync(new("Live", new(run, steps, [new(ordinal, lines)])));
    }

    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public JobRunDto Run { get; set; } = new(9, 42, "Backup", 7, "agent", "owner", JobRunStatusDto.Running, 1,
            100, 100, null, null, "{}", Environment: ClientEnvironment.Prod,
            TargetDisplayName: "Frozen agent", TargetHostName: "frozen-host");
        public IReadOnlyList<JobStepRunDto> Steps { get; set; } = [new(11, 9, 21, JobStepRunStatusDto.Running, 1, "task-1", 100, null, null, ScriptName: "Frozen script", ScriptType: "Bash")];
        public ExecutionLogDto Log { get; set; } = new("task-1", null, null, 0, [Line(1, "initial output")]);
        public TaskCompletionSource<HttpResponseMessage>? PendingSecondLog { get; set; }
        public int StepReads { get; private set; }
        public int LogReads { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("http://fixture") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/jobruns/9/steps/2/logs" && PendingSecondLog is not null)
            {
                LogReads++;
                return await PendingSecondLog.Task;
            }
            object result = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/jobruns/9" => Run,
                "/api/v1/jobruns/10" => Run with { Id = 10, JobName = "Next route" },
                "/api/v1/jobruns/10/steps" => Steps.Select(step => step with { Id = step.Id + 10, JobRunId = 10 }).ToList(),
                "/api/v1/jobruns/10/steps/1/logs" => ReadLog(),
                "/api/v1/jobs/42" => new JobDto(42, "Current mutable definition", "/Fixture/", null, 7, "different-agent", 0, 0),
                "/api/v1/jobs/42/steps" => Array.Empty<JobStepDto>(),
                "/api/v1/jobruns/9/steps" => ReadSteps(),
                "/api/v1/jobruns/9/steps/1/logs" => ReadLog(),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
        private IReadOnlyList<JobStepRunDto> ReadSteps() { StepReads++; return Steps; }
        private ExecutionLogDto ReadLog() { LogReads++; return Log; }
    }
}
