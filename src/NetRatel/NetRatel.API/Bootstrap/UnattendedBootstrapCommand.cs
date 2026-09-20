namespace NetRatel.API.Bootstrap;

/// <summary>
/// Performs an explicitly invoked, deployment-local initialization through the same proof claim
/// and transactional initializer used by the browser wizard. It is never called during startup.
/// </summary>
public sealed class UnattendedBootstrapCommand(
    BootstrapOptions options,
    BootstrapLifecycleService lifecycle,
    BootstrapInitializationService initialization)
{
    public const string CommandName = "--initialize-unattended";

    public async Task<BootstrapInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var unattended = options.Unattended;
        if (string.IsNullOrWhiteSpace(unattended.PasswordFile) ||
            string.IsNullOrWhiteSpace(unattended.DisplayName) ||
            string.IsNullOrWhiteSpace(unattended.Email) ||
            string.IsNullOrWhiteSpace(unattended.TenantName))
        {
            return BootstrapInitializationResult.Invalid("The unattended setup configuration is incomplete.");
        }

        string password;
        try
        {
            // Secret-file writers commonly terminate the value with one newline. Preserve all
            // other whitespace, which is valid in an administrator passphrase.
            password = (await File.ReadAllTextAsync(Path.GetFullPath(unattended.PasswordFile), cancellationToken)
                .ConfigureAwait(false)).TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return BootstrapInitializationResult.Invalid("The unattended password file is unavailable.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return BootstrapInitializationResult.Invalid("The unattended password file is empty.");
        }

        string proof;
        try
        {
            var proofPath = options.SetupProofPath ?? Path.Combine(options.StateDirectory, "setup-proof");
            proof = (await File.ReadAllTextAsync(proofPath, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BootstrapInitializationResult.Invalid("The deployment setup proof is unavailable.");
        }

        var claim = await lifecycle.ClaimSetupAsync(proof, cancellationToken).ConfigureAwait(false);
        if (!claim.Succeeded || claim.Descriptor?.OperationId is not { } operationId)
        {
            return BootstrapInitializationResult.Rejected;
        }

        return await initialization.InitializeAsync(
            operationId,
            new BootstrapInitializationRequest(unattended.DisplayName, unattended.Email, password, unattended.TenantName),
            cancellationToken).ConfigureAwait(false);
    }
}
