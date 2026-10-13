using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Shared.Contracts.Scripts;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Web.Components.Pages.Tasks;
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Services.ClientTasks;
using NetRatel.Web.Services.Script;
using NetRatel.Web.Services.Telemetry;
using NetRatel.Web.Services.Tenants;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class NewTaskTests : AsyncBunitContext
{
    private readonly TaskFixtureHttpFactory _http = new();
    private readonly TenantApi _tenants = new();

    public NewTaskTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IHttpClientFactory>(_http);
        Services.AddSingleton<ITenantApiService>(_tenants);
        Services.AddSingleton<IScriptLibraryService, ScriptLibraryApi>();
        Services.AddScoped<ClientPresenceApiService>();
        Services.AddScoped<GatewayTelemetryApiService>();
        Services.AddScoped<ClientPresentationService>();
        Services.AddScoped<TaskApiService>();
    }

    [Fact]
    public void CompactCommand_PreservesMultilineDraftAcrossExpansionAndModeChanges()
    {
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<NewTask>();
        cut.WaitForAssertion(() => cut.Find("textarea").GetAttribute("rows").Should().Be("2"));
        const string draft = "Write-Output 'first'\nWrite-Output 'second'";
        cut.Find("textarea").Input(draft);
        cut.Find("[data-testid=task-expand-editor]").Click();
        cut.Find("textarea").GetAttribute("rows").Should().Be("12");
        CommandValue(cut).Should().Be(draft);
        cut.FindAll(".mud-chip").Single(chip => chip.TextContent.Trim() == "Library Script").Click();
        dialogs.WaitForAssertion(() => dialogs.Find(".script-picker").Should().NotBeNull());
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();
        cut.FindAll(".mud-chip").Single(chip => chip.TextContent.Trim() == "Shell Command").Click();
        CommandValue(cut).Should().Be(draft);
        cut.Find("[data-testid=task-expand-editor]").Click();
        cut.Find("textarea").GetAttribute("rows").Should().Be("2");
        CommandValue(cut).Should().Be(draft);
        cut.Markup.Should().NotContain("Task Launch Console").And.NotContain("task-hero");
    }

    [Fact]
    public void LibrarySelection_IsRetainedWhenSwitchingToCommandAndBack()
    {
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<NewTask>();
        cut.FindAll(".mud-chip").Single(chip => chip.TextContent.Trim() == "Library Script").Click();
        dialogs.WaitForAssertion(() => dialogs.Find(".script-picker tbody tr").Should().NotBeNull());
        dialogs.Find(".script-picker tbody tr").Click();
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Select").Click();
        cut.WaitForAssertion(() => cut.Find(".task-library-selection").TextContent.Should().Contain("Fixture script"));
        cut.FindAll(".mud-chip").Single(chip => chip.TextContent.Trim() == "Shell Command").Click();
        cut.FindAll(".mud-chip").Single(chip => chip.TextContent.Trim() == "Library Script").Click();
        cut.Find(".task-library-selection").TextContent.Should().Contain("Fixture script").And.Contain("Bash");
        dialogs.FindAll(".script-picker").Should().BeEmpty();
    }

    [Fact]
    public void TargetPresentation_UsesAddressAndConnectionStatusSeparately()
    {
        var cut = Render<NewTask>();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".task-client-table").TextContent.Should().Contain("Fixture client").And.Contain("fixture-host")
                .And.Contain("Linux").And.Contain("192.0.2.20").And.Contain("Online");
            cut.FindAll("th").Select(header => header.TextContent).Should().Contain("Address").And.Contain("Status");
        });
    }

    [Fact]
    public async Task Launch_PreservesPayloadAndParameters_AndShowsActualTerminalStatusWithoutDuplicatePost()
    {
        var cut = ReadyTask();
        cut.FindAll(".task-parameter-row input")[0].Input("Name");
        cut.FindAll(".task-parameter-row input")[1].Input("operator");
        await cut.Find("[data-testid=task-launch]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() =>
        {
            _http.Posts.Should().ContainSingle();
            var request = _http.Posts[0];
            request.TenantId.Should().Be(7);
            request.AgentId.Should().Be(TaskFixtureHttpFactory.AgentId);
            request.Environment.Should().Be(ClientEnvironment.Dev);
            request.TaskType.Should().Be(TaskKinds.ExecShellCommand);
            request.ShellCommand!.Command.Should().Be("echo first\necho second");
            request.Payload.Should().Be(request.ShellCommand.Command);
            request.Parameters.Should().Contain("name", "operator", "the existing task API serializes parameter keys with its camel-case dictionary policy");
            cut.Find("[data-testid=task-launch-status]").TextContent.Should().Contain("Completed").And.Contain("Fixture command completed");
            cut.Find("[data-testid=task-launch]").HasAttribute("disabled").Should().BeTrue();
            cut.Find("[data-testid=task-launch-status] a").GetAttribute("href").Should().StartWith("/tasks/request/");
        });
        await cut.Find("[data-testid=task-launch]").ClickAsync(new MouseEventArgs());
        _http.Posts.Should().ContainSingle();
    }

    [Fact]
    public async Task Launch_RevalidatesTargetAndRejectsAClientThatWentOffline()
    {
        var cut = ReadyTask();
        _http.Online = false;
        await cut.Find("[data-testid=task-launch]").ClickAsync(new MouseEventArgs());
        _http.Posts.Should().BeEmpty();
        cut.Find("[data-testid=task-launch]").HasAttribute("disabled").Should().BeTrue();
        CommandValue(cut).Should().Be("echo first\necho second");
    }

    [Fact]
    public void UnavailableDirectory_DoesNotClaimThereAreNoMatchingClients()
    {
        _http.DirectoryStatus = HttpStatusCode.Forbidden;
        var cut = Render<NewTask>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Target directory unavailable").And.NotContain("No targets match");
            cut.Find("[data-testid=task-launch]").HasAttribute("disabled").Should().BeTrue();
        });
    }

    [Fact]
    public async Task ChangingTenantFencesThePreviousRecentResponseBeforeNewTargetsFinishLoading()
    {
        var secondTenant = new TenantDto(9, "Second tenant", null, null, [], null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _tenants.Tenants = [_tenants.Tenants[0], secondTenant];
        var cut = Render<NewTask>();
        var tenant = cut.FindComponent<MudAutocomplete<TenantDto>>();
        await tenant.InvokeAsync(() => tenant.Instance.ValueChanged.InvokeAsync(_tenants.Tenants[0]));
        cut.WaitForAssertion(() => cut.Find("[aria-label='Select Fixture client']").Should().NotBeNull());
        cut.WaitForAssertion(() =>
        {
            _http.RecentRequestCount.Should().BeGreaterThanOrEqualTo(2);
            cut.Find("[aria-label='Refresh recent tasks']").HasAttribute("disabled").Should().BeFalse();
        });
        _http.HoldRecentTenant7 = true;
        var oldRefresh = cut.Find("[aria-label='Refresh recent tasks']").ClickAsync(new MouseEventArgs());
        Task changeTenant = Task.CompletedTask;
        try
        {
            cut.WaitForAssertion(() => _http.RecentStarted.Task.IsCompleted.Should().BeTrue());
            _http.HoldDirectory = true;
            changeTenant = tenant.InvokeAsync(() => tenant.Instance.ValueChanged.InvokeAsync(secondTenant));
            cut.WaitForAssertion(() => _http.DirectoryStarted.Task.IsCompleted.Should().BeTrue());
            _http.RecentResponse.SetResult(TaskFixtureHttpFactory.Json(new[]
            {
                new TaskDto(99, "old-scope-request", "", 7, ClientEnvironment.Dev, TaskKinds.ExecShellCommand,
                    "Completed", "Old tenant result", null, DateTimeOffset.UtcNow, null, null, null, ClientHostName: "old-tenant-host")
            }));
            await oldRefresh;
            cut.Markup.Should().Contain("Second tenant").And.NotContain("old-tenant-host").And.NotContain("old-scope-request");
        }
        finally
        {
            _http.HoldDirectory = false;
            _http.RecentResponse.TrySetResult(TaskFixtureHttpFactory.Json(Array.Empty<TaskDto>()));
            _http.DirectoryResponse.TrySetResult(_http.Directory());
            await oldRefresh;
            await changeTenant;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeChangeAfterAcceptedLaunchRotatesGeneratedKeysAndPreservesExplicitRequestIds(bool explicitRequestId)
    {
        _http.AcceptWithoutTaskBody = true;
        var cut = ReadyTask();
        if (explicitRequestId)
        {
            cut.Find(".task-advanced .mud-expand-panel-header").Click();
            cut.Find(".task-advanced input").Input("operator-request-id");
        }
        await cut.Find("[data-testid=task-launch]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => _http.Posts.Should().ContainSingle());
        var firstRequestId = _http.Posts[0].RequestId;
        firstRequestId.Should().NotBeNullOrWhiteSpace();
        var environment = cut.FindComponent<MudSelect<ClientEnvironment>>();
        await environment.InvokeAsync(() => environment.Instance.ValueChanged.InvokeAsync(ClientEnvironment.Prod));
        cut.WaitForAssertion(() => cut.Find("[aria-label='Select Fixture client']").Should().NotBeNull());
        cut.Find("[aria-label='Select Fixture client']").Click();
        CommandValue(cut).Should().Be("echo first\necho second");
        await cut.Find("[data-testid=task-launch]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => _http.Posts.Should().HaveCount(2));
        _http.Posts[1].Environment.Should().Be(ClientEnvironment.Prod);
        if (explicitRequestId)
            _http.Posts[1].RequestId.Should().Be("operator-request-id").And.Be(firstRequestId);
        else
            _http.Posts[1].RequestId.Should().NotBe(firstRequestId);
    }

    [Fact]
    public void EnterInCommand_DoesNotSubmit()
    {
        var cut = ReadyTask();
        cut.Find("textarea").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        _http.Posts.Should().BeEmpty();
    }

    private static string CommandValue(IRenderedComponent<NewTask> cut) =>
        cut.Find("textarea").GetAttribute("value") ?? cut.Find("textarea").TextContent;

    private IRenderedComponent<NewTask> ReadyTask()
    {
        var cut = Render<NewTask>();
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Select Fixture client']").Should().NotBeNull());
        cut.Find("button[aria-label='Select Fixture client']").Click();
        cut.Find("textarea").Input("echo first\necho second");
        cut.WaitForAssertion(() => cut.Find("[data-testid=task-launch]").HasAttribute("disabled").Should().BeFalse());
        return cut;
    }

    private sealed class TenantApi : ITenantApiService
    {
        public IReadOnlyList<TenantDto> Tenants { get; set; } = [new TenantDto(7, "Fixture tenant", null, null, [], null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)];
        public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Tenants);
        public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ScriptLibraryApi : IScriptLibraryService
    {
        public Task<PagedResult<ScriptSummaryDto>> SearchAsync(string? search = null, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<ScriptSummaryDto>([new(101, "Fixture script", null, "Bash", [])], page, pageSize, 1));
        public Task<ScriptSummaryDto?> GetByIdAsync(ulong id, CancellationToken cancellationToken = default) => Task.FromResult<ScriptSummaryDto?>(new(101, "Fixture script", null, "Bash", []));
    }

    private sealed class TaskFixtureHttpFactory : IHttpClientFactory
    {
        public static readonly Guid AgentId = Guid.Parse("fecb99c9-1167-43ef-9daa-c63793e30d79");
        public bool Online { get; set; } = true;
        public bool AcceptWithoutTaskBody { get; set; }
        public bool HoldRecentTenant7 { get; set; }
        public bool HoldDirectory { get; set; }
        public int RecentRequestCount { get; private set; }
        public TaskCompletionSource<bool> RecentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DirectoryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> RecentResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> DirectoryResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpStatusCode DirectoryStatus { get; set; } = HttpStatusCode.OK;
        public List<TaskCreateRequestDto> Posts { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this)) { BaseAddress = new Uri("https://task-fixture.test") };

        private sealed class Handler(TaskFixtureHttpFactory fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath == "/api/v2/client-presence")
                {
                    if (fixture.DirectoryStatus != HttpStatusCode.OK) return new HttpResponseMessage(fixture.DirectoryStatus);
                    if (fixture.HoldDirectory)
                    {
                        fixture.DirectoryStarted.TrySetResult(true);
                        return await fixture.DirectoryResponse.Task;
                    }
                    return fixture.Directory();
                }
                if (request.RequestUri.AbsolutePath == "/api/v2/agent-telemetry") return Json(Array.Empty<GatewayTelemetrySummary>());
                if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/v2/tasks")
                {
                    var payload = (await request.Content!.ReadFromJsonAsync<TaskCreateRequestDto>(cancellationToken))!;
                    fixture.Posts.Add(payload);
                    if (fixture.AcceptWithoutTaskBody) return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(string.Empty) };
                    return Json(new TaskDto(101, payload.RequestId!, "", 7, ClientEnvironment.Dev, TaskKinds.ExecShellCommand, "Completed", "Fixture command completed", null, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(-4), DateTimeOffset.UtcNow, 0, "Fixture client", "fixture-host", AgentId: AgentId), HttpStatusCode.Created);
                }
                if (request.RequestUri.AbsolutePath == "/api/v2/tasks/recent") fixture.RecentRequestCount++;
                if (fixture.HoldRecentTenant7 && request.RequestUri.AbsolutePath == "/api/v2/tasks/recent" && request.RequestUri.Query.Contains("tenantId=7"))
                {
                    fixture.RecentStarted.TrySetResult(true);
                    // Deliberately ignore cancellation to exercise late-response fencing.
                    return await fixture.RecentResponse.Task;
                }
                return Json(Array.Empty<TaskDto>());
            }
        }
        public HttpResponseMessage Directory() => Json(new ClientPresenceListDto("fixture", 1,
            [new ClientPresenceDto("fixture-presence", 7, AgentId, "Fixture client", "fixture-host", "Linux", "x64", Online, true, DateTimeOffset.UtcNow, "fixture", [], "fixture", "fixture", true, 1, ReportedAddress: "192.0.2.20")]));
        public static HttpResponseMessage Json<T>(T payload, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(payload) };
    }
}
