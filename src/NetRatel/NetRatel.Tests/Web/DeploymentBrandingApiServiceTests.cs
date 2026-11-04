using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Components.Forms;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Web.Services.Branding;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class DeploymentBrandingApiServiceTests
{
    [Theory]
    [InlineData(80)]
    [InlineData(262144)]
    public async Task Nonseekable_file_is_read_once_and_multipart_can_be_serialized_again(int size)
    {
        var bytes = Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
        var file = new BrowserFile(bytes);
        var factory = new ReplayFactory();
        var result = await new DeploymentBrandingApiService(factory).UploadAsync("logo-light", file);

        Assert.Equal("logo-light", result.Slot);
        Assert.Equal(1, file.OpenCalls);
        Assert.Equal(262144, file.MaximumAllowedSize);
        Assert.Equal(bytes.Length, file.BytesRead);
        Assert.Equal(1, factory.Requests);
        Assert.Equal(2, factory.Bodies.Count);
        Assert.Equal(factory.Bodies[0], factory.Bodies[1]);
        Assert.True(factory.Bodies[0].AsSpan().IndexOf(bytes) >= 0);
        Assert.Contains("Content-Type: image/png", Encoding.UTF8.GetString(factory.Bodies[0]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(262145)]
    public async Task Invalid_size_is_rejected_before_opening_or_sending(long size)
    {
        var file = new BrowserFile([], size);
        var factory = new ReplayFactory();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DeploymentBrandingApiService(factory).UploadAsync("logo-light", file));
        Assert.Equal(0, file.OpenCalls);
        Assert.Equal(0, factory.Requests);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Changed_file_length_is_rejected_without_sending(long reportedSize)
    {
        var file = new BrowserFile([1, 2, 3], reportedSize);
        var factory = new ReplayFactory();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DeploymentBrandingApiService(factory).UploadAsync("logo-light", file));
        Assert.True(file.BytesRead <= reportedSize + 1);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Cancellation_remains_cancellation_and_does_not_send()
    {
        var factory = new ReplayFactory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeploymentBrandingApiService(factory).UploadAsync("logo-light", new BrowserFile([1, 2, 3]), cancellation.Token));
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task File_read_failure_uses_the_existing_UI_error_boundary()
    {
        var factory = new ReplayFactory();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeploymentBrandingApiService(factory).UploadAsync("logo-light", new BrowserFile([1, 2, 3], readFailure: true)));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Upstream_validation_status_is_preserved()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new DeploymentBrandingApiService(new ReplayFactory(HttpStatusCode.BadRequest)).UploadAsync("logo-light", new BrowserFile([1, 2, 3])));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    private sealed class ReplayFactory(HttpStatusCode status = HttpStatusCode.OK) : IHttpClientFactory
    {
        public int Requests { get; private set; }
        public List<byte[]> Bodies { get; } = [];
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("OrchestratorApi", name);
            return new HttpClient(new Handler(this, status)) { BaseAddress = new Uri("https://api.example.test") };
        }
        private sealed class Handler(ReplayFactory owner, HttpStatusCode status) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/v2/branding/assets", request.RequestUri!.AbsolutePath);
                // Exercise the serialization done by a resend, without the
                // buffering behavior of ReadAsByteArrayAsync masking the stream.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var destination = new MemoryStream();
                    await request.Content!.CopyToAsync(destination, cancellationToken);
                    owner.Bodies.Add(destination.ToArray());
                }
                return new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(new BrandingAssetUploadResult("logo-light", "asset", "/asset", 16, 16))
                };
            }
        }
    }

    private sealed class BrowserFile(byte[] bytes, long? reportedSize = null, bool readFailure = false) : IBrowserFile
    {
        public string Name => "brand.png";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => reportedSize ?? bytes.LongLength;
        public string ContentType => "image/png";
        public int OpenCalls { get; private set; }
        public long MaximumAllowedSize { get; private set; }
        public long BytesRead { get; private set; }
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            MaximumAllowedSize = maxAllowedSize;
            return new NonseekableStream(this, bytes, readFailure);
        }
        private sealed class NonseekableStream(BrowserFile owner, byte[] bytes, bool readFailure) : Stream
        {
            private readonly MemoryStream _source = new(bytes, writable: false);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (readFailure) throw new IOException("Fixture file read failed.");
                var read = _source.Read(buffer.Span);
                owner.BytesRead += read;
                return ValueTask.FromResult(read);
            }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _source.Dispose(); base.Dispose(disposing); }
        }
    }
}
