using Xunit.Sdk;

namespace NetRatel.Web.PlaywrightTests;

internal enum ManualHelpdeskServicePhase
{
    Prerequisites,
    PublicSettings,
    CreateClient,
    IssueToken,
    VerifyCatalog,
    RotateClient,
    RevokeClient,
    VerifyBrowserCleanup
}

internal static class ManualHelpdeskServiceDiagnostics
{
    public static async Task RunAsync(Func<Action<ManualHelpdeskServicePhase>, Task> journey)
    {
        var phase = ManualHelpdeskServicePhase.Prerequisites;
        try
        {
            await journey(value => phase = value);
        }
        catch (Exception exception)
        {
            // Playwright request errors can include Authorization headers in their
            // call log. Cover the entire journey and its disposal without retaining
            // the original message, stack, data or inner exception in public TRX.
            throw new XunitException($"Manual service Compose acceptance failed in phase '{phase}' ({exception.GetType().Name}); raw exception and request diagnostics are excluded.");
        }
    }
}
