using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing.Network;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class RatelDeskReceiverOptions
{
    public const string SectionName = "RatelDesk";
    // Existing deployment key; a nonempty list remains an authoritative restriction.
    public string[] AllowedOrigins { get; set; } = [];
    // Additive exact API/path-base restriction. Neither list is populated from discovery.
    public string[] AllowedApiBases { get; set; } = [];
    public bool RestrictToConfiguredPeers { get; set; }
    // Empty optional deployment/env values do not create an enabled restriction.
    // Explicit RestrictToConfiguredPeers remains authoritative even when these are empty.
    public string[] EffectiveOrigins() => (AllowedOrigins ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    public string[] EffectiveApiBases() => (AllowedApiBases ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
}

public sealed class RatelDeskReceiverOptionsValidator : IValidateOptions<RatelDeskReceiverOptions>
{
    public ValidateOptionsResult Validate(string? name, RatelDeskReceiverOptions value)
    {
        if (value.AllowedOrigins is null || value.AllowedApiBases is null ||
            value.AllowedOrigins.Length > 256 || value.AllowedApiBases.Length > 256)
            return ValidateOptionsResult.Fail("RatelDesk peer restrictions exceed the supported bound.");
        try
        {
            foreach (var origin in value.EffectiveOrigins())
            {
                var canonical = RatelDeskApiBase.Canonical(origin);
                var uri = new Uri(canonical);
                // Preserve historical exact root HTTPS AllowedOrigins semantics.
                if (uri.Scheme != "https" || uri.AbsolutePath != "/")
                    throw new ArgumentException("RatelDesk:AllowedOrigins requires root HTTPS origins.");
                PairingEndpointPolicy.Validate(uri, "RatelDesk:AllowedOrigins", allowPrivateHttp: true);
            }
            foreach (var api in value.EffectiveApiBases())
                PairingEndpointPolicy.Validate(new Uri(RatelDeskApiBase.Canonical(api)),
                    "RatelDesk:AllowedApiBases", allowPrivateHttp: true); // Structural restriction; selected mode admits each operation.
            if (value.EffectiveOrigins().Select(RatelDeskApiBase.Canonical).Distinct(StringComparer.Ordinal).Count() != value.EffectiveOrigins().Length ||
                value.EffectiveApiBases().Select(RatelDeskApiBase.Canonical).Distinct(StringComparer.Ordinal).Count() != value.EffectiveApiBases().Length)
                throw new ArgumentException("RatelDesk peer restrictions contain duplicate entries.");
            return ValidateOptionsResult.Success;
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException) { return ValidateOptionsResult.Fail("RatelDesk peer restrictions require canonical approved HTTP(S) API identities."); }
    }
}

public static class RatelDeskApiBase
{
    public static string Canonical(string value)
    {
        if (value is not { Length: > 0 and <= 2048 } || value.Trim() != value || value.Any(char.IsControl) ||
            value.IndexOfAny(['\\', '%', '?', '#']) >= 0 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Host.Length == 0 ||
            uri.HostNameType == UriHostNameType.Unknown ||
            value.TrimEnd('/') != uri.AbsoluteUri.TrimEnd('/') ||
            uri.AbsolutePath.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException("receiver-api-base-invalid");
        return uri.AbsoluteUri.TrimEnd('/');
    }
}

/// <summary>Policy applies to a durable owner-approved connector or exact approved link, never a browser/discovery URL.</summary>
public sealed class RatelDeskReceiverNetworkPolicy(IOptionsMonitor<RatelDeskReceiverOptions> manual)
{
    public bool CurrentAllowPrivateHttp(RatelDeskAuthenticationMode mode) => mode == RatelDeskAuthenticationMode.PairedSystem ? true : throw new UnauthorizedAccessException("paired-connection-required");

    public string ValidateApprovedApiBase(RatelDeskAuthenticationMode mode, string apiBase)
    {
        var current = manual.CurrentValue; // Fresh options on each operation, no cached allowlist.
        if (new RatelDeskReceiverOptionsValidator().Validate(null, current).Failed)
            throw new InvalidOperationException("receiver-deployment-policy-invalid");
        var canonical = RatelDeskApiBase.Canonical(apiBase);
        var uri = new Uri(canonical);
        var origins = current.EffectiveOrigins(); var bases = current.EffectiveApiBases();
        if (origins.Length != 0 && !origins.Any(x =>
                new Uri(RatelDeskApiBase.Canonical(x)).GetLeftPart(UriPartial.Authority) == uri.GetLeftPart(UriPartial.Authority)) ||
            bases.Length != 0 && !bases.Any(x => RatelDeskApiBase.Canonical(x) == canonical) ||
            current.RestrictToConfiguredPeers && origins.Length == 0 && bases.Length == 0)
            throw new UnauthorizedAccessException("receiver-deployment-origin-denied");
        PairingEndpointPolicy.Validate(uri, "Approved RatelDesk API", CurrentAllowPrivateHttp(mode));
        return canonical;
    }

    public void ValidateEndpoint(RatelDeskAuthenticationMode mode, string approvedApiBase, string endpoint)
    {
        var api = ValidateApprovedApiBase(mode, approvedApiBase);
        if (endpoint is not { Length: > 0 and <= 4096 } || endpoint.Any(char.IsControl) ||
            endpoint.IndexOfAny(['\\', '%', '?', '#']) >= 0 ||
            !(endpoint == ReceiverWireValidation.Endpoint(api, ReceiverWireValidation.CapabilitiesPath) ||
              endpoint == ReceiverWireValidation.Endpoint(api, ReceiverWireValidation.CreatePath) ||
              endpoint == ReceiverWireValidation.Endpoint(api, ReceiverWireValidation.TargetsPath) ||
              IsReceiptEndpoint(api, endpoint)))
            throw new InvalidDataException("receiver-endpoint-outside-approved-api-base");
        PairingEndpointPolicy.Validate(new Uri(endpoint, UriKind.Absolute), "RatelDesk receiver endpoint", CurrentAllowPrivateHttp(mode));
    }

    public static bool IsReceiptEndpoint(string canonicalApiBase, string endpoint)
    {
        var prefix = ReceiverWireValidation.Endpoint(canonicalApiBase, ReceiverWireValidation.ReceiptPath)
            .Replace("{key}", "", StringComparison.Ordinal);
        return endpoint.StartsWith(prefix, StringComparison.Ordinal) &&
            RatelDeskReceiverKey.IsConforming(endpoint[prefix.Length..]);
    }
}
