using System;

namespace NetRatel.Shared.Tooling;

/// <summary>
/// Represents a simple task lease that can be renewed or cancelled.
/// </summary>
public class TaskLease
{
    /// <summary>Owner of the lease.</summary>
    public string Owner { get; }

    /// <summary>The UTC time when the lease expires.</summary>
    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Indicates whether the lease has been cancelled.</summary>
    public bool IsCancelled { get; private set; }

    public TaskLease(string owner, DateTime utcNow, TimeSpan duration)
    {
        Owner = owner;
        ExpiresAtUtc = utcNow.Add(duration);
    }

    /// <summary>Renews the lease for the specified <paramref name="duration"/>.</summary>
    public void Renew(DateTime utcNow, TimeSpan duration)
    {
        if (IsCancelled)
        {
            throw new InvalidOperationException("Cannot renew a cancelled lease.");
        }

        ExpiresAtUtc = utcNow.Add(duration);
    }

    /// <summary>Cancels the lease.</summary>
    public void Cancel()
    {
        IsCancelled = true;
    }
}
