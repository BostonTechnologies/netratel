using Microsoft.AspNetCore.Http.Features;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.API.Middleware;

/// <summary>Enforces the CUA JSON bound before body binding, including requests without a content length.</summary>
public sealed class ComputerUseRequestBodyLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || !IsComputerUse(http.Request.Path))
        {
            await next(http).ConfigureAwait(false);
            return;
        }
        if (http.Request.ContentLength > ComputerUseContract.MaximumRequestBytes)
        {
            await RejectAsync(http).ConfigureAwait(false);
            return;
        }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = ComputerUseContract.MaximumRequestBytes;

        var original = http.Request.Body;
        http.Request.Body = new LimitedReadStream(original);
        try { await next(http).ConfigureAwait(false); }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !http.Response.HasStarted)
        {
            http.Response.Clear();
            await RejectAsync(http).ConfigureAwait(false);
        }
        finally { http.Request.Body = original; }
    }

    private static bool IsComputerUse(PathString path)
    {
        if (!path.StartsWithSegments("/api/v2/mcp/operator/agents", StringComparison.OrdinalIgnoreCase, out var remaining) &&
            !path.StartsWithSegments("/api/v2/agents", StringComparison.OrdinalIgnoreCase, out remaining)) return false;
        var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: >= 3 } && string.Equals(segments[2], "computer-use", StringComparison.OrdinalIgnoreCase);
    }

    private static Task RejectAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        http.Response.Headers.CacheControl = "no-store";
        return http.Response.WriteAsJsonAsync(new
        {
            success = false,
            failure = new { code = "computer_use_request_too_large" },
            correlationId = http.TraceIdentifier
        }, http.RequestAborted);
    }

    private sealed class LimitedReadStream(Stream inner) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        private int Budget(int requested)
        {
            if (_read > ComputerUseContract.MaximumRequestBytes) throw TooLarge();
            return (int)Math.Min(requested, ComputerUseContract.MaximumRequestBytes - _read + 1);
        }
        private int Count(int bytes)
        {
            _read += bytes;
            if (_read > ComputerUseContract.MaximumRequestBytes) throw TooLarge();
            return bytes;
        }
        private static BadHttpRequestException TooLarge() =>
            new("The computer-use request exceeds its supported size.", StatusCodes.Status413PayloadTooLarge);
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Budget(count)));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Budget(buffer.Length)]));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Budget(buffer.Length)], cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
