using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.ServiceIdentity;
using Microsoft.Extensions.Options;

namespace NetRatel.API.Security.M2M;

public sealed record M2MDeploymentProfile(string ClientId, string Secret, string Authority, string Audience,
    string[] AllowedAudiences, string[] AllowedScopes, int AccessTokenLifetimeMinutes, string Revision);
public interface IM2MDeploymentProfileResolver : IServiceClientDeploymentCatalog
{
    M2MDeploymentProfile? Resolve(string clientId);
    IReadOnlyList<M2MDeploymentProfile> List();
}

/// <summary>One deployment boundary adapts the historical underscore environment alias into one complete profile.</summary>
public sealed class M2MDeploymentClientsOptions : Dictionary<string, M2MClientRegistration> { }

public sealed class M2MDeploymentOptionsValidator : IValidateOptions<M2MOptions>
{
    public ValidateOptionsResult Validate(string? name, M2MOptions value)
    {
        if (string.IsNullOrWhiteSpace(value.Authority) && string.IsNullOrWhiteSpace(value.Audience) && value.AllowedCallerClientIds.Length == 0) return ValidateOptionsResult.Success;
        if (!Uri.TryCreate(value.Authority, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(value.Audience) || value.Audience.Any(char.IsControl) ||
            value.AllowedCallerClientIds.Any(x => !ServicePrincipalRegistry.IsValidClientId(x)) ||
            value.AllowedCallerClientIds.GroupBy(ServicePrincipalRegistry.AliasKey, StringComparer.Ordinal).Any(x => x.Count() != 1))
            return ValidateOptionsResult.Fail("M2M deployment settings need a complete authority/audience and unambiguous canonical caller identities.");
        return ValidateOptionsResult.Success;
    }
}
public sealed class M2MDeploymentClientsValidator(IOptions<M2MOptions> settings) : IValidateOptions<M2MDeploymentClientsOptions>
{
    public ValidateOptionsResult Validate(string? name, M2MDeploymentClientsOptions value)
    {
        if (value.GroupBy(x => ServicePrincipalRegistry.AliasKey(x.Key), StringComparer.Ordinal)
            .Any(group => group.Count(x => !string.IsNullOrWhiteSpace(x.Value.Secret)) > 1))
            return ValidateOptionsResult.Fail("M2M deployment client aliases are ambiguous; keep one complete profile per canonical client identity.");
        foreach (var (id, profile) in value)
        {
            // Packaged client declarations reserve aliases and permitted metadata.
            // They become issuable only when a deployment supplies a secret.
            if (string.IsNullOrWhiteSpace(profile.Secret)) continue;
            if (!ServicePrincipalRegistry.IsValidClientId(id) || string.IsNullOrWhiteSpace(profile.Secret) ||
                string.IsNullOrWhiteSpace(settings.Value.Authority) || string.IsNullOrWhiteSpace(settings.Value.Audience) ||
                profile.AllowedAudiences.Concat(profile.AllowedScopes).Any(x => string.IsNullOrWhiteSpace(x) || x.Any(char.IsControl)) ||
                profile.AllowedAudiences.Distinct(StringComparer.Ordinal).Count() != profile.AllowedAudiences.Length ||
                profile.AllowedScopes.Distinct(StringComparer.Ordinal).Count() != profile.AllowedScopes.Length)
                return ValidateOptionsResult.Fail("A configured M2M deployment client requires a complete identity/secret/authority/audience and explicit unique allowed sets.");
        }
        return ValidateOptionsResult.Success;
    }
}

public sealed class M2MDeploymentProfileResolver(IOptionsMonitor<M2MOptions> settingsMonitor,
    IOptionsMonitor<M2MDeploymentClientsOptions> clientsMonitor) : IM2MDeploymentProfileResolver
{
    public M2MDeploymentProfileResolver(IConfiguration configuration) : this(
        new FixedMonitor<M2MOptions>(configuration.GetSection("M2M").Get<M2MOptions>() ?? new() { Authority = "", Audience = "" }),
        new FixedMonitor<M2MDeploymentClientsOptions>(configuration.GetSection("M2MClients").Get<M2MDeploymentClientsOptions>() ?? new())) { }
    public M2MDeploymentProfile? Resolve(string clientId) => List().SingleOrDefault(x => x.ClientId == clientId);
    public bool OwnsIdentity(string clientId)
    {
        _ = List(); // Configuration errors never permit fallback to a database profile.
        var aliases = clientsMonitor.CurrentValue.Keys.Concat(settingsMonitor.CurrentValue.AllowedCallerClientIds)
            .Concat(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Select(x => x.Key?.ToString() ?? "")
                .Where(x => x.StartsWith("M2MClients__", StringComparison.Ordinal)).Select(x => x.Split("__", StringSplitOptions.None)[1]));
        return aliases.Any(x => ServicePrincipalRegistry.AliasKey(x) == ServicePrincipalRegistry.AliasKey(clientId));
    }
    public IReadOnlyList<M2MDeploymentProfile> List()
    {
        var settings = settingsMonitor.CurrentValue;
        var profiles = clientsMonitor.CurrentValue.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        // Read process environment here only. Other issuer/authentication code consumes the completed typed profile.
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(x => (Key: x.Key?.ToString() ?? "", Value: x.Value?.ToString() ?? ""))
            .Where(x => x.Key.StartsWith("M2MClients__", StringComparison.Ordinal)).ToArray();
        var prefixes = environment.Select(x => x.Key.Split("__", StringSplitOptions.None)).Where(x => x.Length >= 3)
            .Select(x => x[1]).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var prefix in prefixes)
        {
            var fallback = profiles.GetValueOrDefault(prefix) ?? new();
            var secret = environment.SingleOrDefault(x => x.Key == $"M2MClients__{prefix}__Secret").Value;
            profiles[prefix] = new()
            {
                Secret = string.IsNullOrWhiteSpace(secret) ? fallback.Secret : secret,
                AllowedAudiences = Values("AllowedAudiences", fallback.AllowedAudiences),
                AllowedScopes = Values("AllowedScopes", fallback.AllowedScopes)
            };
            string[] Values(string name, string[] original)
            {
                var start = $"M2MClients__{prefix}__{name}__";
                var values = environment.Where(x => x.Key.StartsWith(start, StringComparison.Ordinal))
                    .Select(x => (Index: int.TryParse(x.Key[start.Length..], out var n) ? n : -1, x.Value))
                    .Where(x => x.Index >= 0 && !string.IsNullOrWhiteSpace(x.Value)).OrderBy(x => x.Index).Select(x => x.Value).ToArray();
                return values.Length == 0 ? original : values;
            }
        }
        foreach (var group in profiles.GroupBy(x => ServicePrincipalRegistry.AliasKey(x.Key), StringComparer.Ordinal).Where(x => x.Count() > 1).ToArray())
        {
            var candidates = group.Where(x => !string.IsNullOrWhiteSpace(x.Value.Secret)).ToArray();
            if (candidates.Length == 0)
            {
                // Empty optional deployment inputs reserve their alias without
                // becoming an issuable profile or blocking unrelated UI clients.
                // Conflicting reserved metadata still cannot choose authority.
                _ = InheritedSet([], group.Select(x => x.Value.AllowedAudiences));
                _ = InheritedSet([], group.Select(x => x.Value.AllowedScopes));
                foreach (var entry in group) profiles.Remove(entry.Key);
                continue;
            }
            if (candidates.Length != 1)
                throw new ServiceClientConflictException("Ambiguous M2M deployment aliases require one secret-bearing profile.");
            var selected = candidates[0];
            var metadata = group.Where(x => string.IsNullOrWhiteSpace(x.Value.Secret)).Select(x => x.Value).ToArray();
            var complete = new M2MClientRegistration
            {
                Secret = selected.Value.Secret,
                AllowedAudiences = InheritedSet(selected.Value.AllowedAudiences, metadata.Select(x => x.AllowedAudiences)),
                AllowedScopes = InheritedSet(selected.Value.AllowedScopes, metadata.Select(x => x.AllowedScopes))
            };
            foreach (var entry in group) profiles.Remove(entry.Key);
            profiles.Add(selected.Key, complete);
        }
        if (settings.AllowedCallerClientIds.GroupBy(ServicePrincipalRegistry.AliasKey, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new ServiceClientConflictException("Ambiguous M2M deployment client aliases must be resolved before issuance.");
        var result = new List<M2MDeploymentProfile>();
        foreach (var (configuredId, registration) in profiles)
        {
            var candidates = settings.AllowedCallerClientIds.Where(x => ServicePrincipalRegistry.AliasKey(x) == ServicePrincipalRegistry.AliasKey(configuredId)).ToArray();
            var clientId = candidates.Length == 1 ? candidates[0] : configuredId;
            if (string.IsNullOrWhiteSpace(registration.Secret)) continue;
            if (!ServicePrincipalRegistry.IsValidClientId(clientId) || string.IsNullOrWhiteSpace(registration.Secret) ||
                string.IsNullOrWhiteSpace(settings.Authority) || string.IsNullOrWhiteSpace(settings.Audience))
                throw new ServiceClientConflictException("A configured M2M deployment profile requires a valid client identity, secret, authority and audience.");
            var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { clientId, registration, settings.Authority, settings.Audience, settings.AccessTokenLifetimeMinutes })));
            result.Add(new(clientId, registration.Secret, settings.Authority, settings.Audience, registration.AllowedAudiences,
                registration.AllowedScopes, settings.AccessTokenLifetimeMinutes, revision));
        }
        return result;
    }
    private static string[] InheritedSet(string[] selected, IEnumerable<string[]> metadata)
    {
        if (selected.Length > 0) return selected;
        var inherited = metadata.Where(x => x.Length > 0).ToArray();
        if (inherited.Length == 0) return [];
        var ordered = inherited[0].Order(StringComparer.Ordinal).ToArray();
        if (inherited.Skip(1).Any(x => !x.Order(StringComparer.Ordinal).SequenceEqual(ordered)))
            throw new ServiceClientConflictException("Reserved deployment aliases disagree on their allowed sets.");
        return inherited[0];
    }
    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
