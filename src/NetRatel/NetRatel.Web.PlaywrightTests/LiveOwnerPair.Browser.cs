using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NetRatel.Shared.ServiceLinks;
using static Microsoft.Playwright.Assertions;

namespace NetRatel.Web.PlaywrightTests;

internal sealed partial class LiveOwnerPair
{
    public void ObserveOnlyOwnerCommandCounts(IPage page)
    {
        foreach (var action in new[] { "start", "respond", "confirm", "continue" }) commandCounts[action] = 0;
        page.PageError += (_, _) => Interlocked.Increment(ref pageErrorCount);
        page.Response += (_, response) =>
        {
            if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var address)) return;
            if (address.GetLeftPart(UriPartial.Authority) != NetRatelWeb && address.GetLeftPart(UriPartial.Authority) != RatelDeskWeb) return;
            // Inspect path and required header directives only; never read/export
            // the query or redirect Location on proof-bearing browser hops.
            if (address.AbsolutePath is not ("/account/integration-credentials/link/approve" or "/account/integration-credentials/link/callback")) return;
            if (address.AbsolutePath.EndsWith("/approve", StringComparison.Ordinal)) Interlocked.Increment(ref approvalHopCount);
            else Interlocked.Increment(ref callbackHopCount);
            if (!response.Headers.TryGetValue("cache-control", out var cache) || !cache.Split(',').Any(x => x.Trim().Equals("no-store", StringComparison.OrdinalIgnoreCase))
                || !response.Headers.TryGetValue("referrer-policy", out var referrer) || referrer != "no-referrer") Interlocked.Increment(ref protectedHopFailures);
        };
        page.Request += (_, request) =>
        {
            if (request.Method != "POST" || !Uri.TryCreate(request.Url, UriKind.Absolute, out var address)) return;
            if (address.GetLeftPart(UriPartial.Authority) != NetRatelWeb && address.GetLeftPart(UriPartial.Authority) != RatelDeskWeb) return;
            var action = address.AbsolutePath.StartsWith("/account/integration-credentials/link/", StringComparison.Ordinal) ? address.AbsolutePath.Split('/')[^1] : "";
            lock (commandCounts) { if (commandCounts.ContainsKey(action)) commandCounts[action]++; }
        };
    }

    public async Task CompleteSetupAndSignInAsync(IPage page)
    {
        browserApi = page.Context.APIRequest;
        setupStep = "netratel-open-setup";
        await page.GotoAsync(NetRatelWeb + "/", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        setupStep = "netratel-setup-client-ready";
        await page.GetByTestId("setup-client-ready").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        setupStep = "netratel-read-setup-proof";
        var setupProof = (await ComposeAsync(["exec", "-T", "nr-api", "cat", "/var/netratel/bootstrap/setup-proof"])).Trim();
        Check(!string.IsNullOrWhiteSpace(setupProof), "The actual disposable setup proof is absent.");
        setupStep = "netratel-enter-setup-proof";
        await page.GetByTestId("setup-proof").FillAsync(setupProof);
        await page.GetByTestId("setup-proof").PressAsync("Tab");
        setupStep = "netratel-submit-setup-claim";
        await page.GetByTestId("setup-claim").ClickAsync();
        setupStep = "netratel-wait-owner-form";
        await page.GetByTestId("setup-tenant").WaitForAsync();
        setupStep = "netratel-fill-owner-form";
        await page.GetByTestId("setup-tenant").FillAsync("Disposable bootstrap tenant");
        await page.GetByTestId("setup-display-name").FillAsync("Disposable owner");
        await page.GetByTestId("setup-email").FillAsync(humanEmail);
        await page.GetByTestId("setup-password").FillAsync(humanPassword);
        await page.GetByTestId("setup-confirm-password").FillAsync(humanPassword);
        setupStep = "netratel-initialize";
        await page.GetByTestId("setup-initialize").ClickAsync();
        setupStep = "netratel-wait-operational-login";
        await page.GetByTestId("local-login-email").WaitForAsync(new() { Timeout = 120_000 });
        setupStep = "netratel-human-sign-in";
        await SignInAsync(page, netRatel: true);
        setupStep = "rateldesk-human-sign-in";
        await SignInAsync(page, netRatel: false);
    }

    private async Task SignInAsync(IPage page, bool netRatel)
    {
        var web = netRatel ? NetRatelWeb : RatelDeskWeb;
        // Ordinary clean Local form; no copied approval proof or invented ReturnUrl.
        setupStep = netRatel ? "netratel-open-login" : "rateldesk-open-login";
        await page.GotoAsync(web + "/login");
        if (netRatel)
        {
            setupStep = "netratel-login-client-ready";
            await page.GetByTestId("local-login-client-ready").WaitForAsync(new() { State = WaitForSelectorState.Attached });
            await page.GetByTestId("local-login-email").FillAsync(humanEmail);
            await page.GetByTestId("local-login-password").FillAsync(humanPassword);
            await page.GetByTestId("local-login-password").PressAsync("Tab");
            var navigation = page.WaitForURLAsync(new Regex("^" + Regex.Escape(web) + @"/$"), new() { Timeout = 60_000 });
            setupStep = "netratel-submit-login";
            await page.GetByTestId("local-login-submit").ClickAsync();
            await navigation;
        }
        else
        {
            var form = page.GetByTestId("local-login-form");
            setupStep = "rateldesk-login-interactive";
            await Expect(form).ToHaveAttributeAsync("data-interactive", "true");
            await form.GetByLabel("Email", new() { Exact = true }).FillAsync(humanEmail);
            await form.GetByLabel("Password", new() { Exact = true }).FillAsync(humanPassword);
            var navigation = page.WaitForURLAsync(new Regex("^" + Regex.Escape(web) + @"/home$"), new() { Timeout = 60_000 });
            setupStep = "rateldesk-submit-login";
            await form.Locator("button[type=submit]").ClickAsync();
            await navigation;
        }
        setupStep = netRatel ? "netratel-verify-browser-storage" : "rateldesk-verify-browser-storage";
        await RequireNoBrowserProofStorageAsync(page);
    }

    public async Task CreateCurrentPrerequisitesAsync(IAPIRequestContext api)
    {
        var human = new Dictionary<string, string> { ["X-NetRatel-Account-Request"] = "1" };
        var tenant = await ReadJsonAsync(await api.PostAsync(NetRatelWeb + "/api/v1/tenants", new()
        {
            Headers = human, DataObject = new { name = "Owned ceremony target tenant" }
        }), 201);
        tenantId = tenant.GetProperty("tenantId").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var enrollment = await ReadJsonAsync(await api.PostAsync(NetRatelWeb + $"/api/v1/tenants/{tenantId}/enrollment-codes", new()
        {
            Headers = human, DataObject = new { validForMinutes = 10, maxUses = 1, note = "Owned owner-ceremony resource" }
        }), 200);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agent = await ReadJsonAsync(await api.PostAsync(NetRatelWeb + "/api/v1/agents/enroll", new()
        {
            DataObject = new { enrollmentCode = enrollment.GetProperty("enrollmentCode").GetString(), publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), keyAlgorithm = "ecdsa-p256" }
        }), 200);
        agentId = agent.GetProperty("agentId").GetGuid().ToString("D");
        var definition = await ReadJsonAsync(await api.PostAsync(NetRatelWeb + "/api/v1/jobs/", new()
        {
            Headers = human, DataObject = new { name = "Owned inert request definition", folderPath = "/", description = (string?)null, tenantId = int.Parse(tenantId, System.Globalization.CultureInfo.InvariantCulture), clientIdentity = "", agentId }
        }), 201);
        definitionId = definition.GetProperty("id").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var organizations = await ReadJsonAsync(await api.GetAsync(RatelDeskWeb + "/api/v1/organizations/"), 200);
        // A genuinely fresh independently bootstrapped instance has exactly one organization.
        // Capture its canonical returned ID, without cross-product name matching.
        Check(organizations.GetArrayLength() == 1, "The fresh companion organization set is not the expected singleton.");
        organizationId = organizations[0].GetProperty("id").GetString()!;
        var customer = await ReadJsonAsync(await api.PostAsync(RatelDeskWeb + "/api/v1/customers/", new()
        {
            Headers = new Dictionary<string, string> { ["X-Requested-With"] = "XMLHttpRequest" },
            DataObject = new { id = Guid.NewGuid().ToString("D"), name = "Owned ceremony requester", email = "requester@example.test", organizationId, state = 0 }
        }), 201);
        customerId = customer.GetProperty("id").GetString()!;
        var authority = await ReadJsonAsync(await api.GetAsync(NetRatelApi + "/api/v2/account/service-clients/authority"), 200);
        Check(authority.GetProperty("canManage").GetBoolean(), "The actual owner lacks current service management authority.");
        var current = authority.GetProperty("tenants").EnumerateArray().Single(x => x.GetProperty("tenantId").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) == tenantId);
        Check(current.GetProperty("resources").EnumerateArray().Any(x => x.GetProperty("id").GetString() == agentId), "The enrolled resource is absent from actual current authority.");
        Check(current.GetProperty("requestDefinitions").EnumerateArray().Any(x => x.GetProperty("id").GetString() == definitionId && x.GetProperty("resourceId").GetString() == agentId), "The exact definition/resource pairing is absent from actual current authority.");
    }

    public async Task ConfigurePublicIdentityThroughUiAsync(IPage page)
    {
        await page.GotoAsync(IntegrationUrl(netRatel: true, openSetup: true));
        await page.GetByTestId("helpdesk-m2m-setup").WaitForAsync();
        await page.GetByText("Service issuer and public addresses (advanced)", new() { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Enable service identity", Exact = true }).CheckAsync();
        foreach (var (label, value) in new[]
        {
            ("Canonical NetRatel Web base URL", NetRatelWeb), ("Canonical NetRatel REST API base URL", NetRatelApi),
            ("Canonical service issuer", NetRatelIssuer), ("Service API audience", "netratel.owner-browser.services")
        })
        {
            var input = page.GetByLabel(label, new() { Exact = true });
            Check(await input.GetAttributeAsync("readonly") is null, "The fresh candidate unexpectedly locks an owner-configurable public service field.");
            await input.FillAsync(value);
        }
        await page.GetByTestId("helpdesk-save-public-settings").ClickAsync();
        await page.GetByText("The service settings are saved. Continue setup with their current canonical identity.", new() { Exact = true }).WaitForAsync();
        await page.GetByText("Service issuer and public addresses (advanced)", new() { Exact = true }).ClickAsync();
        await page.GetByText("Incident producer identity (advanced)", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("Persisted incident producer GUID", new() { Exact = true }).FillAsync(sourceInstanceId.ToString("D"));
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "I approve this exact persisted producer mapping", Exact = true }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Approve producer mapping", Exact = true }).ClickAsync();
        await Expect(page.GetByLabel("Persisted incident producer GUID", new() { Exact = true })).ToHaveCountAsync(0);
        var identity = await ReadJsonAsync(await browserApi!.GetAsync(NetRatelWeb + "/api/v1/admin/service-links/identity"), 200);
        netRatelInstanceId = identity.GetProperty("instanceId").GetString()!;
        Check(identity.GetProperty("sourceInstanceId").GetString() == sourceInstanceId.ToString("D"), "The actual persisted producer mapping differs from its explicitly adopted seed.");
        Check(netRatelInstanceId != ratelDeskInstanceId.ToString("D") && netRatelInstanceId != sourceInstanceId.ToString("D"), "Installation and producer identities must remain distinct.");
    }

    public async Task StartThroughUiAsync(IPage page)
    {
        await page.GotoAsync(IntegrationUrl(nrInitiates, openSetup: true));
        if (nrInitiates)
        {
            await SelectNetRatelGrantAsync(page);
            await page.GetByTestId("helpdesk-peer-url").FillAsync(RatelDeskWeb);
            await Expect(page.GetByTestId("helpdesk-connect")).ToBeEnabledAsync();
            await page.GetByTestId("helpdesk-connect").ClickAsync();
        }
        else
        {
            await Expect(page.GetByTestId("integration-credentials-page")).ToHaveAttributeAsync("data-interactive", "true");
            await SelectExactIdAsync(page, page.GetByRole(AriaRole.Combobox, new() { Name = "Local organization", Exact = true }), organizationId);
            await SelectExactIdAsync(page, page.GetByRole(AriaRole.Combobox, new() { Name = "Approved incident customer", Exact = true }), customerId);
            await page.GetByLabel("NetRatel Web base URL", new() { Exact = true }).FillAsync(NetRatelWeb);
            await page.GetByRole(AriaRole.Checkbox, new() { Name = "Allow RatelDesk to invoke NetRatel orchestration tasks", Exact = true }).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Continue to NetRatel", Exact = true }).ClickAsync();
        }
    }
    private async Task SelectNetRatelGrantAsync(IPage page)
    {
        await page.GetByTestId("helpdesk-local-tenant").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { NameRegex = new Regex(@"\(tenant " + Regex.Escape(tenantId) + @"\)$") }).ClickAsync();
        await SelectExactIdAsync(page, page.GetByTestId("helpdesk-resources"), agentId);
        await SelectExactIdAsync(page, page.GetByTestId("helpdesk-definitions"), definitionId);
        await page.GetByTestId("helpdesk-invoke").GetByRole(AriaRole.Checkbox).CheckAsync();
    }
    private static async Task SelectExactIdAsync(IPage page, ILocator select, string id)
    {
        await select.ClickAsync();
        var option = page.GetByRole(AriaRole.Option, new() { NameRegex = new Regex(@"\(" + Regex.Escape(id) + @"\)$") });
        await Expect(option).ToHaveCountAsync(1);
        await option.ClickAsync();
        await page.Keyboard.PressAsync("Escape");
    }

    public async Task SignOutResponderAsync(IPage page)
    {
        // Production logout clears only B's session; never remove/import/read A's cookie.
        await page.GotoAsync(ResponderWeb + "/logout");
        await page.WaitForURLAsync(new Regex("^" + Regex.Escape(ResponderWeb) + @"/login(?:\?.*)?$"));
    }
    public Task SignInResponderAsync(IPage page) => SignInAsync(page, netRatel: !nrInitiates);
    public async Task ExpectCleanSignInAsync(IPage page)
    {
        await page.WaitForURLAsync(new Regex("^" + Regex.Escape(ResponderWeb) + @"/login(?:\?.*)?$"));
        var current = new Uri(page.Url);
        Check(current.AbsolutePath == "/login" && !current.Query.Contains("approve", StringComparison.OrdinalIgnoreCase) && !current.Query.Contains("callback", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(Uri.UnescapeDataString(current.Query), "browser_state|pairing_code|initiator_web_base_url|attempt_id|oauth_issuer", RegexOptions.IgnoreCase), "Signed-out approval leaked ceremony correlation into the ordinary login location.");
        if (nrInitiates)
        {
            Check(current.Query == "?status=service-link-sign-in-required", "The companion signed-out guard did not use the known clean login status.");
            await page.GetByText("Sign in here, then return to the initiating product's integration credentials and Continue the existing link.", new() { Exact = true }).WaitForAsync();
        }
        Check(!await page.Locator("body").EvaluateAsync<bool>("body => /browser_state|pairing_code|initiator_web_base_url/.test(body.innerHTML)"), "The ordinary login HTML contains ceremony proof fields.");
        await RequireNoBrowserProofStorageAsync(page);
    }
    public async Task ContinueThroughUiAsync(IPage page, OriginalObservation observation)
    {
        await page.GotoAsync(IntegrationUrl(nrInitiates));
        var form = page.GetByTestId(nrInitiates ? "helpdesk-resume-approval" : "netratel-resume-approval");
        await Expect(form).ToBeVisibleAsync();
        var names = await form.Locator("input").EvaluateAllAsync<string[]>("inputs => inputs.map(input => input.name).sort()");
        Check(names.SequenceEqual(new[] { "__RequestVerificationToken", "attemptId" }.Order(StringComparer.Ordinal)), "The actual Continue form contains unexpected correlation/session inputs.");
        Check(await form.Locator("input[name='attemptId']").InputValueAsync() == observation.AttemptId, "Continue is not bound to the original attempt.");
        await form.GetByRole(AriaRole.Button, new() { Name = nrInitiates ? "Resume RatelDesk approval" : "Continue NetRatel approval", Exact = true }).ClickAsync();
        await ExpectCleanConsentRouteAsync(page, responder: true);
        var continued = await ReadOriginalObservationAsync(browserApi!);
        Check(continued == observation, "Explicit Continue changed the original descriptor/attempt/start count or undecided authority.");
    }

    public async Task ExpectCleanConsentRouteAsync(IPage page, bool responder)
    {
        var web = responder ? ResponderWeb : InitiatorWeb;
        var kind = responder ? "respond" : "review";
        await page.WaitForURLAsync(new Regex("^" + Regex.Escape(web + "/account/integration-credentials/link/" + kind + "/") + @"[A-Za-z0-9_-]+$"), new() { Timeout = 45_000 });
        var current = new Uri(page.Url);
        Check(current.Query.Length == 0 && current.Fragment.Length == 0, "Protected consent did not clean the callback/approval location.");
        var routeAttempt = current.Segments[^1];
        Check(attemptId.Length == 0 || routeAttempt == attemptId, "The protected consent navigated to a different attempt.");
        attemptId = routeAttempt;
        await page.GetByTestId("service-link-consent-details").WaitForAsync();
        await RequireNoBrowserProofStorageAsync(page);
        // Read-only fetch of the exact clean route: verify real production response
        // headers without navigating/resubmitting consent or copying query proof.
        var response = await page.Context.APIRequest.GetAsync(current.GetLeftPart(UriPartial.Path), new() { MaxRedirects = 0 });
        Check(response.Status == 200 && response.Headers.TryGetValue("cache-control", out var cache) && cache.Split(',').Any(x => x.Trim().Equals("no-store", StringComparison.OrdinalIgnoreCase))
            && response.Headers.TryGetValue("referrer-policy", out var referrer) && referrer == "no-referrer", "The actual protected consent response lacks the required secrecy headers.");
        original ??= await ReadOriginalObservationAsync(browserApi!);
    }

    public async Task ApproveResponderThroughUiAsync(IPage page, bool captureVisuals)
    {
        if (nrInitiates)
        {
            await SelectExactIdAsync(page, page.GetByRole(AriaRole.Combobox, new() { Name = "Approve local organization", Exact = true }), organizationId);
            await SelectExactIdAsync(page, page.GetByRole(AriaRole.Combobox, new() { Name = "Approve incident customer", Exact = true }), customerId);
        }
        else await SelectNetRatelGrantAsync(page);
        await AssertDisplayedDetailsAsync(page, responder: true);
        if (captureVisuals) await CaptureSafeVisualsAsync(page, "responder-final");
        var form = nrInitiates ? page.Locator("form[action='/account/integration-credentials/link/respond']") : page.GetByTestId("service-link-responder-approval");
        await form.Locator("input[name='confirmed']").CheckAsync();
        await form.GetByRole(AriaRole.Button, new() { Name = nrInitiates ? "Approve and return to NetRatel" : "Approve and return to RatelDesk", Exact = true }).ClickAsync();
    }
    public async Task ConfirmFinalThroughUiAsync(IPage page)
    {
        var form = page.GetByTestId("service-link-final-approval");
        await Expect(form).ToBeVisibleAsync();
        await form.Locator("input[name='confirmed']").CheckAsync();
        var completedNavigation = page.WaitForResponseAsync(response => response.Request.IsNavigationRequest && response.Request.Method == "GET"
            && Uri.TryCreate(response.Url, UriKind.Absolute, out var address) && address.GetLeftPart(UriPartial.Authority) == InitiatorWeb
            && (address.AbsolutePath.StartsWith("/account/integration-credentials/link/review/", StringComparison.Ordinal) || address.AbsolutePath == "/account/integration-credentials/link/result"));
        await form.GetByRole(AriaRole.Button, new() { Name = "Approve and complete setup", Exact = true }).ClickAsync();
        await completedNavigation;
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitForURLAsync(new Regex("^" + Regex.Escape(InitiatorWeb + "/account/integration-credentials/link/") + @"(?:review/[A-Za-z0-9_-]+|result\?status=[a-z-]+)$"), new() { Timeout = 45_000 });
    }
    private async Task AssertDisplayedDetailsAsync(IPage page, bool responder)
    {
        var details = page.GetByTestId("service-link-consent-details");
        var text = Regex.Replace(await details.InnerTextAsync(), @"\s+", " ");
        foreach (var expected in new[] { netRatelInstanceId, ratelDeskInstanceId.ToString("D"), NetRatelWeb, NetRatelApi, NetRatelIssuer, RatelDeskWeb, RatelDeskApi, RatelDeskIssuer,
            sourceInstanceId.ToString("D"), "Organization: " + organizationId, "Customers: " + customerId, "Resources: " + agentId, "Request definitions: " + definitionId,
            "netratel.orchestration.read", "netratel.orchestration.invoke", "rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.orchestration.callback" })
            Check(text.Contains(expected, StringComparison.Ordinal), "The actual displayed consent omits a canonical instance, selected boundary or exact requested scope.");
        if (responder && nrInitiates)
        {
            Check(text.Contains("Caller tenant: " + tenantId + " · target tenant: " + organizationId, StringComparison.Ordinal)
                && text.Contains("Caller tenant: " + organizationId + " · target tenant: " + tenantId, StringComparison.Ordinal), "The companion responder displays a proposal instead of its selected tenant grant.");
            await page.GetByText("The receiver source namespace remains pending until this local consent is saved.", new() { Exact = true }).WaitForAsync();
        }
        else
        {
            // NR labels distinct organization/tenant kinds; RD labels both as tenant.
            Check(text.Contains(tenantId, StringComparison.Ordinal) && text.Contains(organizationId, StringComparison.Ordinal), "The selected caller/target tenant identity is absent.");
        }
    }

    public async Task CaptureSafeVisualsAsync(IPage page, string phase)
    {
        var route = new Uri(page.Url);
        Check(route.Query.Length == 0 && (route.AbsolutePath.Contains("/respond/", StringComparison.Ordinal) || route.AbsolutePath.Contains("/review/", StringComparison.Ordinal)), "A visual capture was requested on an unsafe ceremony route.");
        Check(await page.Locator("input[type=password], [data-testid='helpdesk-service-secret-reveal'], [data-testid='netratel-secret-reveal'], [data-testid='setup-proof']").CountAsync() == 0,
            "Secret/setup/password inputs must be absent before visual capture.");
        var product = route.GetLeftPart(UriPartial.Authority) == NetRatelWeb ? "netratel" : "rateldesk";
        foreach (var theme in new[] { "light", "dark" })
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.EvaluateAsync("() => document.body.style.zoom = ''");
            await page.GetByTestId("theme-preference-menu").ClickAsync();
            await page.GetByTestId("theme-option-" + theme).ClickAsync();
            await page.WaitForFunctionAsync("expected => document.documentElement.dataset[expected.product === 'netratel' ? 'netratelTheme' : 'helpdeskTheme'] === expected.theme", new { product, theme });
            foreach (var (width, height, zoom, label) in new[] { (1440, 900, 100, "desktop"), (360, 800, 100, "narrow"), (720, 900, 200, "zoom-200") })
            {
                await page.SetViewportSizeAsync(width, height);
                await page.EvaluateAsync("zoom => document.body.style.zoom = zoom === 100 ? '' : '200%'", zoom);
                var consent = page.Locator("form[action^='/account/integration-credentials/link/']").Filter(new() { Has = page.Locator("input[name=confirmed]") });
                var button = consent.GetByRole(AriaRole.Button);
                await button.ScrollIntoViewIfNeededAsync();
                Check(!await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"), "The actual consent page overflows horizontally at a required viewport/zoom.");
                Check(await button.EvaluateAsync<bool>("button => { const r=button.getBoundingClientRect(); return r.left>=-1 && r.top>=-1 && r.right<=innerWidth+1 && r.bottom<=innerHeight+1; }"), "The required final consent button is clipped outside its actual viewport.");
                var file = CaseId + "-" + phase + "-" + product + "-" + theme + "-" + label + ".png";
                var path = Path.Combine(evidenceRoot, "screenshots", file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await page.ScreenshotAsync(new() { Path = path, FullPage = true });
                visuals.Add(new("screenshots/" + file, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), product, phase, theme, width, height, zoom));
            }
        }
        await page.EvaluateAsync("() => document.body.style.zoom = ''");
        await page.SetViewportSizeAsync(1440, 900);
        await page.GetByTestId("theme-preference-menu").ClickAsync();
        await page.GetByTestId("theme-option-light").ClickAsync();
    }
    private static async Task RequireNoBrowserProofStorageAsync(IPage page) => Check(!await page.EvaluateAsync<bool>("() => Object.keys(localStorage).concat(Object.keys(sessionStorage)).some(key => /pairing|verifier|browser.?state|client.?secret/i.test(key))"), "Ceremony proof or service secrets were persisted in browser storage.");
    private async Task<JsonElement> ReadJsonAsync(IAPIResponse response, int expectedStatus)
    {
        lastHttpStatus = response.Status;
        Check(response.Status == expectedStatus, "An actual authorized prerequisite/read-only request returned an unexpected HTTP status.");
        var body = await response.BodyAsync();
        Check(body.Length <= 1024 * 1024, "An actual fixture response exceeded its bounded parser limit.");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
