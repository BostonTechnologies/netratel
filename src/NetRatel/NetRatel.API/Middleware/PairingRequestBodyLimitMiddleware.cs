using Microsoft.AspNetCore.Http.Features;
using NetRatel.Shared.SystemPairing;
using NetRatel.Shared.Connectivity;
using NetRatel.API.Endpoints.Auth;

namespace NetRatel.API.Middleware;

/// <summary>Bound pairing setup JSON before endpoint body binding, including chunked request streams.</summary>
public sealed class PairingRequestBodyLimitMiddleware(RequestDelegate next)
{
    public const long MaximumBytes = 65536;
    public async Task InvokeAsync(HttpContext http)
    {
        var path = http.Request.Path;
        var exact = new PathString(path.Value?.TrimEnd('/'));
        const string credentialsPath = "/api/v2/account/integration-credentials";
        var isAccountCreate = HttpMethods.IsPost(http.Request.Method) && exact.Equals(new PathString(credentialsPath), StringComparison.OrdinalIgnoreCase);
        var accountSegments = path.StartsWithSegments(credentialsPath, StringComparison.OrdinalIgnoreCase, out var accountPath)
            ? accountPath.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) : null;
        var isAccountAccessEdit = accountSegments is [_, var accountAction, ..] &&
            string.Equals(accountAction, "access-requests", StringComparison.OrdinalIgnoreCase);
        var grantsAndPolicies = isAccountCreate || isAccountAccessEdit && accountSegments!.Length == 2;
        var isReviewedAccess = isAccountAccessEdit || exact.Equals(new PathString(McpLocalDelegationEndpoints.AccessStartPath), StringComparison.OrdinalIgnoreCase) ||
            exact.Equals(new PathString("/api/v2/mcp/operator/access/requests"), StringComparison.OrdinalIgnoreCase) ||
            exact.Equals(new PathString("/api/v2/mcp/operator/access/receipt"), StringComparison.OrdinalIgnoreCase) ||
            exact.Equals(new PathString("/api/v2/mcp/operator/access/resume"), StringComparison.OrdinalIgnoreCase) ||
            exact.Equals(new PathString("/api/v2/mcp/operator/access/cancel"), StringComparison.OrdinalIgnoreCase);
        var isPairing = isAccountCreate || isReviewedAccess || path.StartsWithSegments(PairingProtocol.Route, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments(PairingProtocol.AdminRoute, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments(IntegrationPairingProtocol.Route, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments(IntegrationPairingProtocol.AccountRoute, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments(HttpHostSetupProtocol.AccountRoute, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments(HttpHostSetupProtocol.Route, StringComparison.OrdinalIgnoreCase);
        if (!isPairing || HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)) { await next(http).ConfigureAwait(false); return; }
        var maximum = grantsAndPolicies ? IntegrationCredentialLimits.MaximumAccountRequestBytes : (int)MaximumBytes;
        if (http.Request.ContentLength > maximum) { await RejectAsync(http, grantsAndPolicies, maximum); return; }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = maximum;
        var original = http.Request.Body;
        http.Request.Body = new LimitedReadStream(original, maximum);
        try
        {
            await next(http).ConfigureAwait(false);
            if (http.Response.StatusCode == StatusCodes.Status413PayloadTooLarge && !http.Response.HasStarted)
                await RejectAsync(http, grantsAndPolicies, maximum);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !http.Response.HasStarted)
        { http.Response.Clear(); await RejectAsync(http, grantsAndPolicies, maximum); }
        finally { http.Request.Body = original; }
    }
    private static Task RejectAsync(HttpContext http, bool grantsAndPolicies, int maximum)
    {
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Correlation-ID"] = http.TraceIdentifier;
        if (grantsAndPolicies)
        {
            return http.Response.WriteAsJsonAsync(IntegrationPairingFailure.ForCode("integration_request_capacity", "credential_and_policies") with
            {
                Field = "policies", Limit = maximum, CorrelationId = http.TraceIdentifier,
                RequestedCount = http.Request.ContentLength is > 0 and <= int.MaxValue ? (int)http.Request.ContentLength.Value : null
            }, http.RequestAborted);
        }
        return http.Response.WriteAsJsonAsync(new { code = "pairing-request-too-large", message = "The pairing request exceeds the supported size.", correlationId = http.TraceIdentifier }, http.RequestAborted);
    }
    private sealed class LimitedReadStream(Stream inner, int maximum) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        private int Budget(int requested) => (int)Math.Min(requested, maximum - _read + 1);
        private int Count(int bytes) { _read += bytes; if (_read > maximum) throw new BadHttpRequestException("pairing-request-limit", StatusCodes.Status413PayloadTooLarge); return bytes; }
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
