using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>Revalidates the production Agent credential without changing the logical presence owner.</summary>
public sealed class AgentGatewayRenewalAuthenticator(
    IOptionsMonitor<JwtBearerOptions> authenticationOptions,
    TimeProvider timeProvider,
    IServiceScopeFactory scopes)
{
    public const string Capability = "presence-auth-renewal-v1";

    public bool CanValidate
    {
        get
        {
            var parameters = authenticationOptions.Get("Agent").TokenValidationParameters;
            return parameters.ValidateIssuer && parameters.ValidateAudience && parameters.ValidateIssuerSigningKey &&
                   !string.IsNullOrWhiteSpace(parameters.ValidIssuer) &&
                   (parameters.ValidAudience is not null || parameters.ValidAudiences?.Any() == true) &&
                   (parameters.IssuerSigningKey is not null || parameters.IssuerSigningKeys?.Any() == true ||
                    parameters.IssuerSigningKeyResolver is not null);
        }
    }

    public async Task<DateTimeOffset> ValidateAsync(
        string accessToken,
        AuthenticatedAgentIdentity expectedIdentity,
        DateTimeOffset? minimumExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanValidate || accessToken.Length is 0 or > 16384)
            throw Rejected();

        // Reuse issuer, audience, signing keys, and algorithm policy. Stream
        // ownership ends at the credential's actual expiry, without the HTTP
        // authentication scheme's clock-skew grace prolonging PTY authority.
        var parameters = authenticationOptions.Get("Agent").TokenValidationParameters.Clone();
        parameters.ValidateLifetime = true;
        parameters.RequireSignedTokens = true;
        parameters.RequireExpirationTime = true;
        parameters.ClockSkew = TimeSpan.Zero;
        parameters.LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            return expires.HasValue && expires.Value > now && (!notBefore.HasValue || notBefore.Value <= now);
        };
        var validation = await new JsonWebTokenHandler { MapInboundClaims = false }
            .ValidateTokenAsync(accessToken, parameters).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!validation.IsValid || validation.ClaimsIdentity is null ||
            !AgentGatewayIdentityResolver.TryResolve(new ClaimsPrincipal(validation.ClaimsIdentity), out var identity, out _) ||
            identity != expectedIdentity || validation.SecurityToken is null)
            throw Rejected();

        var expiresAtUtc = new DateTimeOffset(validation.SecurityToken.ValidTo, TimeSpan.Zero);
        if (expiresAtUtc <= timeProvider.GetUtcNow() ||
            minimumExpiresAtUtc is { } minimum && expiresAtUtc <= minimum)
            throw Rejected();

        using var scope = scopes.CreateScope();
        AgentDetailDto? agent;
        try
        {
            agent = await scope.ServiceProvider.GetRequiredService<IAgentManagementService>()
                .GetAsync(expectedIdentity.TenantId, expectedIdentity.AgentId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Enrollment lookup failures do not extend authority. Do not
            // include a token, validation exception, or secret-bearing claim.
            throw new RpcException(new Status(StatusCode.Unavailable, "Agent renewal admission state is unavailable."));
        }
        if (agent is null || !agent.IsEnabled || agent.RevokedAtUtc.HasValue ||
            agent.DeletedAtUtc.HasValue ||
            agent.TenantId != expectedIdentity.TenantId || agent.AgentId != expectedIdentity.AgentId)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The agent is not active."));
        if (expiresAtUtc <= timeProvider.GetUtcNow()) throw Rejected();
        return expiresAtUtc;
    }

    private static RpcException Rejected() =>
        new(new Status(StatusCode.Unauthenticated, "The authenticated presence renewal credential is invalid."));
}

/// <summary>
/// Process-local expiry cancellation for exact physical presence owners. The
/// authoritative actor decides which owner is current across API replicas.
/// </summary>
public sealed class AgentGatewayAuthenticationLeaseRegistry(TimeProvider timeProvider)
{
    private readonly object _sync = new();
    private readonly Dictionary<(ClientKey Client, Guid ConnectionId, ulong Epoch), AgentGatewayAuthenticationLease> _leases = [];

    public AgentGatewayAuthenticationLease Register(ClientKey client, Guid connectionId, ulong connectionEpoch, DateTimeOffset expiresAtUtc)
    {
        AgentGatewayAuthenticationLease? previous;
        AgentGatewayAuthenticationLease lease;
        lock (_sync)
        {
            if (connectionId == Guid.Empty || connectionEpoch == 0 || expiresAtUtc <= timeProvider.GetUtcNow())
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Presence authentication has expired."));
            var key = (client, connectionId, connectionEpoch);
            _leases.TryGetValue(key, out previous);
            lease = new(this, client, connectionId, connectionEpoch, expiresAtUtc);
            _leases[key] = lease;
            lease.ExpiryTimer = timeProvider.CreateTimer(_ => Expire(lease), null, expiresAtUtc - timeProvider.GetUtcNow(), Timeout.InfiniteTimeSpan);
            if (previous is not null) MarkRetiredLocked(previous);
        }
        previous?.Cancel();
        return lease;
    }

