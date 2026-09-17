using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace NetRatel.Client.Service.RemoteDesktop;

internal sealed class InteractiveHelperRegistry
{
    private readonly ConcurrentDictionary<int, ConnectedUserHelper> _helpers = new();
    private readonly string _serviceVersion;

    public InteractiveHelperRegistry(string serviceVersion)
    {
        _serviceVersion = serviceVersion;
    }

    public int Count => _helpers.Count;

    public ConnectedUserHelper? Get(int windowsSessionId) =>
        _helpers.TryGetValue(windowsSessionId, out var helper) ? helper : null;

    public IReadOnlyCollection<ConnectedUserHelper> Snapshot() =>
        _helpers.Values.ToArray();

    public bool TryRegister(
        ConnectedUserHelper candidate,
        out ConnectedUserHelper? replaced,
        out string? rejectionReason)
    {
        replaced = null;
        rejectionReason = null;
        while (true)
        {
            if (!_helpers.TryGetValue(candidate.SessionId, out var current))
            {
                if (_helpers.TryAdd(candidate.SessionId, candidate))
                {
                    return true;
                }

                continue;
            }

            var currentCompatible = VersionBaseMatches(current.Version, _serviceVersion);
            var candidateCompatible = VersionBaseMatches(candidate.Version, _serviceVersion);
            if (currentCompatible && !candidateCompatible)
            {
                rejectionReason = "existing_service_compatible_helper_preferred";
                return false;
            }

            if (_helpers.TryUpdate(candidate.SessionId, candidate, current))
            {
                replaced = current;
                return true;
            }
        }
    }

    public bool Remove(ConnectedUserHelper helper) =>
        ((ICollection<KeyValuePair<int, ConnectedUserHelper>>)_helpers)
            .Remove(new KeyValuePair<int, ConnectedUserHelper>(helper.SessionId, helper));

    public void Clear() => _helpers.Clear();

    private static bool VersionBaseMatches(string? left, string? right) =>
        string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var core = version.Split('+', 2, StringSplitOptions.TrimEntries)[0].Trim();
        return Version.TryParse(core, out var parsed)
            ? $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}"
            : core;
    }
}
