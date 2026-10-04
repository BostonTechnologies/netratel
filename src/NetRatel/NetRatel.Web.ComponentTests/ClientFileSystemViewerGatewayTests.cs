using System.Net;
using System.Text.Json;
using BlazorDownloadFile;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.FileSystem;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.FileSystem;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientFileSystemViewerGatewayTests : AsyncBunitContext
{
    private static readonly Guid AgentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");

    public ClientFileSystemViewerGatewayTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddBlazorDownloadFile();
        Services.AddSingleton<IHttpClientFactory, GatewayFileHttpClientFactory>();
        Services.AddScoped<FileSystemApiService>();
        Services.AddScoped<GatewayFileSystemApiService>();
    }

    [Fact]
    public async Task Uses_A_UnifiedExplorerLayout_Through_The_V2_Gateway()
    {
        var factory = Services.GetRequiredService<IHttpClientFactory>();
        var dialogProvider = Render<MudBlazor.MudDialogProvider>();
        await Services.GetRequiredService<MudBlazor.IDialogService>()
            .ShowAsync<ClientFileSystemViewer>("File Browser", new MudBlazor.DialogParameters<ClientFileSystemViewer>
            {
                { component => component.TenantId, 3 },
                { component => component.AgentId, AgentId },
                { component => component.HostLabel, "gateway-agent-01" }
            });

        dialogProvider.WaitForAssertion(() =>
        {
            dialogProvider.FindAll("[data-testid='file-explorer']").Should().ContainSingle();
            dialogProvider.FindAll("[data-testid='file-explorer-list']").Should().ContainSingle();
            dialogProvider.FindAll(".fs-pane").Should().BeEmpty();
            dialogProvider.Markup.Should().Contain("Filter files and folders...");
            dialogProvider.Markup.Should().Contain("2 items");
            ((GatewayFileHttpClientFactory)factory).RequestedPaths.Should().ContainSingle()
                .Which.Should().Be($"/api/v2/agents/3/{AgentId:D}/filesystem");
        });
    }

    [Fact]
    public async Task Uses_The_StreamingHttpClient_For_A_GatewayFileRead()
    {
        var factory = Services.GetRequiredService<IHttpClientFactory>();
        var files = Services.GetRequiredService<GatewayFileSystemApiService>();

        using var response = await files.DownloadAsync(3, AgentId, "/etc/hosts");

        response.IsSuccessStatusCode.Should().BeTrue();
        ((GatewayFileHttpClientFactory)factory).RequestedClientNames.Should().Contain("OrchestratorApiStreaming");
    }

    [Fact]
    public async Task Normalizes_A_StaleWindowsDriveRoot_BeforeListing()
    {
        var factory = Services.GetRequiredService<IHttpClientFactory>();
        var dialogProvider = Render<MudBlazor.MudDialogProvider>();
        await Services.GetRequiredService<MudBlazor.IDialogService>()
            .ShowAsync<ClientFileSystemViewer>("File Browser", new MudBlazor.DialogParameters<ClientFileSystemViewer>
            {
                { component => component.TenantId, 3 },
                { component => component.AgentId, AgentId },
                { component => component.InitialPath, "C:" }
            });

        dialogProvider.WaitForAssertion(() =>
        {
            dialogProvider.Find("[data-testid='file-explorer-location']").TextContent.Should().Be("C:\\");
            ((GatewayFileHttpClientFactory)factory).RequestedUris.Should().ContainSingle()
                .Which.Should().Be($"/api/v2/agents/3/{AgentId:D}/filesystem?path=C%3A%5C");
        });
    }

    [Fact]
    public async Task Filters_The_UnifiedExplorerEntries()
    {
        var dialogProvider = Render<MudBlazor.MudDialogProvider>();
        await Services.GetRequiredService<MudBlazor.IDialogService>()
            .ShowAsync<ClientFileSystemViewer>("File Browser", new MudBlazor.DialogParameters<ClientFileSystemViewer>
            {
                { component => component.TenantId, 3 },
                { component => component.AgentId, AgentId }
            });

        dialogProvider.WaitForAssertion(() =>
            dialogProvider.Find("[data-testid='file-explorer-filter']").Should().NotBeNull());

        dialogProvider.Find("[data-testid='file-explorer-filter']").Input("hosts");

        dialogProvider.WaitForAssertion(() =>
        {
            dialogProvider.Find("[data-testid='file-explorer-count']").TextContent.Should().Be("1 item");
            dialogProvider.FindAll("[data-testid='file-explorer-entry']").Should().ContainSingle()
                .Which.TextContent.Should().Contain("hosts");
        });
    }

    [Fact]
    public async Task Refreshes_The_EnteredRemotePath_WhenEnterIsPressed()
    {
        var factory = Services.GetRequiredService<IHttpClientFactory>();
        var dialogProvider = Render<MudBlazor.MudDialogProvider>();
        await Services.GetRequiredService<MudBlazor.IDialogService>()
            .ShowAsync<ClientFileSystemViewer>("File Browser", new MudBlazor.DialogParameters<ClientFileSystemViewer>
            {
                { component => component.TenantId, 3 },
                { component => component.AgentId, AgentId }
            });

        dialogProvider.WaitForAssertion(() =>
            ((GatewayFileHttpClientFactory)factory).RequestedUris.Should().ContainSingle());

        var path = dialogProvider.Find("[data-testid='file-explorer-path']");
        path.Input("/etc");
        path.KeyDown(Key.Enter);

        dialogProvider.WaitForAssertion(() =>
        {
            ((GatewayFileHttpClientFactory)factory).RequestedUris.Should().HaveCount(2);
            ((GatewayFileHttpClientFactory)factory).RequestedUris.Last().Should().Be($"/api/v2/agents/3/{AgentId:D}/filesystem?path=%2Fetc");
            dialogProvider.Find("[data-testid='file-explorer-location']").TextContent.Should().Be("/etc");
        });
    }

    [Fact]
    public async Task InterruptedFolderQuery_ShowsReconnecting_AndRetriesAfterAuthoritativeFileReadiness()
    {
        var factory = (GatewayFileHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>();
        var readiness = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryCount = 0;
        factory.Respond = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/client-presence/")
            {
                return readiness.Task.WaitAsync(token);
            }
            if (Interlocked.Increment(ref queryCount) == 1)
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.Conflict, new { code = "session_unavailable" }));
            }
            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                new GatewayFileSystemListResponse(3, AgentId, "/", "akka",
                    [new GatewayFileSystemEntryDto("hosts", "/hosts", false, 256)])));
        };
        var provider = Render<MudBlazor.MudDialogProvider>();
        var showing = Services.GetRequiredService<MudBlazor.IDialogService>()
            .ShowAsync<ClientFileSystemViewer>("File Browser", new MudBlazor.DialogParameters<ClientFileSystemViewer>
            {
                { component => component.TenantId, 3 },
                { component => component.AgentId, AgentId }
            });
        provider.WaitForAssertion(() =>
        {
            provider.Find("[data-testid='file-explorer-loading']").TextContent.Should().Contain("File gateway reconnecting");
            queryCount.Should().Be(1);
        });
        readiness.SetResult(JsonResponse(HttpStatusCode.OK, new ClientPresenceListDto("akka", 2,
        [new ClientPresenceDto("gateway:3:agent", 3, AgentId, "agent", null, null, null, true, true,
            DateTimeOffset.UtcNow, "test", ["file-gateway"], "gateway", "akka", true, 2,
            File: new GatewayFileCapabilityDto(true, true, true, true, "test", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null))])));
        await showing;
        provider.WaitForAssertion(() =>
        {
            provider.FindAll("[data-testid='file-explorer-loading']").Should().BeEmpty();
            provider.Find("[data-testid='file-explorer-list']").TextContent.Should().Contain("hosts");
            queryCount.Should().Be(2);
            factory.RequestedUris.Should().Contain($"/api/v2/client-presence/?tenantId=3&search={AgentId:D}&limit=1");
        });
    }

    [Fact]
    public async Task InterruptedUpload_IsReportedWithoutAutomaticMutationReplay()
    {
        var factory = (GatewayFileHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>();
        var attempts = 0;
        factory.Respond = (request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Put);
            Interlocked.Increment(ref attempts);
            return Task.FromResult(JsonResponse(HttpStatusCode.Conflict, new { code = "session_unavailable" }));
        };
        using var content = new MemoryStream([1, 2, 3]);
        var upload = () => Services.GetRequiredService<GatewayFileSystemApiService>().UploadAsync(3, AgentId, "/data/target", content);
        await upload.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };

    private sealed class GatewayFileHttpClientFactory : IHttpClientFactory
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
        public List<string> RequestedPaths { get; } = [];
        public List<string> RequestedUris { get; } = [];
        public List<string> RequestedClientNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedClientNames.Add(name);
            return new(new Handler(RequestedPaths, RequestedUris, () => Respond))
            {
                BaseAddress = new Uri("https://netratel.test")
            };
        }
    }

    private sealed class Handler(List<string> requestedPaths, List<string> requestedUris,
        Func<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>?> responder) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            requestedUris.Add(request.RequestUri.PathAndQuery);
            if (responder() is { } respond)
            {
                return respond(request, cancellationToken);
            }
            var payload = new GatewayFileSystemListResponse(3, AgentId, "/", "akka-dev-canary",
                [new GatewayFileSystemEntryDto("etc", "/etc", true, 0), new GatewayFileSystemEntryDto("hosts", "/hosts", false, 256)]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions))
            });
        }
    }
}