    public AgentGatewayAuthenticationLease? Find(ClientKey client, Guid connectionId, ulong connectionEpoch)
    {
        lock (_sync)
            return _leases.TryGetValue((client, connectionId, connectionEpoch), out var lease) &&
                   !lease.Retired && lease.Expiry > timeProvider.GetUtcNow() ? lease : null;
    }

    internal bool IsCurrent(AgentGatewayAuthenticationLease lease)
    {
        lock (_sync) return IsCurrentLocked(lease) && lease.Expiry > timeProvider.GetUtcNow();
    }

    internal DateTimeOffset GetExpiry(AgentGatewayAuthenticationLease lease)
    {
        lock (_sync) return lease.Expiry;
    }

    internal bool TryRenew(AgentGatewayAuthenticationLease lease, Guid operationId, ulong sequence, DateTimeOffset expiresAtUtc)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(lease) || operationId == Guid.Empty || sequence <= lease.LastRenewalSequence ||
                operationId == lease.LastRenewalOperation || lease.Expiry <= timeProvider.GetUtcNow() || expiresAtUtc <= lease.Expiry)
                return false;
            lease.LastRenewalOperation = operationId;
            lease.LastRenewalSequence = sequence;
            lease.Expiry = expiresAtUtc;
            lease.ExpiryTimer!.Change(expiresAtUtc - timeProvider.GetUtcNow(), Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    internal void Retire(AgentGatewayAuthenticationLease lease)
    {
        lock (_sync) MarkRetiredLocked(lease);
        lease.Cancel();
    }

    private bool IsCurrentLocked(AgentGatewayAuthenticationLease lease) =>
        !lease.Retired && _leases.TryGetValue((lease.Client, lease.ConnectionId, lease.ConnectionEpoch), out var current) && ReferenceEquals(current, lease);

    private void MarkRetiredLocked(AgentGatewayAuthenticationLease lease)
    {
        lease.Retired = true;
        lease.ExpiryTimer?.Dispose();
        var key = (lease.Client, lease.ConnectionId, lease.ConnectionEpoch);
        if (_leases.TryGetValue(key, out var current) && ReferenceEquals(current, lease))
            _leases.Remove(key);
    }

    private void Expire(AgentGatewayAuthenticationLease lease)
    {
        lock (_sync)
        {
            if (lease.Retired) return;
            var remaining = lease.Expiry - timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                lease.ExpiryTimer!.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            MarkRetiredLocked(lease);
        }
        lease.Cancel();
    }
}

public sealed class AgentGatewayAuthenticationLease : IDisposable
{
    private readonly AgentGatewayAuthenticationLeaseRegistry _owner;
    private readonly CancellationTokenSource _completion = new();
    private readonly CancellationToken _completionToken;
    private int _cancelled;
    internal AgentGatewayAuthenticationLease(AgentGatewayAuthenticationLeaseRegistry owner, ClientKey client, Guid connectionId, ulong connectionEpoch, DateTimeOffset expiresAtUtc)
    {
        _owner = owner;
        _completionToken = _completion.Token;
        Client = client;
        ConnectionId = connectionId;
        ConnectionEpoch = connectionEpoch;
        Expiry = expiresAtUtc;
    }
    internal ClientKey Client { get; }
    internal Guid ConnectionId { get; }
    internal ulong ConnectionEpoch { get; }
    internal DateTimeOffset Expiry { get; set; }
    internal ITimer? ExpiryTimer { get; set; }
    internal bool Retired { get; set; }
    internal Guid LastRenewalOperation { get; set; }
    internal ulong LastRenewalSequence { get; set; }
    public CancellationToken CompletionToken => _completionToken;
    public DateTimeOffset ExpiresAtUtc => _owner.GetExpiry(this);
    public bool IsCurrent => _owner.IsCurrent(this);
    public bool TryRenew(Guid operationId, ulong sequence, DateTimeOffset expiresAtUtc) => _owner.TryRenew(this, operationId, sequence, expiresAtUtc);
    internal void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) == 0)
        {
            try { _completion.Cancel(); }
            finally { _completion.Dispose(); }
        }
    }
    public void Retire() => _owner.Retire(this);
    public void Dispose() => Retire();
}
