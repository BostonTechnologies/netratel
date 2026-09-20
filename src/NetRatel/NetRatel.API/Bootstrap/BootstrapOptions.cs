using Microsoft.Extensions.Configuration;

namespace NetRatel.API.Bootstrap;

public sealed record BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public string StateDirectory { get; init; } = "/var/netratel/bootstrap";
    public string? SetupProofPath { get; init; }
    public TimeSpan SetupProofLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan OperationLeaseDuration { get; init; } = TimeSpan.FromMinutes(10);
    public string[] AllowedOrigins { get; init; } = [];

    public static BootstrapOptions FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration.GetSection(SectionName).Get<BootstrapOptions>() ?? new BootstrapOptions();
        if (string.IsNullOrWhiteSpace(configured.StateDirectory))
        {
            throw new InvalidOperationException("Bootstrap:StateDirectory must be configured.");
        }

        if (configured.SetupProofLifetime <= TimeSpan.Zero || configured.SetupProofLifetime > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("Bootstrap:SetupProofLifetime must be greater than zero and no more than one day.");
        }

        if (configured.OperationLeaseDuration <= TimeSpan.Zero || configured.OperationLeaseDuration > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("Bootstrap:OperationLeaseDuration must be greater than zero and no more than one hour.");
        }

        return configured with
        {
            StateDirectory = Path.GetFullPath(configured.StateDirectory),
            SetupProofPath = string.IsNullOrWhiteSpace(configured.SetupProofPath)
                ? null
                : Path.GetFullPath(configured.SetupProofPath)
        };
    }
}
