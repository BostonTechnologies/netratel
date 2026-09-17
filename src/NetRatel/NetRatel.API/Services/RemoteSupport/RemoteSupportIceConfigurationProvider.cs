using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.API.Services.RemoteSupport;

public interface IRemoteSupportIceConfigurationProvider
{
    RemoteSupportSessionIceConfiguration GetForSession(RemoteSupportSessionSnapshot session, long generation);
}

/// <summary>Server-owned, coturn REST-compatible ICE credential issuer. It never persists or logs credentials.</summary>
public sealed class RemoteSupportIceConfigurationProvider(
    IOptions<RemoteSupportIceOptions> options,
    TimeProvider timeProvider) : IRemoteSupportIceConfigurationProvider
{
    public RemoteSupportSessionIceConfiguration GetForSession(RemoteSupportSessionSnapshot session, long generation)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));

        var settings = options.Value;
        var expiresAtUtc = session.ExpiresAtUtc ?? timeProvider.GetUtcNow().Add(settings.SessionLifetime);
        var servers = settings.StunUrls.Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => new RemoteSupportIceServerDto([url.Trim()]))
            .ToList();
        if (settings.Turn.Enabled)
        {
            var username = CreateUsername(expiresAtUtc, session.Session, generation, settings.Turn.UsernamePrefix);
            var credential = CreateCredential(username, settings.Turn.SharedSecret);
            servers.Add(new RemoteSupportIceServerDto(settings.Turn.Urls.Select(url => url.Trim()).ToArray(), username, credential));
        }

        return new RemoteSupportSessionIceConfiguration(session.Session, generation, servers, expiresAtUtc);
    }

    internal static string CreateUsername(DateTimeOffset expiresAtUtc, RemoteSupportSessionKey session, long generation, string? prefix)
    {
        var opaque = SHA256.HashData(Encoding.UTF8.GetBytes($"{session.TenantId}:{session.AgentId:N}:{session.RemoteSupportSessionId:N}:{generation}"));
        var token = Convert.ToHexString(opaque.AsSpan(0, 12)).ToLowerInvariant();
        var safePrefix = string.IsNullOrWhiteSpace(prefix) ? "netratel" : prefix.Trim();
        return $"{expiresAtUtc.ToUnixTimeSeconds()}:{safePrefix}-{token}";
    }

    internal static string CreateCredential(string username, string sharedSecret)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(sharedSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
    }
}

public sealed class RemoteSupportIceOptions
{
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public List<string> StunUrls { get; set; } = [];
    public RemoteSupportTurnOptions Turn { get; set; } = new();
}

public sealed class RemoteSupportTurnOptions
{
    public bool Enabled { get; set; }
    public List<string> Urls { get; set; } = [];
    public string Realm { get; set; } = string.Empty;
    public string SharedSecret { get; set; } = string.Empty;
    public string? UsernamePrefix { get; set; }
}

public sealed class RemoteSupportIceOptionsValidator : IValidateOptions<RemoteSupportIceOptions>
{
    public ValidateOptionsResult Validate(string? name, RemoteSupportIceOptions options)
    {
        if (options.SessionLifetime < TimeSpan.FromMinutes(5) || options.SessionLifetime > TimeSpan.FromHours(8))
            return ValidateOptionsResult.Fail("Remote Support ICE session lifetime must be between 5 minutes and 8 hours.");
        if (!options.Turn.Enabled) return ValidateOptionsResult.Success;
        if (options.Turn.Urls.Count == 0 || options.Turn.Urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not "turn" and not "turns"))
            return ValidateOptionsResult.Fail("Enabled TURN requires valid turn: or turns: URLs.");
        return string.IsNullOrWhiteSpace(options.Turn.SharedSecret)
            ? ValidateOptionsResult.Fail("Enabled TURN requires a deployment-provided shared REST secret.")
            : ValidateOptionsResult.Success;
    }
}

public static class RemoteSupportIceServiceCollectionExtensions
{
    public static IServiceCollection AddRemoteSupportIceConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RemoteSupportIceOptions>().Bind(configuration.GetSection("RemoteSupport:Ice")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RemoteSupportIceOptions>, RemoteSupportIceOptionsValidator>();
        services.AddSingleton<IRemoteSupportIceConfigurationProvider, RemoteSupportIceConfigurationProvider>();
        return services;
    }
}
