namespace NetRatel.API.Bootstrap;

/// <summary>Deployment-console commands that do not assemble the operational API host.</summary>
public static class BootstrapOperatorCommand
{
    public const string ShowCode = "--show-setup-code";
    public const string Status = "--setup-status";
    public const string RotateCode = "--rotate-setup-code";

    public static bool IsSupported(string[] args) =>
        args is [ShowCode] or [Status] or [RotateCode];

    public static async Task<int> RunAsync(string command, BootstrapOptions options, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var store = new BootstrapStateStore(options);
        if (command == RotateCode)
        {
            if (options.SetupProofPath is not null)
            {
                await error.WriteLineAsync("This setup code is deployment-owned. Replace the configured Bootstrap:SetupProofPath through your secret store; the API will not overwrite it.").ConfigureAwait(false);
                return 2;
            }

            var result = await store.RotateOperatorProofAsync(cancellationToken).ConfigureAwait(false);
            if (result.Rotated)
            {
                await output.WriteLineAsync("Setup code rotated. Run --show-setup-code to retrieve the new code.").ConfigureAwait(false);
                return 0;
            }

            await error.WriteLineAsync(ExplainUnavailable(result.Snapshot)).ConfigureAwait(false);
            return result.Snapshot.ProofState == BootstrapProofState.Completed ? 4 : ExitCode(result.Snapshot.ProofState);
        }

        var snapshot = await store.InspectOperatorAsync(command == ShowCode, cancellationToken).ConfigureAwait(false);
        if (command == Status)
        {
            await output.WriteLineAsync($"Installation: {snapshot.State?.ToString() ?? "Missing"}").ConfigureAwait(false);
            await output.WriteLineAsync($"Setup code: {snapshot.ProofState}").ConfigureAwait(false);
            if (snapshot.ExpiresAtUtc is { } expiry && snapshot.ProofState is BootstrapProofState.Available or BootstrapProofState.Expired)
                await output.WriteLineAsync($"Expires (UTC): {expiry:O}").ConfigureAwait(false);
            return ExitCode(snapshot.ProofState);
        }

        if (snapshot.ProofState == BootstrapProofState.Available && snapshot.SetupCode is { Length: > 0 } code)
        {
            await output.WriteLineAsync(code).ConfigureAwait(false);
            return 0;
        }

        await error.WriteLineAsync(ExplainUnavailable(snapshot)).ConfigureAwait(false);
        return snapshot.ProofState == BootstrapProofState.Completed ? 4 : ExitCode(snapshot.ProofState);
    }

    private static int ExitCode(BootstrapProofState state) => state switch
    {
        BootstrapProofState.Available or BootstrapProofState.Completed => 0,
        BootstrapProofState.Expired => 3,
        BootstrapProofState.Claimed => 4,
        BootstrapProofState.Recovery or BootstrapProofState.Mismatch => 5,
        _ => 2
    };

    private static string ExplainUnavailable(BootstrapOperatorSnapshot snapshot) => snapshot.ProofState switch
    {
        BootstrapProofState.Expired => "The setup code has expired. Restart the unconfigured API to renew it, or run --rotate-setup-code.",
        BootstrapProofState.Claimed => "Setup has already been claimed. Check --setup-status; rotation cannot reopen it.",
        BootstrapProofState.Completed => "This installation is configured. Sign in with its existing administrator account.",
        BootstrapProofState.Recovery or BootstrapProofState.Mismatch => "Bootstrap state needs recovery. Restore the matching database, bootstrap state, and key material; setup cannot create a replacement owner.",
        _ => "No usable setup code exists. Check --setup-status and the API bootstrap state directory. Start a new installation normally before retrieving its code."
    };
}
