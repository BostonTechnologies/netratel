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
        public ValueTask SendAsync(JobRunDto run, IReadOnlyList<JobStepRunDto> steps, IReadOnlyList<TaskLogDto> lines) =>
            _updates.Writer.WriteAsync(new("Live", new(run, steps, [new(1, lines)])));
    }

    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public JobRunDto Run { get; set; } = new(9, 42, "Backup", 7, "agent", "owner", JobRunStatusDto.Running, 1,
            100, 100, null, null, "{}", Environment: ClientEnvironment.Prod,
            TargetDisplayName: "Frozen agent", TargetHostName: "frozen-host");
        public IReadOnlyList<JobStepRunDto> Steps { get; set; } = [new(11, 9, 21, JobStepRunStatusDto.Running, 1, "task-1", 100, null, null, ScriptName: "Frozen script", ScriptType: "Bash")];
        public ExecutionLogDto Log { get; set; } = new("task-1", null, null, 0, [Line(1, "initial output")]);
        public int StepReads { get; private set; }
        public int LogReads { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("http://fixture") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object result = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/jobruns/9" => Run,
                "/api/v1/jobs/42" => new JobDto(42, "Current mutable definition", "/Fixture/", null, 7, "different-agent", 0, 0),
                "/api/v1/jobs/42/steps" => Array.Empty<JobStepDto>(),
                "/api/v1/jobruns/9/steps" => ReadSteps(),
                "/api/v1/jobruns/9/steps/1/logs" => ReadLog(),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
        private IReadOnlyList<JobStepRunDto> ReadSteps() { StepReads++; return Steps; }
        private ExecutionLogDto ReadLog() { LogReads++; return Log; }
    }
}
