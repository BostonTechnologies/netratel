using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Shared.Contracts.Scripts;
using NetRatel.Web.Components.Pages.Jobs;
using NetRatel.Web.Models.Clients;
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Services.Jobs;
using NetRatel.Web.Services.Script;
using NetRatel.Web.Services.Telemetry;
using NetRatel.Web.Services.Tenants;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class JobsWorkspaceTests : AsyncBunitContext
{
    private readonly JobTransport _transport = new();
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;
    private static readonly Guid AgentId = Guid.Parse("e71aeb4d-022d-4cfa-9141-8cf7f2b3f3b6");
    private static readonly TenantDto Tenant = new(7, "Fixture", null, null, [], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static readonly ClientPresentationModel Client = new(7, AgentId, "Fixture agent", "fixture-host", "Fixture", "Linux", "x64", true, true, null, null, [], null, null);

    public JobsWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IHttpClientFactory>(_transport);
        Services.AddScoped<IJobApiClient, JobApiClient>();
        Services.AddScoped<ClientPresenceApiService>();
        Services.AddScoped<GatewayTelemetryApiService>();
        Services.AddScoped<ClientPresentationService>();
        Services.AddSingleton<ITenantApiService, Tenants>();
        Services.AddSingleton<IScriptLibraryService, ScriptLibrary>();
        _dialogs = Render<MudDialogProvider>();
    }

    [Fact]
    public async Task Inline_run_opens_correct_parameter_flow_once_and_cancel_does_not_execute()
    {
        var page = Render<JobsPage>();
        page.WaitForElement("button[aria-label='Run Nightly backup']");
        page.FindAll(".jobs-row-actions button").Should().HaveCount(4);
        page.Markup.Should().NotContain("Workflow Control");
        page.Find("button[aria-label='Run Nightly backup']").Click();
        _dialogs.WaitForAssertion(() => _dialogs.FindComponent<RunJobDialog>().Instance.Job.Id.Should().Be(42));
        page.Find("button[aria-label='Run Nightly backup']").HasAttribute("disabled").Should().BeTrue();
        page.Find("button[aria-label='Run Nightly backup']").Click();
        _dialogs.FindComponents<RunJobDialog>().Should().ContainSingle();
        _transport.ParamReads.Should().Be(1);
        Button(_dialogs, "Cancel").Click();
        await page.WaitForAssertionAsync(() => page.Find("button[aria-label='Run Nightly backup']").HasAttribute("disabled").Should().BeFalse());
        _transport.RunCalls.Should().Be(0);
        page.Find("button[aria-label='Delete Nightly backup']").Click();
        _dialogs.WaitForElement(".mud-message-box");
        Button(_dialogs, "Cancel").Click();
        _transport.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Create_continues_with_persisted_id_steps_and_same_job_save_without_execution()
    {
        var (cut, reference) = await OpenEditorAsync();
        await FillNewDefinitionAsync(cut, "New workflow");
        _transport.PendingCreate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var create = cut.InvokeAsync(() => Button(cut, "Create and add steps").ClickAsync(new MouseEventArgs()));
        await cut.InvokeAsync(() => Button(cut, "Saving…").ClickAsync(new MouseEventArgs()));
        _transport.CreateCalls.Should().Be(1);
        _transport.PendingCreate.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(_transport.Job) });
        await create;
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Job #43 created"));
        _transport.CreateCalls.Should().Be(1);
        Button(cut, "Add Step").HasAttribute("disabled").Should().BeFalse();
        Button(cut, "Add Step").Click();
        _dialogs.WaitForAssertion(() => _dialogs.FindComponent<StepEditDialog>().Instance.JobId.Should().Be(43));
        var step = _dialogs.FindComponent<StepEditDialog>();
        step.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Command").Find("textarea").Change("echo fixture");
        Button(step, "Save").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("echo fixture"));
        _transport.StepJobId.Should().Be(43);
        Button(cut, "Save").Click();
        var result = await reference.Result;
        result!.Canceled.Should().BeFalse();
        _transport.UpdatedJobId.Should().Be(43);
        _transport.CreateCalls.Should().Be(1);
        _transport.RunCalls.Should().Be(0);
    }

    [Fact]
    public async Task Child_refresh_retains_definition_schedule_runtime_and_tab_drafts()
    {
        var (cut, _) = await OpenEditorAsync(jobId: 42);
        Name(cut).Change("Unsaved renamed workflow");
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Folder (within tenant)").Find("input").Change("Changed/Nested");
        Tab(cut, "Runtime").Click();
        cut.FindComponents<MudNumericField<int>>().Single(field => field.Instance.Label == "Expected Runtime (minutes)").Find("input").Change("47");
        Tab(cut, "Schedule").Click();
        await cut.InvokeAsync(() => cut.FindComponent<MudTimePicker>().Instance.TimeChanged.InvokeAsync(new TimeSpan(9, 30, 0)));
        var enabled = cut.FindComponents<MudSwitch<bool>>().Single(toggle => toggle.Instance.ValueChanged.HasDelegate && toggle.Instance.Label is null);
        await cut.InvokeAsync(() => enabled.Instance.ValueChanged.InvokeAsync(false));
        cut.WaitForAssertion(() => _transport.StepUpdates.Should().Be(1));
        Name(cut).GetAttribute("value").Should().Be("Unsaved renamed workflow");
        cut.FindComponent<MudTimePicker>().Instance.Time.Should().Be(new TimeSpan(9, 30, 0));
        cut.FindComponents<MudTabs>().First().Instance.ActivePanelIndex.Should().Be(2);
        Button(cut, "Save").Click();
        cut.WaitForAssertion(() => _transport.Update.Should().NotBeNull());
        _transport.Update!.Name.Should().Be("Unsaved renamed workflow");
        _transport.Update.FolderPath.Should().Be("/Fixture/Changed/Nested/");
        _transport.Update.ExpectedRuntimeSeconds.Should().Be(47 * 60);
        _transport.Update.Schedule!.TimeOfDay.Should().Be("09:30");
        _transport.Update.Schedule.DaysOfWeek.Should().Equal("Monday", "Wednesday", "Friday");
    }

    [Fact]
    public async Task Failed_validation_and_save_retain_input_and_require_target_reselection()
    {
        var (cut, _) = await OpenEditorAsync(jobId: 42);
        Name(cut).Change("Retained edit");
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<ClientEnvironment>>().Instance.ValueChanged.InvokeAsync(ClientEnvironment.Prod));
        Button(cut, "Save").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Please select an Agent."));
        _transport.UpdateCalls.Should().Be(0);
        await cut.InvokeAsync(() => cut.FindComponent<MudAutocomplete<ClientPresentationModel>>().Instance.ValueChanged.InvokeAsync(Client));
        _transport.FailSave = true;
        Button(cut, "Save").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Failed to save:"));
        Name(cut).GetAttribute("value").Should().Be("Retained edit");
        Button(cut, "Save").HasAttribute("disabled").Should().BeFalse();
        _transport.RunCalls.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_keep_editing_retains_draft_and_explicit_discard_closes()
    {
        var (cut, reference) = await OpenEditorAsync(jobId: 42);
        Name(cut).Change("Draft to keep");
        Button(cut, "Cancel").Click();
        _dialogs.WaitForElement(".mud-message-box");
        Button(_dialogs, "Keep editing").Click();
        Name(cut).GetAttribute("value").Should().Be("Draft to keep");
        Button(cut, "Cancel").Click();
        _dialogs.WaitForElement(".mud-message-box");
        Button(_dialogs, "Discard").Click();
        (await reference.Result)!.Canceled.Should().BeTrue();
        _transport.UpdateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Run_load_failure_stays_disabled_and_double_submission_sends_one_request()
    {
        _transport.FailParams = true;
        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<RunJobDialog>("Run",
            new DialogParameters { ["Job"] = _transport.Job });
        _dialogs.WaitForAssertion(() => Button(_dialogs, "Start Run").HasAttribute("disabled").Should().BeTrue());
        Button(_dialogs, "Start Run").Click();
        _transport.RunCalls.Should().Be(0);
        reference.Close(DialogResult.Cancel());

        _transport.FailParams = false;
        _transport.PendingRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Services.GetRequiredService<IDialogService>().ShowAsync<RunJobDialog>("Run", new DialogParameters { ["Job"] = _transport.Job });
        _dialogs.WaitForAssertion(() => Button(_dialogs, "Start Run").HasAttribute("disabled").Should().BeFalse());
        var first = _dialogs.InvokeAsync(() => Button(_dialogs, "Start Run").ClickAsync(new MouseEventArgs()));
        await _dialogs.InvokeAsync(() => Button(_dialogs, "Start Run").ClickAsync(new MouseEventArgs()));
        _transport.RunCalls.Should().Be(1);
        _transport.PendingRun.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(
            new JobRunDto(9, 42, "Nightly backup", 7, "fixture-host", "ui", JobRunStatusDto.Pending, 0, 0, null, null, null, "{}")) });
        await first;
    }

    [Fact]
    public async Task Clone_creates_new_identity_and_copies_steps_and_parameters_without_running()
    {
        var (cut, _) = await OpenEditorAsync(cloneId: 42);
        Name(cut).GetAttribute("value").Should().Be("Nightly backup (copy)");
        Button(cut, "Create and add steps").Click();
        cut.WaitForAssertion(() => _transport.StepJobId.Should().Be(43));
        _transport.ParamJobId.Should().Be(43);
        _transport.CreateCalls.Should().Be(1);
        _transport.RunCalls.Should().Be(0);
    }

    private async Task<(IRenderedComponent<JobEditDialog> Cut, IDialogReference Reference)> OpenEditorAsync(ulong? jobId = null, ulong? cloneId = null)
    {
        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<JobEditDialog>("Job",
            new DialogParameters { ["JobId"] = jobId, ["CloneFromJobId"] = cloneId });
        _dialogs.WaitForElement(".jobs-editor-fields:not([disabled])");
        return (_dialogs.FindComponent<JobEditDialog>(), reference);
    }
    private static IElement Button<T>(IRenderedComponent<T> cut, string text) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll("button").Single(button => button.TextContent.Trim() == text);
    private static IElement Name(IRenderedComponent<JobEditDialog> cut) =>
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Job Name").Find("input");
    private static IElement Tab(IRenderedComponent<JobEditDialog> cut, string text) =>
        cut.FindAll(".mud-tab").Single(tab => tab.TextContent.Trim() == text);
    private static async Task FillNewDefinitionAsync(IRenderedComponent<JobEditDialog> cut, string name)
    {
        Name(cut).Change(name);
        await cut.InvokeAsync(() => cut.FindComponent<MudAutocomplete<TenantDto>>().Instance.ValueChanged.InvokeAsync(Tenant));
        await cut.InvokeAsync(() => cut.FindComponent<MudAutocomplete<ClientPresentationModel>>().Instance.ValueChanged.InvokeAsync(Client));
    }

    private sealed class Tenants : ITenantApiService
    {
        public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TenantDto>>([Tenant]);
        public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ScriptLibrary : IScriptLibraryService
    {
        public Task<PagedResult<ScriptSummaryDto>> SearchAsync(string? search = null, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScriptSummaryDto?> GetByIdAsync(ulong id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class JobTransport : HttpMessageHandler, IHttpClientFactory
    {
        public JobDto Job { get; private set; } = new(42, "Nightly backup", "/Fixture/Backups/", null, 7, "fixture-host", 0, 0,
            Environment: ClientEnvironment.Dev, Schedule: new(true, "Africa/Johannesburg", "08:00", ["Monday", "Wednesday", "Friday"]), AgentId: AgentId);
        private List<JobStepDto> _steps = [new(1, 42, 1, JobStepTypeDto.RunCommand, "bash", "echo backup", null, null, true)];
        private List<JobParamDto> _parameters = [new(1, 42, "Path", "string", true, "/fixture/backup", null, null)];
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int RunCalls { get; private set; }
        public int ParamReads { get; private set; }
        public int StepUpdates { get; private set; }
        public ulong? UpdatedJobId { get; private set; }
        public ulong? StepJobId { get; private set; }
        public ulong? ParamJobId { get; private set; }
        public UpdateJobRequest? Update { get; private set; }
        public bool FailSave { get; set; }
        public bool FailParams { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? PendingRun { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? PendingCreate { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("https://jobs.example.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/api/v1/jobs") return Ok(new[] { Job });
                if (path == "/api/v1/jobruns/query") return Ok(new PagedJobRunsDto([], 0));
                if (path.EndsWith("/params")) { ParamReads++; return FailParams ? new(HttpStatusCode.ServiceUnavailable) : Ok(_parameters); }
                if (path.EndsWith("/steps")) return Ok(_steps);
                if (path.EndsWith("/details")) return Ok(new JobWithDetailsDto(Job, _steps, _parameters));
                if (path == "/api/v1/jobs/42") return Ok(Job);
                if (path == "/api/v2/client-presence") return Ok(new ClientPresenceListDto("v2", 1, [new("fixture", 7, AgentId, "Fixture agent", "fixture-host", "Linux", "x64", true, true, null, null, [], "fixture", "gateway", true, 1)]));
                if (path == "/api/v2/agent-telemetry") return Ok(Array.Empty<GatewayTelemetrySummary>());
            }
            if (request.Method == HttpMethod.Post)
            {
                if (path == "/api/v1/jobs")
                {
                    CreateCalls++;
                    if (FailSave) return new(HttpStatusCode.BadRequest);
                    var body = (await request.Content!.ReadFromJsonAsync<CreateJobRequest>(cancellationToken))!;
                    Job = Job with { Id = 43, Name = body.Name, FolderPath = body.FolderPath, Schedule = body.Schedule, AgentId = body.AgentId };
                    _steps = []; _parameters = [];
                    return PendingCreate is null ? Ok(Job) : await PendingCreate.Task.WaitAsync(cancellationToken);
                }
                if (path.EndsWith("/steps"))
                {
                    var body = (await request.Content!.ReadFromJsonAsync<AddJobStepRequest>(cancellationToken))!;
                    StepJobId = ulong.Parse(path.Split('/')[4]);
                    var step = new JobStepDto(2, StepJobId.Value, body.Ordinal ?? 1, body.Type, body.Runner, body.Command, body.ScriptId, body.PayloadJson, body.Enabled ?? true);
                    _steps.Add(step); return Ok(step);
                }
                if (path.EndsWith("/params"))
                {
                    var body = (await request.Content!.ReadFromJsonAsync<AddJobParamRequest>(cancellationToken))!;
                    ParamJobId = ulong.Parse(path.Split('/')[4]);
                    var param = new JobParamDto(2, ParamJobId.Value, body.Name, body.Type, body.Required, body.Default, body.Description, body.OptionsJson);
                    _parameters.Add(param); return Ok(param);
                }
                if (path == "/api/v1/jobruns/start/42")
                {
                    RunCalls++;
                    return PendingRun is null ? throw new InvalidOperationException("Execution was not expected.") : await PendingRun.Task.WaitAsync(cancellationToken);
                }
            }
            if (request.Method == HttpMethod.Put)
            {
                if (path == "/api/v1/jobs/steps/1")
                {
                    StepUpdates++;
                    var body = (await request.Content!.ReadFromJsonAsync<UpdateJobStepRequest>(cancellationToken))!;
                    _steps[0] = _steps[0] with { Enabled = body.Enabled ?? _steps[0].Enabled }; return Ok(_steps[0]);
                }
                UpdateCalls++; UpdatedJobId = ulong.Parse(path.Split('/')[4]);
                Update = (await request.Content!.ReadFromJsonAsync<UpdateJobRequest>(cancellationToken))!;
                return FailSave ? new(HttpStatusCode.BadRequest) : Ok(Job with { Name = Update.Name ?? Job.Name });
            }
            if (request.Method == HttpMethod.Delete) { DeleteCalls++; return new(HttpStatusCode.NoContent); }
            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        }
        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
