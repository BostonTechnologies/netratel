using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Contracts;
using NetRatel.Application.Jobs;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Components.Pages.Jobs;
using NetRatel.Web.Services.Jobs;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class MultiNodeWorkspaceTests : AsyncBunitContext
{
    private readonly Transport _transport = new();
    public MultiNodeWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory>(_transport);
        Services.AddScoped<IJobApiClient, JobApiClient>();
        Services.AddSingleton<IJobRunStreamService, NoLiveStream>();
    }

    [Fact]
    public async Task Membership_selection_survives_server_pages_and_selected_view()
    {
        IReadOnlyList<Guid> selected = [];
        var cut = Render<JobNodePicker>(parameters => parameters.Add(picker => picker.TenantId, 7)
            .Add(picker => picker.ValueChanged, ids => selected = ids));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("node-a"));
        await cut.InvokeAsync(() => cut.FindComponents<MudCheckBox<bool>>().Single().Instance.ValueChanged.InvokeAsync(true));
        selected.Should().Equal(Transport.AgentA);
        Button(cut, "Next clients").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("node-b").And.NotContain("node-a"));
        await cut.InvokeAsync(() => cut.FindComponents<MudCheckBox<bool>>().Single().Instance.ValueChanged.InvokeAsync(true));
        selected.Should().BeEquivalentTo([Transport.AgentA, Transport.AgentB]);
        Button(cut, "Selected members (2)").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("node-a").And.Contain("node-b").And.Contain("Offline"));
        selected.Should().HaveCount(2);
        _transport.SearchPages.Should().Equal(1, 2);
    }

    [Fact]
    public void Missing_hostname_is_labelled_without_turning_the_registered_name_into_a_hostname()
    {
        _transport.MissingHostname = true;
        var cut = Render<JobNodePicker>(parameters => parameters.Add(picker => picker.TenantId, 7));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Hostname unavailable").And.Contain("Registered name: registered-a"));
        cut.Find(".node-picker-identity strong").TextContent.Should().Be("Hostname unavailable");
        cut.Markup.Should().Contain(Transport.AgentA.ToString());
    }

    [Fact]
    public async Task Unavailable_saved_members_stay_selected_and_can_be_explicitly_removed_when_metadata_read_fails()
    {
        _transport.DenyIdentityReads = true;
        IReadOnlyList<Guid>? changed = null;
        var cut = Render<JobNodePicker>(parameters => parameters.Add(picker => picker.TenantId, 7)
            .Add(picker => picker.Value, new[] { Transport.AgentB }).Add(picker => picker.ValueChanged, ids => changed = ids));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("node-a"));
        await cut.InvokeAsync(() => Button(cut, "Selected members (1)").Click());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(Transport.AgentB.ToString()).And.Contain("Metadata unavailable"));
        changed.Should().BeNull("failed metadata hydration must not silently remove a stored selection");
        await cut.InvokeAsync(() => Button(cut, "Remove unavailable member").Click());
        cut.WaitForAssertion(() => changed.Should().NotBeNull().And.BeEmpty());
    }

    [Fact]
    public void Literal_builder_quotes_punctuation_without_rewriting_the_existing_advanced_expression()
    {
        const string literal = "spaces + commas, !tags: slash/ quote\" and backslash\\";
        JobTargetDefinition? changed = null;
        var cut = Render<JobTargetPicker>(parameters => parameters.Add(picker => picker.TenantId, 7)
            .Add(picker => picker.Value, new() { Source = JobTargetSource.NodeFilter, Filter = "os: Linux" })
            .Add(picker => picker.ValueChanged, value => changed = value));
        cut.WaitForAssertion(() => Button(cut, "Add literal clause").HasAttribute("disabled").Should().BeTrue());
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Exact literal value").Find("input").Change(literal);
        Button(cut, "Add literal clause").Click();
        cut.WaitForAssertion(() => changed.Should().NotBeNull());
        changed!.Filter.Should().StartWith("os: Linux ");
        var parsed = JobNodeFilterParser.Parse(changed.Filter);
        parsed.Clauses.Should().HaveCount(2);
        parsed.Clauses.Single(clause => clause.Field == "tags").Alternatives.Single().Operands.Single().Value.Should().Be(literal);
    }

    [Fact]
    public async Task Group_save_persists_complete_membership_and_stale_revision_preserves_the_draft()
    {
        var cut = Render<ExecutionGroupsEditor>(parameters => parameters.Add(editor => editor.TenantId, 7));
        cut.WaitForElement("button");
        Button(cut, "New group").Click();
        cut.Find("fieldset").HasAttribute("disabled").Should().BeFalse("saved group controls must remain usable while no save is running");
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Group name").Find("input").Input("TestSystem");
        await cut.InvokeAsync(() => cut.FindComponent<JobNodePicker>().Instance.ValueChanged.InvokeAsync([Transport.AgentA, Transport.AgentB]));
        Button(cut, "Save group").Click();
        cut.WaitForAssertion(() => _transport.SavedGroup.Should().NotBeNull());
        _transport.SavedGroup!.AgentIds.Should().BeEquivalentTo([Transport.AgentA, Transport.AgentB]);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Group saved · 2 members · revision 1"));
        cut.Markup.Should().Contain("does not undo a saved group");
        _transport.ConflictGroupSave = true;
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Group name").Find("input").Input("Retained name");
        Button(cut, "Save group").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Your draft is retained"));
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Group name").Instance.Value.Should().Be("Retained name");
        cut.FindComponent<JobNodePicker>().Instance.Value.Should().BeEquivalentTo([Transport.AgentA, Transport.AgentB]);
        _transport.SavedGroup!.ExpectedRevision.Should().Be(1);
    }

    [Fact]
    public async Task Saving_a_group_preserves_tenant_drafts_and_outer_cancel_does_not_undo_the_saved_group()
    {
        var dialogs = Render<MudDialogProvider>();
        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<TenantEditorDialog>("Tenant", new DialogParameters<TenantEditorDialog>
        {
            { dialog => dialog.Tenant, new TenantDto(7, "Fixture", null, null, [], null, null, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) },
            { dialog => dialog.IsEditMode, true }
        });
        dialogs.WaitForElement("input");
        var tenant = dialogs.FindComponent<TenantEditorDialog>();
        tenant.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Tenant name").Find("input").Input("Unsaved tenant name");
        tenant.FindAll(".mud-tab").Single(tab => tab.TextContent.Trim() == "Execution groups").Click();
        tenant.WaitForElement("[data-testid='execution-groups-editor']");
        Button(tenant, "New group").Click();
        tenant.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Group name").Find("input").Input("Persisted group");
        await tenant.InvokeAsync(() => tenant.FindComponent<JobNodePicker>().Instance.ValueChanged.InvokeAsync([Transport.AgentA, Transport.AgentB]));
        Button(tenant, "Save group").Click();
        tenant.WaitForAssertion(() => tenant.Markup.Should().Contain("Group saved · 2 members"));
        _transport.SavedGroup!.Name.Should().Be("Persisted group");
        tenant.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Tenant name").Find("input").GetAttribute("value").Should().Be("Unsaved tenant name");
        Button(tenant, "Cancel tenant changes").Click();
        (await reference.Result)!.Canceled.Should().BeTrue();
        _transport.SavedGroup!.AgentIds.Should().BeEquivalentTo([Transport.AgentA, Transport.AgentB]);
    }

    [Fact]
    public async Task Runtime_launch_requires_read_only_preview_and_binds_the_exact_plan_digest()
    {
        var dialogs = Render<MudDialogProvider>();
        var target = new JobTargetDefinition { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.ClientId, "ClientId") };
        var job = new JobDto(42, "Investigate low disk space", "/Fixture/", null, 7, "", 0, 0)
        { Target = target, ExecutionPolicy = new(100, JobNodeFailurePolicy.ContinueRemaining) };
        await Services.GetRequiredService<IDialogService>().ShowAsync<RunJobDialog>("Run", new DialogParameters<RunJobDialog> { { dialog => dialog.Job, job } });
        dialogs.WaitForAssertion(() => Button(dialogs, "Start Run").HasAttribute("disabled").Should().BeTrue());
        dialogs.FindComponent<RunJobDialog>().FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "ClientId (ClientId)").Find("textarea").Input(Transport.AgentA.ToString());
        Button(dialogs, "Preview invocation targets").Click();
        dialogs.WaitForAssertion(() => _transport.PreviewedRun.Should().NotBeNull());
        _transport.RunRequests.Should().Be(0, "preview must never execute a job");
        _transport.PreviewedRun!.RuntimeTarget!.Values.Should().Equal(Transport.AgentA.ToString());
        dialogs.WaitForAssertion(() => Button(dialogs, "Start Run").HasAttribute("disabled").Should().BeFalse());
        Button(dialogs, "Start Run").Click();
        dialogs.WaitForAssertion(() => _transport.StartedRun.Should().NotBeNull());
        _transport.StartedRun!.ExpectedTargetPlanDigest.Should().Be(_transport.Plan!.ComputeDigest());
        _transport.StartedRun.RuntimeTarget!.Kind.Should().Be(JobRuntimeTargetKind.ClientId);
        _transport.RunRequests.Should().Be(1);
    }

    [Fact]
    public async Task Equal_step_ordinals_on_two_nodes_load_lazily_through_distinct_node_and_step_run_routes()
    {
        var run = new JobRunDto(9, 42, "Five-node workflow", 7, "", "owner", JobRunStatusDto.Succeeded, 0, 0, 0, 1, null, null)
        {
            IsAggregate = true, TargetCount = 2, ExecutionPolicy = new(2), EffectiveConcurrency = 2,
            NodeCounts = new(0, 0, 2, 0, 0, 0, 0, 0), Nodes = [Node(Transport.AgentA, 101, 1), Node(Transport.AgentB, 102, 2)], NodesTotalCount = 2
        };
        var cut = Render<JobRunNodes>(parameters => parameters.Add(component => component.Run, run));
        _transport.StepReads.Should().Be(0);
        var panels = cut.FindComponents<MudExpansionPanel>().ToArray();
        await cut.InvokeAsync(() => panels[0].Instance.ExpandedChanged.InvokeAsync(true));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("node-a output"));
        await cut.InvokeAsync(() => panels[1].Instance.ExpandedChanged.InvokeAsync(true));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("node-b output"));
        var outputs = cut.FindAll(".node-run-output");
        outputs.Should().HaveCount(2);
        outputs[0].TextContent.Should().Be("node-a output");
        outputs[1].TextContent.Should().Be("node-b output");
        _transport.LogPaths.Should().BeEquivalentTo([
            $"/api/v1/jobruns/9/nodes/{Transport.AgentA}/steps/1001/logs",
            $"/api/v1/jobruns/9/nodes/{Transport.AgentB}/steps/1002/logs"
        ]);
        cut.Markup.Should().Contain("Follow selected node");
        // Refreshing committed parent summaries preserves both expanded node identities.
        cut.Render(parameters => parameters.Add(component => component.Run, run with { NodeCounts = new(0, 0, 2, 0, 0, 0, 0, 0) }));
        cut.FindAll(".node-run-output").Should().HaveCount(2);
        _transport.StepReads.Should().Be(2);
    }

    [Fact]
    public async Task Forward_output_pages_keep_same_sequence_streams_and_follow_the_returned_cursor()
    {
        _transport.PagedLogs = true;
        var live = new ControlledLiveStream();
        Services.AddSingleton<IJobRunStreamService>(live);
        var run = new JobRunDto(9, 42, "Output fixture", 7, "", "owner", JobRunStatusDto.Running, 0, 0, 0, null, null, null)
        { IsAggregate = true, TargetCount = 1, NodesTotalCount = 1, Nodes = [Node(Transport.AgentA, 101, 1) with { State = JobRunNodeState.Running, CompletedAtMicros = null }] };
        var cut = Render<JobRunNodes>(parameters => parameters.Add(component => component.Run, run));
        cut.WaitForAssertion(() => cut.Find(".node-run-output").TextContent.Should().Be("stdout line\nstderr line [truncated]"));
        cut.Markup.Should().Contain("Captured log lines truncated by the byte limit: 2");
        cut.FindAll("a").Single(anchor => anchor.TextContent.Trim() == "Full output").GetAttribute("href").Should().Be("/tasks/request/task-a");
        Button(cut, "Load more output").Click();
        cut.WaitForAssertion(() => cut.Find(".node-run-output").TextContent.Should().Be("stdout line\nstderr line [truncated]\nnext page"));
        _transport.LogQueries.Last().Should().Contain("cursor=1%3A2").And.Contain("maximumBytes=65536");
        var native = run with { Id = 101, IsAggregate = false, AgentId = Transport.AgentA };
        var step = new JobStepRunDto(1001, 101, 1, JobStepRunStatusDto.Running, 1, "task-a", 0, null, null);
        await live.SendAsync(new("Live heartbeat", new(native, [step], [new(1, [new(99, "task-a", "node-a", DateTimeOffset.UnixEpoch, "stdout", "latest live tail", 99)])])));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Live heartbeat"));
        cut.Find(".node-run-output").TextContent.Should().Be("stdout line\nstderr line [truncated]\nnext page");
        cut.Markup.Should().NotContain("latest live tail");
        Button(cut, "Load more output").Click();
        cut.WaitForAssertion(() => cut.Find(".node-run-output").TextContent.Should().EndWith("next page\nfinal page"));
        _transport.LogQueries.Last().Should().Contain("cursor=2%3A3");
        cut.FindAll("button").Should().NotContain(button => button.TextContent.Trim() == "Load more output");
        _transport.NativeStepsTerminal = true;
        cut.Render(parameters => parameters.Add(component => component.Run, run with
        { Status = JobRunStatusDto.Succeeded, Nodes = [run.Nodes![0] with { State = JobRunNodeState.Succeeded, CompletedAtMicros = 1 }] }));
        cut.WaitForAssertion(() => cut.Find(".node-run-output").TextContent.Should().EndWith("next page\nfinal page"));
        _transport.LogQueries.Should().HaveCount(3, "terminal status reconciliation must not replace an inspected window with page one");
    }

    [Fact]
    public async Task Late_http_output_cannot_replace_live_output_or_its_pagination_metadata()
    {
        var live = new ControlledLiveStream();
        Services.AddSingleton<IJobRunStreamService>(live);
        var run = new JobRunDto(9, 42, "Live output fixture", 7, "", "owner", JobRunStatusDto.Running, 0, 0, 0, null, null, null)
        { IsAggregate = true, TargetCount = 1, NodesTotalCount = 1, Nodes = [Node(Transport.AgentA, 101, 1) with { State = JobRunNodeState.Running, CompletedAtMicros = null }] };
        var cut = Render<JobRunNodes>(parameters => parameters.Add(component => component.Run, run));
        cut.WaitForElement(".node-run-output");
        _transport.PendingLog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var browse = cut.InvokeAsync(() => Button(cut, "Browse output from start").ClickAsync(new()));
        cut.WaitForAssertion(() => _transport.LogQueries.Should().HaveCount(2));
        await cut.InvokeAsync(() => cut.FindComponents<MudSwitch<bool>>().Single().Instance.ValueChanged.InvokeAsync(true));
        var native = run with { Id = 101, IsAggregate = false, AgentId = Transport.AgentA };
        await live.SendAsync(new("Live newest", new(native,
            [new JobStepRunDto(1001, 101, 1, JobStepRunStatusDto.Running, 1, "task-a", 0, null, null)],
            [new(1, [new(99, "task-a", "node-a", DateTimeOffset.UnixEpoch, "stdout", "new live output", 99)])])));
        cut.WaitForAssertion(() => cut.Find(".node-run-output").TextContent.Should().Be("new live output"));
        _transport.PendingLog.SetResult(new(HttpStatusCode.OK)
        { Content = JsonContent.Create(new ExecutionLogDto("task-a", "old HTTP page", null, null, []) { HasMore = true, NextCursor = "1:2" }) });
        await browse;
        cut.Find(".node-run-output").TextContent.Should().Be("new live output");
        cut.FindAll("button").Should().NotContain(button => button.TextContent.Trim() == "Load more output");
    }

    private static JobRunNodeDto Node(Guid agent, ulong native, int ordinal) => new(9, agent, ordinal, native, JobRunNodeState.Succeeded,
        agent == Transport.AgentA ? "node-a" : "node-b", null, null, 0, 1);
    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> cut, string name) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll("button").Single(button => button.TextContent.Trim() == name);

    private sealed class NoLiveStream : IJobRunStreamService
    {
        public async IAsyncEnumerable<JobRunLiveUpdate> SubscribeAsync(ulong runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield break; }
    }
    private sealed class ControlledLiveStream : IJobRunStreamService
    {
        private readonly Channel<JobRunLiveUpdate> _updates = Channel.CreateUnbounded<JobRunLiveUpdate>();
        public ValueTask SendAsync(JobRunLiveUpdate update) => _updates.Writer.WriteAsync(update);
        public async IAsyncEnumerable<JobRunLiveUpdate> SubscribeAsync(ulong runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken)) yield return update; }
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public static readonly Guid AgentA = Guid.Parse("4f84ac18-865d-4b83-ae41-10fb77043d01");
        public static readonly Guid AgentB = Guid.Parse("4f84ac18-865d-4b83-ae41-10fb77043d02");
        private static readonly Guid GroupId = Guid.Parse("6a6677d9-d1a9-433d-84b6-033f89853601");
        public List<int> SearchPages { get; } = [];
        public List<string> LogPaths { get; } = [];
        public List<string> LogQueries { get; } = [];
        public bool PagedLogs { get; set; }
        public bool NativeStepsTerminal { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? PendingLog { get; set; }
        public ExecutionGroupWriteDto? SavedGroup { get; private set; }
        public bool ConflictGroupSave { get; set; }
        public bool MissingHostname { get; set; }
        public bool DenyIdentityReads { get; set; }
        public RunJobRequest? PreviewedRun { get; private set; }
        public RunJobRequest? StartedRun { get; private set; }
        public JobResolvedTargetPlan? Plan { get; private set; }
        public int RunRequests { get; private set; }
        public int StepReads { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://fixture.example.test") };
        private static JobNodeInventoryDto Inventory(Guid id) => new(id, id == AgentA ? "node-a" : "node-b", "Fixture node", null, ["TestSystem"], "Linux", "x64", ClientEnvironment.Dev, id == AgentA, true, null);
        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/job-targets/properties")) return Ok(JobNodeFilterParser.Properties);
            if (request.Method == HttpMethod.Get && path.EndsWith("/job-targets/nodes"))
            {
                var page = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
                SearchPages.Add(page);
                var node = Inventory(page == 1 ? AgentA : AgentB);
                if (MissingHostname && page == 1) node = node with { Hostname = "", Name = "registered-a" };
                return Ok(new JobNodePageDto([node], page, 25, 26, page == 1 ? 2 : null));
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/nodes/identities"))
            { if (DenyIdentityReads) return new(HttpStatusCode.Forbidden); var ids = await request.Content!.ReadFromJsonAsync<Guid[]>(cancellationToken); return Ok(ids!.Select(Inventory).ToArray()); }
            if (request.Method == HttpMethod.Get && path.EndsWith("/execution-groups")) return Ok(new ExecutionGroupPageDto([], 1, 25, 0, null));
            if ((request.Method == HttpMethod.Post || request.Method == HttpMethod.Put) && path.Contains("/execution-groups", StringComparison.Ordinal))
            {
                SavedGroup = await request.Content!.ReadFromJsonAsync<ExecutionGroupWriteDto>(cancellationToken);
                if (ConflictGroupSave) return new(HttpStatusCode.Conflict);
                return Ok(new ExecutionGroupDetailsDto(new(GroupId, 7, SavedGroup!.Name, SavedGroup.Description, 1, true, false, SavedGroup.AgentIds.Count, 1, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), SavedGroup.AgentIds.Select(Inventory).ToArray()));
            }
            if (path == "/api/v1/jobs/42/params") return Ok(Array.Empty<JobParamDto>());
            if (path == "/api/v1/jobruns/preview/42")
            {
                PreviewedRun = await request.Content!.ReadFromJsonAsync<RunJobRequest>(cancellationToken);
                Plan = new(1, 7, 42, new string('A', 64), new() { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.ClientId, "ClientId") }, null,
                    [new("ClientId", "Caller")], [new(AgentA, "node-a", "Fixture node", ["TestSystem"])], new(100, JobNodeFailurePolicy.ContinueRemaining));
                return Ok(new JobLaunchPreviewDto(Plan.ComputeDigest(), Plan.DefinitionDigest, Plan.Nodes.Count, Plan.Target, Plan.ExecutionPolicy, 1, Plan.GroupRevision, Plan.Bindings, Plan.Nodes, 1, 25, null));
            }
            if (path == "/api/v1/jobruns/start/42")
            {
                RunRequests++; StartedRun = await request.Content!.ReadFromJsonAsync<RunJobRequest>(cancellationToken);
                return Ok(new JobRunDto(9, 42, "Investigate low disk space", 7, "", "ui", JobRunStatusDto.Pending, 0, 0, null, null, null, "{}") { IsAggregate = true, TargetCount = 1 });
            }
            if (path.Contains("/nodes/", StringComparison.Ordinal) && path.EndsWith("/steps"))
            {
                StepReads++;
                var first = path.Contains(AgentA.ToString(), StringComparison.Ordinal);
                var running = PagedLogs && !NativeStepsTerminal;
                return Ok(new[] { new JobStepRunDto(first ? 1001UL : 1002UL, first ? 101UL : 102UL, 1, running ? JobStepRunStatusDto.Running : JobStepRunStatusDto.Succeeded, 1, first ? "task-a" : "task-b", 0, running ? null : 1, null, ScriptName: "Same ordinal") });
            }
            if (path.EndsWith("/logs"))
            {
                LogPaths.Add(path);
                LogQueries.Add(request.RequestUri.Query);
                if (PendingLog is not null) return await PendingLog.Task.WaitAsync(cancellationToken);
                if (PagedLogs)
                {
                    TaskLogDto Row(long id, long sequence, string stream, string text) => new(id, "task-a", "node-a", DateTimeOffset.UnixEpoch, stream, text, sequence);
                    return request.RequestUri.Query.Contains("cursor=2%3A3", StringComparison.OrdinalIgnoreCase)
                        ? Ok(new ExecutionLogDto("task-a", null, null, 0, [Row(4, 3, "stdout", "final page")]))
                        : request.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal)
                        ? Ok(new ExecutionLogDto("task-a", null, null, 0, [Row(3, 2, "stdout", "next page")]) { HasMore = true, NextCursor = "2:3" })
                        : Ok(new ExecutionLogDto("task-a", null, null, 0, [Row(1, 1, "stdout", "stdout line"), Row(2, 1, "stderr", "stderr line [truncated]")], Truncated: true)
                            { HasMore = true, NextCursor = "1:2", TruncatedLogIds = [2], FullOutputUrl = "/tasks/request/task-a" });
                }
                var first = path.Contains(AgentA.ToString(), StringComparison.Ordinal);
                return Ok(new ExecutionLogDto(first ? "task-a" : "task-b", first ? "node-a output" : "node-b output", null, 0, []));
            }
            throw new InvalidOperationException($"Unexpected fixture request: {request.Method} {path}");
        }
    }
}
