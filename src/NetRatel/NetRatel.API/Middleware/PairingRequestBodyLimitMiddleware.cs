using Microsoft.AspNetCore.Http.Features;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.API.Middleware;

/// <summary>Bound pairing setup JSON before endpoint body binding, including chunked request streams.</summary>
public sealed class PairingRequestBodyLimitMiddleware(RequestDelegate next)
{
    public const long MaximumBytes = 65536;
    public async Task InvokeAsync(HttpContext http)
    {
        var path = http.Request.Path.Value;
        var isPairing = path is not null && (path.StartsWith(PairingProtocol.Route, StringComparison.Ordinal) || path.StartsWith(PairingProtocol.AdminRoute, StringComparison.Ordinal));
        if (!isPairing || HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)) { await next(http).ConfigureAwait(false); return; }
        if (http.Request.ContentLength > MaximumBytes) { await RejectAsync(http); return; }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = MaximumBytes;
        var original = http.Request.Body; http.Request.Body = new LimitedReadStream(original);
        try { await next(http).ConfigureAwait(false); }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !http.Response.HasStarted)
        { http.Response.Clear(); await RejectAsync(http); }
        finally { http.Request.Body = original; }
    }
    private static Task RejectAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; http.Response.Headers.CacheControl = "no-store";
        return http.Response.WriteAsJsonAsync(new { code = "pairing-request-too-large", message = "The pairing request exceeds the supported size.", correlationId = Guid.NewGuid().ToString("N") }, http.RequestAborted);
    }
    private sealed class LimitedReadStream(Stream inner) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        private int Budget(int requested) => (int)Math.Min(requested, MaximumBytes - _read + 1);
        private int Count(int bytes) { _read += bytes; if (_read > MaximumBytes) throw new BadHttpRequestException("pairing-request-limit", StatusCodes.Status413PayloadTooLarge); return bytes; }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Budget(count)));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Budget(buffer.Length)]));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Count(await inner.ReadAsync(buffer[..Budget(buffer.Length)], cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
