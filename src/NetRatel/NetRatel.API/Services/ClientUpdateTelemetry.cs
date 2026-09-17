using System.Diagnostics.Metrics;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services;

/// <summary>
/// Low-cardinality measurements for the client update authority. Identifiers are
/// intentionally kept out of metric tags and belong only in logs and traces.
/// </summary>
public static class ClientUpdateTelemetry
{
    private static readonly Meter Meter = new("NetRatel", "1.0.0");
    private static readonly Counter<long> Offers = Meter.CreateCounter<long>("client_update_offers_total");
    private static readonly Counter<long> Claims = Meter.CreateCounter<long>("client_update_claims_total");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("client_update_outcomes_total");
    private static readonly Histogram<double> ActivationDuration =
        Meter.CreateHistogram<double>("client_update_activation_duration_seconds", "s");

    private static long _catalogRevision;
    private static long _catalogRefreshedAtUnixSeconds;

    static ClientUpdateTelemetry()
    {
        Meter.CreateObservableGauge("client_update_catalog_revision", () => Volatile.Read(ref _catalogRevision));
        Meter.CreateObservableGauge(
            "client_update_catalog_age_seconds",
            () => Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() -
                Volatile.Read(ref _catalogRefreshedAtUnixSeconds)),
            "s");
    }

    public static void OfferObserved(string runtimeId, string channel) =>
        Offers.Add(1, new("runtime_id", NormalizeRuntime(runtimeId)), new("channel", NormalizeChannel(channel)));

    public static void ClaimObserved(string outcome) =>
        Claims.Add(1, new KeyValuePair<string, object?>[] { new("outcome", NormalizeOutcome(outcome)) });

    public static void OutcomeObserved(ClientUpdateAttemptState state) =>
        Outcomes.Add(1, new KeyValuePair<string, object?>[] { new("outcome", state.ToString()) });

    public static void ActivationCompleted(TimeSpan duration, string outcome) =>
        ActivationDuration.Record(
            Math.Max(0, duration.TotalSeconds),
            new KeyValuePair<string, object?>[] { new("outcome", NormalizeOutcome(outcome)) });

    public static void CatalogRefreshed(long revision, DateTimeOffset refreshedAtUtc)
    {
        Volatile.Write(ref _catalogRevision, revision);
        Volatile.Write(ref _catalogRefreshedAtUnixSeconds, refreshedAtUtc.ToUnixTimeSeconds());
    }

    private static string NormalizeRuntime(string runtimeId) => runtimeId.ToLowerInvariant() switch
    {
        "win-x64" => "win-x64",
        "linux-x64" => "linux-x64",
        _ => "other"
    };

    private static string NormalizeChannel(string channel) =>
        string.Equals(channel, "prerelease", StringComparison.OrdinalIgnoreCase) ? "prerelease" : "stable";

    private static string NormalizeOutcome(string outcome) => outcome.ToLowerInvariant() switch
    {
        "accepted" => "accepted",
        "eligible" => "eligible",
        "ineligible" => "ineligible",
        "rejected" => "rejected",
        "rollback" => "rollback",
        _ => "other"
    };
}
