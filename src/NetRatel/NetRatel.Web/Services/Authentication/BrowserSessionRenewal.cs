using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.JSInterop;

namespace NetRatel.Web.Services.Authentication;

/// <summary>
/// Transfers an access token from a cookie-authenticated HTTP request to its waiting
/// circuit. Refresh tokens remain in the encrypted cookie and never enter this broker.
/// </summary>
public sealed class BrowserSessionRenewalBroker
{
    private readonly ConcurrentDictionary<string, PendingRenewal> _pending = new(StringComparer.Ordinal);

    internal string Begin(ClaimsPrincipal principal)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _pending.Where(entry => entry.Value.ExpiresAt <= now))
        {
            _pending.TryRemove(entry.Key, out _);
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _pending[id] = new PendingRenewal(principal, now.AddMinutes(1));
        return id;
    }

    internal bool Complete(string id, ClaimsPrincipal principal, string accessToken, DateTimeOffset expiresAt)
    {
        if (!_pending.TryGetValue(id, out var pending)
            || pending.ExpiresAt <= DateTimeOffset.UtcNow
            || !OperatorApiCredentialState.SamePrincipal(pending.Principal, principal)
            || expiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref pending.Result, new RenewedAccessToken(accessToken, expiresAt), null) is null;
    }

    internal RenewedAccessToken? Take(string id)
    {
        if (_pending.TryRemove(id, out var pending)
            && pending.ExpiresAt > DateTimeOffset.UtcNow
            && pending.Result is { } result)
        {
            return result;
        }

        return null;
    }

    private sealed record PendingRenewal(ClaimsPrincipal Principal, DateTimeOffset ExpiresAt)
    {
        public RenewedAccessToken? Result;
    }
}

internal sealed record RenewedAccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Renews the browser cookie on a fresh HTTP response without navigating or replaying an API request.</summary>
public sealed class BrowserSessionRenewal(
    IJSRuntime js,
    BrowserSessionRenewalBroker broker,
    OperatorApiCredentialState credentials,
    ILogger<BrowserSessionRenewal> logger)
{
    internal async Task<RenewedAccessToken?> RenewAsync(ClaimsPrincipal principal, long generation, CancellationToken cancellationToken)
    {
        if (!credentials.IsBoundTo(principal, generation))
        {
            return null;
        }

        var id = broker.Begin(principal);
        try
        {
            // Only an opaque, one-use correlation crosses JS. Set-Cookie and the
            // refresh grant both belong to the authenticated HTTP request below.
            var succeeded = await js.InvokeAsync<bool>("netratelSessionRenewal.renew", cancellationToken, id);
            var result = broker.Take(id);
            return succeeded && credentials.IsBoundTo(principal, generation) ? result : null;
        }
        catch (JSException exception)
        {
            logger.LogWarning(exception, "Browser session renewal did not complete; requiring reauthentication.");
            return null;
        }
        catch (InvalidOperationException exception)
        {
            // Static prerendering cannot use JS. It must not turn a missing
            // interactive browser into a server-side token-refresh fallback.
            logger.LogDebug(exception, "Browser session renewal is unavailable outside an interactive circuit.");
            return null;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Browser session renewal timed out; requiring reauthentication.");
            return null;
        }
        finally
        {
            _ = broker.Take(id);
        }
    }
}
