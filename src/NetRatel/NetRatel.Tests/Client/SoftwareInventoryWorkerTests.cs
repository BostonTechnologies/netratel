using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.SoftwareInventory;
using NetRatel.Shared.Contracts.SoftwareInventory;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class SoftwareInventoryWorkerTests
{
    [Fact]
    public async Task Lost_acknowledgement_replays_durable_outbox_after_worker_restart_without_recollecting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-software-" + Guid.NewGuid().ToString("N"));
        var session = new GatewayPresenceSession(7, Guid.NewGuid(), 1, Guid.NewGuid());
        var id = Guid.NewGuid();
        var collector = new Collector();
        try
        {
            using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var firstHttp = new HttpClient(new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/work")) return Json(new SoftwareScanWork(new(), id, null));
                var chunk = await request.Content!.ReadFromJsonAsync<SoftwareChunkUpload>(cancellationToken: ct);
                chunk!.CollectionId.Should().Be(id);
                firstStop.Cancel();
                throw new HttpRequestException("Acknowledgement was lost after upload.");
            })) { BaseAddress = new Uri("https://api.example") };
            var first = new SoftwareInventoryWorker(firstHttp, directory, _ => { }, collector, TimeSpan.Zero);
            try { await first.RunAsync(session, "test-token", firstStop.Token); } catch (OperationCanceledException) { }
            var pending = Path.Combine(directory, "software-inventory", "7-" + session.AgentId.ToString("N"), "pending.json");
            File.Exists(pending).Should().BeTrue();
            collector.Calls.Should().Be(1);
            using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var uploads = 0;
            using var secondHttp = new HttpClient(new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/work"))
                {
                    File.Exists(pending).Should().BeFalse("only a matching successful acknowledgement retires the outbox");
                    secondStop.Cancel();
                    throw new OperationCanceledException(ct);
                }
                var chunk = await request.Content!.ReadFromJsonAsync<SoftwareChunkUpload>(cancellationToken: ct);
                uploads++;
                return Json(new SoftwareChunkAcknowledgement(chunk!.CollectionId, chunk.Index, true, "Complete"));
            })) { BaseAddress = new Uri("https://api.example") };
            await new SoftwareInventoryWorker(secondHttp, directory, _ => { }, collector, TimeSpan.Zero).RunAsync(session, "renewed-test-token", secondStop.Token);
            uploads.Should().Be(1);
            collector.Calls.Should().Be(1);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static HttpResponseMessage Json<T>(T data) => new(HttpStatusCode.OK) { Content = JsonContent.Create(data) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Collector : ISoftwareInventoryCollector
    {
        public int Calls { get; private set; }
        public Task<SoftwareCollection> CollectAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new SoftwareCollection([new("Example", "1", "Publisher", "x64", "example", "rpm")], true, "linux", []));
        }
    }
}
