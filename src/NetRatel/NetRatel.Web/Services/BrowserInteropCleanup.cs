using Microsoft.JSInterop;

namespace NetRatel.Web.Services;

internal static class BrowserInteropCleanup
{
    // Custom observer cleanup and each handle release are independent. A failed
    // optional measurement must never abort navigation or leak the other handles.
    internal static async ValueTask ReleaseAsync(ILogger logger, IJSObjectReference? observer, params IJSObjectReference?[] modules)
    {
        if (observer is not null)
        {
            try { await observer.InvokeVoidAsync("dispose"); }
            catch (Exception error) when (IsCleanupFailure(error)) { logger.LogDebug("Optional browser cleanup failed ({FailureType}); remaining handles will still be released.", error.GetType().Name); }
            finally { await ReleaseReferenceAsync(logger, observer); }
        }
        foreach (var module in modules)
            if (module is not null) await ReleaseReferenceAsync(logger, module);
    }

    private static async ValueTask ReleaseReferenceAsync(ILogger logger, IJSObjectReference reference)
    {
        try { await reference.DisposeAsync(); }
        catch (Exception error) when (IsCleanupFailure(error)) { logger.LogDebug("Optional browser cleanup failed ({FailureType}); remaining handles will still be released.", error.GetType().Name); }
    }

    private static bool IsCleanupFailure(Exception error) => error is JSException or JSDisconnectedException or OperationCanceledException;
}
