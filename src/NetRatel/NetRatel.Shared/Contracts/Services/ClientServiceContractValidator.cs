namespace NetRatel.Shared.Contracts.Services;

/// <summary>Bounded transport-neutral validation; errors contain fixed categories, never supplied values.</summary>
public static class ClientServiceContractValidator
{
    public static StringComparer GetServiceNameComparer(ClientServicePlatform platform) =>
        platform == ClientServicePlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool IsValidServiceName(string? name, ClientServicePlatform platform = ClientServicePlatform.Unknown)
    {
        if (name is null || !IsBoundedText(name, ClientServicesLimits.MaximumNameLength, allowEmpty: false) ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return platform switch
        {
            ClientServicePlatform.Windows => !name.Contains('/') && !name.Contains('\\'),
            ClientServicePlatform.LinuxSystemd => name.EndsWith(".service", StringComparison.Ordinal) &&
                                                  !name.StartsWith('-') && !name.Contains('/'),
            ClientServicePlatform.Unknown => !name.Contains('/'),
            _ => false
        };
    }

    public static bool TryValidateObservation(ClientServiceObservation? observation, out string? error)
    {
        error = null;
        if (observation is null || observation.Platform is not (ClientServicePlatform.Windows or ClientServicePlatform.LinuxSystemd) ||
            !Enum.IsDefined(observation.State) || !IsValidServiceName(observation.Name, observation.Platform) ||
            !IsBoundedText(observation.DisplayName, ClientServicesLimits.MaximumDisplayNameLength) ||
            !IsBoundedText(observation.RawState, ClientServicesLimits.MaximumRawStateLength) ||
            !IsOptionalState(observation.StartMode) || !IsOptionalState(observation.LoadState) ||
            !IsOptionalState(observation.ActiveState) || !IsOptionalState(observation.SubState) ||
            !IsOptionalState(observation.UnitFileState) || observation.ObservedAtUtc == default)
        {
            error = "invalid_service_observation";
            return false;
        }

        if ((observation.State == ClientServiceState.Missing) != observation.AuthoritativeMissing)
        {
            error = "invalid_missing_evidence";
            return false;
        }

        return true;
    }

    public static bool TryValidateResult(ServiceCollectionResult? result, out string? error)
    {
        error = null;
        if (result is null || result.CollectionId == Guid.Empty ||
            result.Kind is not (ServiceSnapshotKind.Inventory or ServiceSnapshotKind.Watch) ||
            result.Status is not (ServiceCollectionStatus.Complete or ServiceCollectionStatus.Partial or ServiceCollectionStatus.Error or ServiceCollectionStatus.Unsupported) ||
            result.ObservedAtUtc == default || result.Services is null || !IsValidErrorCode(result.ErrorCode))
        {
            error = "invalid_collection";
            return false;
        }

        var limit = result.Kind == ServiceSnapshotKind.Watch ? ClientServicesLimits.MaximumWatchServices : ClientServicesLimits.MaximumServices;
        if (result.Services.Count > limit)
        {
            error = "service_count_exceeded";
            return false;
        }

        if ((result.Status is ServiceCollectionStatus.Error or ServiceCollectionStatus.Unsupported) && result.Services.Count != 0)
        {
            error = "invalid_collection_evidence";
            return false;
        }

        var windowsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linuxNames = new HashSet<string>(StringComparer.Ordinal);
        ClientServicePlatform? platform = null;
        foreach (var observation in result.Services)
        {
            if (!TryValidateObservation(observation, out error)) return false;
            if (platform.HasValue && platform.Value != observation.Platform)
            {
                error = "mixed_service_platforms";
                return false;
            }
            platform = observation.Platform;
            var names = observation.Platform == ClientServicePlatform.Windows ? windowsNames : linuxNames;
            if (!names.Add(observation.Name))
            {
                error = "duplicate_service_name";
                return false;
            }
            if (observation.AuthoritativeMissing && result.Status != ServiceCollectionStatus.Complete)
            {
                error = "invalid_missing_evidence";
                return false;
            }
        }

        return true;
    }

    public static bool IsValidPolicy(ClientServiceWatchPolicyDto policy, DateTimeOffset now) =>
        TryValidatePolicy(policy, now, out _);

    public static bool TryValidatePolicy(ClientServiceWatchPolicyDto? policy, DateTimeOffset now, out string? error)
    {
        error = null;
        if (policy is null || policy.Revision == 0 || policy.ServiceNames is null ||
            policy.ServiceNames.Count > ClientServicesLimits.MaximumWatchServices ||
            policy.WatchIntervalSeconds < ClientServicesLimits.MinimumWatchIntervalSeconds ||
            policy.WatchIntervalSeconds > ClientServicesLimits.MaximumWatchIntervalSeconds ||
            policy.InventoryIntervalSeconds < ClientServicesLimits.MinimumInventoryIntervalSeconds ||
            policy.InventoryIntervalSeconds > ClientServicesLimits.MaximumInventoryIntervalSeconds ||
            policy.ExpiresAtUtc <= now || policy.ExpiresAtUtc - now > ClientServicesLimits.MaximumPolicyLifetime ||
            policy.RefreshRequestId == Guid.Empty)
        {
            error = "invalid_watch_policy";
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in policy.ServiceNames)
        {
            if (!IsValidServiceName(name) || !names.Add(name))
            {
                error = "invalid_watch_selection";
                return false;
            }
        }

        return true;
    }

    public static bool IsValidErrorCode(string? code) => code is null ||
        code.Length <= ClientServicesLimits.MaximumErrorCodeLength &&
        code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool IsOptionalState(string? value) =>
        value is null || IsBoundedText(value, ClientServicesLimits.MaximumRawStateLength);

    private static bool IsBoundedText(string? value, int maximumLength, bool allowEmpty = true) =>
        value is not null && value.Length <= maximumLength && (allowEmpty || !string.IsNullOrWhiteSpace(value)) &&
        HasValidTextCharacters(value);

    // These limits count UTF-16 characters. Encoded frame bytes are measured by
    // the transport; malformed surrogate pairs must not change names during UTF-8 encoding.
    private static bool HasValidTextCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) || char.IsLowSurrogate(character)) return false;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                index++;
            }
        }
        return true;
    }
}
