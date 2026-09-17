using NetRatel.Application.Abstractions;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Services;

public sealed record McpOperatorConnectivitySettings(
    bool Enabled,
    bool RemoteEndpointConfigured,
    bool RemoteAudienceConfigured,
    string? RemoteSystemName);

public sealed record McpOperatorNetRatelConnectivity(
    bool BaseUrlConfigured,
    bool AuthorityConfigured,
    bool AudienceConfigured,
    bool TokenEndpointConfigured,
    bool ScopeConfigured);

public sealed record McpOperatorConnectivityProbe(
    string Name,
    string Status,
    int? HttpStatus,
    long? LatencyMilliseconds,
    string? RemoteSystemName);

public sealed record McpOperatorConnectivityTestResult(
    IReadOnlyList<McpOperatorConnectivityProbe> Probes,
    bool TimedOut);

public interface IMcpOperatorConnectivityAuthority
{
    Task<McpOperatorConnectivitySettings> GetSettingsAsync(CancellationToken cancellationToken);
    McpOperatorNetRatelConnectivity GetNetRatel();
    Task<McpOperatorConnectivityTestResult> TestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs only the server-owned ExternalService M2M probes.  Callers cannot supply a
/// host, path, headers, timeout, or settings override; result details are
/// reduced to safe probe health facts.
/// </summary>
public sealed class McpOperatorConnectivityAuthority(
    IM2MConnectivityService connectivity,
    IConfiguration configuration) : IMcpOperatorConnectivityAuthority
{
    private static readonly TimeSpan MaximumProbeDuration = TimeSpan.FromSeconds(10);

    public async Task<McpOperatorConnectivitySettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await connectivity.GetAsync(cancellationToken).ConfigureAwait(false);
        return new(
            settings.Enabled,
            !string.IsNullOrWhiteSpace(settings.RemoteBaseUrl),
            !string.IsNullOrWhiteSpace(settings.RemoteAudience),
            string.IsNullOrWhiteSpace(settings.RemoteSystemName) ? null : settings.RemoteSystemName);
    }

    public McpOperatorNetRatelConnectivity GetNetRatel()
    {
        var authority = configuration["M2M:Authority"];
        var audience = configuration["M2M:Audience"];
        return new(
            !string.IsNullOrWhiteSpace(configuration["Orchestration:NetRatel:BaseUrl"] ?? configuration["NetRatelApi:BaseUrl"]),
            !string.IsNullOrWhiteSpace(authority),
            !string.IsNullOrWhiteSpace(audience),
            !string.IsNullOrWhiteSpace(authority),
            !string.IsNullOrWhiteSpace(audience));
    }

    public async Task<McpOperatorConnectivityTestResult> TestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaximumProbeDuration);
        try
        {
            var result = await connectivity.TestAsync(new M2MConnectivityTestRequestDto(true, true), timeout.Token).ConfigureAwait(false);
            return new(result.Probes.Select(ToProbe).ToArray(), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], true);
        }
        catch
        {
            return new([new McpOperatorConnectivityProbe("Connectivity", "Red", null, null, null)], false);
        }
    }

    private static McpOperatorConnectivityProbe ToProbe(M2MConnectivityProbeResultDto probe) => new(
        probe.ProbeName,
        probe.Status.ToString(),
        probe.HttpStatus,
        probe.LatencyMs,
        string.IsNullOrWhiteSpace(probe.RemoteSystemName) ? null : probe.RemoteSystemName);
}
