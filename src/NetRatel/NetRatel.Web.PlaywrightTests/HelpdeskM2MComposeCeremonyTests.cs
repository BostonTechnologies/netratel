using Microsoft.Playwright;
using Xunit.Sdk;

namespace NetRatel.Web.PlaywrightTests;

[CollectionDefinition(HelpdeskOwnerComposeCollection.Name, DisableParallelization = true)]
public sealed class HelpdeskOwnerComposeCollection
{
    public const string Name = "Actual Helpdesk owner Compose";
}

/// <summary>Proposed actual two-Web acceptance. No ingress/protocol/authentication fixture replaces either product.</summary>
[Trait("category", "compose")]
[Collection(HelpdeskOwnerComposeCollection.Name)]
public sealed class HelpdeskM2MComposeCeremonyTests
{
    [Theory]
    [InlineData("connected", true)]
    [InlineData("connected", false)]
    [InlineData("signed-out", true)]
    [InlineData("signed-out", false)]
    [InlineData("peer-loss", true)]
    [InlineData("peer-loss", false)]
    public async Task Actual_owners_approve_and_recover_the_same_reciprocal_link(string scenario, bool netRatelInitiates)
    {
        LiveOwnerPair? pair = null;
        IPage? page = null;
        var phase = "start-isolated-products";
        try
        {
            pair = await LiveOwnerPair.StartAsync(scenario, netRatelInitiates);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            // Deliberately no trace, video, HAR, storage-state export, request dump,
            // browser routing, or service-secret/token inspection.
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 1440, Height = 900 }
            });
            page = await context.NewPageAsync();
            page.SetDefaultTimeout(20_000);
            pair.ObserveOnlyOwnerCommandCounts(page);
            phase = "actual-setup-and-human-sign-in";
            await pair.CompleteSetupAndSignInAsync(page);
            phase = "owned-resource-prerequisites";
            await pair.CreateCurrentPrerequisitesAsync(context.APIRequest);
            phase = "owner-public-identity-and-producer-mapping";
            await pair.ConfigurePublicIdentityThroughUiAsync(page);
            await pair.VerifyPublicMetadataAsync();
            if (scenario == "signed-out") await pair.SignOutResponderAsync(page);

            phase = "owner-start";
            await pair.StartThroughUiAsync(page);
            if (scenario == "signed-out")
            {
                phase = "clean-sign-in-and-original-owner-continue";
                await pair.ExpectCleanSignInAsync(page);
                var original = await pair.ReadOriginalObservationAsync(context.APIRequest);
                await pair.SignInResponderAsync(page);
                await pair.ContinueThroughUiAsync(page, original);
            }
            phase = "protected-responder-review";
            await pair.ExpectCleanConsentRouteAsync(page, responder: true);
            await pair.AssertNoBusinessAuthorityAsync(context.APIRequest);
            phase = "responder-exact-final-consent";
            await pair.ApproveResponderThroughUiAsync(page, captureVisuals: scenario == "connected");
            phase = "original-initiator-exact-final-review";
            await pair.ExpectCleanConsentRouteAsync(page, responder: false);
            await pair.AssertExactStoredGrantsAndDisplayedDetailsAsync(page);
            await pair.AssertNoBusinessAuthorityAsync(context.APIRequest);
            if (scenario == "connected") await pair.CaptureSafeVisualsAsync(page, "initiator-final");

            if (scenario == "peer-loss")
            {
                phase = "real-peer-loss-before-final-consent";
                await pair.StopResponderAsync();
                try
                {
                    phase = "durable-consent-and-truthful-partial-ui";
                    await pair.ConfirmFinalThroughUiAsync(page);
                    await pair.ExpectIncompleteUiAndDurableConsentAsync(page);
                }
                finally
                {
                    phase = "restore-only-owned-peer";
                    await pair.RestartResponderAsync();
                }
                phase = "rendered-idempotent-resume";
                await pair.ResumeThroughUiAsync(page);
            }
            else await pair.ConfirmFinalThroughUiAsync(page);

            phase = "both-real-web-cards-and-authority";
            await pair.WaitForConnectedAsync(page);
            await pair.AssertOnlyOriginalAttemptsClientsAndConsentsAsync();
            phase = "actual-read-only-connection-test";
            await pair.AssertReadOnlyProbeThroughUiAsync(page);
            phase = "safe-success-receipt";
            await pair.WriteReceiptAsync("passed", phase, page);
        }
        catch (Exception exception)
        {
            if (pair is not null) await pair.WriteReceiptAsync("failed", phase, page);
            // Playwright diagnostics may contain callback/login URLs, DOM inputs or
            // redirect proof. Failure stays a failure, with phase/type only in public TRX.
            throw new XunitException($"Actual owner Compose acceptance failed in phase '{phase}' ({exception.GetType().Name}); inspect the allowlisted receipt. No raw exception or browser trace is published.");
        }
        finally
        {
            if (pair is not null) await pair.DisposeAsync();
        }
    }
}
