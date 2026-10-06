using Microsoft.Playwright;
using Xunit.Sdk;

namespace NetRatel.Web.PlaywrightTests;

public sealed class ManualHelpdeskServiceDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_failure_publishes_only_phase_and_exception_type(bool afterAwait)
    {
        var privateValue = "fixture-only-private-" + Guid.NewGuid().ToString("N");
        var failure = await Record.ExceptionAsync(() => ManualHelpdeskServiceDiagnostics.RunAsync(async setPhase =>
        {
            setPhase(ManualHelpdeskServicePhase.IssueToken);
            if (afterAwait) await Task.Yield();
            throw new PlaywrightException("Call log: Authorization: Bearer " + privateValue);
        }));

        AssertSafeFailure(failure, privateValue, ManualHelpdeskServicePhase.IssueToken, nameof(PlaywrightException));
    }

    [Fact]
    public async Task Cleanup_failure_drops_nested_exception_and_request_data()
    {
        var privateValue = "fixture-only-private-" + Guid.NewGuid().ToString("N");
        var failure = await Record.ExceptionAsync(() => ManualHelpdeskServiceDiagnostics.RunAsync(async setPhase =>
        {
            setPhase(ManualHelpdeskServicePhase.RevokeClient);
            try
            {
                await Task.Yield();
            }
            finally
            {
                var cleanupFailure = new IOException(privateValue, new InvalidOperationException(privateValue));
                cleanupFailure.Data["request-body"] = privateValue;
                throw cleanupFailure;
            }
        }));

        AssertSafeFailure(failure, privateValue, ManualHelpdeskServicePhase.RevokeClient, nameof(IOException));
    }

    [Fact]
    public async Task Successful_journey_is_awaited()
    {
        var completed = false;
        await ManualHelpdeskServiceDiagnostics.RunAsync(async setPhase =>
        {
            setPhase(ManualHelpdeskServicePhase.VerifyCatalog);
            await Task.Yield();
            completed = true;
        });

        Assert.True(completed);
    }

    private static void AssertSafeFailure(Exception? failure, string privateValue, ManualHelpdeskServicePhase phase, string sourceExceptionType)
    {
        var expected = $"Manual service Compose acceptance failed in phase '{phase}' ({sourceExceptionType}); raw exception and request diagnostics are excluded.";
        // Boolean assertions deliberately avoid printing either privateValue or
        // the actual exception/message if the diagnostics boundary regresses.
        Assert.True(failure is XunitException, "The journey must fail with a sanitized test exception.");
        Assert.True(failure?.Message == expected, "Public diagnostics must contain only the fixed phase and exception type.");
        Assert.True(failure?.InnerException is null, "Public diagnostics must not retain the original exception.");
        Assert.True(failure?.Data.Count == 0, "Public diagnostics must not retain request data.");
        Assert.True(failure is not null && !failure.ToString().Contains(privateValue, StringComparison.Ordinal), "Rendered diagnostics must exclude private request values.");
    }
}
