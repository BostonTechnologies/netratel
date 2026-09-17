using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Web.Controllers;
using NetRatel.Web.Services;
using NetRatel.Web.Services.FileSystem;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FileBrowserTransfersControllerTests
{
    private static readonly Guid AgentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");
    private const int GeneratedFileBytes = 16 * 1024 * 1024;

    [Fact]
    public async Task Download_ForwardsAGeneratedLargeFile_WithBoundedReads()
    {
        var source = new GeneratedStream(GeneratedFileBytes);
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source)
            {
                Headers = { ContentType = new("application/octet-stream") }
            }
        })))
        {
            BaseAddress = new Uri("https://api.test")
        };
        var output = new CountingWriteStream();
        var controller = CreateController(client, output);

        var result = await controller.Download(3, AgentId, "/tmp/generated.iso", Guid.NewGuid(), GeneratedFileBytes, false, CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        output.BytesWritten.Should().Be(GeneratedFileBytes);
        source.MaximumRequestedBuffer.Should().BeLessThanOrEqualTo(256 * 1024);
        controller.Response.ContentType.Should().Be("application/octet-stream");
        controller.Response.Headers.ContentDisposition.ToString().Should().Contain("generated.iso");
    }

    [Fact]
    public async Task ImagePreview_StreamsInlineWithASafeContentType()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x89, 0x50, 0x4e, 0x47])
            {
                Headers = { ContentType = new("application/octet-stream") }
            }
        })))
        {
            BaseAddress = new Uri("https://api.test")
        };
        var controller = CreateController(client, new CountingWriteStream());

        var result = await controller.Download(3, AgentId, "/tmp/photo.PNG", Guid.NewGuid(), 4, true, CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        controller.Response.ContentType.Should().Be("image/png");
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline;");
    }

    [Fact]
    public async Task Upload_ForwardsAGeneratedLargeRequestBody_WithoutStagingIt()
    {
        var input = new GeneratedStream(GeneratedFileBytes);
        long received = 0;
        var largestRead = 0;
        using var client = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            var content = request.Content ?? throw new InvalidOperationException("The raw upload body was not forwarded.");
            content.Should().BeOfType<StreamContent>();
            await using var body = await content.ReadAsStreamAsync(ct);
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var read = await body.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                received += read;
                largestRead = Math.Max(largestRead, read);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        {
            BaseAddress = new Uri("https://api.test")
        };
        var controller = CreateController(client, Stream.Null, input, GeneratedFileBytes);

        var result = await controller.Upload(3, AgentId, "/tmp/upload.iso", CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        received.Should().Be(GeneratedFileBytes);
        largestRead.Should().BeLessThanOrEqualTo(32 * 1024);
        input.MaximumRequestedBuffer.Should().BeLessThanOrEqualTo(32 * 1024);
    }

    [Fact]
    public async Task Upload_MarksASessionUnavailableConflict_ForNativeRetry()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"code\":\"session_unavailable\"}", Encoding.UTF8, "application/problem+json")
        })))
        {
            BaseAddress = new Uri("https://api.test")
        };
        var controller = CreateController(client, Stream.Null, new GeneratedStream(64), 64);

        var result = await controller.Upload(3, AgentId, "/tmp/retry.iso", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        controller.Response.Headers["X-NetRatel-File-Transfer-Retryable"].ToString().Should().Be("true");
    }

    [Fact]
    public async Task Upload_MarksAnUnexpectedGatewayCancellation_ForNativeRetry()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromException<HttpResponseMessage>(new OperationCanceledException())))
        {
            BaseAddress = new Uri("https://api.test")
        };
        var controller = CreateController(client, Stream.Null, new GeneratedStream(64), 64);

        var result = await controller.Upload(3, AgentId, "/tmp/retry.iso", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        controller.Response.Headers["X-NetRatel-File-Transfer-Retryable"].ToString().Should().Be("true");
    }

    [Fact]
    public void DownloadRegistry_IsolatedByOperator_AndCancelsTheActiveStream()
    {
        var registry = new FileBrowserDownloadTransferRegistry(TimeProvider.System);
        var transferId = Guid.NewGuid();
        var lease = registry.Begin("operator-a", transferId, "sample.iso", 1024);

        registry.Streaming(transferId);
        registry.Progress(transferId, 512);

        registry.TryGet("operator-a", transferId, out var active).Should().BeTrue();
        active.State.Should().Be("streaming");
        active.BytesTransferred.Should().Be(512);
        registry.TryGet("operator-b", transferId, out _).Should().BeFalse();

        registry.TryCancel("operator-a", transferId).Should().BeTrue();
        lease.CancellationToken.IsCancellationRequested.Should().BeTrue();
        registry.TryGet("operator-a", transferId, out var cancelled).Should().BeTrue();
        cancelled.State.Should().Be("cancelled");
    }

    private static FileBrowserTransfersController CreateController(
        HttpClient client,
        Stream responseBody,
        Stream? requestBody = null,
        long? requestLength = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = responseBody;
        context.Request.Body = requestBody ?? Stream.Null;
        context.Request.ContentLength = requestLength;

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "operator-test")], "test"));

        return new FileBrowserTransfersController(
            new StaticUploadsApiClient(client),
            new FileBrowserDownloadTransferRegistry(TimeProvider.System),
            NullLogger<FileBrowserTransfersController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class StaticUploadsApiClient(HttpClient http) : IUploadsApiClient
    {
        public HttpClient Http { get; } = http;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class GeneratedStream(long length) : Stream
    {
        private long _position;

        public int MaximumRequestedBuffer { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer)
        {
            MaximumRequestedBuffer = Math.Max(MaximumRequestedBuffer, buffer.Length);
            var read = (int)Math.Min(buffer.Length, length - _position);
            buffer[..read].Fill(0xA5);
            _position += read;
            return read;
        }
    }

    private sealed class CountingWriteStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            BytesWritten += count;
            return Task.CompletedTask;
        }
    }
}
