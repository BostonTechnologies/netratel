using Microsoft.AspNetCore.Http.Features;
using NetRatel.API.Services.Monitoring;

namespace NetRatel.API.Middleware;

public sealed record MonitoringHttpBounds;

/// <summary>Caps monitoring JSON before binding, without changing artifact/file upload limits.</summary>
public sealed class MonitoringHttpBoundsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<MonitoringHttpBounds>() is null) { await next(http).ConfigureAwait(false); return; }
        const long maximum = MonitoringApiService.MaximumHttpBytes;
        if (http.Request.ContentLength > maximum) { await RejectAsync(http).ConfigureAwait(false); return; }
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maximum;
        var original = http.Request.Body;
        using var bounded = new BoundedBody(original, maximum);
        http.Request.Body = bounded;
        try { await next(http).ConfigureAwait(false); }
        catch (MonitoringBodyTooLargeException) when (!http.Response.HasStarted) { await RejectAsync(http).ConfigureAwait(false); }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !http.Response.HasStarted) { await RejectAsync(http).ConfigureAwait(false); }
        finally { http.Request.Body = original; }
    }

    private static Task RejectAsync(HttpContext http) => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "monitoring_payload_capacity_exceeded").ExecuteAsync(http);

    private sealed class MonitoringBodyTooLargeException : Exception { }

    private sealed class BoundedBody(Stream inner, long maximum) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, maximum + 1 - _read));
            Check(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, maximum + 1 - _read)], cancellationToken).ConfigureAwait(false);
            Check(read);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private void Check(int count) { _read += count; if (_read > maximum) throw new MonitoringBodyTooLargeException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
