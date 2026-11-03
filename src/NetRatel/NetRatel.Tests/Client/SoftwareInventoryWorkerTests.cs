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
    public async Task Legacy_outbox_and_acknowledgement_preserve_Web_read_defaults_and_upload_bytes()
    {
        const string upload = """
            {"collectionId":"12345678-90ab-cdef-8123-456789abcdef","index":0,"count":1,"complete":true,"os":"linux","detectedAtUtc":"2026-11-03T12:34:56.1234567+00:00","errors":[],"payload":"H4sIAAAAAAAEA4uOBQApu0wNAgAAAA=="}
            """;
        const string legacy = """
            {"CHUNKS":[{"COLLECTIONID":"12345678-90ab-cdef-8123-456789abcdef","INDEX":"0","COUNT":"1","COMPLETE":true,"OS":"linux","DETECTEDATUTC":"2026-11-03T12:34:56.1234567+00:00","ERRORS":[],"PAYLOAD":"H4sIAAAAAAAEA4uOBQApu0wNAgAAAA==","futureField":null}],"futureField":true}
            """;
        var directory = Path.Combine(Path.GetTempPath(), "netratel-software-" + Guid.NewGuid().ToString("N"));
        var session = new GatewayPresenceSession(7, Guid.NewGuid(), 1, Guid.NewGuid());
        var pending = Path.Combine(directory, "software-inventory", "7-" + session.AgentId.ToString("N"), "pending.json");
        var collector = new Collector();
        var uploads = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
            await File.WriteAllTextAsync(pending, legacy);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var http = new HttpClient(new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/work"))
                {
                    File.Exists(pending).Should().BeFalse("a matching acknowledgement retires the original outbox");
                    stop.Cancel();
                    throw new OperationCanceledException(ct);
                }
                uploads++;
                (await request.Content!.ReadAsStringAsync(ct)).Should().Be(upload);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"COLLECTIONID":"12345678-90ab-cdef-8123-456789abcdef","INDEX":"0","ACCEPTED":true,"STATUS":"Complete","futureField":null}
                        """, System.Text.Encoding.UTF8, "application/json")
                };
            })) { BaseAddress = new Uri("https://api.example") };
            await new SoftwareInventoryWorker(http, directory, _ => { }, collector, TimeSpan.Zero)
                .RunAsync(session, "test-token", stop.Token);
            uploads.Should().Be(1);
            collector.Calls.Should().Be(0);
            File.Exists(pending).Should().BeFalse();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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
