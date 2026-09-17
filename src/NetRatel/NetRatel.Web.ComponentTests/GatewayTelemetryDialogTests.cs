using System.Net;
using System.Net.Http.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.Telemetry;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class GatewayTelemetryDialogTests : AsyncBunitContext
{
    private static readonly Guid AgentId = Guid.Parse("9a0ce4b0-7f3d-4b22-a059-d33b2b3f9ec5");

    public GatewayTelemetryDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory, TelemetryHttpClientFactory>();
        Services.AddScoped<GatewayTelemetryApiService>();
        Services.AddScoped<GatewayTelemetryLiveStreamService>();
    }

    [Fact]
    public async Task UsesOneOffGatewayFallbackWhenNoInitialSnapshotWasSupplied()
    {
        var provider = Render<MudDialogProvider>();
        await Services.GetRequiredService<IDialogService>().ShowAsync<GatewayTelemetryDialog>("Telemetry",
            new DialogParameters<GatewayTelemetryDialog>
            {
                { dialog => dialog.TenantId, 9 },
                { dialog => dialog.AgentId, AgentId },
                { dialog => dialog.HostLabel, "fallback-host" },
                { dialog => dialog.TenantLabel, "Fallback tenant" }
            },
            new DialogOptions { FullScreen = true, CloseOnEscapeKey = true, CloseButton = false });

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Fallback authority");
            provider.Markup.Should().Contain("fallback-host");
            ((TelemetryHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>()).SnapshotRequests.Should().Be(1);
        });
    }

    [Fact]
    public async Task KeepsTheCloseControlAndCadenceStateIndependentlyAddressable()
    {
        var provider = Render<MudDialogProvider>();
        await Services.GetRequiredService<IDialogService>().ShowAsync<GatewayTelemetryDialog>("Telemetry",
            new DialogParameters<GatewayTelemetryDialog>
            {
                { dialog => dialog.TenantId, 9 },
                { dialog => dialog.AgentId, AgentId },
                { dialog => dialog.HostLabel, "responsive-host" },
                { dialog => dialog.TenantLabel, "Responsive tenant" },
                { dialog => dialog.InitialSnapshot, CreateSnapshot() }
            },
            new DialogOptions { FullScreen = true, CloseOnEscapeKey = true, CloseButton = false });

        provider.WaitForAssertion(() =>
        {
            provider.Find(".telemetry-close-button [data-testid='close-telemetry-dashboard']").Should().NotBeNull();
            provider.Find(".telemetry-stream-state").TextContent.Should().Contain("Connecting");
        });
    }

    private static GatewayTelemetrySummary CreateSnapshot() => new(
        9, AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new GatewayTelemetryCpu(17, 0.3, 20), new GatewayTelemetryMemory(100, 40, 60, 40),
        [], [], new GatewayTelemetryTransportHealth(60, "0.4.131-rc.1+long-build-metadata-for-small-viewports", "Linux", DateTimeOffset.UtcNow),
        "Akka", true);

    private sealed class TelemetryHttpClientFactory : IHttpClientFactory
    {
        private readonly Handler _handler = new();
        public int SnapshotRequests => _handler.SnapshotRequests;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://netratel.test")
        };
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int SnapshotRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    // Keep the transport open while the dialog asserts its initial lifecycle state.
                    // A completed response correctly transitions the production stream to Stale,
                    // which would make this test timing-dependent.
                    Content = new StreamContent(new WaitingStream())
                });
            }

            SnapshotRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GatewayTelemetrySummary(
                    9, AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    new GatewayTelemetryCpu(17, 0.3, 20), new GatewayTelemetryMemory(100, 40, 60, 40),
                    [], [], new GatewayTelemetryTransportHealth(60, "0.4.130-rc.1", "Linux", DateTimeOffset.UtcNow),
                    "Fallback authority", true))
            });
        }
    }

    private sealed class WaitingStream : Stream
    {
        private readonly TaskCompletionSource<int> _readCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(),
                _readCancelled);
            return await _readCancelled.Task;
        }
    }
}
