using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Trait("category", "hosted")]
[SupportedOSPlatform("windows")]
public sealed class WindowsHostedOnboardingBrowserSmokeTests
{
    private const string ContextEnvironmentVariable = "NETRATEL_HOSTED_ONBOARDING_CONTEXT_FILE";
    private const string LocalCookieName = "NetRatel.Local";
    private const string DirectoryStateTestId = "client-directory-state";
    private const string ClientCardTestId = "client-card";
    private const int MaxProtectedContextBytes = 65_536;
    private const int MaxProtectedReceiptBytes = 32_768;
    private const int MaxProtectedPasswordBytes = 4_096;
    private const int MaxControlFileBytes = 4_096;
    private static readonly TimeSpan ControlAcknowledgementTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan ControlPollInterval = TimeSpan.FromMilliseconds(200);
    private const string NetworkUnavailableMessage = "The web app could not reach the API. Check web-to-API connectivity, then retry.";
    private static readonly FileSystemRights HandoffModificationRights =
        FileSystemRights.Write |
        FileSystemRights.Delete |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes;

    [Fact]
    public async Task ProductionLocalOnboarding_DirectoryAndScopedIdentitiesUseTheSameAuthenticatedRuntime()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("This hosted acceptance requires the Windows browser runner.");

        var context = await RunSanitizedPhaseAsync("protected-context", LoadAndValidateContextAsync);
        using var api = CreatePublicApiClient(context.PublicOrigin);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Channel = "msedge"
        });

        await using var operatorBrowserContext = await browser.NewContextAsync(CreateStrictContextOptions(
            width: 1440,
            height: 1000,
            ColorScheme.Light));
        var operatorPage = await operatorBrowserContext.NewPageAsync();
        operatorPage.SetDefaultTimeout(20_000);

        var operatorCookie = await RunSanitizedPhaseAsync(
            "operator-authentication",
            async () =>
            {
                var passwordBytes = await ReadProtectedHandoffFileAsync(
                    context.OperatorPasswordFile,
                    context.HandoffRoot,
                    MaxProtectedPasswordBytes);
                var password = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(passwordBytes)
                    .TrimEnd('\r', '\n');
                Require(password.Length > 0, "The protected Local operator credential is empty.");
                return await SignInLocallyAsync(operatorPage, context.WebBaseUri, context.OperatorEmail, password);
            });

        await RunSanitizedPhaseAsync("operator-directory-api", async () =>
        {
            await VerifyLocalSessionIdentityAsync(api, operatorCookie, context.OperatorEmail);
            await VerifyApiVersionAsync(api, operatorCookie, context.ProductVersion);
            await VerifyOnlineDirectoryIdentityAsync(api, operatorCookie, context.AgentId, context.TenantId);
            await VerifyOperatorAgentListAsync(api, operatorCookie, context.TenantId, context.AgentId);
            return true;
        });

        var restrictedTenantId = await RunSanitizedPhaseAsync(
            "restricted-tenant-setup",
            () => CreateRestrictedTenantAsync(api, operatorCookie, context.RunId));

        var tenantAdministrator = await RunSanitizedPhaseAsync(
            "tenant-administrator-setup",
            () => CreateLocalAccountAsync(
                api,
                operatorCookie,
                context.RunId,
                "tenant-administrator",
                "Hosted tenant administrator",
                restrictedTenantId));

        var unprivileged = await RunSanitizedPhaseAsync(
            "no-role-account-setup",
            () => CreateLocalAccountAsync(
                api,
                operatorCookie,
                context.RunId,
                "unprivileged",
                "Hosted user without a role",
                tenantId: null));

        await NavigateToClientDirectoryAsync(operatorPage, context.WebBaseUri);
        await WaitForDirectoryLoadAsync(operatorPage, expectedTrigger: "initial", previousCount: 0);
        await AssertOnlineDirectoryCardAsync(operatorPage, context.AgentId, context.TenantId);
        await operatorPage.Locator("html[data-netratel-theme='light']").WaitForAsync();
        await CaptureDirectoryScreenshotAsync(
            operatorPage,
            context.SafeEvidenceDirectory,
            context.HandoffRoot,
            "clients-operator-desktop-light.png");

        var loadCount = await ReadDirectoryLoadCountAsync(operatorPage);
        await operatorPage.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
        await WaitForDirectoryLoadAsync(operatorPage, expectedTrigger: "manual", loadCount);
        await AssertOnlineDirectoryCardAsync(operatorPage, context.AgentId, context.TenantId);

        await operatorPage.SetViewportSizeAsync(390, 844);
        await operatorPage.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
        await operatorPage.Locator("html[data-netratel-theme='dark']").WaitForAsync();
        await AssertOnlineDirectoryCardAsync(operatorPage, context.AgentId, context.TenantId);
        await CaptureDirectoryScreenshotAsync(
            operatorPage,
            context.SafeEvidenceDirectory,
            context.HandoffRoot,
            "clients-operator-phone-dark.png");

        loadCount = await ReadDirectoryLoadCountAsync(operatorPage);
        await WaitForDirectoryLoadAsync(operatorPage, expectedTrigger: "periodic", loadCount, timeoutMilliseconds: 25_000);
        await AssertOnlineDirectoryCardAsync(operatorPage, context.AgentId, context.TenantId);
        await RunSanitizedPhaseAsync("operator-directory-refresh", async () =>
        {
            await VerifyOnlineDirectoryIdentityAsync(api, operatorCookie, context.AgentId, context.TenantId);
            return true;
        });

        await VerifyApiOutageAndSameCircuitRecoveryAsync(api, context, operatorPage, operatorCookie);

        await VerifyTenantAdministratorBoundaryAsync(
            browser,
            api,
            context,
            operatorPage,
            operatorCookie,
            tenantAdministrator,
            restrictedTenantId);

        await VerifyNoRoleBoundaryAsync(
            browser,
            api,
            context,
            operatorPage,
            operatorCookie,
            unprivileged);

        // The original operator circuit remains alive throughout the isolated
        // logins and API checks. A fresh operator request must still carry its
        // own validated Local session and return the same online identity.
        await RunSanitizedPhaseAsync("operator-after-restricted-circuits", async () =>
        {
            await VerifyOnlineDirectoryIdentityAsync(api, operatorCookie, context.AgentId, context.TenantId);
            return true;
        });
        loadCount = await ReadDirectoryLoadCountAsync(operatorPage);
        await operatorPage.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
        await WaitForDirectoryLoadAsync(operatorPage, expectedTrigger: "manual", loadCount);
        await AssertOnlineDirectoryCardAsync(operatorPage, context.AgentId, context.TenantId);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<HostedContext> LoadAndValidateContextAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The protected hosted context requires Windows ACL validation.");
        var path = Environment.GetEnvironmentVariable(ContextEnvironmentVariable);
        Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path), "The protected hosted context is required.");
        var contextPath = Path.GetFullPath(path!);
        var handoffRoot = Path.GetDirectoryName(contextPath);
        Require(!string.IsNullOrWhiteSpace(handoffRoot), "The protected hosted context root is invalid.");
        handoffRoot = Path.GetFullPath(handoffRoot!);
        ValidateProtectedHandoffRoot(handoffRoot);
        ValidateHandoffPath(contextPath, handoffRoot, requireExists: true, expectDirectory: false);

        HostedContext context;
        try
        {
            var file = await ReadProtectedHandoffFileAsync(contextPath, handoffRoot, MaxProtectedContextBytes);
            Require(file.Length > 0, "The protected hosted context has an invalid size.");
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
            context = ParseContext(document.RootElement);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("The protected hosted context could not be validated.");
        }

        context = ValidateContextShape(context, handoffRoot);
        await ValidateHostedSourceAsync(context);
        await ValidateCandidateArchiveAsync(context, handoffRoot);
        var receipt = await ReadIdentityReceiptAsync(context.IdentityReceiptPath, handoffRoot);
        ValidateIdentityReceipt(context, receipt);
        ValidateHandoffPath(context.SafeEvidenceDirectory, handoffRoot, requireExists: true, expectDirectory: true);
        return context with { AgentId = receipt.AgentId, HandoffRoot = handoffRoot };
    }

    private static HostedContext ParseContext(JsonElement root)
    {
        Require(root.ValueKind == JsonValueKind.Object, "The protected hosted context is malformed.");
        Require(!root.TryGetProperty("fields", out _), "The protected hosted context must be flat JSON.");

        return new HostedContext(
            SchemaVersion: ReadInt32(root, "schemaVersion"),
            Scope: ReadString(root, "scope"),
            SourceSha: ReadString(root, "sourceSha"),
            TestMergeSha: ReadString(root, "testMergeSha"),
            ProductVersion: ReadString(root, "productVersion"),
            RunId: ReadString(root, "runId"),
            PublicOrigin: ReadUri(root, "publicOrigin"),
            ApiBaseUri: ReadUri(root, "apiBaseUri"),
            GatewayBaseUri: ReadUri(root, "gatewayBaseUri"),
            WebBaseUri: ReadUri(root, "webBaseUri"),
            TenantId: ReadInt32(root, "tenantId"),
            OperatorEmail: ReadString(root, "operatorEmail"),
            OperatorPasswordFile: ReadString(root, "operatorPasswordFile"),
            CandidateArchive: ReadString(root, "candidateArchive"),
            CandidateRuntimeId: ReadString(root, "candidateRuntimeId"),
            CandidateVersion: ReadString(root, "candidateVersion"),
            CandidateSha256: ReadString(root, "candidateSha256"),
            InstallRoot: ReadString(root, "installRoot"),
            StateRoot: ReadString(root, "stateRoot"),
            IdentityReceiptPath: ReadString(root, "identityReceiptPath"),
            ControlRequestPath: ReadString(root, "controlRequestPath"),
            ControlAckPath: ReadString(root, "controlAckPath"),
            SafeEvidenceDirectory: ReadString(root, "safeEvidenceDirectory"),
            AgentId: Guid.Empty);
    }

    [SupportedOSPlatform("windows")]
    private static HostedContext ValidateContextShape(HostedContext context, string handoffRoot)
    {
        Require(context.SchemaVersion == 1 && context.Scope == "integrated-onboarding", "The hosted context scope is unsupported.");
        Require(IsLowerHex(context.SourceSha, 40) && IsLowerHex(context.TestMergeSha, 40), "The hosted source identity is malformed.");
        Require(Guid.TryParseExact(context.RunId, "D", out _), "The hosted run identity is malformed.");
        Require(context.ProductVersion.Length is > 0 and <= 64 && context.CandidateVersion == context.ProductVersion, "The candidate version does not match the hosted context.");
        Require(context.TenantId > 0, "The initialized tenant identity is invalid.");
        Require(context.OperatorEmail.Contains('@'), "The Local operator identity is invalid.");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Require(!string.IsNullOrWhiteSpace(programFiles) && !string.IsNullOrWhiteSpace(commonApplicationData),
            "The shipped Windows installation roots are unavailable.");
        Require(PathsEqual(context.InstallRoot, Path.Combine(programFiles, "NetRatel", "Client")),
            "The candidate install root is not the shipped protected default.");
        Require(PathsEqual(context.StateRoot, Path.Combine(commonApplicationData, "NetRatel", "update")),
            "The candidate state root is not the shipped protected default.");
        Require(context.CandidateRuntimeId == "win-x64" && IsLowerHex(context.CandidateSha256, 64), "The candidate archive identity is malformed.");
        Require(IsStrictPublicOrigin(context.PublicOrigin), "The public origin is not a strict HTTPS origin.");
        Require(IsHttpOrigin(context.ApiBaseUri) && IsHttpOrigin(context.GatewayBaseUri), "The private service origins are malformed.");
        Require(SameOrigin(context.PublicOrigin, context.WebBaseUri) && IsStrictPublicOrigin(context.WebBaseUri), "The Web origin does not match the public origin.");

        var normalized = context with
        {
            OperatorPasswordFile = ValidateHandoffPath(context.OperatorPasswordFile, handoffRoot, requireExists: true, expectDirectory: false),
            CandidateArchive = ValidateHandoffPath(context.CandidateArchive, handoffRoot, requireExists: true, expectDirectory: false),
            IdentityReceiptPath = ValidateHandoffPath(context.IdentityReceiptPath, handoffRoot, requireExists: true, expectDirectory: false),
            ControlRequestPath = ValidateHandoffPath(context.ControlRequestPath, handoffRoot, requireExists: false, expectDirectory: false),
            ControlAckPath = ValidateHandoffPath(context.ControlAckPath, handoffRoot, requireExists: false, expectDirectory: false),
            SafeEvidenceDirectory = ValidateHandoffPath(context.SafeEvidenceDirectory, handoffRoot, requireExists: true, expectDirectory: true)
        };
        return normalized;
    }

    private static async Task ValidateHostedSourceAsync(HostedContext context)
    {
        var workflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        var eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
        Require(IsLowerHex(workflowSha, 40) && string.Equals(workflowSha, context.TestMergeSha, StringComparison.Ordinal), "The context does not match the executed workflow revision.");
        Require(!string.IsNullOrWhiteSpace(eventPath) && Path.IsPathFullyQualified(eventPath), "The hosted workflow event is unavailable.");

        try
        {
            using var eventDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(eventPath!));
            if (eventDocument.RootElement.TryGetProperty("pull_request", out var pullRequest) &&
                pullRequest.TryGetProperty("head", out var head) &&
                head.TryGetProperty("sha", out var headSha) &&
                headSha.ValueKind == JsonValueKind.String)
            {
                Require(string.Equals(headSha.GetString(), context.SourceSha, StringComparison.Ordinal), "The context source does not match the pull request head.");
            }
            else
            {
                Require(string.Equals(context.SourceSha, context.TestMergeSha, StringComparison.Ordinal), "The hosted source and tested revisions do not match.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("The hosted workflow source could not be validated.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task ValidateCandidateArchiveAsync(HostedContext context, string handoffRoot)
    {
        try
        {
            ValidateHandoffPath(context.CandidateArchive, handoffRoot, requireExists: true, expectDirectory: false);
            await using var archive = File.OpenRead(context.CandidateArchive);
            var digest = await SHA256.HashDataAsync(archive);
            var actualHash = Convert.ToHexString(digest).ToLowerInvariant();
            Require(string.Equals(actualHash, context.CandidateSha256, StringComparison.Ordinal), "The candidate archive digest does not match its protected receipt.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException("The protected candidate archive could not be verified.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<IdentityReceipt> ReadIdentityReceiptAsync(string path, string handoffRoot)
    {
        try
        {
            var bytes = await ReadProtectedHandoffFileAsync(path, handoffRoot, MaxProtectedReceiptBytes);
            Require(bytes.Length > 0, "The hosted identity receipt has an invalid size.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("fields", out _), "The hosted identity receipt must be flat JSON.");
            return new IdentityReceipt(
                SchemaVersion: ReadInt32(root, "schemaVersion"),
                Scope: ReadString(root, "scope"),
                RunId: ReadString(root, "runId"),
                SourceSha: ReadString(root, "sourceSha"),
                TestMergeSha: ReadString(root, "testMergeSha"),
                ProductVersion: ReadString(root, "productVersion"),
                AgentId: ReadGuid(root, "agentId"),
                TenantId: ReadInt32(root, "tenantId"),
                ConnectionEpoch: ReadInt64(root, "connectionEpoch"),
                ConnectionId: ReadString(root, "connectionId"),
                HeartbeatSequence: ReadInt64(root, "heartbeatSequence"),
                LocalSystem: ReadBoolean(root, "localSystem"),
                SessionZero: ReadBoolean(root, "sessionZero"),
                CurrentServiceProcessMatched: ReadBoolean(root, "currentServiceProcessMatched"),
                FreshnessVerified: ReadBoolean(root, "freshnessVerified"),
                OnlineDirectoryVerified: ReadBoolean(root, "onlineDirectoryVerified"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("The protected hosted identity receipt could not be read.");
        }
    }

    private static void ValidateIdentityReceipt(HostedContext context, IdentityReceipt receipt)
    {
        Require(receipt.SchemaVersion == 1 && receipt.Scope == context.Scope, "The hosted identity receipt scope is invalid.");
        Require(receipt.RunId == context.RunId && receipt.SourceSha == context.SourceSha && receipt.TestMergeSha == context.TestMergeSha, "The identity receipt does not match the hosted run.");
        Require(receipt.ProductVersion == context.ProductVersion && receipt.TenantId == context.TenantId, "The identity receipt does not match the hosted product identity.");
        Require(receipt.AgentId != Guid.Empty && receipt.ConnectionEpoch > 0 &&
                Guid.TryParseExact(receipt.ConnectionId, "D", out var connectionId) && connectionId != Guid.Empty,
            "The protected agent identity receipt is incomplete.");
        Require(receipt.HeartbeatSequence >= 2, "The hosted service has not acknowledged the required heartbeat sequence.");
        Require(receipt.LocalSystem && receipt.SessionZero && receipt.CurrentServiceProcessMatched && receipt.FreshnessVerified && receipt.OnlineDirectoryVerified,
            "The protected service identity receipt did not establish the required current-start readiness.");
    }

    private static async Task<string> SignInLocallyAsync(IPage page, Uri webBaseUri, string email, string password)
    {
        var loginUri = new Uri(webBaseUri, "login?ReturnUrl=%2Fclients");
        var response = await page.GotoAsync(loginUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Require(response is not null && response.Status == (int)HttpStatusCode.OK, "The Local login route is unavailable.");
        await page.GetByTestId("local-login-client-ready").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        await page.GetByTestId("local-login-email").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("local-login-email").FillAsync(email);
        await page.GetByTestId("local-login-email").PressAsync("Tab");
        await page.GetByTestId("local-login-password").FillAsync(password);
        await page.GetByTestId("local-login-password").PressAsync("Tab");

        var loginResponseTask = page.WaitForResponseAsync(response =>
            response.Request.Method == "POST" && IsLocalLoginUrl(response.Url));
        var signedInTask = page.WaitForURLAsync(new Uri(webBaseUri, "clients").ToString(), new PageWaitForURLOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30_000
        });
        await page.GetByTestId("local-login-submit").ClickAsync();
        Require((await loginResponseTask).Status == (int)HttpStatusCode.NoContent, "Local authentication was not accepted.");
        await signedInTask;

        var cookies = await page.Context.CookiesAsync();
        var localCookie = cookies.SingleOrDefault(cookie => cookie.Name == LocalCookieName);
        Require(localCookie is not null && !string.IsNullOrEmpty(localCookie.Value) && localCookie.Secure && localCookie.HttpOnly,
            "The strict Local session cookie was not established.");
        return localCookie!.Value;
    }

    private static async Task VerifyApiVersionAsync(HttpClient api, string cookie, string productVersion)
    {
        using var response = await SendWithCookieAsync(api, HttpMethod.Get, "/api/v1/system/version", cookie);
        RequireStatus(response, HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var expectedDisplayVersion = productVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? productVersion : $"v{productVersion}";
        Require(ReadString(document.RootElement, "displayVersion") == expectedDisplayVersion, "The public API version does not match the protected candidate context.");
    }

    private static async Task VerifyLocalSessionIdentityAsync(HttpClient api, string cookie, string expectedEmail)
    {
        using var response = await SendWithCookieAsync(api, HttpMethod.Get, "/api/v2/local-auth/me", cookie);
        RequireStatus(response, HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        Require(string.Equals(ReadString(document.RootElement, "email"), expectedEmail, StringComparison.OrdinalIgnoreCase),
            "The protected Local cookie resolved to a different account.");
    }

    private static async Task VerifyOnlineDirectoryIdentityAsync(HttpClient api, string cookie, Guid agentId, int tenantId)
    {
        using var response = await SendWithCookieAsync(api, HttpMethod.Get, "/api/v2/client-presence", cookie);
        RequireStatus(response, HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var items = RequiredArray(document.RootElement, "items");
        var matching = items.EnumerateArray()
            .Where(item => ReadGuid(item, "agentId") == agentId && ReadInt32(item, "tenantId") == tenantId)
            .ToArray();
        Require(matching.Length == 1 && ReadBoolean(matching[0], "online"), "The authenticated directory API did not return the expected online identity.");
    }

    private static async Task VerifyOperatorAgentListAsync(HttpClient api, string cookie, int tenantId, Guid agentId)
    {
        using var response = await SendWithCookieAsync(api, HttpMethod.Get, AgentListPath(tenantId), cookie);
        RequireStatus(response, HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var items = RequiredArray(document.RootElement, "items");
        Require(items.EnumerateArray().Any(item => ReadGuid(item, "agentId") == agentId && ReadInt32(item, "tenantId") == tenantId),
            "The authenticated operator agent list did not include the installed identity.");
    }

    private static async Task VerifyApiOutageAndSameCircuitRecoveryAsync(
        HttpClient api,
        HostedContext context,
        IPage operatorPage,
        string operatorCookie)
    {
        var control = new ProtectedApiPhaseControl(context);
        Exception? primaryFailure = null;
        Exception? restorationFailure = null;

        try
        {
            await RunSanitizedPhaseAsync("api-control-initialize", async () =>
            {
                await control.InitializeAsync();
                return true;
            });
            await RunSanitizedPhaseAsync("api-stop-control", async () =>
            {
                await control.SendAsync("stop_api");
                return true;
            });

            await RunSanitizedPhaseAsync("operator-directory-api-outage", async () =>
            {
                var loadCount = await ReadDirectoryLoadCountAsync(operatorPage);
                await operatorPage.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
                await WaitForDirectoryLoadAsync(operatorPage, expectedTrigger: "manual", loadCount);
                await AssertApiUnavailableDirectoryAsync(operatorPage);

                loadCount = await ReadDirectoryLoadCountAsync(operatorPage);
                await operatorPage.GetByTestId("retry-client-directory").ClickAsync();
                await WaitForDirectoryLoadAsync(operatorPage, expectedTrigger: "manual", loadCount);
                await AssertApiUnavailableDirectoryAsync(operatorPage);

                loadCount = await ReadDirectoryLoadCountAsync(operatorPage);
                await WaitForDirectoryLoadAsync(
                    operatorPage,
                    expectedTrigger: "periodic",
                    loadCount,
                    timeoutMilliseconds: 25_000);
                await AssertApiUnavailableDirectoryAsync(operatorPage);
                await CaptureDirectoryScreenshotAsync(
                    operatorPage,
                    context.SafeEvidenceDirectory,
                    context.HandoffRoot,
                    "hosted-directory-api-unavailable.png");
                return true;
            });
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                await RunSanitizedPhaseAsync("api-start-control", async () =>
                {
                    await control.StartApiForCleanupAsync();
                    return true;
                });
            }
            catch (Exception exception)
            {
                restorationFailure = exception;
            }
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (restorationFailure is not null)
            ExceptionDispatchInfo.Capture(restorationFailure).Throw();

        await RunSanitizedPhaseAsync("operator-api-public-recovery", async () =>
        {
            await WaitForOnlineDirectoryIdentityAsync(api, operatorCookie, context.AgentId, context.TenantId);
            return true;
        });

        await RunSanitizedPhaseAsync("operator-directory-same-circuit-recovery", async () =>
        {
            var loadCount = await ReadDirectoryLoadCountAsync(operatorPage);
            var retry = operatorPage.GetByTestId("retry-client-directory");
            if (await retry.IsVisibleAsync())
            {
                await retry.ClickAsync();
            }
            else
            {
                // The page's independent 15-second refresh can recover before
                // the public probe finishes. The registered agent may still be
                // offline until the gateway reconnects, so the explicit manual
                // load below remains the authoritative recovery assertion.
                await operatorPage.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
            }
            await WaitForDirectoryLoadAsync(operatorPage, expectedTrigger: "manual", loadCount);
            await AssertOnlineDirectoryCardAsync(operatorPage, context.AgentId, context.TenantId);
            Require(
                await operatorPage.GetByText(NetworkUnavailableMessage, new() { Exact = true }).CountAsync() == 0,
                "The original operator circuit kept its API connectivity error after recovery.");
            await CaptureDirectoryScreenshotAsync(
                operatorPage,
                context.SafeEvidenceDirectory,
                context.HandoffRoot,
                "hosted-directory-api-recovered.png");
            return true;
        });
    }

    private static async Task AssertApiUnavailableDirectoryAsync(IPage page)
    {
        var retry = page.GetByTestId("retry-client-directory");
        await retry.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var alertMessage = page.GetByText(NetworkUnavailableMessage, new() { Exact = true });
        await alertMessage.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Require(await page.GetByText("No registered clients yet.", new() { Exact = false }).CountAsync() == 0,
            "An API outage was rendered as a successful empty directory.");
        Require(await page.Locator(".client-card, .client-grid-view tbody tr").CountAsync() == 0,
            "A stale client row remained visible after the directory API became unavailable.");
    }

    private static async Task WaitForOnlineDirectoryIdentityAsync(
        HttpClient api,
        string cookie,
        Guid agentId,
        int tenantId)
    {
        var recoveryTimeout = TimeSpan.FromSeconds(60);
        using var overallTimeout = new CancellationTokenSource(recoveryTimeout);
        using var pollingTimer = new PeriodicTimer(ControlPollInterval);
        var transientFailures = 0;
        while (!overallTimeout.IsCancellationRequested)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(overallTimeout.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                using var response = await SendWithCookieAsync(
                    api,
                    HttpMethod.Get,
                    "/api/v2/client-presence",
                    cookie,
                    cancellationToken: requestTimeout.Token);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    using var document = await ReadJsonAsync(response, requestTimeout.Token);
                    var items = RequiredArray(document.RootElement, "items");
                    var matching = items.EnumerateArray()
                        .Where(item => ReadGuid(item, "agentId") == agentId && ReadInt32(item, "tenantId") == tenantId)
                        .ToArray();
                    if (matching.Length == 1 && ReadBoolean(matching[0], "online"))
                        return;
                }
                else if ((int)response.StatusCode < 500)
                {
                    throw new InvalidOperationException("The authenticated public directory API rejected the operator during recovery.");
                }
                else
                {
                    transientFailures++;
                }
            }
            catch (HttpRequestException exception) when (exception.StatusCode is null || (int)exception.StatusCode >= 500)
            {
                // A public route can take a short time to become reachable after
                // the producer acknowledges API readiness.
                transientFailures++;
            }
            catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (requestTimeout.IsCancellationRequested)
            {
                // Bound each probe while retaining the overall recovery deadline.
                transientFailures++;
            }

            try
            {
                await pollingTimer.WaitForNextTickAsync(overallTimeout.Token);
            }
            catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"The authenticated public directory API did not recover with the expected online identity within {recoveryTimeout.TotalSeconds:0} seconds after {transientFailures} transient request failures.");
    }

    private static async Task<int> CreateRestrictedTenantAsync(HttpClient api, string cookie, string runId)
    {
        var tenantName = $"Hosted browser tenant {runId[..8]} {Guid.NewGuid():N}";
        using var response = await SendWithCookieAsync(api, HttpMethod.Post, "/api/v1/tenants/", cookie, new { name = tenantName });
        RequireStatus(response, HttpStatusCode.Created);
        using var document = await ReadJsonAsync(response);
        var tenantId = ReadInt32(document.RootElement, "tenantId");
        Require(tenantId > 0, "The disposable restricted tenant was not created.");
        return tenantId;
    }

    private static async Task<LocalTestAccount> CreateLocalAccountAsync(
        HttpClient api,
        string operatorCookie,
        string runId,
        string suffix,
        string displayName,
        int? tenantId)
    {
        var email = $"hosted-{runId[..8]}-{suffix}-{Guid.NewGuid():N}@example.test";
        var password = $"H{Guid.NewGuid():N}x7!";

        using var createResponse = await SendWithCookieAsync(api, HttpMethod.Post, "/api/v2/local-auth/users", operatorCookie,
            new { displayName, email });
        RequireStatus(createResponse, HttpStatusCode.Created);
        using var activationDocument = await ReadJsonAsync(createResponse);
        var activationToken = ReadString(activationDocument.RootElement, "activationToken");
        Require(!string.IsNullOrWhiteSpace(activationToken), "The ordinary Local activation handoff is incomplete.");

        using var usersResponse = await SendWithCookieAsync(api, HttpMethod.Get, "/api/v2/access/users", operatorCookie);
        RequireStatus(usersResponse, HttpStatusCode.OK);
        using var usersDocument = await ReadJsonAsync(usersResponse);
        var principal = RequireRootArray(usersDocument.RootElement).EnumerateArray()
            .SingleOrDefault(user => string.Equals(ReadString(user, "email"), email, StringComparison.OrdinalIgnoreCase));
        Require(principal.ValueKind == JsonValueKind.Object, "The ordinary Local principal was not available for assignment.");
        var principalId = ReadString(principal, "principalId");
        Require(!string.IsNullOrWhiteSpace(principalId), "The ordinary Local principal identifier is unavailable.");

        if (tenantId is int restrictedTenantId)
        {
            using var rolesResponse = await SendWithCookieAsync(api, HttpMethod.Get,
                $"/api/v2/access/roles?tenantId={restrictedTenantId}", operatorCookie);
            RequireStatus(rolesResponse, HttpStatusCode.OK);
            using var rolesDocument = await ReadJsonAsync(rolesResponse);
            var tenantAdministratorRole = RequireRootArray(rolesDocument.RootElement).EnumerateArray()
                .SingleOrDefault(role => string.Equals(ReadString(role, "name"), "TenantAdministrator", StringComparison.Ordinal));
            Require(tenantAdministratorRole.ValueKind == JsonValueKind.Object, "The built-in tenant administrator role is unavailable.");
            var roleId = ReadString(tenantAdministratorRole, "id");
            using var assignmentResponse = await SendWithCookieAsync(api, HttpMethod.Put,
                $"/api/v2/access/principals/{Uri.EscapeDataString(principalId)}/assignments",
                operatorCookie,
                new { roleId, tenantId = restrictedTenantId });
            RequireStatus(assignmentResponse, HttpStatusCode.Created);
        }

        using var activationResponse = await SendWithCookieAsync(api, HttpMethod.Post, "/api/v2/local-auth/activate", string.Empty,
            new { email, activationToken, newPassword = password });
        RequireStatus(activationResponse, HttpStatusCode.NoContent);
        return new LocalTestAccount(email, password);
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyTenantAdministratorBoundaryAsync(
        IBrowser browser,
        HttpClient api,
        HostedContext context,
        IPage operatorPage,
        string operatorCookie,
        LocalTestAccount account,
        int restrictedTenantId)
    {
        await using var restrictedContext = await browser.NewContextAsync(CreateStrictContextOptions(1280, 900, ColorScheme.Light));
        var page = await restrictedContext.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var cookie = await RunSanitizedPhaseAsync(
            "tenant-administrator-authentication",
            () => SignInLocallyAsync(page, context.WebBaseUri, account.Email, account.Password));

        await RunSanitizedPhaseAsync("tenant-administrator-api-boundaries", async () =>
        {
            await VerifyLocalSessionIdentityAsync(api, cookie, account.Email);
            var accessibleTenantIds = await GetAccessibleTenantIdsAsync(api, cookie);
            Require(accessibleTenantIds.SequenceEqual([restrictedTenantId]), "The tenant administrator received an unexpected tenant scope.");

            using var originalTenantResponse = await SendWithCookieAsync(api, HttpMethod.Get, AgentListPath(context.TenantId), cookie);
            RequireStatus(originalTenantResponse, HttpStatusCode.Forbidden);

            using var allowedTenantResponse = await SendWithCookieAsync(api, HttpMethod.Get, AgentListPath(restrictedTenantId), cookie);
            RequireStatus(allowedTenantResponse, HttpStatusCode.OK);
            using var allowedTenantDocument = await ReadJsonAsync(allowedTenantResponse);
            var allowedAgents = RequiredArray(allowedTenantDocument.RootElement, "items");
            Require(!allowedAgents.EnumerateArray().Any(agent => ReadGuid(agent, "agentId") == context.AgentId),
                "The tenant administrator saw an agent from another tenant.");

            using var presenceResponse = await SendWithCookieAsync(api, HttpMethod.Get, "/api/v2/client-presence", cookie);
            RequireStatus(presenceResponse, HttpStatusCode.Forbidden);
            return true;
        });

        await AssertDeniedDirectoryAsync(page, context.WebBaseUri, context.SafeEvidenceDirectory, context.HandoffRoot, "clients-tenant-administrator-denied.png");
        await RefreshOperatorAfterRestrictedCircuitAsync(api, operatorPage, operatorCookie, context.AgentId, context.TenantId);
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyNoRoleBoundaryAsync(
        IBrowser browser,
        HttpClient api,
        HostedContext context,
        IPage operatorPage,
        string operatorCookie,
        LocalTestAccount account)
    {
        await using var restrictedContext = await browser.NewContextAsync(CreateStrictContextOptions(1280, 900, ColorScheme.Light));
        var page = await restrictedContext.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var cookie = await RunSanitizedPhaseAsync(
            "no-role-authentication",
            () => SignInLocallyAsync(page, context.WebBaseUri, account.Email, account.Password));

        await RunSanitizedPhaseAsync("no-role-api-boundaries", async () =>
        {
            await VerifyLocalSessionIdentityAsync(api, cookie, account.Email);
            var accessibleTenantIds = await GetAccessibleTenantIdsAsync(api, cookie);
            Require(accessibleTenantIds.Length == 0, "The no-role account received an unexpected tenant scope.");

            using var originalTenantResponse = await SendWithCookieAsync(api, HttpMethod.Get, AgentListPath(context.TenantId), cookie);
            RequireStatus(originalTenantResponse, HttpStatusCode.Forbidden);

            using var presenceResponse = await SendWithCookieAsync(api, HttpMethod.Get, "/api/v2/client-presence", cookie);
            RequireStatus(presenceResponse, HttpStatusCode.Forbidden);
            return true;
        });

        await AssertDeniedDirectoryAsync(page, context.WebBaseUri, context.SafeEvidenceDirectory, context.HandoffRoot, "clients-no-role-denied.png");
        await RefreshOperatorAfterRestrictedCircuitAsync(api, operatorPage, operatorCookie, context.AgentId, context.TenantId);
    }

    private static async Task<int[]> GetAccessibleTenantIdsAsync(HttpClient api, string cookie)
    {
        using var response = await SendWithCookieAsync(api, HttpMethod.Get, "/api/v2/access/tenants", cookie);
        RequireStatus(response, HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        return RequireRootArray(document.RootElement).EnumerateArray()
            .Select(tenant => ReadInt32(tenant, "tenantId"))
            .ToArray();
    }

    private static async Task RefreshOperatorAfterRestrictedCircuitAsync(
        HttpClient api,
        IPage operatorPage,
        string operatorCookie,
        Guid agentId,
        int tenantId)
    {
        await RunSanitizedPhaseAsync("operator-isolation-check", async () =>
        {
            await VerifyOnlineDirectoryIdentityAsync(api, operatorCookie, agentId, tenantId);
            return true;
        });
        var previousCount = await ReadDirectoryLoadCountAsync(operatorPage);
        await operatorPage.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
        await WaitForDirectoryLoadAsync(operatorPage, expectedTrigger: "manual", previousCount);
        await AssertOnlineDirectoryCardAsync(operatorPage, agentId, tenantId);
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertDeniedDirectoryAsync(IPage page, Uri webBaseUri, string evidenceDirectory, string handoffRoot, string screenshotName)
    {
        await NavigateToClientDirectoryAsync(page, webBaseUri);
        await WaitForDirectoryLoadAsync(page, expectedTrigger: "initial", previousCount: 0);
        await AssertPermissionDeniedAsync(page);
        var previousCount = await ReadDirectoryLoadCountAsync(page);
        await page.GetByTestId("retry-client-directory").ClickAsync();
        await WaitForDirectoryLoadAsync(page, expectedTrigger: "manual", previousCount);
        await AssertPermissionDeniedAsync(page);
        previousCount = await ReadDirectoryLoadCountAsync(page);
        await WaitForDirectoryLoadAsync(page, expectedTrigger: "periodic", previousCount, timeoutMilliseconds: 25_000);
        await AssertPermissionDeniedAsync(page);
        await CaptureDirectoryScreenshotAsync(page, evidenceDirectory, handoffRoot, screenshotName);
    }

    private static async Task NavigateToClientDirectoryAsync(IPage page, Uri webBaseUri)
    {
        var response = await page.GotoAsync(new Uri(webBaseUri, "clients").ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        Require(response is not null && response.Status == (int)HttpStatusCode.OK, "The protected client directory route is unavailable.");
        await page.GetByTestId("clients-page-interactive").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached
        });
    }

    private static async Task WaitForDirectoryLoadAsync(IPage page, string expectedTrigger, int previousCount, int timeoutMilliseconds = 30_000)
    {
        await page.WaitForFunctionAsync(
            $"() => {{ const state = document.querySelector('[data-testid={DirectoryStateTestId}]'); return state?.getAttribute('data-last-load-trigger') === '{expectedTrigger}' && state?.getAttribute('data-loading') === 'false' && Number(state?.getAttribute('data-load-count') || 0) > {previousCount}; }}",
            null,
            new PageWaitForFunctionOptions { Timeout = timeoutMilliseconds });
    }

    private static async Task<int> ReadDirectoryLoadCountAsync(IPage page)
    {
        var value = await page.GetByTestId(DirectoryStateTestId).GetAttributeAsync("data-load-count");
        Require(int.TryParse(value, out var count) && count >= 0, "The directory load counter is unavailable.");
        return count;
    }

    private static async Task AssertOnlineDirectoryCardAsync(IPage page, Guid agentId, int tenantId)
    {
        var selector = $"[data-testid='{ClientCardTestId}'][data-agent-id='{agentId:D}'][data-tenant-id='{tenantId}']";
        var card = page.Locator(selector);
        Require(await card.CountAsync() == 1 && await card.IsVisibleAsync(), "The directory did not render the expected agent identity.");
        Require(await card.GetByText("Online", new LocatorGetByTextOptions { Exact = true }).IsVisibleAsync(), "The expected directory identity is not online.");
    }

    private static async Task AssertPermissionDeniedAsync(IPage page)
    {
        var retry = page.GetByTestId("retry-client-directory");
        await retry.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var alertText = await page.GetByRole(AriaRole.Alert).InnerTextAsync();
        Require(alertText.Contains("does not have permission to read the client directory", StringComparison.OrdinalIgnoreCase),
            "The restricted directory did not show its actionable permission error.");
        Require(await page.GetByText("No registered clients yet.", new() { Exact = false }).CountAsync() == 0,
            "A permission denial was rendered as a successful empty directory.");
        Require(await page.Locator(".client-card, .client-grid-view tbody tr").CountAsync() == 0,
            "A restricted directory disclosed client rows.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task CaptureDirectoryScreenshotAsync(IPage page, string evidenceDirectory, string handoffRoot, string fileName)
    {
        var screenshotPath = ValidateHandoffPath(
            Path.Combine(evidenceDirectory, fileName),
            handoffRoot,
            requireExists: false,
            expectDirectory: false);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = screenshotPath,
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled
        });
    }

    private static BrowserNewContextOptions CreateStrictContextOptions(int width, int height, ColorScheme colorScheme) => new()
    {
        IgnoreHTTPSErrors = false,
        ViewportSize = new ViewportSize { Width = width, Height = height },
        ColorScheme = colorScheme
    };

    private static HttpClient CreatePublicApiClient(Uri publicOrigin) => new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false
    })
    {
        BaseAddress = publicOrigin,
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static async Task<HttpResponseMessage> SendWithCookieAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string cookie,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(cookie))
            Require(request.Headers.TryAddWithoutValidation("Cookie", $"{LocalCookieName}={cookie}"), "The Local session could not be forwarded to the protected API origin.");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(content, new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken);
    }

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode expected) =>
        Require(response.StatusCode == expected, "A protected hosted API operation returned an unexpected status.");

    private static bool IsLocalLoginUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/api/v2/local-auth/login";

    private static string AgentListPath(int tenantId) => $"/api/v1/tenants/{tenantId}/agents?page=1&pageSize=50";

    private static JsonElement RequiredArray(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
            return value;
        throw new InvalidOperationException("A hosted API response did not match its required shape.");
    }

    private static JsonElement RequireRootArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;
        throw new InvalidOperationException("A hosted API response did not match its required shape.");
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("A protected hosted value is missing or malformed.");
        return value.GetString() ?? string.Empty;
    }

    private static int ReadInt32(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value) || !value.TryGetInt32(out var number))
            throw new InvalidOperationException("A protected hosted integer is missing or malformed.");
        return number;
    }

    private static long ReadInt64(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value) || !value.TryGetInt64(out var number))
            throw new InvalidOperationException("A protected hosted number is missing or malformed.");
        return number;
    }

    private static Guid ReadGuid(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id))
            throw new InvalidOperationException("A protected hosted identity is missing or malformed.");
        return id;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("A protected hosted status is missing or malformed.");
        return value.GetBoolean();
    }

    private static Uri ReadUri(JsonElement root, string propertyName)
    {
        var value = ReadString(root, propertyName);
        Require(Uri.TryCreate(value, UriKind.Absolute, out var uri), "A protected hosted origin is malformed.");
        return uri!;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateProtectedHandoffRoot(string handoffRoot)
    {
        Require(Directory.Exists(handoffRoot), "The protected hosted handoff directory is unavailable.");
        var attributes = File.GetAttributes(handoffRoot);
        Require((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0,
            "The protected hosted handoff directory is invalid.");

        var trustedSids = GetTrustedHandoffSids();
        var security = new DirectoryInfo(handoffRoot).GetAccessControl();
        Require(security.AreAccessRulesProtected, "The protected hosted handoff directory allows inherited access.");
        ValidateHandoffSecurity(security, trustedSids);

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var administratorSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        Require(HasExplicitFullControl(rules, administratorSid) && HasExplicitFullControl(rules, systemSid),
            "The protected hosted handoff directory does not grant its trusted principals the required access.");
    }

    [SupportedOSPlatform("windows")]
    private static string ValidateHandoffPath(string path, string handoffRoot, bool requireExists, bool expectDirectory)
    {
        Require(Path.IsPathFullyQualified(path), "A protected hosted handoff path is malformed.");
        var root = Path.GetFullPath(handoffRoot);
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        Require(fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase),
            "A protected hosted path is outside the private handoff directory.");

        var relativePath = Path.GetRelativePath(root, fullPath);
        Require(!Path.IsPathRooted(relativePath) && relativePath != "." && relativePath != ".." &&
                !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal),
            "A protected hosted path is outside the private handoff directory.");

        var trustedSids = GetTrustedHandoffSids();
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        Require(segments.Length > 0, "A protected hosted path is malformed.");
        var currentPath = root;
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);
            var isLeaf = index == segments.Length - 1;
            var isDirectory = Directory.Exists(currentPath);
            var isFile = File.Exists(currentPath);
            if (!isDirectory && !isFile)
            {
                Require(isLeaf && !requireExists && !expectDirectory,
                    "A required protected hosted handoff input is missing.");
                continue;
            }

            var attributes = File.GetAttributes(currentPath);
            Require((attributes & FileAttributes.ReparsePoint) == 0,
                "A protected hosted path traverses an unsupported filesystem link.");
            if (!isLeaf)
                Require(isDirectory, "A protected hosted path traverses a non-directory entry.");
            else
                Require(isDirectory == expectDirectory, "A protected hosted input has an unexpected filesystem type.");

            FileSystemSecurity security = isDirectory
                ? new DirectoryInfo(currentPath).GetAccessControl()
                : new FileInfo(currentPath).GetAccessControl();
            ValidateHandoffSecurity(security, trustedSids);
        }

        return fullPath;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateHandoffSecurity(FileSystemSecurity security, HashSet<string> trustedSids)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        Require(owner is not null && trustedSids.Contains(owner.Value),
            "A protected hosted handoff entry has an untrusted owner.");

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        Require(!rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                                   (rule.FileSystemRights & HandoffModificationRights) != 0 &&
                                   rule.IdentityReference is SecurityIdentifier sid && !trustedSids.Contains(sid.Value)),
            "A protected hosted handoff entry grants modification access to an untrusted principal.");
    }

    private static bool HasExplicitFullControl(IEnumerable<FileSystemAccessRule> rules, string sid) =>
        rules.Any(rule => !rule.IsInherited && rule.AccessControlType == AccessControlType.Allow &&
                          rule.IdentityReference is SecurityIdentifier identity && identity.Value == sid &&
                          (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

    [SupportedOSPlatform("windows")]
    private static HashSet<string> GetTrustedHandoffSids()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentSid = identity.User;
        Require(currentSid is not null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            "The hosted browser runner is not an authorized handoff administrator.");

        return new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            currentSid!.Value
        };
    }

    [SupportedOSPlatform("windows")]
    private static async Task<byte[]> ReadProtectedHandoffFileAsync(
        string path,
        string handoffRoot,
        int maximumBytes,
        FileShare fileShare = FileShare.Read)
    {
        var fullPath = ValidateHandoffPath(path, handoffRoot, requireExists: true, expectDirectory: false);
        var fileLength = new FileInfo(fullPath).Length;
        Require(fileLength is > 0 && fileLength <= maximumBytes,
            "A protected hosted handoff file has an invalid size.");

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            fileShare,
            bufferSize: 8_192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var buffer = new MemoryStream(capacity: Math.Min(maximumBytes, 65_536));
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory());
            if (read == 0)
                break;
            Require(buffer.Length + read <= maximumBytes, "A protected hosted handoff file exceeds its size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read));
        }

        return buffer.ToArray();
    }

    private static bool PathsEqual(string actual, string expected)
    {
        if (!Path.IsPathFullyQualified(actual) || !Path.IsPathFullyQualified(expected))
            return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrictPublicOrigin(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
        uri.HostNameType == UriHostNameType.Dns && uri.Host.Contains('.', StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsHttpOrigin(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp && !string.IsNullOrEmpty(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static bool IsLowerHex(string? value, int length) =>
        value is { Length: var actualLength } && actualLength == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Require(bool condition, string safeMessage)
    {
        if (!condition)
            throw new InvalidOperationException(safeMessage);
    }

    private sealed class ProtectedApiPhaseControl(HostedContext context)
    {
        private static readonly string[] RequestFields =
        ["schemaVersion", "scope", "runId", "requestId", "sequence", "action"];
        private static readonly string[] AcknowledgementFields =
        ["schemaVersion", "scope", "runId", "requestId", "sequence", "action", "result"];
        private readonly string _root = context.HandoffRoot;
        private long _lastPublishedSequence;
        private bool _initialized;

        public async Task InitializeAsync()
        {
            if (_initialized)
                return;

            var request = await ReadRequestAsync();
            var acknowledgement = await ReadAcknowledgementAsync();
            if (request is null)
            {
                Require(acknowledgement is null,
                    "The protected API control acknowledgement has no matching request.");
                _initialized = true;
                return;
            }

            _lastPublishedSequence = request.Sequence;
            _initialized = true;

            if (acknowledgement is null || acknowledgement.Sequence < request.Sequence)
            {
                await WaitForAcknowledgementAsync(request, ControlAcknowledgementTimeout);
                return;
            }

            Require(IsMatchingAcknowledgement(request, acknowledgement),
                "The protected API control request and acknowledgement do not match.");
            Require(acknowledgement.Result == "passed",
                "The previous protected API control action did not complete successfully.");
        }

        public async Task SendAsync(string action)
        {
            if (!_initialized)
                await InitializeAsync();

            await PublishAndWaitAsync(action);
        }

        public async Task StartApiForCleanupAsync()
        {
            if (!_initialized)
                await InitializeAsync();

            await PublishAndWaitAsync("start_api");
        }

        private async Task PublishAndWaitAsync(string action)
        {
            Require(action is "stop_api" or "start_api" or "restart_api",
                "The protected API control action is not supported.");
            Require(_lastPublishedSequence < long.MaxValue,
                "The protected API control sequence is exhausted.");

            var request = new ControlRequest(
                SchemaVersion: 1,
                Scope: "integrated-onboarding",
                RunId: context.RunId,
                RequestId: Guid.NewGuid().ToString("D"),
                Sequence: _lastPublishedSequence + 1,
                Action: action);
            await WriteRequestAtomicallyAsync(request);
            _lastPublishedSequence = request.Sequence;
            await WaitForAcknowledgementAsync(request, ControlAcknowledgementTimeout);
        }

        private async Task WaitForAcknowledgementAsync(ControlRequest request, TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            using var pollingTimer = new PeriodicTimer(ControlPollInterval);
            while (!deadline.IsCancellationRequested)
            {
                var acknowledgement = await ReadAcknowledgementAsync();
                if (acknowledgement is not null && acknowledgement.Sequence >= request.Sequence)
                {
                    Require(IsMatchingAcknowledgement(request, acknowledgement),
                        "The protected API control acknowledgement does not match the published request.");
                    Require(acknowledgement.Result == "passed",
                        "The protected API control action did not complete successfully.");
                    return;
                }

                try
                {
                    await pollingTimer.WaitForNextTickAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    break;
                }
            }

            throw new TimeoutException($"The protected API control action was not acknowledged within {timeout.TotalSeconds:0} seconds.");
        }

        private static bool IsMatchingAcknowledgement(ControlRequest request, ControlAcknowledgement acknowledgement) =>
            acknowledgement.SchemaVersion == request.SchemaVersion &&
            acknowledgement.Scope == request.Scope &&
            acknowledgement.RunId == request.RunId &&
            acknowledgement.RequestId == request.RequestId &&
            acknowledgement.Sequence == request.Sequence &&
            acknowledgement.Action == request.Action;

        private async Task<ControlRequest?> ReadRequestAsync()
        {
            var bytes = await ReadOptionalControlFileAsync(context.ControlRequestPath);
            if (bytes is null)
                return null;

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            RequireExactProperties(root, RequestFields);
            var request = new ControlRequest(
                SchemaVersion: ReadInt32(root, "schemaVersion"),
                Scope: ReadString(root, "scope"),
                RunId: ReadString(root, "runId"),
                RequestId: ReadControlRequestId(root),
                Sequence: ReadInt64(root, "sequence"),
                Action: ReadString(root, "action"));
            Require(request.SchemaVersion == 1 && request.Scope == "integrated-onboarding" &&
                    request.RunId == context.RunId && request.Sequence > 0 &&
                    request.Action is "stop_api" or "start_api" or "restart_api",
                "The protected API control request is malformed.");
            return request;
        }

        private async Task<ControlAcknowledgement?> ReadAcknowledgementAsync()
        {
            var bytes = await ReadOptionalControlFileAsync(context.ControlAckPath);
            if (bytes is null)
                return null;

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            RequireExactProperties(root, AcknowledgementFields);
            var acknowledgement = new ControlAcknowledgement(
                SchemaVersion: ReadInt32(root, "schemaVersion"),
                Scope: ReadString(root, "scope"),
                RunId: ReadString(root, "runId"),
                RequestId: ReadControlRequestId(root),
                Sequence: ReadInt64(root, "sequence"),
                Action: ReadString(root, "action"),
                Result: ReadString(root, "result"));
            Require(acknowledgement.SchemaVersion == 1 && acknowledgement.Scope == "integrated-onboarding" &&
                    acknowledgement.RunId == context.RunId && acknowledgement.Sequence > 0 &&
                    acknowledgement.Action is "stop_api" or "start_api" or "restart_api" &&
                    acknowledgement.Result is "passed" or "failed",
                "The protected API control acknowledgement is malformed.");
            return acknowledgement;
        }

        private async Task<byte[]?> ReadOptionalControlFileAsync(string path)
        {
            var fullPath = ValidateHandoffPath(path, _root, requireExists: false, expectDirectory: false);
            if (!File.Exists(fullPath))
                return null;

            try
            {
                return await ReadProtectedHandoffFileAsync(
                    fullPath,
                    _root,
                    MaxControlFileBytes,
                    FileShare.Read | FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        private async Task WriteRequestAtomicallyAsync(ControlRequest request)
        {
            var targetPath = ValidateHandoffPath(context.ControlRequestPath, _root, requireExists: false, expectDirectory: false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = request.SchemaVersion,
                scope = request.Scope,
                runId = request.RunId,
                requestId = request.RequestId,
                sequence = request.Sequence,
                action = request.Action
            });
            Require(payload.Length is > 0 and <= MaxControlFileBytes,
                "The protected API control request exceeds its size limit.");
            var targetDirectory = Path.GetDirectoryName(targetPath);
            Require(!string.IsNullOrWhiteSpace(targetDirectory),
                "The protected API control request directory is invalid.");
            var temporaryPath = ValidateHandoffPath(
                Path.Combine(targetDirectory!, $".control-request-{Guid.NewGuid():N}.tmp"),
                _root,
                requireExists: false,
                expectDirectory: false);

            Exception? writeFailure = null;
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: MaxControlFileBytes,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                    stream.Flush(flushToDisk: true);
                }

                ValidateHandoffPath(temporaryPath, _root, requireExists: true, expectDirectory: false);
                await MoveRequestWithSharingRetryAsync(temporaryPath, targetPath);
                _lastPublishedSequence = request.Sequence;
                ValidateHandoffPath(targetPath, _root, requireExists: true, expectDirectory: false);
            }
            catch (Exception exception)
            {
                writeFailure = exception;
                throw;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        var safeTemporaryPath = ValidateHandoffPath(
                            temporaryPath,
                            _root,
                            requireExists: true,
                            expectDirectory: false);
                        File.Delete(safeTemporaryPath);
                    }
                    catch (Exception cleanupException)
                    {
                        if (writeFailure is not null)
                            writeFailure.Data["control-temp-cleanup-failure"] = cleanupException.GetType().Name;
                        else
                            throw new IOException("The protected API control temporary file could not be removed.", cleanupException);
                    }
                }
            }
        }

        private static async Task MoveRequestWithSharingRetryAsync(string temporaryPath, string targetPath)
        {
            var timeout = TimeSpan.FromSeconds(5);
            var stopwatch = Stopwatch.StartNew();
            using var pollingTimer = new PeriodicTimer(ControlPollInterval);
            while (true)
            {
                try
                {
                    File.Move(temporaryPath, targetPath, overwrite: true);
                    return;
                }
                catch (IOException exception) when (IsWindowsSharingViolation(exception) && stopwatch.Elapsed < timeout)
                {
                    await pollingTimer.WaitForNextTickAsync();
                }
            }
        }

        private static bool IsWindowsSharingViolation(IOException exception) =>
            (exception.HResult & 0xFFFF) is 32 or 33;

        private static string ReadControlRequestId(JsonElement root)
        {
            var value = ReadString(root, "requestId");
            Require(Guid.TryParseExact(value, "D", out var requestId) && requestId != Guid.Empty,
                "The protected API control request identifier is malformed.");
            return requestId.ToString("D");
        }

        private static void RequireExactProperties(JsonElement root, IReadOnlyCollection<string> expectedFields)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("A protected API control object is malformed.");

            var actualFields = root.EnumerateObject().Select(property => property.Name).ToArray();
            Require(actualFields.Length == expectedFields.Count &&
                    actualFields.Distinct(StringComparer.Ordinal).Count() == expectedFields.Count &&
                    expectedFields.All(field => actualFields.Contains(field, StringComparer.Ordinal)),
                "A protected API control object has an unexpected shape.");
        }

        private sealed record ControlRequest(
            int SchemaVersion,
            string Scope,
            string RunId,
            string RequestId,
            long Sequence,
            string Action);

        private sealed record ControlAcknowledgement(
            int SchemaVersion,
            string Scope,
            string RunId,
            string RequestId,
            long Sequence,
            string Action,
            string Result);
    }

    private static async Task<T> RunSanitizedPhaseAsync<T>(string phase, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (exception is not SanitizedHostedPhaseException)
        {
            var exceptionType = exception.GetType().Name;
            var safeMessage = $"Hosted onboarding phase '{phase}' failed ({exceptionType}).";
            if (exception is TimeoutException)
                throw new TimeoutException(safeMessage);
            if (exception is OperationCanceledException)
                throw new OperationCanceledException(safeMessage);
            throw new SanitizedHostedPhaseException(safeMessage);
        }
    }

    private sealed class SanitizedHostedPhaseException(string message) : InvalidOperationException(message);

    private sealed record HostedContext(
        int SchemaVersion,
        string Scope,
        string SourceSha,
        string TestMergeSha,
        string ProductVersion,
        string RunId,
        Uri PublicOrigin,
        Uri ApiBaseUri,
        Uri GatewayBaseUri,
        Uri WebBaseUri,
        int TenantId,
        string OperatorEmail,
        string OperatorPasswordFile,
        string CandidateArchive,
        string CandidateRuntimeId,
        string CandidateVersion,
        string CandidateSha256,
        string InstallRoot,
        string StateRoot,
        string IdentityReceiptPath,
        string ControlRequestPath,
        string ControlAckPath,
        string SafeEvidenceDirectory,
        Guid AgentId,
        string HandoffRoot = "");

    private sealed record IdentityReceipt(
        int SchemaVersion,
        string Scope,
        string RunId,
        string SourceSha,
        string TestMergeSha,
        string ProductVersion,
        Guid AgentId,
        int TenantId,
        long ConnectionEpoch,
        string ConnectionId,
        long HeartbeatSequence,
        bool LocalSystem,
        bool SessionZero,
        bool CurrentServiceProcessMatched,
        bool FreshnessVerified,
        bool OnlineDirectoryVerified);

    private sealed record LocalTestAccount(string Email, string Password);
}
