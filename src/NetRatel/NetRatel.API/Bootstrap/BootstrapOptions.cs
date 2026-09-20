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
    public BootstrapUnattendedOptions Unattended { get; init; } = new();

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

/// <summary>
/// Deployment-owned, non-interactive first-administrator inputs. The password is intentionally
/// file-backed so an invocation never needs a password on its command line or in shell history.
/// </summary>
public sealed record BootstrapUnattendedOptions
{
    public string? PasswordFile { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string? TenantName { get; init; }
    public string? RecoveryEmail { get; init; }
}
