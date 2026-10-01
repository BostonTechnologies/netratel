using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace NetRatel.Web.Services.Authentication;

public enum OperatorApiCredentialKind
{
    Bearer,
    LocalSessionCookie
}

public sealed record OperatorApiCredential(
    OperatorApiCredentialKind Kind,
    string Value,
    DateTimeOffset? ExpiresAtUtc = null,
    string? CookieName = null)
{
    internal static readonly HttpRequestOptionsKey<OperatorApiCredential> RequestOptionsKey = new("NetRatel.OperatorApiCredential");
}

/// <summary>Holds only the current user's API credential inside one Web request or Blazor circuit scope.</summary>
public sealed class OperatorApiCredentialState
{
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private string? _identityKey;
    private string? _authMode;
    private ClaimsPrincipal? _boundPrincipal;
    private OperatorApiCredential? _credential;
    private long _generation;

    /// <summary>Switches identity only at an explicit circuit lifecycle boundary.</summary>
    public long BindPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identityKey = GetIdentityKey(principal);
        var authMode = GetAuthMode(principal);

        lock (_gate)
        {
            _generation++;
            _credential = null;
            _identityKey = identityKey;
            _authMode = identityKey is null ? null : authMode;
            _boundPrincipal = identityKey is null ? null : principal;
            return _generation;
        }
    }

    /// <summary>
    /// Binds an uninitialized request scope or verifies the existing circuit identity.
    /// Request handling cannot silently change the identity owned by a circuit.
    /// </summary>
    public bool BindPrincipalForRequest(ClaimsPrincipal principal)
        => BindPrincipalForRequest(principal, out _);

    public bool BindPrincipalForRequest(ClaimsPrincipal principal, out long generation)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identityKey = GetIdentityKey(principal);
        if (identityKey is null)
        {
            generation = GetGeneration();
            return false;
        }

        lock (_gate)
        {
            if (_identityKey is not null && !string.Equals(_identityKey, identityKey, StringComparison.Ordinal))
            {
                generation = _generation;
                return false;
            }

            if (_identityKey is null)
            {
                _generation++;
            }

            _identityKey = identityKey;
            _authMode = GetAuthMode(principal);
            _boundPrincipal = principal;
            generation = _generation;
            return true;
        }
    }

    /// <summary>Atomically verifies the principal and stores only a credential valid for that identity.</summary>
    public bool SetCredential(ClaimsPrincipal principal, OperatorApiCredential credential, long? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(credential);
        var identityKey = GetIdentityKey(principal);
        if (identityKey is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (expectedGeneration is { } expected && expected != _generation)
            {
                return false;
            }

            if (_identityKey is not null && !string.Equals(_identityKey, identityKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (_identityKey is null)
            {
                _generation++;
            }

            _identityKey = identityKey;
            _authMode = GetAuthMode(principal);
            _boundPrincipal = principal;

            var accepted = credential.Kind switch
            {
                OperatorApiCredentialKind.LocalSessionCookie => _authMode == "local" && !string.IsNullOrWhiteSpace(credential.Value),
                OperatorApiCredentialKind.Bearer => _authMode != "local"
                                                     && !string.IsNullOrWhiteSpace(credential.Value)
                                                     && credential.ExpiresAtUtc is { } expiresAt
                                                     && expiresAt > DateTimeOffset.UtcNow.Add(TokenRefreshBuffer),
                _ => false
            };
            if (!accepted)
            {
                _credential = null;
                _generation++;
            }
            else
            {
                _credential = credential;
            }

            return accepted;
        }
    }

    public OperatorApiCredential? GetCredential(ClaimsPrincipal? expectedPrincipal = null)
        => GetCredential(expectedPrincipal, out _);

    public OperatorApiCredential? GetCredential(ClaimsPrincipal? expectedPrincipal, out long generation)
    {
        lock (_gate)
        {
            if (_identityKey is null
                || _credential is null
                || expectedPrincipal is not null && !string.Equals(_identityKey, GetIdentityKey(expectedPrincipal), StringComparison.Ordinal))
            {
                generation = _generation;
                return null;
            }

            if (_credential.Kind == OperatorApiCredentialKind.Bearer
                && (_credential.ExpiresAtUtc is not { } expiresAt || expiresAt <= DateTimeOffset.UtcNow.Add(TokenRefreshBuffer)))
            {
                _credential = null;
                _generation++;
                generation = _generation;
                return null;
            }

            generation = _generation;
            return _credential;
        }
    }

    internal ClaimsPrincipal? GetBoundPrincipal(out long generation)
    {
        lock (_gate)
        {
            generation = _generation;
            return _boundPrincipal;
        }
    }

    internal ClaimsPrincipal? GetBoundPrincipal() => GetBoundPrincipal(out _);

    internal long GetGeneration()
    {
        lock (_gate)
        {
            return _generation;
        }
    }

    internal bool IsBoundTo(ClaimsPrincipal principal)
        => IsBoundTo(principal, expectedGeneration: null);

    internal bool IsBoundTo(ClaimsPrincipal principal, long expectedGeneration) => IsBoundTo(principal, (long?)expectedGeneration);

    private bool IsBoundTo(ClaimsPrincipal principal, long? expectedGeneration)
    {
        var identityKey = GetIdentityKey(principal);
        lock (_gate)
        {
            return identityKey is not null
                   && string.Equals(_identityKey, identityKey, StringComparison.Ordinal)
                   && (expectedGeneration is null || expectedGeneration.Value == _generation);
        }
    }

    public void ClearCredential(ClaimsPrincipal principal, long? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identityKey = GetIdentityKey(principal);
        lock (_gate)
        {
            if (identityKey is not null
                && string.Equals(_identityKey, identityKey, StringComparison.Ordinal)
                && (expectedGeneration is null || expectedGeneration.Value == _generation))
            {
                _credential = null;
                _generation++;
            }
        }
    }

    public bool IsLocalPrincipal
    {
        get
        {
            lock (_gate)
            {
                return _identityKey is not null && string.Equals(_authMode, "local", StringComparison.Ordinal);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _identityKey = null;
            _authMode = null;
            _boundPrincipal = null;
            _credential = null;
        }
    }

    internal static bool IsLocal(ClaimsPrincipal principal) => string.Equals(GetAuthMode(principal), "local", StringComparison.Ordinal);

    internal static bool SamePrincipal(ClaimsPrincipal left, ClaimsPrincipal right) =>
        string.Equals(GetIdentityKey(left), GetIdentityKey(right), StringComparison.Ordinal);

    internal static DateTimeOffset? ReadBearerExpiration(string accessToken)
    {
        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
            return token.ValidTo == DateTime.MinValue
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(token.ValidTo, DateTimeKind.Utc));
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? GetIdentityKey(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subjectClaim = principal.FindFirst("sub") ?? principal.FindFirst(ClaimTypes.NameIdentifier);
        var subject = subjectClaim?.Value ?? principal.Identity.Name;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var issuer = principal.FindFirst("iss")?.Value ?? subjectClaim?.Issuer;
        if (string.Equals(issuer, ClaimsIdentity.DefaultIssuer, StringComparison.Ordinal))
        {
            issuer = null;
        }

        var authenticationType = principal.Identity.AuthenticationType ?? string.Empty;
        return $"{GetAuthMode(principal)}\u001f{issuer}\u001f{authenticationType}\u001f{subject}";
    }

    private static string GetAuthMode(ClaimsPrincipal principal) =>
        principal.FindFirst("auth_mode")?.Value?.Trim().ToLowerInvariant() ?? "oidc";
}
