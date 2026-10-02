using Microsoft.AspNetCore.Http.Features;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.API.Middleware;

/// <summary>Bound graph/edit/preview JSON before endpoint body binding, including chunked request streams.</summary>
public sealed class FlowRequestBodyLimitMiddleware(RequestDelegate next)
{
    public const long MaximumBytes = FlowLimits.MaximumGraphBytes + FlowLimits.MaximumEventBytes + 4096;
    public async Task InvokeAsync(HttpContext http)
    {
        var path = http.Request.Path.Value;
        var isFlow = path is not null && (path.StartsWith("/api/v1/flows/", StringComparison.Ordinal) ||
            path.StartsWith("/api/v1/tenants/", StringComparison.Ordinal) && path.Contains("/flows", StringComparison.Ordinal));
        if (!isFlow || HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)) { await next(http).ConfigureAwait(false); return; }
        if (http.Request.ContentLength > MaximumBytes) { http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = MaximumBytes;
        var original = http.Request.Body; http.Request.Body = new LimitedReadStream(original);
        try { await next(http).ConfigureAwait(false); }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !http.Response.HasStarted)
        { http.Response.Clear(); http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; }
        finally { http.Request.Body = original; }
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
        private int Count(int bytes) { _read += bytes; if (_read > MaximumBytes) throw new BadHttpRequestException("flow-request-limit", StatusCodes.Status413PayloadTooLarge); return bytes; }
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
