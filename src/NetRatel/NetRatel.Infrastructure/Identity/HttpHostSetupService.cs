using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Shared.Authentication;
using NetRatel.Shared.Client;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Identity;

public sealed class HttpHostSetupUnavailableException() : Exception("The host setup request is unavailable. Start setup again.");
public sealed class HttpHostSetupAdministratorException() : Exception("Sign in as a current instance administrator to approve this host deployment.");

/// <summary>
/// Approves the exact locally prepared host generation. Applying its protected deployment bundle
/// registers the service and delegation in the running API; approval alone never claims readiness.
/// </summary>
public sealed class HttpHostSetupService(NetRatelIdentityDbContext db, IEffectiveAccessService access,
    IDeploymentBrandingService branding, IConfiguration configuration, TimeProvider clock)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<HttpHostSetupCapabilities> CapabilitiesAsync(CancellationToken ct = default)
    {
        string origin;
        try { origin = ClientEndpointAddress.NormalizePublicOrigin((await branding.GetEffectiveAsync(ct).ConfigureAwait(false)).SiteUrl.Value).TrimEnd('/'); }
        catch (ArgumentException) { throw new HttpHostSetupUnavailableException(); }
        if (!McpResourceUri.TryNormalize(configuration["Integration:HttpMcp:PublicResourceUri"] ?? origin + "/mcp", out var resource))
            throw new HttpHostSetupUnavailableException();
        var mode = Configured("Integration:HttpMcp:AuthenticationMode") ?? Configured("WebGateway:Mcp:AuthenticationMode");
        if (mode is not null && !string.Equals(mode, "LocalCredential", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "Oidc", StringComparison.OrdinalIgnoreCase))
            throw new HttpHostSetupUnavailableException();
        var local = mode is not null ? string.Equals(mode, "LocalCredential", StringComparison.OrdinalIgnoreCase) :
            AuthenticationModeConfiguration.Resolve(configuration) != "Oidc";
        return new(HttpHostSetupProtocol.Version, origin, resource!, origin + "/account/integration-credentials?tab=credentials",
            local, Configured("Integration:HttpMcp:Authority") ?? Configured("NetRatel:Mcp:Http:Authority"),
            Values("RequiredGroups"), Values("RequiredScopes"))
        {
            AuthenticationModeKnown = mode is not null || !string.Equals(configuration["NetRatel:Mcp:Delegation:Enabled"], "true", StringComparison.OrdinalIgnoreCase)
        };
    }

    public async Task<HttpHostSetupStartResponse> StartAsync(HttpHostSetupStartRequest input, CancellationToken ct = default)
    {
        var binding = await CapabilitiesAsync(ct).ConfigureAwait(false);
        string suppliedOrigin;
        try { suppliedOrigin = ClientEndpointAddress.NormalizePublicOrigin(input.PublicOrigin).TrimEnd('/'); }
        catch (ArgumentException) { throw new HttpHostSetupUnavailableException(); }
        if (suppliedOrigin != binding.PublicOrigin || !McpResourceUri.TryNormalize(input.Resource, out var resource) || resource != binding.Resource ||
            input.RegistrationFingerprint is not { Length: 64 } || input.RegistrationFingerprint.Any(c => !char.IsAsciiHexDigit(c)) ||
            input.ServicePrincipal is not { Length: > 0 and <= 128 } || input.ServicePrincipal.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_' and not ':' and not '/'))
            throw new HttpHostSetupUnavailableException();
        if (await db.HttpHostSetupRequests.CountAsync(x => x.State == HttpHostSetupState.Pending || x.State == HttpHostSetupState.Approved, ct).ConfigureAwait(false) >= 4096)
            throw new HttpHostSetupUnavailableException();
        var code = string.Concat(RandomNumberGenerator.GetBytes(8).Select(value => Alphabet[value % Alphabet.Length]));
        var capability = "nrt_host_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = clock.GetUtcNow();
        var request = new HttpHostSetupRequest
        {
            PublicOrigin = binding.PublicOrigin, Resource = binding.Resource, ServicePrincipal = input.ServicePrincipal,
            RegistrationFingerprint = input.RegistrationFingerprint.ToUpperInvariant(),
            UserCodeHash = IntegrationCredentialService.Hash(code), DeviceCapabilityHash = IntegrationCredentialService.Hash(capability),
            CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(10), NextPollAtUtc = now.AddSeconds(HttpHostSetupProtocol.PollIntervalSeconds)
        };
        db.HttpHostSetupRequests.Add(request);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(request.Id, FormatCode(code), capability, binding.VerificationUri, binding.PublicOrigin, request.ExpiresAtUtc, request.PollIntervalSeconds);
    }

    public async Task<HttpHostSetupReview> ReviewAsync(ClaimsPrincipal actor, string code, CancellationToken ct = default)
    {
        await RequireAdministratorAsync(actor, ct).ConfigureAwait(false);
        return await MutateAsync(CodeHash(code), false, async request =>
        {
            await ValidateBindingAsync(request, ct).ConfigureAwait(false);
            if (request.State != HttpHostSetupState.Pending) throw new HttpHostSetupUnavailableException();
            return new HttpHostSetupReview(request.Id, FormatCode(NormalizeCode(code)), request.PublicOrigin, request.Resource,
                request.ServicePrincipal, request.RegistrationFingerprint, request.ExpiresAtUtc, Status(request));
        }, ct).ConfigureAwait(false);
    }

    public async Task<HttpHostSetupPollResponse> DecideAsync(ClaimsPrincipal actor, string code, bool approve, CancellationToken ct = default)
    {
        await RequireAdministratorAsync(actor, ct).ConfigureAwait(false);
        return await MutateAsync(CodeHash(code), false, async request =>
        {
            await ValidateBindingAsync(request, ct).ConfigureAwait(false);
            if (request.State != HttpHostSetupState.Pending) throw new HttpHostSetupUnavailableException();
            request.OwnerPrincipalId = actor.FindFirstValue("netratel_principal_id");
            request.State = approve ? HttpHostSetupState.Approved : HttpHostSetupState.Denied;
            return Response(request);
        }, ct).ConfigureAwait(false);
    }

    public Task<HttpHostSetupPollResponse> PollAsync(string capability, bool cancel = false, CancellationToken ct = default) =>
        MutateAsync(CapabilityHash(capability), true, async request =>
        {
            await ValidateBindingAsync(request, ct).ConfigureAwait(false);
            if (cancel && request.State is HttpHostSetupState.Pending or HttpHostSetupState.Approved) request.State = HttpHostSetupState.Cancelled;
            if (request.State == HttpHostSetupState.Approved && !await CurrentAdministratorAsync(request.OwnerPrincipalId, ct).ConfigureAwait(false))
                request.State = HttpHostSetupState.Denied;
            if (request.State is not (HttpHostSetupState.Pending or HttpHostSetupState.Approved)) return Response(request);
            var now = clock.GetUtcNow();
            if (now < request.NextPollAtUtc)
            {
                request.PollIntervalSeconds = Math.Min(30, request.PollIntervalSeconds + 5);
                request.NextPollAtUtc = now.AddSeconds(request.PollIntervalSeconds);
                return new HttpHostSetupPollResponse("slow_down", request.PollIntervalSeconds);
            }
            request.NextPollAtUtc = now.AddSeconds(request.PollIntervalSeconds);
            return Response(request);
        }, ct);

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().AddDays(-1);
        var ids = await db.HttpHostSetupRequests.Where(x => x.ExpiresAtUtc < cutoff).OrderBy(x => x.ExpiresAtUtc)
            .Take(256).Select(x => x.Id).ToArrayAsync(ct).ConfigureAwait(false);
        await db.HttpHostSetupRequests.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private string? Configured(string key) => configuration[key] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private IReadOnlyList<string> Values(string name) => configuration.GetSection("Integration:HttpMcp:" + name).GetChildren()
        .Select(x => x.Value).OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

    private async Task RequireAdministratorAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        if (actor.Identity?.IsAuthenticated != true || actor.HasClaim("auth_mode", "service") || actor.HasClaim("auth_mode", "machine_token") ||
            actor.FindFirst("netratel_integration_credential_id") is not null ||
            !await CurrentAdministratorAsync(actor.FindFirstValue("netratel_principal_id"), ct).ConfigureAwait(false))
            throw new HttpHostSetupAdministratorException();
    }

    private async Task<bool> CurrentAdministratorAsync(string? owner, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(owner) || !await db.ApplicationPrincipals.AsNoTracking().AnyAsync(x => x.Id == owner, ct).ConfigureAwait(false) ||
            await db.Users.AsNoTracking().AnyAsync(x => x.PrincipalId == owner && !x.IsEnabled, ct).ConfigureAwait(false)) return false;
        var retained = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", owner)], "host-setup-owner"));
        return (await access.GetSnapshotAsync(retained, null, ct).ConfigureAwait(false)).IsInstanceAdministrator;
    }

    private async Task ValidateBindingAsync(HttpHostSetupRequest request, CancellationToken ct)
    {
        if (request.State is not (HttpHostSetupState.Pending or HttpHostSetupState.Approved)) return;
        if (request.ExpiresAtUtc <= clock.GetUtcNow()) { request.State = HttpHostSetupState.Expired; return; }
        var binding = await CapabilitiesAsync(ct).ConfigureAwait(false);
        if (request.PublicOrigin != binding.PublicOrigin || request.Resource != binding.Resource) request.State = HttpHostSetupState.Cancelled;
    }

    private async Task<T> MutateAsync<T>(string hash, bool device, Func<HttpHostSetupRequest, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var request = await db.HttpHostSetupRequests.SingleOrDefaultAsync(x => device ? x.DeviceCapabilityHash == hash : x.UserCodeHash == hash, ct).ConfigureAwait(false)
                    ?? throw new HttpHostSetupUnavailableException();
                var result = await action(request).ConfigureAwait(false);
                request.Revision++;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3) { db.ChangeTracker.Clear(); }
        }
    }

    private static HttpHostSetupPollResponse Response(HttpHostSetupRequest request) => new(Status(request), request.PollIntervalSeconds,
        request.State == HttpHostSetupState.Approved ? request.Id : null,
        request.State == HttpHostSetupState.Approved ? request.RegistrationFingerprint : null);
    private static string Status(HttpHostSetupRequest request) => request.State.ToString().ToLowerInvariant();
    private static string FormatCode(string code) => code[..4] + "-" + code[4..];
    private static string NormalizeCode(string? code)
    {
        var normalized = code?.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return normalized is { Length: 8 } && normalized.All(Alphabet.Contains) ? normalized : throw new HttpHostSetupUnavailableException();
    }
    private static string CodeHash(string code) => IntegrationCredentialService.Hash(NormalizeCode(code));
    private static string CapabilityHash(string? capability) => capability is { Length: 73 } && capability.StartsWith("nrt_host_", StringComparison.Ordinal) &&
        capability[9..].All(char.IsAsciiHexDigit) ? IntegrationCredentialService.Hash(capability) : throw new HttpHostSetupUnavailableException();
}
