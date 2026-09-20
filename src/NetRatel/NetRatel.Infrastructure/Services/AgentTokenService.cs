using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    private static readonly SemaphoreSlim AgentTokenEventsRepairGate = new(1, 1);
    private static volatile bool _agentTokenEventsTableEnsured;

    public AgentTokenService(
        OrchestratorDbContext db,
        OidcSigningService signing,
        IOptions<AgentAuthOptions> options,
        IOptions<SecurityHardeningOptions> hardening,
        AgentNonceReplayService nonceReplay,
        ILogger<AgentTokenService> logger)
    {
        _db = db;
        _signing = signing;
        _options = options.Value;
        _hardening = hardening.Value;
        _nonceReplay = nonceReplay;
        _logger = logger;
    }

    public async Task<AgentTokenResponse> ExchangeRefreshTokenAsync(AgentTokenRequest request, CancellationToken ct)
    {
        if (request.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new AgentAuthException(400, "agentId and refreshToken are required.");
        }

        var agent = await _db.Agents.FirstOrDefaultAsync(x => x.Id == request.AgentId, ct);
        if (agent is null)
        {
            await WriteTokenEventAsync(tenantId: null, request.AgentId, "TokenRejectedUnknownAgent", new { reason = "agent_not_found" }, ct);
            await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
            throw new AgentAuthException(403, "Agent not found.", "agent_not_found");
        }

        var now = DateTimeOffset.UtcNow;
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

        if (_hardening.EnablePoP)
        {
            await ValidateProofAsync(agent, request, now, ct);
        }

        var hash = EnrollmentService.HashRefreshToken(request.RefreshToken.Trim());
        string? rotatedRefreshToken = null;
        var rotationRecovered = false;

        if (_hardening.EnableRefreshRotation)
        {
            var refresh = await _db.AgentRefreshTokens
                .FirstOrDefaultAsync(x => x.AgentId == request.AgentId && x.TokenHash == hash, ct);

            if (refresh is null)
            {
                await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedInvalidRefresh", null, ct);
                await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
                throw new AgentAuthException(401, "Invalid credentials.");
            }

            if (refresh.RevokedAtUtc.HasValue)
            {
                var recoveryWindow = TimeSpan.FromMinutes(Math.Max(1, _hardening.RefreshRotationRecoveryMinutes));
                var canRecover = refresh.RecoveryUsedAtUtc is null &&
                                 refresh.ReplacedByTokenId.HasValue &&
                                 now - refresh.RevokedAtUtc.Value <= recoveryWindow;
                if (!canRecover)
                {
                    await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedRefreshReuse", null, ct);
                    await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
                    throw new AgentAuthException(401, "Refresh token has already been used.", "refresh_token_reused");
                }

                if (!_hardening.EnablePoP)
                {
                    await ValidateProofAsync(agent, request, now, ct).ConfigureAwait(false);
                }

                var replacementTokenId = refresh.ReplacedByTokenId.GetValueOrDefault();
                var lostReplacement = await _db.AgentRefreshTokens
                    .FirstOrDefaultAsync(token => token.Id == replacementTokenId, ct)
                    .ConfigureAwait(false);
                if (lostReplacement is null || lostReplacement.AgentId != agent.Id)
                {
                    await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedRotationRecovery", new { reason = "replacement_not_found" }, ct);
                    await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
                    throw new AgentAuthException(401, "Refresh rotation recovery is unavailable.", "refresh_rotation_recovery_unavailable");
                }

                var recoveryRefreshValue = GenerateRefreshToken();
                var recoveryReplacement = new AgentRefreshToken
                {
                    Id = Guid.NewGuid(),
                    AgentId = request.AgentId,
                    TokenHash = EnrollmentService.HashRefreshToken(recoveryRefreshValue),
                    CreatedAtUtc = now,
                    ExpiresAtUtc = _options.RefreshTokenLifetimeDays > 0 ? now.AddDays(_options.RefreshTokenLifetimeDays) : null
                };
                refresh.RecoveryUsedAtUtc = now;
                refresh.LastUsedUtc = now;
                lostReplacement.RevokedAtUtc ??= now;
                lostReplacement.ReplacedByTokenId = recoveryReplacement.Id;
                _db.AgentRefreshTokens.Add(recoveryReplacement);
                rotatedRefreshToken = recoveryRefreshValue;
                rotationRecovered = true;
            }
            else if (refresh.ExpiresAtUtc.HasValue && refresh.ExpiresAtUtc <= now)
            {
                await WriteTokenEventAsync(agent.TenantId, agent.Id, "TokenRejectedExpiredRefresh", null, ct);
                await SaveChangesWithAgentTokenEventsRecoveryAsync(ct);
                throw new AgentAuthException(401, "Credential expired.");
            }

            else
            {
                var newRefreshValue = GenerateRefreshToken();
                var replacement = new AgentRefreshToken
                {
                    Id = Guid.NewGuid(),
                    AgentId = request.AgentId,
                    TokenHash = EnrollmentService.HashRefreshToken(newRefreshValue),
                    CreatedAtUtc = now,
                    ExpiresAtUtc = _options.RefreshTokenLifetimeDays > 0 ? now.AddDays(_options.RefreshTokenLifetimeDays) : null
                };

                refresh.RevokedAtUtc = now;
                refresh.ReplacedByTokenId = replacement.Id;
                refresh.LastUsedUtc = now;
                _db.AgentRefreshTokens.Add(replacement);
                rotatedRefreshToken = newRefreshValue;
            }
        }
        else
        {
            var credential = await _db.AgentCredentials
                .Include(x => x.Agent)
                .FirstOrDefaultAsync(x => x.AgentId == request.AgentId && x.RefreshTokenHash == hash, ct);

            if (credential is null)
            {
                throw new AgentAuthException(401, "Invalid credentials.");
            }

            if (credential.RevokedAtUtc.HasValue)
            {
                throw new AgentAuthException(401, "Credential revoked.");
            }

            if (credential.ExpiresAtUtc.HasValue && credential.ExpiresAtUtc <= now)
            {
                throw new AgentAuthException(401, "Credential expired.");
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
        var grantedScopes = ResolveScopes(agent, request.RequestedScopes);
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

        return new AgentTokenResponse(tokenValue, (int)(expires - now).TotalSeconds, rotatedRefreshToken);
    }

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
        if (ex.InnerException is SqliteException sqliteEx)
        {
            return sqliteEx.SqliteErrorCode == 1 &&
                   sqliteEx.Message.Contains("AgentTokenEvents", StringComparison.OrdinalIgnoreCase);
        }

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

        var bodyHash = PopSignatureService.ComputeTokenBodyHash(request.AgentId, request.RefreshToken, request.RequestedScopes);
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
