using System;
using FluentAssertions;
using NetRatel.Shared.Tooling;
using Xunit;

namespace NetRatel.Shared.Tests;

public class TaskLeaseTests
{
    [Fact]
    public void Renew_Extends_Expiry()
    {
        var now = DateTime.UtcNow;
        var lease = new TaskLease("agent", now, TimeSpan.FromSeconds(30));

        var later = now.AddSeconds(10);
        lease.Renew(later, TimeSpan.FromSeconds(30));

        lease.ExpiresAtUtc.Should().Be(later.AddSeconds(30));
    }

    [Fact]
    public void Cancel_Prevents_Renewal()
    {
        var lease = new TaskLease("agent", DateTime.UtcNow, TimeSpan.FromSeconds(30));
        lease.Cancel();

        Action act = () => lease.Renew(DateTime.UtcNow, TimeSpan.FromSeconds(30));
        act.Should().Throw<InvalidOperationException>();
    }
}
