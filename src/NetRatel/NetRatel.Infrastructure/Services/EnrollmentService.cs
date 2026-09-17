using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NetRatel.Application.Agents;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class EnrollmentService : IEnrollmentService
{
    private static readonly SemaphoreSlim[] EnrollmentGates =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly OrchestratorDbContext _db;
    private readonly AgentAuthOptions _options;
    private readonly SecurityHardeningOptions _hardening;
    private readonly IPrimaryClientAgentBindingService _primaryClientBindings;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(
        OrchestratorDbContext db,
        IOptions<AgentAuthOptions> options,
        IOptions<SecurityHardeningOptions> hardening,
        IPrimaryClientAgentBindingService primaryClientBindings,
        ILogger<EnrollmentService> logger)
    {
        _db = db;
        _options = options.Value;
        _hardening = hardening.Value;
        _primaryClientBindings = primaryClientBindings;
        _logger = logger;
    }

    public async Task<AgentEnrollResponse> EnrollAsync(AgentEnrollRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentCode))
        {
            throw new AgentAuthException(400, "Enrollment code is required.");
        }

        var normalizedCode = request.EnrollmentCode.Trim().ToUpperInvariant();
        var installation = AgentInstallationIdentity.Parse(request.PublicKey, request.KeyAlgorithm);
        var enrollmentGate = EnrollmentGates[GetEnrollmentGateIndex(installation.Fingerprint)];
        await enrollmentGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EnrollCanonicalAsync(normalizedCode, request, installation, ct).ConfigureAwait(false);
        }
        finally
        {
            enrollmentGate.Release();
        }
    }

    private async Task<AgentEnrollResponse> EnrollCanonicalAsync(
        string normalizedCode,
        AgentEnrollRequest request,
        AgentInstallationKey installation,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var code = await LoadActiveCodeAsync(normalizedCode, now, ct).ConfigureAwait(false);
        var existing = await _db.Agents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(agent =>
                agent.TenantId == code.TenantId &&
                agent.PublicKeyFingerprint == installation.Fingerprint &&
                agent.SupersededAtUtc == null,
                ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            await ValidateEnrollmentProofAsync(existing.Id, request, installation, now, required: true, ct).ConfigureAwait(false);
            var recoveredToken = IssueRefreshCredential(existing, now, revokeExisting: true);
            UpdateSafeMetadata(existing, request, installation, now);
            if (!code.MaxUses.HasValue || code.Uses < code.MaxUses.Value)
            {
                code.Uses += 1;
            }

            AddLifecycleEvent(existing, NetRatelEventTypes.Agent.IdentityRecovered, "Agent identity recovered.", now);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await BindPendingPrimaryClientAsync(code, existing, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Existing Agent installation identity recovered. tenantId={TenantId} agentId={AgentId} fingerprint={Fingerprint}",
                existing.TenantId,
                existing.Id,
                ShortFingerprint(installation.Fingerprint));
            return new AgentEnrollResponse(existing.Id, recoveredToken, _options.RefreshTokenLifetimeDays);
        }

        if (code.MaxUses.HasValue && code.Uses >= code.MaxUses.Value)
        {
            throw new AgentAuthException(400, "Enrollment code has reached maximum uses.");
        }

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            TenantId = code.TenantId,
            Status = AgentStatus.Active,
            IsEnabled = true,
            CreatedAtUtc = now,
            CreatedBy = "enrollment",
            PublicKey = installation.PublicKey,
            PublicKeyFingerprint = installation.Fingerprint,
            KeyAlgorithm = installation.Algorithm,
            KeyRegisteredAtUtc = now,
            AllowedScopesJson = JsonSerializer.Serialize(NormalizeScopes(request.RequestedScopes)),
            DeviceInfoJson = request.DeviceInfoJson,
            MtlsThumbprint = string.IsNullOrWhiteSpace(request.MtlsThumbprint) ? null : request.MtlsThumbprint.Trim().ToUpperInvariant()
        };
        await ValidateEnrollmentProofAsync(agent.Id, request, installation, now, required: false, ct).ConfigureAwait(false);
        var refreshToken = IssueRefreshCredential(agent, now, revokeExisting: false);

        code.Uses += 1;

        _db.Agents.Add(agent);
        AddLifecycleEvent(agent, NetRatelEventTypes.Agent.Registered, "Agent registered.", now);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await BindPendingPrimaryClientAsync(code, agent, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "New Agent installation identity enrolled. tenantId={TenantId} agentId={AgentId} fingerprint={Fingerprint}",
                agent.TenantId,
                agent.Id,
                ShortFingerprint(installation.Fingerprint));
            return new AgentEnrollResponse(agent.Id, refreshToken, _options.RefreshTokenLifetimeDays);
        }
        catch (DbUpdateException exception) when (IsInstallationIdentityConflict(exception))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            return await RecoverAfterConcurrentEnrollmentAsync(normalizedCode, request, installation, ct).ConfigureAwait(false);
        }
    }

    public static string HashRefreshToken(string refreshToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private async Task<EnrollmentCode> LoadActiveCodeAsync(string normalizedCode, DateTimeOffset now, CancellationToken ct)
    {
        var code = await _db.EnrollmentCodes.FirstOrDefaultAsync(candidate => candidate.Code == normalizedCode, ct).ConfigureAwait(false);
        if (code is null)
        {
            throw new AgentAuthException(400, "Enrollment code is invalid.");
        }

        if (code.RevokedAtUtc.HasValue)
        {
            throw new AgentAuthException(400, "Enrollment code is revoked.");
        }

        if (code.ValidFromUtc > now || code.ValidToUtc <= now)
        {
            throw new AgentAuthException(400, "Enrollment code is expired or not active yet.");
        }

        return code;
    }

    private async Task ValidateEnrollmentProofAsync(
        Guid agentId,
        AgentEnrollRequest request,
        AgentInstallationKey installation,
        DateTimeOffset now,
        bool required,
        CancellationToken ct)
    {
        var hasProof = !string.IsNullOrWhiteSpace(request.ProofSignature) &&
                       !string.IsNullOrWhiteSpace(request.ProofNonce) &&
                       request.ProofTimestampUtc.HasValue;
        if (!hasProof)
        {
            if (required)
            {
                throw new AgentAuthException(401, "Proof of device-key possession is required to recover an existing Agent.", "enrollment_pop_required");
            }

            return;
        }

        var tolerance = TimeSpan.FromMinutes(Math.Max(1, _hardening.PopTimestampToleranceMinutes));
        if ((now - request.ProofTimestampUtc!.Value).Duration() > tolerance)
        {
            throw new AgentAuthException(401, "Enrollment proof timestamp is out of tolerance.", "enrollment_pop_timestamp_invalid");
        }

        var bodyHash = PopSignatureService.ComputeEnrollmentBodyHash(
            request.EnrollmentCode,
            installation.PublicKey,
            installation.Algorithm,
            request.DeviceInfoJson,
            request.RequestedScopes);
        var message = PopSignatureService.BuildSigningMessage(
            "POST",
            "/api/v1/agents/enroll",
            request.ProofTimestampUtc.Value,
            request.ProofNonce!.Trim(),
            bodyHash);
        if (!PopSignatureService.VerifySignature(
                installation.Algorithm,
                installation.PublicKey,
                request.ProofSignature!,
                message))
        {
            throw new AgentAuthException(401, "Enrollment proof signature is invalid.", "enrollment_pop_invalid");
        }

        var nonceExists = await _db.AgentNonceLogs.AnyAsync(
            nonce => nonce.AgentId == agentId && nonce.Nonce == request.ProofNonce.Trim(),
            ct).ConfigureAwait(false);
        if (nonceExists)
        {
            throw new AgentAuthException(401, "Enrollment proof nonce was already used.", "enrollment_pop_replay");
        }

        _db.AgentNonceLogs.Add(new AgentNonceLog
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            Nonce = request.ProofNonce.Trim(),
            CreatedAtUtc = now
        });
    }

    private string IssueRefreshCredential(Agent agent, DateTimeOffset now, bool revokeExisting)
    {
        if (revokeExisting)
        {
            foreach (var credential in _db.AgentCredentials.Where(item => item.AgentId == agent.Id && item.RevokedAtUtc == null))
            {
                credential.RevokedAtUtc = now;
            }

            foreach (var token in _db.AgentRefreshTokens.Where(item => item.AgentId == agent.Id && item.RevokedAtUtc == null))
            {
                token.RevokedAtUtc = now;
            }
        }

        var refreshToken = GenerateRefreshToken();
        var hash = HashRefreshToken(refreshToken);
        DateTimeOffset? expiresAtUtc = _options.RefreshTokenLifetimeDays > 0
            ? now.AddDays(_options.RefreshTokenLifetimeDays)
            : null;
        _db.AgentCredentials.Add(new AgentCredential
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            RefreshTokenHash = hash,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc
        });
        _db.AgentRefreshTokens.Add(new AgentRefreshToken
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            TokenHash = hash,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc
        });
        return refreshToken;
    }

    private static void UpdateSafeMetadata(Agent agent, AgentEnrollRequest request, AgentInstallationKey installation, DateTimeOffset now)
    {
        agent.PublicKey = installation.PublicKey;
        agent.PublicKeyFingerprint = installation.Fingerprint;
        agent.KeyAlgorithm = installation.Algorithm;
        agent.KeyRegisteredAtUtc ??= now;
        agent.DeviceInfoJson = request.DeviceInfoJson;
        agent.AllowedScopesJson = JsonSerializer.Serialize(NormalizeScopes(request.RequestedScopes));
        agent.MtlsThumbprint = string.IsNullOrWhiteSpace(request.MtlsThumbprint)
            ? agent.MtlsThumbprint
            : request.MtlsThumbprint.Trim().ToUpperInvariant();
    }

    private void AddLifecycleEvent(Agent agent, string type, string message, DateTimeOffset now)
    {
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredUtc = now,
            Type = type,
            PayloadJson = JsonSerializer.Serialize(new AgentLifecyclePayload(agent.Id, agent.TenantId, Actor: "enrollment")),
            Source = "Agent",
            CorrelationId = $"netratel-agent-{agent.Id:N}",
            TenantId = agent.TenantId.ToString(),
            EntityId = agent.Id.ToString(),
            Severity = "Info",
            Message = message,
            Status = OutboxStatuses.Pending,
            Attempts = 0,
            NextAttemptUtc = now
        });
    }

    private async Task BindPendingPrimaryClientAsync(EnrollmentCode code, Agent agent, CancellationToken ct)
    {
        var hasPendingPrimaryBinding = await _db.PrimaryClientAgentBindings
            .AsNoTracking()
            .AnyAsync(binding =>
                binding.TenantId == agent.TenantId &&
                binding.EnrollmentCodeId == code.Id &&
                binding.Status == PrimaryClientAgentBindingStatus.Pending,
                ct)
            .ConfigureAwait(false);
        if (!hasPendingPrimaryBinding)
        {
            return;
        }

        var binding = await _primaryClientBindings
            .BindEnrollmentAsync(agent.TenantId, code.Id, agent.Id, ct)
            .ConfigureAwait(false);
        if (binding.Disposition != PrimaryClientAgentBindingDisposition.Created)
        {
            throw new AgentAuthException(409, "Primary-client binding could not be completed for this enrollment code.");
        }
    }

    private async Task<AgentEnrollResponse> RecoverAfterConcurrentEnrollmentAsync(
        string normalizedCode,
        AgentEnrollRequest request,
        AgentInstallationKey installation,
        CancellationToken ct)
    {
        await using var recoveryTransaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var code = await LoadActiveCodeAsync(normalizedCode, now, ct).ConfigureAwait(false);
        var agent = await _db.Agents
            .IgnoreQueryFilters()
            .SingleAsync(candidate =>
                candidate.TenantId == code.TenantId &&
                candidate.PublicKeyFingerprint == installation.Fingerprint &&
                candidate.SupersededAtUtc == null,
                ct)
            .ConfigureAwait(false);
        await ValidateEnrollmentProofAsync(agent.Id, request, installation, now, required: true, ct).ConfigureAwait(false);
        var refreshToken = IssueRefreshCredential(agent, now, revokeExisting: true);
        UpdateSafeMetadata(agent, request, installation, now);
        AddLifecycleEvent(agent, NetRatelEventTypes.Agent.IdentityRecovered, "Agent identity recovered after concurrent enrollment.", now);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await recoveryTransaction.CommitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Concurrent Agent enrollment resolved to the canonical identity. tenantId={TenantId} agentId={AgentId} fingerprint={Fingerprint}",
            agent.TenantId,
            agent.Id,
            ShortFingerprint(installation.Fingerprint));
        return new AgentEnrollResponse(agent.Id, refreshToken, _options.RefreshTokenLifetimeDays);
    }

    private static bool IsInstallationIdentityConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, "IX_Agents_TenantId_PublicKeyFingerprint", StringComparison.Ordinal);

    private static string ShortFingerprint(string fingerprint) => fingerprint[..Math.Min(12, fingerprint.Length)];

    private static int GetEnrollmentGateIndex(string fingerprint) =>
        (int)(uint.Parse(fingerprint.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture) % EnrollmentGates.Length);

    private static IReadOnlyList<string> NormalizeScopes(IReadOnlyList<string>? requestedScopes)
    {
        if (requestedScopes is null || requestedScopes.Count == 0)
        {
            return ["netratel:connect"];
        }

        return requestedScopes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
