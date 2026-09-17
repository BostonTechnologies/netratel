using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.FileSystem;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Shared.Contracts.RemoteSupport;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Services.ClientTasks;
using NetRatel.Web.Services.FileSystem;
using NetRatel.Web.Services.Requests;
using NetRatel.Web.Services.RemoteSupport;
using NetRatel.Web.Services.Telemetry;
using NetRatel.Web.Services.Tenants;
using NetRatel.Web.Services.Terminal;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class WebApiRouteVersioningTests
{
    [Fact]
    public async Task TaskApiService_uses_v2_agent_task_routes()
    {
        var factory = new RecordingHttpClientFactory();
        var service = new TaskApiService(factory);

        var agentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await service.GetRecentAsync(limit: 10, agentId: agentId);
        await service.GetAsync(42);
        await service.GetByRequestIdAsync("request-1");
        await service.GetLogsAsync(42, sinceId: 7, stream: "stdout");
        await service.GetLogsByRequestIdAsync("request-1", sinceId: 8, stream: "stderr");
        await service.CreateAsync(new TaskCreateRequestDto
        {
            AgentId = agentId,
            TaskType = TaskKinds.ExecShellCommand,
            Environment = ClientEnvironment.Dev
        });

        factory.RequestedPaths.Should().Contain($"/api/v2/tasks/recent?limit=10&agentId={agentId}");
        factory.RequestedPaths.Should().Contain("/api/v2/tasks/42");
        factory.RequestedPaths.Should().Contain("/api/v2/tasks?requestId=request-1");
        factory.RequestedPaths.Should().Contain("/api/v2/tasks/42/logs?sinceId=7&stream=stdout");
        factory.RequestedPaths.Should().Contain("/api/v2/tasks/logs?requestId=request-1&sinceId=8&stream=stderr");
        factory.RequestedPaths.Should().Contain("/api/v2/tasks");
    }

    [Fact]
    public async Task TerminalService_uses_v1_terminal_routes()
    {
        var factory = new RecordingHttpClientFactory();
        var service = new TerminalService(factory, new StubTokenService(), NullLogger<TerminalService>.Instance);
        var identity = "ABC-123";

        await service.OpenSessionAsync(identity, new OpenTerminalRequest("bash", 120, 32));
        await service.CloseAsync("session-1", "done");
        await service.SendInputAsync("session-1", "pwd");
        await service.ResizeAsync("session-1", 100, 30);
        await service.GetSessionsAsync(identity);
        await service.GetSessionAsync("session-1");

        var stream = service.StreamSessionAsync("session-1").GetAsyncEnumerator();
        try
        {
            await stream.MoveNextAsync();
        }
        finally
        {
            await stream.DisposeAsync();
        }

        factory.RequestedPaths.Should().Contain("/api/v1/clients/abc123/terminal/open");
        factory.RequestedPaths.Should().Contain("/api/v1/terminal/session-1/close");
        factory.RequestedPaths.Should().Contain("/api/v1/terminal/session-1/stdin");
        factory.RequestedPaths.Should().Contain("/api/v1/terminal/session-1/resize");
        factory.RequestedPaths.Should().Contain("/api/v1/clients/abc123/terminal/sessions");
        factory.RequestedPaths.Should().Contain("/api/v1/terminal/session-1");
        factory.RequestedPaths.Should().Contain("/api/v1/terminal/session-1/stream");
    }

    [Fact]
    public async Task Filesystem_tenant_and_client_services_use_v1_routes()
    {
        var factory = new RecordingHttpClientFactory();

        var files = new FileSystemApiService(factory);
        await files.ListDirectoryAsync("client 1", "/tmp");
        await files.ReadFileAsync("client 1", "/tmp/a.txt");
        await files.WriteFileAsync("client 1", new FileSystemWriteRequest("/tmp/a.txt", "hello"));

        var tenants = new TenantApiService(factory);
        await tenants.CreateTenantAsync(new CreateTenantRequest { Name = "Acme" });
        await tenants.UpdateTenantAsync(3, new UpdateTenantRequest { Name = "Acme Updated" });
        await tenants.DeleteTenantAsync(3);

        var clients = new ClientApiService(factory, new StubTokenProvider(), NullLogger<ClientApiService>.Instance);
        await clients.DeleteClientAsync("client 1");

        factory.RequestedPaths.Should().Contain("/api/v1/clients/client%201/filesystem?path=%2Ftmp");
        factory.RequestedPaths.Should().Contain("/api/v1/clients/client%201/filesystem/file?path=%2Ftmp%2Fa.txt");
        factory.RequestedPaths.Should().Contain("/api/v1/clients/client%201/filesystem/file");
        factory.RequestedPaths.Should().Contain("/api/v1/tenants/");
        factory.RequestedPaths.Should().Contain("/api/v1/tenants/3");
        factory.RequestedPaths.Should().Contain("/api/v1/clients/client%201");
    }

    [Fact]
    public async Task Sse_services_use_v1_stream_routes()
    {
        var factory = new RecordingHttpClientFactory();
        var tokens = new StubTokenProvider();

        await using (var clientStream = new ClientStreamService(factory, tokens))
        {
            await clientStream.StartAsync(
                "/api/v1/clients/stream",
                _ => Task.CompletedTask,
                _ => Task.CompletedTask);
            await factory.WaitForPathAsync("/api/v1/clients/stream");
        }

        await using (var telemetry = new TelemetryOverviewStreamService(factory, tokens))
        {
            await telemetry.StartAsync(_ => Task.CompletedTask);
            await factory.WaitForPathAsync("/api/v1/telemetry/stream");
        }

        await using (var clientTelemetry = new ClientTelemetryStreamService(factory, tokens))
        {
            await clientTelemetry.StartAsync("client 1", _ => Task.CompletedTask);
            await factory.WaitForPathAsync("/api/v1/clients/client%201/telemetry/stream");
        }

        await using (var requests = new RequestStreamService(factory))
        {
            await requests.StartAsync(_ => Task.CompletedTask);
            await factory.WaitForPathAsync("/api/v1/requests/stream");
        }
    }

    [Fact]
    public void Web_sources_do_not_call_known_unversioned_api_routes()
    {
        var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web"));
        var staleFragments = new[]
        {
            "api/client-tasks",
            "api/terminal",
            "api/clients/latency-stream",
            "api/clients/stream",
            "api/telemetry/stream",
            "api/clients/{",
            "api/tenants"
        };

        var offenders = Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => new { path, line, index }))
            .Where(x => staleFragments.Any(fragment => x.line.Contains(fragment, StringComparison.Ordinal)))
            .Select(x => $"{Path.GetRelativePath(webRoot, x.path)}:{x.index + 1}:{x.line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty();
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly RecordingHandler _handler = new();
        public IReadOnlyList<string> RequestedPaths => _handler.RequestedPaths;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://netratel.test")
            };

        public async Task WaitForPathAsync(string path)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!cts.IsCancellationRequested)
            {
                if (_handler.RequestedPaths.Contains(path))
                {
                    return;
                }

                await Task.Delay(25, cts.Token);
            }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<string> _requestedPaths = [];
        public IReadOnlyList<string> RequestedPaths => _requestedPaths;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requestedPaths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(CreateResponse(request));
        }

        private static HttpResponseMessage CreateResponse(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;

            // The legacy-terminal route test models a server without a gateway session.
            // TerminalService must discover that with a V2 lookup before it selects V1.
            if (request.Method == HttpMethod.Get &&
                path.StartsWith("/api/v2/gateway-terminal/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/stream", StringComparison.Ordinal))
            {
                return JsonResponse("data: {\"kind\":\"output\",\"data\":\"ok\"}\n\n", "text/event-stream");
            }

            if (path.Contains("/client-tasks/recent", StringComparison.Ordinal) ||
                path.EndsWith("/client-tasks", StringComparison.Ordinal) ||
                path.Contains("/client-tasks/logs", StringComparison.Ordinal))
            {
                return JsonResponse(Array.Empty<TaskDto>());
            }

            if (path.Contains("/client-tasks/", StringComparison.Ordinal) &&
                path.EndsWith("/logs", StringComparison.Ordinal))
            {
                return JsonResponse(Array.Empty<TaskLogDto>());
            }

            if (path.Contains("/client-tasks/", StringComparison.Ordinal))
            {
                return JsonResponse(SampleTask());
            }

            if (path.Contains("/api/v2/tasks/recent", StringComparison.Ordinal) ||
                path.EndsWith("/api/v2/tasks", StringComparison.Ordinal) ||
                path.Contains("/api/v2/tasks/logs", StringComparison.Ordinal) ||
                path.Contains("/api/v2/tasks/", StringComparison.Ordinal) && path.EndsWith("/logs", StringComparison.Ordinal))
            {
                return JsonResponse(Array.Empty<TaskDto>());
            }

            if (path.Contains("/api/v2/tasks/", StringComparison.Ordinal))
            {
                return JsonResponse(SampleTask());
            }

            if (path.EndsWith("/terminal/open", StringComparison.Ordinal))
            {
                return JsonResponse(new TerminalOpenResponse("track-1", "session-1", "opened"));
            }

            if (path.EndsWith("/terminal/sessions", StringComparison.Ordinal))
            {
                return JsonResponse(new[] { SampleTerminalSession() });
            }

            if (path.Contains("/terminal/", StringComparison.Ordinal) &&
                !path.EndsWith("/stream", StringComparison.Ordinal))
            {
                return path.EndsWith("/session-1", StringComparison.Ordinal)
                    ? JsonResponse(SampleTerminalSession())
                    : JsonResponse(new TerminalActionResponse("track-1", "ok", "session-1"));
            }

            if (path.Contains("/filesystem/file", StringComparison.Ordinal))
            {
                return path.EndsWith("/file", StringComparison.Ordinal)
                    ? JsonResponse(new FileSystemWriteResponse("request-1", "/tmp/a.txt", "ok", null))
                    : JsonResponse(new FileSystemFileResponse("request-1", "/tmp/a.txt", "ok", null, "hello", 5, null, "text/plain", "text", true));
            }

            if (path.Contains("/filesystem", StringComparison.Ordinal))
            {
                return JsonResponse(new FileSystemListResponse("request-1", "/tmp", "ok", null, Array.Empty<FileSystemEntryDto>()));
            }

            return JsonResponse(new { ok = true });
        }

        private static TaskDto SampleTask() =>
            new(
                42,
                "request-1",
                "abc",
                1,
                ClientEnvironment.Dev,
                TaskKinds.ExecShellCommand,
                "Succeeded",
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                0);

        private static TerminalSessionDto SampleTerminalSession() =>
            new(
                "session-1",
                "abc123",
                "bash",
                "open",
                true,
                1,
                1,
                null);

        private static HttpResponseMessage JsonResponse<T>(T payload) =>
            JsonResponse(JsonSerializer.Serialize(payload), "application/json");

        private static HttpResponseMessage JsonResponse(string content, string contentType) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, contentType)
            };
    }

    private sealed class StubTokenService : ITokenService
    {
        public Task<string> GetValidAccessTokenAsync() => Task.FromResult("token");
    }

    private sealed class StubTokenProvider : ITokenProvider
    {
        public Task<string?> GetBearerAsync(CancellationToken ct) => Task.FromResult<string?>("token");
    }
}
