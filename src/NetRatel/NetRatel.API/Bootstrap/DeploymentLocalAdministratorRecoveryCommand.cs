namespace NetRatel.API.Bootstrap;

/// <summary>
/// Resets one existing instance administrator from deployment-owned secret material. It cannot
/// create a replacement owner and is only reachable through an explicit process invocation.
/// </summary>
public sealed class DeploymentLocalAdministratorRecoveryCommand(
    BootstrapOptions options,
    BootstrapInitializationService initialization)
{
    public const string CommandName = "--recover-local-admin";

    public async Task<BootstrapInitializationResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var unattended = options.Unattended;
        if (string.IsNullOrWhiteSpace(unattended.PasswordFile) || string.IsNullOrWhiteSpace(unattended.RecoveryEmail))
        {
            return BootstrapInitializationResult.Invalid("The deployment-local recovery configuration is incomplete.");
        }

        string password;
        try
        {
            password = (await File.ReadAllTextAsync(Path.GetFullPath(unattended.PasswordFile), cancellationToken)
                .ConfigureAwait(false)).TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return BootstrapInitializationResult.Invalid("The recovery password file is unavailable.");
        }

        return await initialization.RecoverAdministratorAsync(unattended.RecoveryEmail, password, cancellationToken)
            .ConfigureAwait(false);
    }
}
