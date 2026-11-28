using System;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Client.Service.Tasks;

internal readonly record struct ProtectedJobDispatchLease(TimeProvider Clock, long ReceivedAt, TimeSpan Remaining)
{
    public bool IsExpired => Clock.GetElapsedTime(ReceivedAt) >= Remaining;

    public static ProtectedJobDispatchLease? FromServerExpiry(
        DateTimeOffset expiresAt, TimeProvider clock, DateTimeOffset? serverAtAcceptance, long helloSentAt)
    {
        var receivedAt = clock.GetTimestamp();
        // The server samples its time after receiving hello. Measuring from before
        // hello charges the full handshake to the budget, so transit cannot extend it.
        var remaining = serverAtAcceptance is { } serverTime
            ? expiresAt - serverTime - clock.GetElapsedTime(helloSentAt, receivedAt)
            : expiresAt - clock.GetUtcNow(); // Compatibility with servers without a clock sample.
        var lifetime = ProtectedJobParameterRules.DispatchLifetime;
        if (remaining <= TimeSpan.Zero || remaining > lifetime + ProtectedJobParameterRules.MaximumClockSkew)
            return null;
        return new(clock, receivedAt, remaining < lifetime ? remaining : lifetime);
    }
}
