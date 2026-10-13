using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Scripts;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Web.Services.ClientTasks;
using NetRatel.Web.Services.Script;

namespace NetRatel.Web.PlaywrightTests;

/// <summary>In-memory task API for bounded workspace rendering. It never executes commands.</summary>
internal static class NewTaskWorkspaceFixture
{
    public static void AddServices(IServiceCollection services)
    {
        // Preserve the host's client directory and all other APIs. Only the task
        // service uses this isolated fake transport; other workspace fixtures
        // retain their own IHttpClientFactory registrations.
        services.AddScoped<TaskApiService>(_ => new TaskApiService(new TaskFactory()));
        services.TryAddSingleton<IScriptLibraryService, ScriptPickerFixture>();
    }

    private sealed class TaskFactory : IHttpClientFactory
    {
        private TaskDto? _task;
        public HttpClient CreateClient(string name) => new(new Handler(this)) { BaseAddress = new Uri("https://task-workspace-fixture.test") };
        private sealed class Handler(TaskFactory factory) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath == "/api/v2/tasks" && request.Method == HttpMethod.Post)
                {
                    var input = (await request.Content!.ReadFromJsonAsync<TaskCreateRequestDto>(cancellationToken))!;
                    var now = DateTimeOffset.UtcNow;
                    factory._task = new TaskDto(101, input.RequestId!, "", input.TenantId, input.Environment, input.TaskType,
                        "Completed", "Disposable fixture result", "{\"stdout\":[\"Fixture only\"]}", now.AddSeconds(-3), now.AddSeconds(-2), now,
                        0, "gateway-agent-01", "gateway-agent-01", AgentId: input.AgentId);
                    return Json(factory._task, HttpStatusCode.Created);
                }
                if (request.RequestUri.AbsolutePath is "/api/v2/tasks" or "/api/v2/tasks/recent")
                    return Json(factory._task is null ? Array.Empty<TaskDto>() : [factory._task]);
                throw new InvalidOperationException($"Unexpected task fixture request: {request.Method} {request.RequestUri.AbsolutePath}");
            }
        }
    }

    private sealed class ScriptPickerFixture : IScriptLibraryService
    {
        private static readonly ScriptSummaryDto Script = new(101, "Inspect fixture", "Disposable library fixture", "Bash", []);
        public Task<PagedResult<ScriptSummaryDto>> SearchAsync(string? search = null, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<ScriptSummaryDto>([Script], page, pageSize, 1));
        public Task<ScriptSummaryDto?> GetByIdAsync(ulong id, CancellationToken cancellationToken = default) => Task.FromResult<ScriptSummaryDto?>(id == Script.Id ? Script : null);
    }

    private static HttpResponseMessage Json<T>(T payload, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(payload) };
}
