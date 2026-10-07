using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.Infrastructure.ServiceIdentity;

/// <summary>Issuance and every authenticated request resolve current durable authority across API replicas.</summary>
public sealed class ServicePrincipalRegistry(OrchestratorDbContext db, IServiceIdentityRuntimeOptions options,
    IOptionsMonitor<ServiceIdentityOptions> rawOptions, IServicePublicSettingsResolver publicSettings, IServiceClientDeploymentCatalog deployment, TimeProvider time) : IServicePrincipalRegistry
{
    public static bool IsValidClientId(string value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    public static string AliasKey(string value) => new(value.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
    public static string[] ReadArray(string value) => JsonSerializer.Deserialize<string[]>(value) ?? [];
    public static ServiceLinkResourceConstraints ReadConstraints(ServicePrincipalRegistration value) => ServiceLinkCanonicalJson.Deserialize<ServiceLinkResourceConstraints>(value.ResourceConstraintsJson);
    public static bool IsMachinePrincipal(ClaimsPrincipal principal) => principal.HasClaim("auth_mode", "service") && principal.HasClaim("token_use", ServiceIdentityClaims.Purpose);
    public Task<CreatedServiceClient> CreatePendingAsync(ServiceClientCreateRequest request, string actorId, CancellationToken ct = default) => CreateAsync(request, actorId, true, ct);

    public async Task<CreatedServiceClient> CreateAsync(ServiceClientCreateRequest request, string actorId, bool pending = false, CancellationToken ct = default)
    {
        if (!(await options.GetAsync(ct)).Enabled) throw new InvalidOperationException("The managed service issuer is disabled.");
        var constraints = ValidateRequest(request);
        if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentException("An approving principal is required.");
        var clientId = request.ClientId ?? $"nrsvc_{Guid.NewGuid():N}";
        if (!IsValidClientId(clientId)) throw new ArgumentException("Invalid client identity.");
        var alias = AliasKey(clientId);
        if (deployment.OwnsIdentity(clientId) || await db.Set<ServicePrincipalRegistration>().AnyAsync(x => x.AliasKey == alias, ct))
            throw new ServiceClientConflictException("The complete client identity or its deployment alias is already owned.");
        if (!await CurrentGrantAsync(request.TenantId, constraints, request.Scopes, ct)) throw new ArgumentException("The tenant, resources or request definitions are unavailable for this grant.");
        var now = time.GetUtcNow();
        var row = new ServicePrincipalRegistration
        {
            ClientId = clientId, NormalizedClientId = clientId.ToUpperInvariant(), AliasKey = alias, Name = request.Name,
            TenantId = request.TenantId, PeerInstanceId = request.PeerInstanceId, PeerTenantId = request.PeerTenantId,
            AllowedScopesJson = JsonSerializer.Serialize(request.Scopes.Order(StringComparer.Ordinal)),
            ResourceConstraintsJson = JsonSerializer.Serialize(constraints, ServiceLinkCanonicalJson.Json), LinkId = request.LinkId, AttemptId = request.AttemptId,
            GrantHash = request.GrantHash, DescriptorHash = request.DescriptorHash, DirectionId = request.DirectionId,
            LinkRevision = request.LinkRevision, Status = pending ? "pending" : "active", CreatedBy = actorId, ApprovedBy = actorId,
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var secret = NewSecret(row.Id, 1, pending ? "pending" : "active");
        db.Set<ServicePrincipalRegistration>().Add(row); db.Set<ServicePrincipalSecret>().Add(secret.Row);
        await db.SaveChangesAsync(ct);
        return new(row, secret.Value, 1) { CredentialExpiresAtUtc = secret.Row.ExpiresAtUtc };
    }

    public async Task<AuthenticatedServiceClient?> AuthenticateClientAsync(string clientId, string secret, CancellationToken ct = default)
    {
        if (!(await options.GetAsync(ct)).Enabled || !IsValidClientId(clientId) || secret is null || secret.Length is < 32 or > 1024 || deployment.OwnsIdentity(clientId)) return null;
        var normalized = clientId.ToUpperInvariant();
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedClientId == normalized, ct);
        if (row is null || row.ClientId != clientId || !await RegistrationEnabledAsync(row, ct) || !await LocalIdentityCurrentAsync(row, ct)) return null;
        var credentials = await db.Set<ServicePrincipalSecret>().AsNoTracking().Where(x => x.ServicePrincipalId == row.Id && x.Status != "revoked").ToListAsync(ct);
        ServicePrincipalSecret? selected = null;
        foreach (var credential in credentials)
            if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(credential.SecretHash), Convert.FromHexString(Hash(credential.Salt, secret))) && CredentialEnabled(credential)) selected = credential;
        return selected is null ? null : new(row, selected);
    }

    public async Task<ServicePrincipalRegistration?> ResolvePrincipalAsync(ClaimsPrincipal principal, string? requiredScope = null, CancellationToken ct = default)
    {
        if (!(await options.GetAsync(ct)).Enabled || !IsMachinePrincipal(principal) || !Guid.TryParseExact(principal.FindFirstValue(ServiceIdentityClaims.PrincipalId), "N", out var id) ||
            !long.TryParse(principal.FindFirstValue(ServiceIdentityClaims.CredentialRevision), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) return null;
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == revision, ct);
        if (row is null || credential is null || deployment.OwnsIdentity(row.ClientId) || !CredentialEnabled(credential) || !await RegistrationEnabledAsync(row, ct) || !await LocalIdentityCurrentAsync(row, ct)) return null;
        if (principal.FindFirstValue("sub") != $"service:{row.Id:N}" || principal.FindFirstValue("client_id") != row.ClientId ||
            principal.FindFirstValue(ServiceIdentityClaims.GrantRevision) != Number(row.Revision) || principal.FindFirstValue(ServiceIdentityClaims.TenantId) != Number(row.TenantId) ||
            principal.FindFirstValue(ServiceIdentityClaims.PeerInstanceId) != row.PeerInstanceId || principal.FindFirstValue(ServiceIdentityClaims.PeerTenantId) != row.PeerTenantId ||
            !Bound(principal, ServiceIdentityClaims.LinkId, row.LinkId) || !Bound(principal, ServiceIdentityClaims.AttemptId, row.AttemptId) ||
            !Bound(principal, ServiceIdentityClaims.GrantHash, row.GrantHash) || !Bound(principal, ServiceIdentityClaims.DirectionId, row.DirectionId) ||
            principal.FindFirstValue(ServiceIdentityClaims.LinkRevision) != Number(row.LinkRevision)) return null;
        var scopes = principal.FindAll("scope").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (scopes.Length == 0 || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length || scopes.Any(x => !PermittedScopes(row, credential).Contains(x, StringComparer.Ordinal)) ||
            requiredScope is not null && !scopes.Contains(requiredScope, StringComparer.Ordinal) || !await BusinessEnabledAsync(row, scopes, ct)) return null;
        return row;
    }

    public string[] PermittedScopes(ServicePrincipalRegistration row, ServicePrincipalSecret credential)
    {
        if (row.Status == "revoked") return row.LinkId is not null && row.TerminalControlUntilUtc > time.GetUtcNow() ? [ServiceIdentityScopes.Control] : [];
        if (row.LinkId is null) return row.Status == "active" && credential.Status != "pending" ? ReadArray(row.AllowedScopesJson) : [];
        if (row.Status == "active" && credential.Status is "active" or "retiring") return [.. ReadArray(row.AllowedScopesJson).Concat([ServiceIdentityScopes.Verify, ServiceIdentityScopes.Control]).Distinct(StringComparer.Ordinal)];
        return row.Status is "pending" or "prepared" or "verified" or "in_doubt" or "active" ? [ServiceIdentityScopes.Verify, ServiceIdentityScopes.Control] : [];
    }
    public async Task<bool> CanIssueScopesAsync(AuthenticatedServiceClient client, string[] scopes, CancellationToken ct = default)
    {
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == client.Principal.Id, ct);
        var secret = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x => x.ServicePrincipalId == client.Principal.Id && x.CredentialRevision == client.Credential.CredentialRevision, ct);
        return row is not null && secret is not null && row.Version == client.Principal.Version && row.Revision == client.Principal.Revision &&
            CredentialEnabled(secret) && await RegistrationEnabledAsync(row, ct) && scopes.All(x => PermittedScopes(row, secret).Contains(x, StringComparer.Ordinal)) &&
            await LocalIdentityCurrentAsync(row, ct) && await BusinessEnabledAsync(row, scopes, ct);
    }

    private async Task<bool> BusinessEnabledAsync(ServicePrincipalRegistration row, string[] scopes, CancellationToken ct)
    {
        if (!scopes.Any(x => ServiceIdentityScopes.Business.Contains(x, StringComparer.Ordinal))) return true;
        if (!await CurrentGrantAsync(row.TenantId, ReadConstraints(row), scopes, ct)) return false;
        if (row.LinkId is null) return row.Status == "active";
        var tenant = Number(row.TenantId);
        return await db.Set<ServiceLinkAttempt>().AsNoTracking().AnyAsync(x => x.LinkId == row.LinkId && x.AttemptId == row.AttemptId &&
            x.LinkRevision == row.LinkRevision && x.GrantHash == row.GrantHash && x.InboundPrincipalId == row.Id && x.LocalTenantId == tenant &&
            x.PeerInstanceId == row.PeerInstanceId && x.PeerTenantId == row.PeerTenantId && x.Decision == "commit" && x.LocalInboundActive &&
            (x.LifecycleState == "commit_decided" || x.LifecycleState == "active"), ct);
    }
    private async Task<bool> LocalIdentityCurrentAsync(ServicePrincipalRegistration row, CancellationToken ct)
    {
        var current = await publicSettings.ResolveAsync(ct);
        if (!current.Identity.Enabled) return false;
        if (row.LinkId is null) return true;
        var tenant = Number(row.TenantId);
        var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.LinkId == row.LinkId && x.AttemptId == row.AttemptId &&
            x.LinkRevision == row.LinkRevision && x.GrantHash == row.GrantHash && x.InboundPrincipalId == row.Id && x.LocalTenantId == tenant &&
            x.PeerInstanceId == row.PeerInstanceId && x.PeerTenantId == row.PeerTenantId, ct);
        if (attempt?.GrantSummaryJson is null || attempt.DescriptorHash != row.DescriptorHash) return false;
        try
        {
            var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(attempt.GrantSummaryJson);
            if (summary.Contract != ServiceLinkContract.Version || summary.AttemptId != attempt.AttemptId || summary.LinkId != attempt.LinkId ||
                summary.ProposedLinkRevision != attempt.LinkRevision || summary.DescriptorHash != attempt.DescriptorHash ||
                !ServiceLinkPayloadNormalization.SummaryHashMatches(summary, attempt.GrantHash!) || !ServiceLinkAuthority.LocalIdentityMatches(summary, attempt.Role, current)) return false;
            var direction = attempt.Role == "initiator" ? ServiceLinkContract.ResponderToInitiator : ServiceLinkContract.InitiatorToResponder;
            var matches = summary.Grants.Where(x => x.DirectionId == direction).ToArray();
            if (matches.Length != 1 || row.DirectionId != direction) return false;
            var grant = matches[0];
            return grant.TargetProduct == "netratel" && grant.TargetInstanceId == current.Identity.InstanceId && grant.TargetTenantId == Number(row.TenantId) &&
                grant.CallerInstanceId == row.PeerInstanceId && grant.CallerTenantId == row.PeerTenantId && grant.Issuer == current.Identity.Issuer && grant.Audience == current.Identity.Audience &&
                ReadArray(row.AllowedScopesJson).Order(StringComparer.Ordinal).SequenceEqual(grant.Scopes.Order(StringComparer.Ordinal)) &&
                ServiceLinkCanonicalJson.HashObject(ReadConstraints(row)) == ServiceLinkCanonicalJson.HashObject(grant.ResourceConstraints);
        }
        catch (JsonException) { return false; }
    }
    private async Task<bool> RegistrationEnabledAsync(ServicePrincipalRegistration row, CancellationToken ct) =>
        (row.Status is "pending" or "prepared" or "verified" or "in_doubt" or "active" || row.Status == "revoked" && row.TerminalControlUntilUtc > time.GetUtcNow()) &&
        await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == row.TenantId, ct);
    private bool CredentialEnabled(ServicePrincipalSecret row) => row.Status != "revoked" && row.ExpiresAtUtc > time.GetUtcNow() && (row.RetireAtUtc is null || row.RetireAtUtc > time.GetUtcNow());

    public Task ActivateAsync(Guid id, CancellationToken ct = default) => SetStatusAsync(id, "active", ct);
    public Task RevokeAsync(Guid id, CancellationToken ct = default) => SetStatusAsync(id, "revoked", ct);
    public async Task SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        if (status is not ("pending" or "prepared" or "verified" or "in_doubt" or "active" or "revoked" or "expired" or "failed")) throw new ArgumentException("Unsupported registration state.");
        var row = await ManagedAsync(id, ct);
        if (row.Status == "revoked" && status != "revoked") throw new ServiceClientConflictException("A revoked registration cannot be reopened.");
        if (status == "active" && !await CurrentGrantAsync(row.TenantId, ReadConstraints(row), ReadArray(row.AllowedScopesJson), ct)) throw new ServiceClientConflictException("The approved grant is no longer available.");
        row.Status = status; row.Version++; row.UpdatedAtUtc = time.GetUtcNow();
        if (status == "active") (await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == row.CurrentCredentialRevision, ct)).Status = "active";
        if (status == "revoked")
        {
            row.RevokedAtUtc ??= time.GetUtcNow(); row.TerminalControlUntilUtc ??= time.GetUtcNow().AddSeconds(rawOptions.CurrentValue.TerminalControlRecoverySeconds);
            if (row.LinkId is null)
                foreach (var secret in await db.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == id).ToListAsync(ct)) secret.Status = "revoked";
            else
            {
                var link = await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.LinkId == row.LinkId && x.InboundPrincipalId == row.Id, ct);
                if (link is not null) { link.LocalInboundActive = false; link.LocalBusinessSenderEnabled = false; link.Revision++; link.UpdatedAtUnixSeconds = time.GetUtcNow().ToUnixTimeSeconds(); }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<CreatedServiceClient> CreateSuccessorAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        var row = await ManagedAsync(id, ct);
        if (row.Status != "active" || row.CurrentCredentialRevision != expectedRevision || await db.Set<ServicePrincipalSecret>().AnyAsync(x => x.ServicePrincipalId == id && x.Status == "pending", ct)) throw new ServiceClientConflictException("A successor exists or the current credential changed.");
        var next = (await db.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == id).MaxAsync(x => (long?)x.CredentialRevision, ct) ?? 0) + 1;
        var secret = NewSecret(id, next, "pending"); db.Set<ServicePrincipalSecret>().Add(secret.Row); row.Version++;
        await db.SaveChangesAsync(ct); return new(row, secret.Value, next) { CredentialExpiresAtUtc = secret.Row.ExpiresAtUtc };
    }
    public async Task ActivateSuccessorAsync(Guid id, long revision, CancellationToken ct = default)
    {
        var row = await ManagedAsync(id, ct);
        var secret = await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == revision, ct);
        if (row.Status != "active" || secret.Status == "revoked" || revision < row.CurrentCredentialRevision || !CredentialEnabled(secret)) throw new ServiceClientConflictException("The successor cannot be activated.");
        secret.Status = "active"; row.CurrentCredentialRevision = revision; row.Version++; row.UpdatedAtUtc = time.GetUtcNow(); await db.SaveChangesAsync(ct);
    }
    public async Task RetirePredecessorAsync(Guid id, long revision, DateTimeOffset retireAt, CancellationToken ct = default)
    {
        var row = await ManagedAsync(id, ct);
        if (revision >= row.CurrentCredentialRevision || retireAt > time.GetUtcNow().AddSeconds(rawOptions.CurrentValue.ManualRotationOverlapSeconds)) throw new ServiceClientConflictException("Invalid predecessor overlap.");
        var predecessor = await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == revision, ct);
        predecessor.Status = "retiring"; predecessor.RetireAtUtc = predecessor.RetireAtUtc is { } existing && existing < retireAt ? existing : retireAt; await db.SaveChangesAsync(ct);
    }
    public async Task<CreatedServiceClient> RotateAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        var row = await ManagedAsync(id, ct);
        if (row.LinkId is not null) throw new ServiceClientConflictException("Reciprocal clients use coordinated link rotation.");
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var result = await CreateSuccessorAsync(id, expectedRevision, ct); await ActivateSuccessorAsync(id, result.CredentialRevision, ct);
        await RetirePredecessorAsync(id, expectedRevision, time.GetUtcNow().AddSeconds(rawOptions.CurrentValue.ManualRotationOverlapSeconds), ct);
        if (tx is not null) await tx.CommitAsync(ct); return result;
    }
    public async Task<IReadOnlyList<ServiceClientMetadata>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync(ct);
        var secrets = await db.Set<ServicePrincipalSecret>().AsNoTracking().ToListAsync(ct);
        return rows.OrderByDescending(x => x.CreatedAtUtc).Select(x => Metadata(x, secrets.Single(s => s.ServicePrincipalId == x.Id && s.CredentialRevision == x.CurrentCredentialRevision).ExpiresAtUtc)).ToArray();
    }
    public static ServiceClientMetadata Metadata(ServicePrincipalRegistration row, DateTimeOffset expires) => new(row.Id, row.Name, row.ClientId, row.TenantId,
        row.PeerInstanceId, row.PeerTenantId, ReadArray(row.AllowedScopesJson), row.ResourceConstraintsJson, row.Status, row.Source, row.Source == "deployment",
        row.Revision, row.CurrentCredentialRevision, row.CreatedAtUtc, expires, row.LinkId, row.DirectionId);
    private async Task<ServicePrincipalRegistration> ManagedAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Set<ServicePrincipalRegistration>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (row.Source != "database") throw new ServiceClientConflictException("Deployment identities are read-only."); return row;
    }
    private (ServicePrincipalSecret Row, string Value) NewSecret(Guid id, long revision, string status)
    {
        var value = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)); var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return (new() { ServicePrincipalId = id, CredentialRevision = revision, Salt = salt, SecretHash = Hash(salt, value), Status = status,
            CreatedAtUtc = time.GetUtcNow(), ExpiresAtUtc = time.GetUtcNow().AddDays(rawOptions.CurrentValue.CredentialMaximumAgeDays) }, value);
    }
    private static string Hash(string salt, string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{value}")));
    private static bool Bound(ClaimsPrincipal p, string name, string? value) => p.FindFirstValue(name) == value;
    private static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);

    public static ServiceLinkResourceConstraints ValidateRequest(ServiceClientCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || request.TenantId <= 0 ||
            string.IsNullOrWhiteSpace(request.PeerInstanceId) || request.PeerInstanceId.Length > 256 || string.IsNullOrWhiteSpace(request.PeerTenantId) || request.PeerTenantId.Length > 256 ||
            request.Name.Any(char.IsControl) || request.PeerInstanceId.Any(char.IsControl) || request.PeerTenantId.Any(char.IsControl)) throw new ArgumentException("Explicit tenant, name and peer instance/tenant are required.");
        if (request.Scopes is not { Length: > 0 and <= 2 } || request.Scopes.Distinct(StringComparer.Ordinal).Count() != request.Scopes.Length || request.Scopes.Any(x => !ServiceIdentityScopes.Business.Contains(x, StringComparer.Ordinal)) && !(request.LinkId is not null && ServiceLinkValidation.ControlOnlyScopes(request.Scopes))) throw new ArgumentException("Only exact supported narrow business scopes may be approved.");
        if (request.LinkId is not null && (request.AttemptId is null || request.GrantHash?.Length != 64 || request.DescriptorHash?.Length != 64 || request.DirectionId is not ("initiator_to_responder" or "responder_to_initiator") || request.LinkRevision < 1)) throw new ArgumentException("The complete approved ceremony binding is required.");
        if (request.LinkId is null && new[] { request.AttemptId, request.GrantHash, request.DescriptorHash, request.DirectionId }.Any(x => x is not null)) throw new ArgumentException("Partial reciprocal bindings are invalid.");
        ServiceLinkResourceConstraints result;
        try
        {
            if (request.ResourceConstraintsJson is null || request.ResourceConstraintsJson.Length > 8192) throw new ArgumentException("Constraints are required and bounded.");
            using var document = JsonDocument.Parse(request.ResourceConstraintsJson);
            var root = document.RootElement;
            var names = new[] { "organization_id", "customer_ids", "request_ids", "task_ids", "tenant_id", "resource_ids", "request_definition_ids" };
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(x => !names.Contains(x.Name, StringComparer.Ordinal)) || root.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) throw new ArgumentException("Unknown or duplicate constraint fields.");
            if (request.LinkId is not null) result = ServiceLinkCanonicalJson.Deserialize<ServiceLinkResourceConstraints>(request.ResourceConstraintsJson);
            else result = new() { TenantId = root.TryGetProperty("tenant_id", out var tenant) ? tenant.GetString() : Number(request.TenantId),
                OrganizationId = root.TryGetProperty("organization_id", out var org) ? org.GetString() : null,
                CustomerIds = List("customer_ids"), RequestIds = List("request_ids"), TaskIds = List("task_ids"), ResourceIds = List("resource_ids"), RequestDefinitionIds = List("request_definition_ids") };
            string[] List(string key) => root.TryGetProperty(key, out var value) ? value.EnumerateArray().Select(x => x.GetString() ?? throw new ArgumentException("Null constraint identities are invalid.")).ToArray() : [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new ArgumentException("Malformed resource constraints."); }
        if (result.CustomerIds is null || result.RequestIds is null || result.TaskIds is null || result.ResourceIds is null || result.RequestDefinitionIds is null)
            throw new ArgumentException("Constraint identity sets cannot be null.");
        if (result.TenantId != Number(request.TenantId) || result.OrganizationId is not null || result.CustomerIds.Length != 0 || result.RequestIds.Length != 0 || result.TaskIds.Length != 0) throw new ArgumentException("The local grant must name exactly the selected NetRatel tenant and its supported resources.");
        foreach (var set in new[] { result.ResourceIds, result.RequestDefinitionIds }) if (set.Length > 100 || set.Any(string.IsNullOrWhiteSpace) || set.Distinct(StringComparer.Ordinal).Count() != set.Length) throw new ArgumentException("Constraint identities must be bounded unique sets.");
        if (request.Scopes.Contains(ServiceIdentityScopes.OrchestrationInvoke, StringComparer.Ordinal) && (result.RequestDefinitionIds.Length == 0 || result.ResourceIds.Length == 0)) throw new ArgumentException("Invocation requires explicit current resource and request-definition grants.");
        return result;
    }

    private async Task<bool> CurrentGrantAsync(int tenantId, ServiceLinkResourceConstraints constraints, string[] scopes, CancellationToken ct)
    {
        if (constraints.TenantId != Number(tenantId) || constraints.OrganizationId is not null || constraints.CustomerIds.Length != 0 ||
            constraints.RequestIds.Length != 0 || constraints.TaskIds.Length != 0 || !await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenantId, ct)) return false;
        if (ServiceLinkValidation.ControlOnlyScopes(scopes))
            return await ServiceLinkGrantAuthority.ControlResourcesCurrentAsync(db, constraints, ct);
        if (!await ServiceLinkGrantAuthority.LocalResourcesCurrentAsync(db, constraints, ct)) return false;
        return !scopes.Contains(ServiceIdentityScopes.OrchestrationInvoke, StringComparer.Ordinal) || constraints.RequestDefinitionIds.Length > 0 && constraints.ResourceIds.Length > 0;
    }
}
