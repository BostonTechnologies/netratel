using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Security.Authorization;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Clients;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.API.Services.Secrets;

internal sealed class ExecutionAccountException(string code, string message, int statusCode = 409) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed class ExecutionAccountService(OrchestratorDbContext db, ITenantSecretService vault, IEffectiveAccessService access)
{
    internal sealed record Binding(JobExecutionAccount Account, BoundSecretReference? Username, BoundSecretReference? Password);

    internal static string? PrincipalKey(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue("netratel_principal_id") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        return string.IsNullOrEmpty(id) || !MonitoringFlowPermissionAuthorization.IsSupportedAccount(principal)
            ? null : $"{principal.Identity!.AuthenticationType}:{id}";
    }

    public async Task<bool> IsAllowedAsync(ClaimsPrincipal principal, int tenantId, CancellationToken ct) =>
        MonitoringFlowPermissionAuthorization.IsSupportedAccount(principal) &&
        !principal.HasClaim(claim => claim.Type == "netratel_integration_credential_id") &&
        await HasPermissionAsync(principal, tenantId, ct);

    private async Task<bool> HasPermissionAsync(ClaimsPrincipal principal, int tenantId, CancellationToken ct) =>
        await access.AuthorizeAsync(principal, NetRatelPermissions.RunAs, tenantId, ct) &&
        await access.AuthorizeAsync(principal, NetRatelPermissions.SecretUse, tenantId, ct);

    public async Task RequireAsync(ClaimsPrincipal principal, int tenantId, CancellationToken ct)
    {
        if (!await IsAllowedAsync(principal, tenantId, ct))
            throw new ExecutionAccountException("run_as_denied", "Permission to use execution credentials is required.", 403);
    }

