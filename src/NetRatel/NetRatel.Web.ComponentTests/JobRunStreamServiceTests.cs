using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Web.Services.Jobs;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class JobRunStreamServiceTests
{
    [Fact]
    public async Task Authenticated_subscription_ignores_other_runs_stops_on_unknown_and_releases_response()
    {
        var body = Snapshot(99, JobRunStatusDto.Running) + Snapshot(9, JobRunStatusDto.Running)
            + "data: {\"kind\":\"heartbeat\",\"connected\":false}\n\n" + Snapshot(9, JobRunStatusDto.Unknown);
        var transport = new Transport(body);
        var updates = new List<JobRunLiveUpdate>();
        await foreach (var update in new JobRunStreamService(transport).SubscribeAsync(9, CancellationToken.None)) updates.Add(update);
        updates.Where(update => update.Snapshot is not null).Should().HaveCount(2)
            .And.OnlyContain(update => update.Snapshot!.Run.Id == 9);
        updates.Should().Contain(update => update.State == "Reconnecting");
        updates.Last().Snapshot!.Run.Status.Should().Be(JobRunStatusDto.Unknown);
        transport.ClientName.Should().Be("OrchestratorApiStreaming");
        transport.RequestPath.Should().Be("/api/v1/jobruns/9/stream");
        transport.Accept.Should().Contain("text/event-stream");
        transport.Requests.Should().Be(1);
        transport.ContentDisposed.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Denied_subscription_ends_without_retrying_or_displaying_a_snapshot(HttpStatusCode status)
    {
        var transport = new Transport(string.Empty, status);
        var updates = new List<JobRunLiveUpdate>();
        await foreach (var update in new JobRunStreamService(transport).SubscribeAsync(9, CancellationToken.None)) updates.Add(update);
        updates.Last().State.Should().Be("Live updates unavailable.");
        updates.Should().OnlyContain(update => update.Snapshot == null);
        transport.Requests.Should().Be(1);
        transport.ContentDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Closing_viewer_disposes_owned_stream_without_waiting_for_the_next_event()
    {
        var transport = new Transport(Snapshot(9, JobRunStatusDto.Running));
        var subscription = new JobRunStreamService(transport).SubscribeAsync(9, CancellationToken.None).GetAsyncEnumerator();
        await subscription.MoveNextAsync(); // Connecting.
        await subscription.MoveNextAsync(); // Snapshot: enumeration still owns the open response.
        subscription.Current.Snapshot.Should().NotBeNull();
        transport.ContentDisposed.Should().BeFalse();
        await subscription.DisposeAsync();
        transport.ContentDisposed.Should().BeTrue();
        transport.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Socket_end_reconnects_with_a_replacement_snapshot_and_releases_both_connections()
    {
        var transport = new Transport(Snapshot(9, JobRunStatusDto.Running), secondBody: Snapshot(9, JobRunStatusDto.Succeeded));
        var updates = new List<JobRunLiveUpdate>();
        await foreach (var update in new JobRunStreamService(transport).SubscribeAsync(9, CancellationToken.None)) updates.Add(update);
        updates.Should().Contain(update => update.State == "Reconnecting");
        updates.Last().Snapshot!.Run.Status.Should().Be(JobRunStatusDto.Succeeded);
        transport.Requests.Should().Be(2);
        transport.DisposedContents.Should().Be(2);
    }

    private static string Snapshot(ulong id, JobRunStatusDto status)
    {
        var run = new JobRunDto(id, 42, "Backup", 7, "agent", "owner", status, 1, 100, 100, null, null, null);
        return "data: " + JsonSerializer.Serialize(new { kind = "snapshot", id = "event", connected = true,
            snapshot = new JobRunStreamSnapshotDto(run, [], []) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n\n";
    }

    private sealed class Transport(string body, HttpStatusCode status = HttpStatusCode.OK, string? secondBody = null)
        : HttpMessageHandler, IHttpClientFactory
    {
        public string? ClientName { get; private set; }
        public string? RequestPath { get; private set; }
        public string Accept { get; private set; } = string.Empty;
        public int Requests { get; private set; }
        public int DisposedContents { get; private set; }
        public bool ContentDisposed => DisposedContents == Requests && Requests > 0;
        public HttpClient CreateClient(string name)
        {
            ClientName = name;
            return new(this, false) { BaseAddress = new Uri("http://fixture") };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            RequestPath = request.RequestUri!.AbsolutePath;
            Accept = string.Join(',', request.Headers.Accept);
            var payload = Requests == 1 ? body : secondBody ?? body;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new TrackedContent(payload, () => DisposedContents++) });
        }
    }

    private sealed class TrackedContent(string body, Action disposed) : StringContent(body, Encoding.UTF8, "text/event-stream")
    {
        private bool _disposed;
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _disposed = true; disposed(); }
            base.Dispose(disposing);
        }
    }
}
