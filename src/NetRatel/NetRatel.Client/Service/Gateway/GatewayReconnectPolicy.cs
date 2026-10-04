using System;
using System.IO;
using System.Net.Http;
using Grpc.Core;

namespace NetRatel.Client.Service.Gateway;

/// <summary>One application-owned retry policy; admission alone never clears failure history.</summary>
internal sealed class GatewayReconnectPolicy(TimeProvider timeProvider, Func<double> nextRandom, TimeSpan stabilityThreshold)
{
    private int _failures;
    private long? _healthySince;

    internal void BeginAttempt() => _healthySince = null;

    internal void Acknowledged()
    {
        var now = timeProvider.GetTimestamp();
        _healthySince ??= now;
        if (timeProvider.GetElapsedTime(_healthySince.Value, now) >= stabilityThreshold)
            _failures = 0;
    }

    internal TimeSpan FailureDelay()
    {
        var seconds = Math.Min(Math.Pow(2, Math.Min(_failures, 5)), 30);
        _failures = Math.Min(_failures + 1, 6);
        // 20% jitter prevents synchronized retries while preserving the 30s ceiling.
        var random = nextRandom();
        if (!double.IsFinite(random) || random < 0 || random > 1)
            throw new InvalidOperationException("Gateway retry randomness must be between zero and one.");
        return TimeSpan.FromSeconds(Math.Min(seconds * (0.8 + 0.4 * random), 30));
    }

    internal static bool IsRecoverable(Exception exception) => exception switch
    {
        RpcException rpc => rpc.StatusCode is StatusCode.Unavailable or StatusCode.Internal or
            StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted or StatusCode.Cancelled,
        HttpRequestException or IOException => true,
        _ => false
    };
}