    public async Task RequireSavedAsync(string? principalId, string? integrationCredentialId, int tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(principalId) || integrationCredentialId is not null)
            throw new ExecutionAccountException("run_as_denied", "This execution source has no approved Run as authority.", 403);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", principalId)], "NetclawSavedAuthority"));
        if (!await HasPermissionAsync(principal, tenantId, ct))
            throw new ExecutionAccountException("run_as_denied", "Permission to use execution credentials is required.", 403);
    }

    public async Task<ExecutionAccountOption[]> ListAsync(int tenantId, Guid? agentId, bool forTerminal, CancellationToken ct)
    {
        var clients = agentId is { } id ? [await GetClientAsync(tenantId, id, ct)] :
            await db.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && agent.IsEnabled &&
                agent.DeletedAtUtc == null && agent.RevokedAtUtc == null && agent.SupersededByAgentId == null).ToArrayAsync(ct);
        var platforms = clients.ToDictionary(client => client.Id, Platform);
        return (await vault.ListProfilesAsync(tenantId, ct))
            .Where(profile => !profile.NeedsUsernameVersion() && (forTerminal ? profile.Scope.Terminals : profile.Scope.Jobs))
            .Select(profile => new ExecutionAccountOption(profile.Id, profile.DisplayName, profile.Mode,
                profile.Scope.AgentIds.Where(id => platforms.TryGetValue(id, out var platform) && profile.SupportsPlatform(platform)).ToArray()))
            .Where(option => option.AgentIds.Length != 0)
            .ToArray();
    }

    public async Task ValidateJobSettingAsync(int tenantId, ExecutionIdentitySetting requested, CancellationToken ct)
    {
        if (requested.CredentialProfileId is not { } id) return;
        var profile = await GetProfileAsync(tenantId, id, ct);
        if (profile.Mode != requested.Mode || !profile.Scope.Jobs || profile.NeedsUsernameVersion())
            throw new ExecutionAccountException("run_as_profile_ineligible", "Choose a credential that is available for jobs.");
    }

    public async Task<CredentialProfile> GetEligibleAsync(Agent client, Guid profileId, bool forTerminal, CancellationToken ct)
    {
        var profile = await GetProfileAsync(client.TenantId, profileId, ct);
        VerifyProfile(profile, client, forTerminal, null);
        return profile;
    }

    public async Task<JobExecutionAccount> ChooseJobAccountAsync(Agent client, ExecutionIdentitySetting? requested,
        Dictionary<Guid, CredentialProfile> profiles, CancellationToken ct)
    {
        var profileId = ChooseProfile(requested, client.DefaultJobCredentialProfileId)
            ?? throw new InvalidOperationException("The node has no requested saved account.");
        if (!profiles.TryGetValue(profileId, out var profile))
        {
            profile = await GetProfileAsync(client.TenantId, profileId, ct);
            profiles.Add(profileId, profile);
        }
        VerifyProfile(profile, client, false, requested?.CredentialProfileId == profileId ? requested.Mode : null);
        return new(profile.Id, profile.Mode, profile.MappingRevision);
    }

    internal static Guid? ChooseProfile(ExecutionIdentitySetting? requested, Guid? clientDefault) =>
        requested?.OverrideClientDefault == true ? requested.CredentialProfileId : clientDefault ?? requested?.CredentialProfileId;

    public async Task<Agent> GetClientAsync(int tenantId, Guid agentId, CancellationToken ct) =>
        await db.Agents.AsNoTracking().SingleOrDefaultAsync(agent => agent.TenantId == tenantId && agent.Id == agentId &&
            agent.DeletedAtUtc == null && agent.RevokedAtUtc == null && agent.SupersededAtUtc == null && agent.SupersededByAgentId == null, ct)
        ?? throw new ExecutionAccountException("run_as_client_unavailable", "This client is unavailable in the tenant.", 404);

    public async Task<Binding> BindAsync(Agent client, JobExecutionAccount account, bool forTerminal, CancellationToken ct)
    {
        var profile = await ReadBoundProfileAsync(client, account, forTerminal, ct);
        return new(account, await PinAsync(client.TenantId, profile.UsernameReference, ct),
            await PinAsync(client.TenantId, profile.PasswordReference, ct));
    }

    public async Task<RunAsCredential> ResolveAsync(Agent client, Binding binding, bool forTerminal, CancellationToken ct)
    {
        var profile = await ReadBoundProfileAsync(client, binding.Account, forTerminal, ct);
        var bindings = new[] { binding.Username, binding.Password }.OfType<BoundSecretReference>().ToArray();
        var resolved = bindings.Length == 0 ? [] :
            await vault.ResolveForExecutionAsync(client.TenantId, bindings.Select(x => x.Reference).ToArray(), ct);
        if (resolved.Count != bindings.Length || resolved.Where((item, index) =>
            item.ScopeRevision != bindings[index].ScopeRevision || item.ExactReference != bindings[index].Reference).Any())
            throw new ExecutionAccountException("run_as_secret_changed", "An execution secret or provider scope changed. Start a new execution.");
        var username = binding.Username is null ? profile.PublicUsername : resolved[0].Value;
        var password = binding.Password is null ? null : resolved[^1].Value;
        if (username is not { Length: > 0 and <= 256 } || string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl) ||
            profile.Mode == ExecutionIdentityMode.WindowsPassword && (password is not { Length: > 0 and <= 4096 } || password.Contains('\0')))
            throw new ExecutionAccountException("run_as_invalid_material", "The credential profile did not resolve to valid account fields.");
        // Provider I/O can outlive the profile or a scope change. Read the mapping again before yielding material.
        await ReadBoundProfileAsync(client, binding.Account, forTerminal, ct);
        return new()
        {
            ProfileId = profile.Id.ToString("D"),
            Mode = profile.Mode == ExecutionIdentityMode.WindowsPassword
                ? RunAsCredential.Types.Mode.WindowsPassword : RunAsCredential.Types.Mode.UnixPrivilegedLocal,
            Username = username,
            Domain = profile.Domain ?? "",
            Password = password ?? "",
            ExpectedAccountId = profile.Scope.AccountId ?? ""
        };
    }

    private async Task<BoundSecretReference?> PinAsync(int tenantId, SecretReference? reference, CancellationToken ct)
    {
        if (reference is null) return null;
        var binding = await vault.BindReferenceAsync(tenantId, reference, ct);
        var version = binding.Reference.Version ?? binding.ExactVersion ??
            throw new ExecutionAccountException("run_as_secret_unversioned", "An exact version of the execution secret is unavailable.");
        return binding with { Reference = binding.Reference with { VersionMode = SecretVersionMode.Pinned, Version = version } };
    }

    private async Task<CredentialProfile> ReadBoundProfileAsync(Agent client, JobExecutionAccount account, bool forTerminal, CancellationToken ct)
    {
        var profile = await GetProfileAsync(client.TenantId, account.ProfileId, ct);
        VerifyProfile(profile, client, forTerminal, account.Mode);
        if (profile.MappingRevision != account.MappingRevision)
            throw new ExecutionAccountException("run_as_profile_changed", "The execution credential changed. Start a new execution.");
        return profile;
    }

    private async Task<CredentialProfile> GetProfileAsync(int tenantId, Guid profileId, CancellationToken ct)
    {
        try { return await vault.GetProfileAsync(tenantId, profileId, ct); }
        catch (SecretProviderException error) when (error.Reason == SecretProviderFailure.NotFound)
        { throw new ExecutionAccountException("run_as_profile_unavailable", "The execution credential is unavailable in this tenant."); }
    }

    private static string? Platform(Agent client) => ClientDirectoryIdentity.Parse(client.DeviceInfoJson).OperatingSystem;

    private static void VerifyProfile(CredentialProfile profile, Agent client, bool forTerminal, ExecutionIdentityMode? requestedMode)
    {
        if (!profile.Allows(client.Id, Platform(client), forTerminal) ||
            (requestedMode is { } mode && profile.Mode != mode))
            throw new ExecutionAccountException("run_as_profile_ineligible", "The credential is outside the execution scope or incompatible with this client's operating system.");
        if (profile.NeedsUsernameVersion())
            throw new ExecutionAccountException("run_as_username_version_needed", "Save the credential profile again to pin its username reference.");
    }
}
