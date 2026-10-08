using System.Net;
using System.Net.Http.Headers;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using NetRatel.Web.Services.ServiceLinks;
using Xunit;

namespace NetRatel.Web.ComponentTests;

// These tests exercise HttpClient's actual completion/buffering behavior and the
// production administration reader. They do not provide authentication or peer proof.
public sealed class HelpdeskM2MApiClientResponseBoundsTests
{
    private const int Bound = 131072;

    [Theory]
    [InlineData("create", true)]
    [InlineData("create", false)]
    [InlineData("settings", true)]
    [InlineData("settings", false)]
    public async Task Oversized_success_responses_are_rejected_without_prefetching_the_whole_body(string action, bool declaredLength)
    {
        using var transport = new StreamingTransport(HttpStatusCode.OK, declaredLength);
        var client = new HelpdeskM2MApiClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => ExecuteAsync(client, action));

        Assert.Equal(0, transport.Content.SerializationCount);
        Assert.Equal(declaredLength ? 0 : Bound + 1, transport.Content.Body.BytesRead);
        Assert.True(transport.Content.IsDisposed);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("settings")]
    [InlineData("revoke")]
    [InlineData("resume")]
    [InlineData("authority")]
    public async Task Chunked_error_responses_stop_at_the_bound_for_each_send_family(string action)
    {
        using var transport = new StreamingTransport(HttpStatusCode.BadRequest, declaredLength: false);
        var client = new HelpdeskM2MApiClient(transport);

        var error = await Assert.ThrowsAsync<ServiceAdministrationException>(() => ExecuteAsync(client, action));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("invalid-request", error.Code);
        Assert.Equal(0, transport.Content.SerializationCount);
        Assert.Equal(Bound + 1, transport.Content.Body.BytesRead);
        Assert.True(transport.Content.IsDisposed);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("resume")]
    public async Task Commands_without_a_response_DTO_do_not_download_unused_success_bodies(string action)
    {
        using var transport = new StreamingTransport(HttpStatusCode.OK, declaredLength: false);
        var client = new HelpdeskM2MApiClient(transport);

        await ExecuteAsync(client, action);

        Assert.Equal(0, transport.Content.SerializationCount);
        Assert.Equal(0, transport.Content.Body.BytesRead);
        Assert.True(transport.Content.IsDisposed);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("settings")]
    [InlineData("authority")]
    public async Task The_existing_client_timeout_also_cancels_a_stalled_stream_after_headers(string action)
    {
        using var transport = new StreamingTransport(HttpStatusCode.OK, declaredLength: false, stallBody: true);
        var client = new HelpdeskM2MApiClient(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ExecuteAsync(client, action).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.True(transport.Content.Body.ReadCancellationObserved);
        Assert.Equal(0, transport.Content.SerializationCount);
        Assert.True(transport.Content.IsDisposed);
    }

    private static Task ExecuteAsync(HelpdeskM2MApiClient client, string action) => action switch
    {
        "create" => client.CreateAsync(new ServiceClientCreateRequest("Bounded fixture", 7, "synthetic-peer", "tenant", [])),
        "settings" => client.UpdatePublicSettingsAsync(new ServicePublicSettingsUpdate(true, "https://web.example.test", "https://api.example.test", "https://api.example.test", "fixture", 1)),
        "revoke" => client.RevokeAsync(new ServiceClientMetadata(Guid.NewGuid(), "Bounded fixture", "fixture-client", 7,
            "synthetic-peer", "tenant", [], "{}", "active", "manual", false, 1, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1))),
        "resume" => client.ActOnLinkAsync(new ServiceLinkAdminStatus("attempt", "link", 1, "prepared", "7", "peer", "tenant",
            "undecided", null, null, null!, null, false, false, false, false, false, null, false, []), "resume"),
        "authority" => client.GetAuthorityAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private sealed class StreamingTransport(HttpStatusCode status, bool declaredLength, bool stallBody = false)
        : HttpMessageHandler, IHttpClientFactory
    {
        public CountingContent Content { get; } = new(declaredLength, stallBody);
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("ServiceLinkApi", name);
            return new HttpClient(this, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.example.test/"),
                Timeout = TimeSpan.FromMilliseconds(250)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = Content });
    }

    private sealed class CountingContent : HttpContent
    {
        private readonly bool declaredLength;
        public CountingStream Body { get; }
        public int SerializationCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public CountingContent(bool declaredLength, bool stallBody)
        {
            this.declaredLength = declaredLength;
            Body = new CountingStream(stallBody);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Body.Length;
            return declaredLength;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(Body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            SerializationCount++;
            await Body.CopyToAsync(stream, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { IsDisposed = true; Body.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream(bool stall) : Stream
    {
        public int BytesRead { get; private set; }
        public bool ReadCancellationObserved { get; private set; }
        public override long Length => Bound * 8;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stall)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { ReadCancellationObserved = true; throw; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return ReadCore(buffer.Span);
        }
        private int ReadCore(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, checked((int)Length) - BytesRead);
            buffer[..count].Fill((byte)'x');
            BytesRead += count;
            return count;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
