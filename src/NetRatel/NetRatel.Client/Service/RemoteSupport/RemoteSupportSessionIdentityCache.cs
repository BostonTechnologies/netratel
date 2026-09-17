using System;
using System.Collections.Generic;
using System.Linq;

namespace NetRatel.Client.Service.RemoteSupport;

internal sealed class RemoteSupportSessionIdentityCache
{
    private readonly object _sync = new();
    private readonly Dictionary<uint, Entry> _entries = new();

    public string? ResolveSid(
        uint sessionId,
        string? domain,
        string? username,
        string? observedSid,
        bool connected)
    {
        var account = AccountIdentity.Create(domain, username);
        lock (_sync)
        {
            if (!connected || account is null)
            {
                _entries.Remove(sessionId);
                return null;
            }

            if (_entries.TryGetValue(sessionId, out var cached) && !cached.Account.Matches(account))
            {
                _entries.Remove(sessionId);
                cached = null;
            }

            if (!string.IsNullOrWhiteSpace(observedSid))
            {
                var sid = observedSid.Trim();
                _entries[sessionId] = new Entry(account.Merge(cached?.Account), sid);
                return sid;
            }

            return cached?.Sid;
        }
    }

    public void Reconcile(IEnumerable<uint> observedSessionIds)
    {
        var observed = observedSessionIds.ToHashSet();
        lock (_sync)
        {
            foreach (var staleSessionId in _entries.Keys.Where(id => !observed.Contains(id)).ToArray())
            {
                _entries.Remove(staleSessionId);
            }
        }
    }

    internal bool Contains(uint sessionId)
    {
        lock (_sync)
        {
            return _entries.ContainsKey(sessionId);
        }
    }

    private sealed record Entry(AccountIdentity Account, string Sid);

    private sealed record AccountIdentity(string Username, string? Domain)
    {
        public static AccountIdentity? Create(string? domain, string? username)
        {
            var normalizedUsername = Normalize(username);
            return normalizedUsername is null
                ? null
                : new AccountIdentity(normalizedUsername, Normalize(domain));
        }

        public bool Matches(AccountIdentity other) =>
            string.Equals(Username, other.Username, StringComparison.Ordinal) &&
            (Domain is null || other.Domain is null || string.Equals(Domain, other.Domain, StringComparison.Ordinal));

        public AccountIdentity Merge(AccountIdentity? other) =>
            new(Username, Domain ?? other?.Domain);

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }
}
