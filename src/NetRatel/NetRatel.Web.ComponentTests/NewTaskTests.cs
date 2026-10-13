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

    public NewTaskTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IHttpClientFactory>(_http);
        Services.AddSingleton<ITenantApiService, TenantApi>();
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
            request.Parameters.Should().Contain("Name", "operator");
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
        public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantDto>>([new TenantDto(7, "Fixture tenant", null, null, [], null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
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
                    return Json(new ClientPresenceListDto("fixture", 1, [new ClientPresenceDto("fixture-presence", 7, AgentId, "Fixture client", "fixture-host", "Linux", "x64", fixture.Online, true, DateTimeOffset.UtcNow, "fixture", [], "fixture", "fixture", true, 1, ReportedAddress: "192.0.2.20")]));
                }
                if (request.RequestUri.AbsolutePath == "/api/v2/agent-telemetry") return Json(Array.Empty<GatewayTelemetrySummary>());
                if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/v2/tasks")
                {
                    var payload = (await request.Content!.ReadFromJsonAsync<TaskCreateRequestDto>(cancellationToken))!;
                    fixture.Posts.Add(payload);
                    return Json(new TaskDto(101, payload.RequestId!, "", 7, ClientEnvironment.Dev, TaskKinds.ExecShellCommand, "Completed", "Fixture command completed", null, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(-4), DateTimeOffset.UtcNow, 0, "Fixture client", "fixture-host", AgentId: AgentId), HttpStatusCode.Created);
                }
                return Json(Array.Empty<TaskDto>());
            }
        }
        private static HttpResponseMessage Json<T>(T payload, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(payload) };
    }
}
