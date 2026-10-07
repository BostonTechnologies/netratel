using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NetRatel.Application.Agents;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class AgentTokenService : IAgentTokenService
{
    private readonly OrchestratorDbContext _db;
    private readonly OidcSigningService _signing;
    private readonly AgentAuthOptions _options;
    private readonly SecurityHardeningOptions _hardening;
    private readonly AgentNonceReplayService _nonceReplay;
    private readonly ILogger<AgentTokenService> _logger;
    private readonly IDataProtectionProvider _protection;
    private readonly TimeProvider _clock;
    private static readonly SemaphoreSlim AgentTokenEventsRepairGate = new(1, 1);
    // This service is scoped to a database. A process-wide flag incorrectly skips
    // recovery for another database/connection after one database was repaired.
    private bool _agentTokenEventsTableEnsured;

    public AgentTokenService(
        OrchestratorDbContext db,
        OidcSigningService signing,
        IOptions<AgentAuthOptions> options,
        IOptions<SecurityHardeningOptions> hardening,
        AgentNonceReplayService nonceReplay,
        ILogger<AgentTokenService> logger,
        IDataProtectionProvider protection,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _signing = signing;
        _options = options.Value;
        _hardening = hardening.Value;
        _nonceReplay = nonceReplay;
        _logger = logger;
        _protection = protection;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentTokenResponse> ExchangeRefreshTokenAsync(AgentTokenRequest request, CancellationToken ct)
    {
        // Serialize every rotating native request, including old-client requests and
        // acknowledgement, against the same agent/parent rows in PostgreSQL.
        await using var transaction = _hardening.EnableRefreshRotation && _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            var result = await ExchangeCoreAsync(request, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return result;
        }
        catch (AgentAuthException)
        {
            // Proof nonces, security events and eligible expired-result removal survive
            // rejection. Consumption only occurs after all authorization decisions.
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            throw;
        }
    }

    private async Task<AgentTokenResponse> ExchangeCoreAsync(AgentTokenRequest request, CancellationToken ct)
    {
        if (request.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new AgentAuthException(400, "agentId and refreshToken are required.");
        }

        var extended = request.ExchangeVersion.HasValue || request.ExchangeId.HasValue;
        if (extended && (request.ExchangeVersion != 1 || !request.ExchangeId.HasValue || request.ExchangeId == Guid.Empty))
            throw new AgentAuthException(400, "Native refresh exchange version/binding is unsupported.", "refresh_exchange_unsupported");

        var agent = _hardening.EnableRefreshRotation && _db.Database.IsNpgsql()
            ? await _db.Agents.FromSqlInterpolated($"SELECT * FROM \"Agents\" WHERE \"Id\" = {request.AgentId} FOR UPDATE").FirstOrDefaultAsync(ct)
            : await _db.Agents.FirstOrDefaultAsync(x => x.Id == request.AgentId, ct);
        if (agent is null)
        {
            await WriteTokenEventAsync(tenantId: null, request.AgentId, "TokenRejectedUnknownAgent", new { reason = "agent_not_found" }, ct);
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
            throw new AgentAuthException(403, "Agent not found.", "agent_not_found");
        }

        var now = _clock.GetUtcNow();
        if (!agent.IsEnabled || agent.Status != AgentStatus.Active)
        {
            await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedDisabled", null, ct);
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
            throw new AgentAuthException(403, "Agent disabled.", "agent_disabled");
        }

        if (_hardening.EnableMTls && _hardening.RequireMTls)
        {
            if (string.IsNullOrWhiteSpace(request.ClientCertificateThumbprint))
            {
                await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedMtlsMissing", null, ct);
                await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
                throw new AgentAuthException(401, "Client certificate is required.", "mtls_required");
            }

            if (!string.IsNullOrWhiteSpace(agent.MtlsThumbprint) &&
                !string.Equals(agent.MtlsThumbprint, request.ClientCertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedMtlsMismatch", null, ct);
                await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
                throw new AgentAuthException(401, "Client certificate does not match registered thumbprint.", "mtls_mismatch");
            }
        }

        if (_hardening.EnablePoP || extended)
        {
            await ValidateProofAsync(agent, request, now, ct);
        }

        var hash = EnrollmentService.HashRefreshToken(request.RefreshToken.Trim());
        string? rotatedRefreshToken = null;
        var rotationRecovered = false;

        if (!_hardening.EnableRefreshRotation && await _db.AgentRefreshTokens.AnyAsync(x =>
                x.AgentId == request.AgentId && x.TokenHash == hash && x.ExchangeId != null, ct))
            throw new AgentAuthException(403, "The recorded refresh exchange requires the current rotation policy.",
                "refresh_exchange_policy_changed");
        if (extended) await ValidateExchangeAgentAsync(agent, ct);

        IReadOnlyList<string>? exchangeScopes = null;
        if (_hardening.EnableRefreshRotation)
        {
            var refresh = _db.Database.IsNpgsql()
                ? await _db.AgentRefreshTokens.FromSqlInterpolated($"SELECT * FROM \"AgentRefreshTokens\" WHERE \"AgentId\" = {request.AgentId} AND \"TokenHash\" = {hash} FOR UPDATE").FirstOrDefaultAsync(ct)
                : await _db.AgentRefreshTokens.FirstOrDefaultAsync(x => x.AgentId == request.AgentId && x.TokenHash == hash, ct);
            if (refresh is null)
                throw new AgentAuthException(401, "Invalid credentials.", "refresh_token_invalid");

            // No recovery, including legacy grace, may outlive the consumed parent.
            if (refresh.ExpiresAtUtc.HasValue && refresh.ExpiresAtUtc <= now)
            {
                RetireExchangeResult(refresh);
                throw new AgentAuthException(401, "Credential expired.", "refresh_token_expired");
            }

            if (refresh.ExchangeId.HasValue)
            {
                // This check deliberately precedes generic spent-token/grace handling.
                // An old binary may recover only the immediate, unacknowledged result.
                if (extended && refresh.ExchangeId != request.ExchangeId)
                    throw new AgentAuthException(401, "Refresh exchange conflicts with the consumed parent.", "refresh_exchange_conflict");
                if (!_hardening.EnablePoP && !extended)
                    await ValidateProofAsync(agent, request, now, ct);
                if (refresh.ExchangeAcknowledgedAtUtc.HasValue || string.IsNullOrWhiteSpace(refresh.ProtectedSuccessorToken))
                    throw new AgentAuthException(401, "Refresh exchange result is no longer available.", "refresh_exchange_unavailable");
                exchangeScopes = await ValidateExchangeBindingAsync(agent, refresh, request, ct);
                rotatedRefreshToken = await ReadExchangeResultAsync(refresh, now, ct);
                rotationRecovered = true;
            }
            else if (refresh.RevokedAtUtc.HasValue)
            {
                if (extended)
                    throw new AgentAuthException(401, "The parent credential has already been consumed.", "refresh_token_reused");
                var recoveryWindow = TimeSpan.FromMinutes(Math.Max(1, _hardening.RefreshRotationRecoveryMinutes));
                var canRecover = refresh.RecoveryUsedAtUtc is null && refresh.ReplacedByTokenId.HasValue &&
                                 now - refresh.RevokedAtUtc.Value <= recoveryWindow;
                if (!canRecover)
                    throw new AgentAuthException(401, "Refresh token has already been used.", "refresh_token_reused");
                if (!_hardening.EnablePoP)
                    await ValidateProofAsync(agent, request, now, ct);
                var lostReplacement = await _db.AgentRefreshTokens.FirstOrDefaultAsync(x => x.Id == refresh.ReplacedByTokenId, ct);
                if (lostReplacement is null || lostReplacement.AgentId != agent.Id || lostReplacement.RevokedAtUtc.HasValue ||
                    (lostReplacement.ExpiresAtUtc.HasValue && lostReplacement.ExpiresAtUtc <= now))
                    throw new AgentAuthException(401, "Refresh rotation recovery is unavailable.", "refresh_rotation_recovery_unavailable");
                rotatedRefreshToken = GenerateRefreshToken();
                var replacement = NewRefreshToken(request.AgentId, rotatedRefreshToken, now,
                    _options.RefreshTokenLifetimeDays > 0 ? now.AddDays(_options.RefreshTokenLifetimeDays) : null);
                refresh.RecoveryUsedAtUtc = now;
                refresh.LastUsedUtc = now;
                lostReplacement.RevokedAtUtc = now;
                lostReplacement.ReplacedByTokenId = replacement.Id;
                _db.AgentRefreshTokens.Add(replacement);
                rotationRecovered = true;
            }
            else
            {
                if (extended)
                {
                    if (await _db.AgentRefreshTokens.AnyAsync(x => x.AgentId == agent.Id && x.ExchangeId == request.ExchangeId, ct))
                        throw new AgentAuthException(401, "Exchange ID belongs to another parent.", "refresh_exchange_conflict");
                    exchangeScopes = ResolveExchangeScopes(agent, request.RequestedScopes);
                }
                await AcknowledgeSuccessorAsync(refresh, now, ct);
                rotatedRefreshToken = GenerateRefreshToken();
                var expiry = extended ? refresh.ExpiresAtUtc :
                    (_options.RefreshTokenLifetimeDays > 0 ? now.AddDays(_options.RefreshTokenLifetimeDays) : (DateTimeOffset?)null);
                var replacement = NewRefreshToken(request.AgentId, rotatedRefreshToken, now, expiry);
                refresh.RevokedAtUtc = now;
                refresh.ReplacedByTokenId = replacement.Id;
                refresh.LastUsedUtc = now;
                if (extended)
                {
                    refresh.ExchangeId = request.ExchangeId;
                    refresh.ExchangeTenantId = agent.TenantId;
                    refresh.ExchangeKeyHash = DeviceKeyHash(agent);
                    refresh.ExchangeRequestedScopesJson = JsonSerializer.Serialize(request.RequestedScopes);
                    refresh.ExchangeGrantedScopesJson = JsonSerializer.Serialize(exchangeScopes);
                    refresh.ExchangeMtlsThumbprint = NormalizeThumbprint(request.ClientCertificateThumbprint);
                    refresh.ProtectedSuccessorToken = ResultProtector(refresh).Protect(rotatedRefreshToken);
                }
                _db.AgentRefreshTokens.Add(replacement);
            }
        }
        else
        {
            var credential = await _db.AgentCredentials
                .Include(x => x.Agent)
                .FirstOrDefaultAsync(x => x.AgentId == request.AgentId && x.RefreshTokenHash == hash, ct);

            if (credential is null)
            {
                throw new AgentAuthException(401, "Invalid credentials.", "refresh_token_invalid");
            }

            if (credential.RevokedAtUtc.HasValue)
            {
                throw new AgentAuthException(401, "Credential revoked.", "refresh_token_revoked");
            }

            if (credential.ExpiresAtUtc.HasValue && credential.ExpiresAtUtc <= now)
            {
                throw new AgentAuthException(401, "Credential expired.", "refresh_token_expired");
            }

            credential.LastUsedUtc = now;
        }

        var signingKey = await _signing.GetActiveSigningKeyAsync(ct);
        var signing = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256);
        var expires = now.AddMinutes(Math.Max(_options.AccessTokenLifetimeMinutes, 1));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, request.AgentId.ToString()),
            new("tenant_id", agent.TenantId.ToString()),
            new("agent_id", request.AgentId.ToString()),
            new("role", "agent"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };
        var grantedScopes = exchangeScopes ?? (extended ? ResolveExchangeScopes(agent, request.RequestedScopes) : ResolveScopes(agent, request.RequestedScopes));
        if (grantedScopes.Count > 0)
        {
            claims.Add(new Claim("scope", string.Join(' ', grantedScopes)));
        }

        if (_hardening.EnablePoP && !string.IsNullOrWhiteSpace(agent.PublicKey))
        {
            var cnf = JsonSerializer.Serialize(new
            {
                jwk = new
                {
                    kty = string.Equals(agent.KeyAlgorithm, "ecdsa-p256", StringComparison.OrdinalIgnoreCase) ? "EC" : "OKP",
                    crv = string.Equals(agent.KeyAlgorithm, "ecdsa-p256", StringComparison.OrdinalIgnoreCase) ? "P-256" : "Ed25519",
                    x = agent.PublicKey
                }
            });
            claims.Add(new Claim("cnf", cnf, System.IdentityModel.Tokens.Jwt.JsonClaimValueTypes.Json));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = signing
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        var tokenValue = handler.WriteToken(token);

        agent.LastSeenUtc = now;
        agent.LastTokenIssuedAtUtc = now;
        await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);

        try
        {
            await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenIssued", new { scopes = grantedScopes }, ct);
            if (!string.IsNullOrWhiteSpace(rotatedRefreshToken))
            {
                await WriteTokenEventAsync(
                    agent.TenantId,
                    agent.Id,
                    rotationRecovered ? "TokenRotationRecovered" : "TokenRotated",
                    null,
                    ct);
            }

            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist agent token event records. agentId={AgentId} tenantId={TenantId} details={Diagnostics}",
                agent.Id,
                agent.TenantId,
                BuildPersistenceDiagnostics(ex));

            await TryRecordTokenIssueFailureAsync(
                request.AgentId,
                agent.TenantId,
                BuildSafeErrorSummary(ex),
                ct);
        }

        return new AgentTokenResponse(tokenValue, (int)(expires - now).TotalSeconds, rotatedRefreshToken, request.ExchangeId);
    }

    private async Task ValidateExchangeAgentAsync(Agent agent, CancellationToken ct)
    {
        if (agent.RevokedAtUtc.HasValue || agent.DeletedAtUtc.HasValue || agent.SupersededByAgentId.HasValue)
            throw new AgentAuthException(403, "Agent authorization is revoked.", "agent_revoked");
        var tenantExists = _db.Database.IsNpgsql() && _db.Database.CurrentTransaction is not null
            ? await _db.Tenants.FromSqlInterpolated($"SELECT * FROM \"Tenants\" WHERE \"Id\" = {agent.TenantId} FOR SHARE").FirstOrDefaultAsync(ct) is not null
            : await _db.Tenants.AnyAsync(x => x.Id == agent.TenantId, ct);
        if (!tenantExists)
            throw new AgentAuthException(403, "Agent tenant is unavailable.", "agent_tenant_invalid");
    }

    private async Task<IReadOnlyList<string>> ValidateExchangeBindingAsync(Agent agent, AgentRefreshToken parent,
        AgentTokenRequest request, CancellationToken ct)
    {
        await ValidateExchangeAgentAsync(agent, ct);
        if (parent.ExchangeTenantId != agent.TenantId || parent.ExchangeKeyHash != DeviceKeyHash(agent) ||
            parent.ExchangeRequestedScopesJson != JsonSerializer.Serialize(request.RequestedScopes) ||
            parent.ExchangeMtlsThumbprint != NormalizeThumbprint(request.ClientCertificateThumbprint))
            throw new AgentAuthException(401, "Refresh exchange device or scope binding has changed.", "refresh_exchange_binding_mismatch");
        var granted = JsonSerializer.Deserialize<string[]>(parent.ExchangeGrantedScopesJson ?? "[]") ?? [];
        var current = ResolveExchangeScopes(agent, request.RequestedScopes);
        if (granted.Any(scope => !current.Contains(scope, StringComparer.OrdinalIgnoreCase)))
            throw new AgentAuthException(403, "Refresh exchange grants are no longer authorized.", "refresh_exchange_authorization_changed");
        // New grants cannot broaden the authorization captured by this exchange.
        return granted;
    }

    private async Task<string> ReadExchangeResultAsync(AgentRefreshToken parent, DateTimeOffset now, CancellationToken ct)
    {
        if (parent.ExchangeAcknowledgedAtUtc.HasValue || string.IsNullOrWhiteSpace(parent.ProtectedSuccessorToken) ||
            !parent.ReplacedByTokenId.HasValue)
            throw new AgentAuthException(401, "Refresh exchange result is no longer available.", "refresh_exchange_unavailable");
        var successor = await _db.AgentRefreshTokens.FirstOrDefaultAsync(x => x.Id == parent.ReplacedByTokenId, ct);
        if (successor is null || successor.AgentId != parent.AgentId || successor.RevokedAtUtc.HasValue ||
            successor.ReplacedByTokenId.HasValue || (successor.ExpiresAtUtc.HasValue && successor.ExpiresAtUtc <= now))
        {
            RetireExchangeResult(parent);
            throw new AgentAuthException(401, "Refresh exchange successor is no longer valid.", "refresh_exchange_unavailable");
        }
        try
        {
            var token = ResultProtector(parent).Unprotect(parent.ProtectedSuccessorToken);
            if (EnrollmentService.HashRefreshToken(token) != successor.TokenHash)
                throw new CryptographicException("Protected native successor does not match its recorded hash.");
            parent.LastUsedUtc = now;
            return token;
        }
        catch (CryptographicException)
        {
            // A missing/changed key ring is attention, never permission to rotate again.
            throw new AgentAuthException(503, "Protected refresh exchange result is unavailable.", "refresh_exchange_protection_unavailable");
        }
    }

    private async Task AcknowledgeSuccessorAsync(AgentRefreshToken successor, DateTimeOffset now, CancellationToken ct)
    {
        var parents = await _db.AgentRefreshTokens.Where(x => x.AgentId == successor.AgentId &&
            x.ReplacedByTokenId == successor.Id && x.ExchangeId != null && x.ExchangeAcknowledgedAtUtc == null).ToListAsync(ct);
        foreach (var parent in parents)
        {
            parent.ExchangeAcknowledgedAtUtc = now;
            RetireExchangeResult(parent);
        }
    }

    private static void RetireExchangeResult(AgentRefreshToken parent)
    {
        parent.ProtectedSuccessorToken = null;
        parent.ExchangeTenantId = null;
        parent.ExchangeKeyHash = null;
        parent.ExchangeRequestedScopesJson = null;
        parent.ExchangeGrantedScopesJson = null;
        parent.ExchangeMtlsThumbprint = null;
        // Exchange ID and immediate replacement decision prevent legacy grace or
        // a different ID from reclaiming this consumed parent after cleanup.
    }

    private IDataProtector ResultProtector(AgentRefreshToken parent) => _protection.CreateProtector(
        "NetRatel.NativeAgent.RefreshExchange.v1", parent.AgentId.ToString("N"), parent.Id.ToString("N"),
        parent.ExchangeId!.Value.ToString("N"), parent.ReplacedByTokenId!.Value.ToString("N"));

    private static string DeviceKeyHash(Agent agent) => PopSignatureService.ComputeBodyHash(
        $"{agent.KeyAlgorithm.Trim().ToLowerInvariant()}:{agent.PublicKey?.Trim()}");

    private static string? NormalizeThumbprint(string? thumbprint) => string.IsNullOrWhiteSpace(thumbprint)
        ? null : thumbprint.Trim().ToUpperInvariant();

    private IReadOnlyList<string> ResolveExchangeScopes(Agent agent, IReadOnlyList<string>? requestedScopes)
    {
        var allowed = NormalizeScopes(TryParseScopes(agent.AllowedScopesJson));
        if (allowed.Count == 0) allowed = ["netratel:connect"];
        var requested = NormalizeScopes(requestedScopes ?? []);
        return requested.Count == 0 ? allowed : requested.Where(scope => allowed.Contains(scope, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static AgentRefreshToken NewRefreshToken(Guid agentId, string value, DateTimeOffset now, DateTimeOffset? expiry) => new()
    {
        Id = Guid.NewGuid(), AgentId = agentId, TokenHash = EnrollmentService.HashRefreshToken(value),
        CreatedAtUtc = now, ExpiresAtUtc = expiry
    };

    public async Task<string> CreateServiceTokenAsync(string tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new AgentAuthException(400, "tenantId is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var signingKey = await _signing.GetActiveSigningKeyAsync(ct);
        var signing = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256);
        var expires = now.AddMinutes(15);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "netratel.api"),
            new("tenant_id", tenantId.Trim()),
            new("role", "service"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = signing
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    public async Task DisableAgentAsync(Guid agentId, CancellationToken ct)
    {
        if (agentId == Guid.Empty)
        {
            throw new AgentAuthException(400, "Agent id is required.");
        }

        var agent = await _db.Agents.FirstOrDefaultAsync(x => x.Id == agentId, ct);
        if (agent is null)
        {
            throw new AgentAuthException(404, "Agent not found.");
        }

        if (agent.Status != AgentStatus.Disabled)
        {
            agent.Status = AgentStatus.Disabled;
            agent.IsEnabled = false;
            agent.RevokedAtUtc = DateTimeOffset.UtcNow;
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
        }
    }

    public async Task EnableAgentAsync(Guid agentId, CancellationToken ct)
    {
        if (agentId == Guid.Empty)
        {
            throw new AgentAuthException(400, "Agent id is required.");
        }

        var agent = await _db.Agents.FirstOrDefaultAsync(x => x.Id == agentId, ct);
        if (agent is null)
        {
            throw new AgentAuthException(404, "Agent not found.", "agent_not_found");
        }

        if (!agent.IsEnabled || agent.Status != AgentStatus.Active)
        {
            agent.Status = AgentStatus.Active;
            agent.IsEnabled = true;
            agent.DisabledReason = null;
            agent.RevokedAtUtc = null;
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
        }
    }

    public Task<OpenIdConfigurationDto> GetOpenIdConfigurationAsync(CancellationToken ct)
    {
        var issuer = _options.Issuer.TrimEnd('/');
        var dto = new OpenIdConfigurationDto(
            issuer,
            $"{issuer}/connect/token",
            $"{issuer}/.well-known/jwks.json",
            ["ES256"]);

        return Task.FromResult(dto);
    }

    public Task<JsonWebKeySetDto> GetJwksAsync(CancellationToken ct)
        => _signing.GetJwksAsync(ct);

    private async Task WriteTokenEventAsync(int? tenantId, Guid agentId, string eventType, object? details, CancellationToken ct)
    {
        if (!tenantId.HasValue)
        {
            return;
        }

        var row = new AgentTokenEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId.Value,
            AgentId = agentId,
            EventType = eventType,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Ip = null,
            UserAgent = null,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details)
        };

        _db.AgentTokenEvents.Add(row);

        var occurred = DateTimeOffset.UtcNow;
        var severity = eventType.Contains("Rejected", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Info";
        var mappedType = eventType switch
        {
            "TokenIssued" => NetRatel.Application.Events.NetRatelEventTypes.AgentToken.Issued,
            "TokenRotated" => NetRatel.Application.Events.NetRatelEventTypes.AgentToken.Rotated,
            "TokenRotationRecovered" => NetRatel.Application.Events.NetRatelEventTypes.AgentToken.RotationRecovered,
            _ => $"DomainEvent.Orchestration.Agent.{eventType}"
        };

        var outbox = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredUtc = occurred,
            Type = mappedType,
            PayloadJson = details is null ? "{}" : JsonSerializer.Serialize(details),
            Source = "Agent",
            CorrelationId = $"netratel-agent-{agentId:N}",
            TenantId = tenantId.Value.ToString(),
            EntityId = agentId.ToString(),
            Severity = severity,
            Message = eventType,
            Status = OutboxStatuses.Pending,
            Attempts = 0,
            NextAttemptUtc = occurred
        };

        _db.OutboxMessages.Add(outbox);
    }

    private IReadOnlyList<string> ResolveScopes(Agent agent, IReadOnlyList<string>? requestedScopes)
    {
        var allowed = NormalizeScopes(TryParseScopes(agent.AllowedScopesJson));
        if (allowed.Count == 0)
        {
            allowed = ["netratel:connect"];
        }

        if (!_hardening.RequireScopes)
        {
            return allowed;
        }

        var requested = NormalizeScopes(requestedScopes ?? []);

        if (requested.Count == 0)
        {
            return allowed;
        }

        return requested
            .Where(scope => allowed.Contains(scope, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeScopes(IEnumerable<string> scopes)
        => scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Select(scope => string.Equals(scope, "sto:connect", StringComparison.OrdinalIgnoreCase)
                ? "netratel:connect"
                : scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> TryParseScopes(string? scopesJson)
    {
        if (string.IsNullOrWhiteSpace(scopesJson))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(scopesJson);
            if (parsed is null)
            {
                return [];
            }

            return parsed
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveChangesWithAgentTokenEventsRecoveryAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsMissingAgentTokenEventsTable(ex))
        {
            _logger.LogWarning(
                ex,
                "AgentTokenEvents save failed because table is missing. Attempting self-heal create+retry.");
            await EnsureAgentTokenEventsTableAsync(ct);
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task EnsureAgentTokenEventsTableAsync(CancellationToken ct)
    {
        if (_agentTokenEventsTableEnsured)
        {
            return;
        }

        await AgentTokenEventsRepairGate.WaitAsync(ct);
        try
        {
            if (_agentTokenEventsTableEnsured)
            {
                return;
            }

            var createSql = _db.Database.IsNpgsql()
                ? """
                    CREATE TABLE IF NOT EXISTS "AgentTokenEvents" (
                        "Id" uuid NOT NULL,
                        "TenantId" integer NOT NULL,
                        "AgentId" uuid NOT NULL,
                        "EventType" text NOT NULL,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "Ip" text NULL,
                        "UserAgent" text NULL,
                        "DetailsJson" text NULL,
                        CONSTRAINT "PK_AgentTokenEvents" PRIMARY KEY ("Id")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_AgentTokenEvents_TenantId_AgentId_CreatedAtUtc"
                        ON "AgentTokenEvents" ("TenantId", "AgentId", "CreatedAtUtc");
                    """
                : """
                    CREATE TABLE IF NOT EXISTS "AgentTokenEvents" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_AgentTokenEvents" PRIMARY KEY,
                        "TenantId" INTEGER NOT NULL,
                        "AgentId" TEXT NOT NULL,
                        "EventType" TEXT NOT NULL,
                        "CreatedAtUtc" INTEGER NOT NULL,
                        "Ip" TEXT NULL,
                        "UserAgent" TEXT NULL,
                        "DetailsJson" TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS "IX_AgentTokenEvents_TenantId_AgentId_CreatedAtUtc"
                        ON "AgentTokenEvents" ("TenantId", "AgentId", "CreatedAtUtc");
                    """;
            await _db.Database.ExecuteSqlRawAsync(createSql, ct);
            _agentTokenEventsTableEnsured = true;
            _logger.LogInformation("AgentTokenEvents table self-heal completed.");
        }
        finally
        {
            AgentTokenEventsRepairGate.Release();
        }
    }

    private static bool IsMissingAgentTokenEventsTable(DbUpdateException ex)
    {
        if (ex.InnerException is not PostgresException pgEx)
        {
            return false;
        }

        if (!string.Equals(pgEx.SqlState, PostgresErrorCodes.UndefinedTable, StringComparison.Ordinal))
        {
            return false;
        }

        var tableName = pgEx.TableName ?? string.Empty;
        return tableName.Contains("AgentTokenEvents", StringComparison.OrdinalIgnoreCase)
               || pgEx.MessageText.Contains("AgentTokenEvents", StringComparison.OrdinalIgnoreCase)
               || (pgEx.Detail?.Contains("AgentTokenEvents", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task ValidateProofAsync(Agent agent, AgentTokenRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agent.PublicKey))
        {
            throw new AgentAuthException(401, "Agent public key is not registered.", "agent_key_missing");
        }

        if (string.IsNullOrWhiteSpace(request.ProofSignature) ||
            string.IsNullOrWhiteSpace(request.ProofNonce) ||
            !request.ProofTimestampUtc.HasValue)
        {
            await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedMissingProof", null, ct);
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
            throw new AgentAuthException(401, "Proof-of-possession headers are required.", "pop_headers_required");
        }

        var tolerance = TimeSpan.FromMinutes(Math.Max(1, _hardening.PopTimestampToleranceMinutes));
        if ((now - request.ProofTimestampUtc.Value).Duration() > tolerance)
        {
            await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedStaleProof", null, ct);
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
            throw new AgentAuthException(401, "Proof timestamp is out of tolerance.", "pop_timestamp_invalid");
        }

        var nonceAccepted = await _nonceReplay.TryRegisterAsync(
            request.AgentId,
            request.ProofNonce.Trim(),
            TimeSpan.FromMinutes(Math.Max(1, _hardening.NonceRetentionMinutes)),
            ct);
        if (!nonceAccepted)
        {
            await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedNonceReplay", null, ct);
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
            throw new AgentAuthException(401, "Nonce replay detected.", "nonce_replay");
        }

        var bodyHash = PopSignatureService.ComputeTokenBodyHash(request.AgentId, request.RefreshToken, request.RequestedScopes, request.ExchangeVersion, request.ExchangeId);
        var message = PopSignatureService.BuildSigningMessage(
            "POST",
            "/api/v1/agents/token",
            request.ProofTimestampUtc.Value,
            request.ProofNonce.Trim(),
            bodyHash);

        var signatureValid = PopSignatureService.VerifySignature(
            agent.KeyAlgorithm,
            agent.PublicKey,
            request.ProofSignature,
            message);
        if (!signatureValid)
        {
            await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedInvalidProof", null, ct);
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
            throw new AgentAuthException(401, "Request proof signature is invalid.", "pop_signature_invalid");
        }

        if (string.IsNullOrWhiteSpace(request.ProofJwt))
        {
            return;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var signingKey = await _signing.GetActiveSigningKeyAsync(ct);
            handler.ValidateToken(request.ProofJwt, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = true,
                ValidAudience = _options.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            }, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PoP proof token failed JWT validation for agent {AgentId}", agent.Id);
            throw new AgentAuthException(401, "Proof token is invalid.", "pop_jwt_invalid");
        }
    }

    private async Task TryRecordTokenIssueFailureAsync(Guid agentId, int tenantId, string errorSummary, CancellationToken ct)
    {
        try
        {
            _db.ChangeTracker.Clear();
            var now = DateTimeOffset.UtcNow;
            _db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                OccurredUtc = now,
                Type = NetRatelEventTypes.System.AgentTokenIssueFailed,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    agentId,
                    tenantId,
                    error = errorSummary
                }),
                Source = nameof(AgentTokenService),
                CorrelationId = $"netratel-agent-{agentId:N}",
                TenantId = tenantId.ToString(),
                EntityId = agentId.ToString(),
                Severity = "Warning",
                Message = "Agent token event persistence failed.",
                Status = OutboxStatuses.Pending,
                Attempts = 0,
                NextAttemptUtc = now
            });

            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to emit AgentTokenIssueFailed notification. agentId={AgentId} tenantId={TenantId}",
                agentId,
                tenantId);
        }
    }

    private static string BuildSafeErrorSummary(Exception ex)
    {
        var baseMessage = ex.GetType().Name + ": " + ex.Message;
        if (ex is DbUpdateException dbEx && dbEx.InnerException is PostgresException pgEx)
        {
            return $"{baseMessage}; SqlState={pgEx.SqlState}; Constraint={pgEx.ConstraintName}; Column={pgEx.ColumnName}";
        }

        return baseMessage;
    }

    private static string BuildPersistenceDiagnostics(Exception ex)
    {
        if (ex is DbUpdateException dbEx && dbEx.InnerException is PostgresException pgEx)
        {
            return $"SqlState={pgEx.SqlState}, Constraint={pgEx.ConstraintName}, Column={pgEx.ColumnName}, Detail={pgEx.Detail}";
        }

        return ex.ToString();
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
