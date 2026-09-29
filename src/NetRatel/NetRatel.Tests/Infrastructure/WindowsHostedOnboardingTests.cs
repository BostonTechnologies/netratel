using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ServiceProcess;
using NetRatel.API.Services;
using Xunit;
using TimeoutException = System.TimeoutException;

namespace NetRatel.Tests.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WindowsHostedOnboardingTests
{
    private const string ContextEnvironmentVariable = "NETRATEL_HOSTED_ONBOARDING_CONTEXT_FILE";
    private const string Scope = "integrated-onboarding";
    private const string ServiceName = "NetRatel.Client";
    private const string RequestSchema = "netratel.install-readiness.request.v1";
    private const string ReadySchema = "netratel.install-readiness.ready.v1";
    private const string LocalSystemSid = "S-1-5-18";
    private const int MaximumContextBytes = 64 * 1024;
    private const int MaximumPasswordBytes = 4096;
    private const long MaximumCandidateBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumReadinessBytes = 32 * 1024;
    private const int MaximumJsonResponseBytes = 256 * 1024;
    private const int InstallerTimeoutMinutes = 25;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Regex LowerSha40 = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex LowerSha64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly FileSystemRights MutationRights =
        FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;

    [Fact]
    [Trait("category", "hosted")]
    public async Task ProductionLocalOnboarding_ActualSystemServiceReachesTwoAcknowledgedHeartbeatsAndDirectory()
    {
        var phase = "prerequisites";
        Process? installer = null;
        ReadinessObserver? readinessObserver = null;
        CancellationTokenSource? readinessCancellation = null;
        Task<ReadinessSnapshot>? readinessTask = null;
        string? commandPath = null;
        string? failureCode = null;
        string? cleanupFailureCode = null;
        Task? stdoutDrain = null;
        Task? stderrDrain = null;
        try
        {
            ValidateHostPrerequisites();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(InstallerTimeoutMinutes));
            var cancellationToken = timeout.Token;

            phase = "context";
            var context = await ReadAndValidateContextAsync(cancellationToken);

            phase = "candidate";
            await ValidateCandidateAsync(context, cancellationToken);

            phase = "baseline";
            ValidateFreshInstallBaseline(context);

            phase = "operator_login";
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = true,
                CookieContainer = cookies
            };
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = context.PublicOrigin,
                Timeout = Timeout.InfiniteTimeSpan
            };
            await LoginAsync(client, cookies, context, cancellationToken);

            phase = "artifact_upload";
            await UploadCandidateAsync(client, context, cancellationToken);

            phase = "install_link";
            var installLink = await CreateInstallLinkAsync(client, context, cancellationToken);

            phase = "installer_command_file";
            commandPath = await WriteProtectedInstallCommandAsync(context, installLink.InstallCommand, cancellationToken);

            phase = "readiness_observer";
            readinessObserver = new ReadinessObserver(
                context.StateRoot,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                context.TrustedSids);
            readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readinessTask = readinessObserver.WaitForTwoAcknowledgedHeartbeatsAsync(readinessCancellation.Token);

            phase = "installer_execution";
            installer = StartInstaller(commandPath);
            stdoutDrain = DrainAndDiscardAsync(installer.StandardOutput, CancellationToken.None);
            stderrDrain = DrainAndDiscardAsync(installer.StandardError, CancellationToken.None);
            await WaitForInstallerAsync(installer, stdoutDrain, stderrDrain, cancellationToken);
            if (installer.ExitCode != 0)
            {
                throw Failure("installer_nonzero_exit");
            }

            phase = "readiness_validation";
            var readinessSnapshot = await readinessTask.WaitAsync(cancellationToken);
            ValidatedReadiness readiness;
            try
            {
                readiness = ValidateReadinessSnapshot(readinessSnapshot, context, readinessObserver.StartedAtUtc);
            }
            finally
            {
                readinessSnapshot.ClearSensitiveBuffers();
            }

            phase = "service_identity";
            ValidateInstalledCredentialFile(context);
            var service = await ValidateCurrentSystemServiceAsync(context, readiness, cancellationToken);

            phase = "operator_directory";
            await WaitForOnlineDirectoryIdentityAsync(client, context, readiness, cancellationToken);

            phase = "install_grant_consumption";
            await VerifyInstallGrantConsumedAsync(client, context, installLink.Id, cancellationToken);

            phase = "service_identity_final";
            var finalService = await ValidateCurrentSystemServiceAsync(context, readiness, cancellationToken);
            if (finalService.ProcessId != service.ProcessId ||
                Math.Abs((finalService.ProcessStartedAtUtc - service.ProcessStartedAtUtc).TotalSeconds) > 1 ||
                !string.Equals(finalService.UserSid, service.UserSid, StringComparison.Ordinal) ||
                finalService.SessionId != service.SessionId)
            {
                throw Failure("service_restarted_after_readiness");
            }

            phase = "identity_receipt";
            await WriteIdentityReceiptAsync(context, readiness, finalService, cancellationToken);
        }
        catch (HostedOnboardingFailure failure)
        {
            failureCode = failure.Code;
        }
        catch (Exception exception)
        {
            failureCode = ClassifyException(exception);
        }
        finally
        {
            readinessCancellation?.Cancel();
            try
            {
                if (readinessTask is not null)
                {
                    cleanupFailureCode ??= await ObserveReadinessTaskAsync(readinessTask);
                }
            }
            catch (Exception exception)
            {
                cleanupFailureCode = ClassifyException(exception);
            }
            finally
            {
                try
                {
                    readinessObserver?.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailureCode ??= ClassifyException(exception);
                }
                finally
                {
                    readinessCancellation?.Dispose();
                }
            }

            if (installer is not null)
            {
                try
                {
                    await StopOwnedInstallerAsync(installer, stdoutDrain, stderrDrain);
                }
                catch (HostedOnboardingFailure failure)
                {
                    cleanupFailureCode ??= failure.Code;
                }
                catch (Exception exception)
                {
                    cleanupFailureCode ??= ClassifyException(exception);
                }
                finally
                {
                    installer.Dispose();
                }
            }

            if (commandPath is not null)
            {
                try
                {
                    if (File.Exists(commandPath)) File.Delete(commandPath);
                }
                catch (IOException)
                {
                    cleanupFailureCode ??= "installer_command_cleanup_io";
                }
                catch (UnauthorizedAccessException)
                {
                    cleanupFailureCode ??= "installer_command_cleanup_access_denied";
                }
                catch (System.Security.SecurityException)
                {
                    cleanupFailureCode ??= "installer_command_cleanup_security_boundary";
                }
            }
        }

        if (failureCode is not null || cleanupFailureCode is not null)
        {
            throw new InvalidOperationException(
                $"Hosted onboarding failed; phase={(failureCode is null ? "cleanup" : phase)}; " +
                $"code={failureCode ?? "none"}; cleanupCode={cleanupFailureCode ?? "none"}.");
        }
    }

    private static void ValidateHostPrerequisites()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw Failure("requires_windows_11_or_newer");
        }

        using var currentIdentity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(currentIdentity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator) || currentIdentity.User is null)
        {
            throw Failure("requires_administrator_context");
        }

        using var currentVersion = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
        var installationType = currentVersion?.GetValue("InstallationType") as string;
        var currentBuildText = currentVersion?.GetValue("CurrentBuildNumber") as string;
        if (!string.Equals(installationType, "Client", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(currentBuildText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var build) || build < 22000)
        {
            throw Failure("requires_windows_11_client_host");
        }

        if (RuntimeInformation.OSArchitecture != Architecture.Arm64 ||
            RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            throw Failure("requires_windows_11_arm64_host");
        }
    }

    private static async Task<HostedContext> ReadAndValidateContextAsync(CancellationToken cancellationToken)
    {
        var contextPathValue = Environment.GetEnvironmentVariable(ContextEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(contextPathValue) || !Path.IsPathFullyQualified(contextPathValue))
        {
            throw Failure("context_file_missing_or_not_absolute");
        }

        var contextPath = Path.GetFullPath(contextPathValue);
        var root = Path.GetDirectoryName(contextPath);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || !File.Exists(contextPath))
        {
            throw Failure("context_root_or_file_missing");
        }

        root = Path.GetFullPath(root);
        EnsureNoReparsePoint(root, mustExist: true);
        ValidateProtectedRootAcl(root, TrustedSids());
        EnsureDescendantPath(root, contextPath);
        EnsureNoReparsePoint(contextPath, mustExist: true);
        ValidateTrustedLeafAcl(contextPath, TrustedSids());

        var contextBytes = await ReadBoundedFileAsync(contextPath, MaximumContextBytes, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(contextBytes, new JsonDocumentOptions { MaxDepth = 16 });
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object || ReadRequiredInt32(json, "schemaVersion") != 1 ||
                !string.Equals(ReadRequiredString(json, "scope"), Scope, StringComparison.Ordinal))
            {
                throw Failure("context_schema_invalid");
            }

            using var currentIdentity = WindowsIdentity.GetCurrent();
            var currentUserSid = currentIdentity.User?.Value
                ?? throw Failure("administrator_sid_unavailable");
            var trustedSids = TrustedSids(currentUserSid);
            var sourceSha = ReadRequiredString(json, "sourceSha");
            var testMergeSha = ReadRequiredString(json, "testMergeSha");
            var productVersion = ReadRequiredString(json, "productVersion");
            var runId = ReadRequiredString(json, "runId");
            var publicOriginText = ReadRequiredString(json, "publicOrigin");
            var webBaseUri = ReadRequiredString(json, "webBaseUri");
            var apiBaseUri = ReadRequiredString(json, "apiBaseUri");
            var gatewayBaseUri = ReadRequiredString(json, "gatewayBaseUri");
            var tenantId = ReadRequiredInt32(json, "tenantId");
            var operatorEmail = ReadRequiredString(json, "operatorEmail");
            var operatorPasswordFileValue = ReadRequiredString(json, "operatorPasswordFile");
            var candidateArchiveValue = ReadRequiredString(json, "candidateArchive");
            var candidateRuntimeId = ReadRequiredString(json, "candidateRuntimeId");
            var candidateVersion = ReadRequiredString(json, "candidateVersion");
            var candidateSha256 = ReadRequiredString(json, "candidateSha256");
            var installRootValue = ReadRequiredString(json, "installRoot");
            var stateRootValue = ReadRequiredString(json, "stateRoot");
            var identityReceiptValue = ReadRequiredString(json, "identityReceiptPath");
            var controlRequestValue = ReadRequiredString(json, "controlRequestPath");
            var controlAckValue = ReadRequiredString(json, "controlAckPath");
            var safeEvidenceValue = ReadRequiredString(json, "safeEvidenceDirectory");

            if (!IsLowerHex(sourceSha, 40) || !IsLowerHex(testMergeSha, 40) ||
                !IsLowerHex(candidateSha256, 64) || tenantId <= 0 ||
                !Guid.TryParseExact(runId, "D", out var parsedRunId) || parsedRunId == Guid.Empty ||
                !string.Equals(candidateRuntimeId, "win-x64", StringComparison.Ordinal) ||
                !string.Equals(candidateVersion, productVersion, StringComparison.Ordinal))
            {
                throw Failure("context_identity_or_candidate_invalid");
            }

            var currentVersion = typeof(WindowsHostedOnboardingTests).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .SingleOrDefault()?.InformationalVersion;
            var evaluatedProductVersion = currentVersion?.Split('+', 2)[0];
            if (!string.Equals(evaluatedProductVersion, productVersion, StringComparison.Ordinal))
            {
                throw Failure("context_product_version_mismatch");
            }

            var publicOrigin = ParsePublicOrigin(publicOriginText);
            var publicWebOrigin = ParsePublicOrigin(webBaseUri);
            if (!SameOrigin(publicOrigin, publicWebOrigin))
            {
                throw Failure("public_web_origin_mismatch");
            }

            ValidatePrivateServiceOrigin(apiBaseUri, "http");
            ValidatePrivateServiceOrigin(gatewayBaseUri, "http");

            var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(workspace))
            {
                var checkoutSha = await ReadGitHeadShaAsync(workspace, cancellationToken);
                if (!string.Equals(checkoutSha, testMergeSha, StringComparison.Ordinal))
                {
                    throw Failure("context_checkout_sha_mismatch");
                }
            }
            else
            {
                var githubSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
                if (!IsLowerHex(githubSha, 40) || !string.Equals(githubSha, testMergeSha, StringComparison.Ordinal))
                {
                    throw Failure("context_checkout_sha_unverified");
                }
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programFiles) || string.IsNullOrWhiteSpace(commonApplicationData))
            {
                throw Failure("windows_default_directories_unavailable");
            }

            var expectedInstallRoot = Path.GetFullPath(Path.Combine(programFiles, "NetRatel", "Client"));
            var expectedStateRoot = Path.GetFullPath(Path.Combine(commonApplicationData, "NetRatel", "update"));
            var installRoot = NormalizeAbsolutePath(installRootValue);
            var stateRoot = NormalizeAbsolutePath(stateRootValue);
            if (!PathEquals(installRoot, expectedInstallRoot) || !PathEquals(stateRoot, expectedStateRoot))
            {
                throw Failure("product_default_root_mismatch");
            }

            EnsureNoReparseChain(programFiles, installRoot, mustExist: false);
            EnsureNoReparseChain(commonApplicationData, stateRoot, mustExist: false);

            var operatorPasswordFile = NormalizeContainedPath(root, operatorPasswordFileValue);
            var candidateArchive = NormalizeContainedPath(root, candidateArchiveValue);
            var identityReceiptPath = NormalizeContainedPath(root, identityReceiptValue);
            var controlRequestPath = NormalizeContainedPath(root, controlRequestValue);
            var controlAckPath = NormalizeContainedPath(root, controlAckValue);
            var safeEvidenceDirectory = NormalizeContainedPath(root, safeEvidenceValue);
            var credentialPath = Path.GetFullPath(Path.Combine(commonApplicationData, "NetRatel", "agent.dat"));

            var protectedPaths = new[]
            {
                contextPath, operatorPasswordFile, candidateArchive, identityReceiptPath,
                controlRequestPath, controlAckPath, safeEvidenceDirectory
            };
            if (protectedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != protectedPaths.Length)
            {
                throw Failure("context_path_alias_detected");
            }

            EnsureNoReparseChain(root, contextPath, mustExist: true);
            EnsureNoReparseChain(root, operatorPasswordFile, mustExist: true);
            EnsureNoReparseChain(root, candidateArchive, mustExist: true);
            EnsureNoReparseChain(root, identityReceiptPath, mustExist: false);
            EnsureNoReparseChain(root, controlRequestPath, mustExist: false);
            EnsureNoReparseChain(root, controlAckPath, mustExist: false);
            EnsureNoReparseChain(root, safeEvidenceDirectory, mustExist: true);
            EnsureNoReparseChain(commonApplicationData, credentialPath, mustExist: false);
            ValidatePathWithinRoot(root, contextPath, isDirectory: false, mustExist: true, trustedSids);
            ValidatePathWithinRoot(root, operatorPasswordFile, isDirectory: false, mustExist: true, trustedSids);
            ValidatePathWithinRoot(root, candidateArchive, isDirectory: false, mustExist: true, trustedSids);
            ValidatePathWithinRoot(root, identityReceiptPath, isDirectory: false, mustExist: false, trustedSids);
            ValidatePathWithinRoot(root, controlRequestPath, isDirectory: false, mustExist: false, trustedSids);
            ValidatePathWithinRoot(root, controlAckPath, isDirectory: false, mustExist: false, trustedSids);
            ValidatePathWithinRoot(root, safeEvidenceDirectory, isDirectory: true, mustExist: true, trustedSids);

            if (File.Exists(identityReceiptPath) || Directory.Exists(identityReceiptPath))
            {
                throw Failure("identity_receipt_already_exists");
            }

            var passwordInfo = new FileInfo(operatorPasswordFile);
            if (passwordInfo.Length is <= 0 or > MaximumPasswordBytes ||
                new FileInfo(candidateArchive).Length is <= 0 or > MaximumCandidateBytes)
            {
                throw Failure("context_file_size_invalid");
            }

            return new HostedContext(
                root,
                trustedSids,
                sourceSha,
                testMergeSha,
                productVersion,
                parsedRunId,
                publicOrigin,
                tenantId,
                operatorEmail,
                operatorPasswordFile,
                candidateArchive,
                candidateRuntimeId,
                candidateVersion,
                candidateSha256,
                installRoot,
                stateRoot,
                credentialPath,
                identityReceiptPath,
                controlRequestPath,
                controlAckPath,
                safeEvidenceDirectory);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contextBytes);
        }
    }

    private static async Task ValidateCandidateAsync(HostedContext context, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            context.CandidateArchive, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(actualHash, context.CandidateSha256, StringComparison.Ordinal))
        {
            throw Failure("candidate_sha256_mismatch");
        }

        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 2 or > 100_000)
        {
            throw Failure("candidate_archive_layout_invalid");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string packageRoot = "netratel-client-win-x64/";
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(packageRoot, StringComparison.Ordinal) ||
                name.Contains('\\') || name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl) ||
                name.Split('/').Any(segment => segment is ".." or ".") || !names.Add(name))
            {
                throw Failure("candidate_archive_layout_invalid");
            }

            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (unixType != 0 && unixType is not (0x4000 or 0x8000)))
            {
                throw Failure("candidate_archive_layout_invalid");
            }
        }

        var manifestEntries = archive.Entries.Where(entry =>
            string.Equals(entry.FullName, packageRoot + "netratel-client-manifest.json", StringComparison.Ordinal)).ToArray();
        var executableEntries = archive.Entries.Where(entry =>
            string.Equals(entry.FullName, packageRoot + "NetRatel.Client.exe", StringComparison.Ordinal)).ToArray();
        if (manifestEntries.Length != 1 || executableEntries.Length != 1 ||
            manifestEntries[0].Length is <= 0 or > 64 * 1024 || executableEntries[0].Length <= 0)
        {
            throw Failure("candidate_manifest_or_executable_missing");
        }

        await using var manifestStream = manifestEntries[0].Open();
        using var manifest = await JsonDocument.ParseAsync(
            manifestStream, new JsonDocumentOptions { MaxDepth = 8 }, cancellationToken);
        var root = manifest.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !string.Equals(ReadRequiredString(root, "schema"), "netratel.client.manifest.v1", StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(root, "product"), "NetRatel.Client", StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(root, "version"), context.CandidateVersion, StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(root, "runtimeId"), context.CandidateRuntimeId, StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(root, "commitSha"), context.TestMergeSha, StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(root, "executable"), "NetRatel.Client.exe", StringComparison.Ordinal))
        {
            throw Failure("candidate_manifest_mismatch");
        }
    }

    private static void ValidateFreshInstallBaseline(HostedContext context)
    {
        using var serviceKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}", writable: false);
        if (serviceKey is not null)
        {
            throw Failure("service_baseline_not_clean");
        }

        if (Directory.Exists(context.InstallRoot) || File.Exists(context.InstallRoot) ||
            Directory.Exists(context.StateRoot) || File.Exists(context.StateRoot) ||
            File.Exists(context.CredentialPath) || Directory.Exists(context.CredentialPath))
        {
            throw Failure("product_install_baseline_not_clean");
        }
    }

    private static async Task LoginAsync(
        HttpClient client,
        CookieContainer cookies,
        HostedContext context,
        CancellationToken cancellationToken)
    {
        var passwordBytes = await ReadBoundedFileAsync(context.OperatorPasswordFile, MaximumPasswordBytes, cancellationToken);
        string password;
        try
        {
            password = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(passwordBytes);
            if (string.IsNullOrEmpty(password) || password.Contains('\0'))
            {
                throw Failure("operator_password_file_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new LocalLoginRequest(context.OperatorEmail, password, RememberMe: false), WebJson);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/local-auth/login")
            {
                Content = new ByteArrayContent(body)
            };
            request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw Failure($"login_status_{(int)response.StatusCode}");
            }

            if (cookies.GetCookies(context.PublicOrigin).Count == 0)
            {
                throw Failure("local_login_cookie_missing");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static async Task UploadCandidateAsync(
        HttpClient client,
        HostedContext context,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(context.CandidateRuntimeId, Encoding.UTF8), "rid");
        form.Add(new StringContent(context.CandidateVersion, Encoding.UTF8), "version");
        form.Add(new StringContent("Windows 11 integrated onboarding acceptance", Encoding.UTF8), "notes");
        await using var archive = new FileStream(
            context.CandidateArchive, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archiveContent = new StreamContent(archive, 128 * 1024);
        archiveContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(archiveContent, "file", "netratel-client-win-x64.zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/client-artifacts/upload")
        {
            Content = form
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw Failure($"artifact_upload_status_{(int)response.StatusCode}");
        }

        using var document = await ReadJsonDocumentAsync(response, MaximumJsonResponseBytes, cancellationToken);
        var artifact = GetRequiredProperty(document.RootElement, "artifact");
        var returnedRid = ReadRequiredString(artifact, "rid");
        var returnedVersion = ReadRequiredString(artifact, "version");
        var returnedHash = ReadRequiredString(artifact, "sha256");
        var returnedSize = ReadRequiredInt64(artifact, "size");
        var localSize = new FileInfo(context.CandidateArchive).Length;
        if (!string.Equals(returnedRid, context.CandidateRuntimeId, StringComparison.Ordinal) ||
            !string.Equals(returnedVersion, context.CandidateVersion, StringComparison.Ordinal) ||
            !string.Equals(returnedHash, context.CandidateSha256, StringComparison.Ordinal) ||
            returnedSize != localSize)
        {
            throw Failure("artifact_upload_receipt_mismatch");
        }
    }

    private static async Task<IssuedInstallLink> CreateInstallLinkAsync(
        HttpClient client,
        HostedContext context,
        CancellationToken cancellationToken)
    {
        var createRequest = new ClientInstallLinkCreateRequest(
            context.TenantId,
            context.CandidateRuntimeId,
            context.CandidateVersion,
            ValidForMinutes: 60,
            MaxUses: 1,
            InstallAsService: true,
            SilentInstall: true,
            IdempotencyKey: Guid.NewGuid().ToString("D"));
        var body = JsonSerializer.SerializeToUtf8Bytes(createRequest, WebJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/client-install-links")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage responseValue;
        try
        {
            responseValue = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }

        using var response = responseValue;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Failure($"install_link_status_{(int)response.StatusCode}");
        }

        var responseBytes = await ReadBoundedStreamAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), MaximumJsonResponseBytes, cancellationToken);
        try
        {
            // The API response also contains a grant-bearing script. Deserialize only the public
            // metadata and command so the secret script is never materialized as a managed string.
            var result = JsonSerializer.Deserialize<ClientInstallLinkResponse>(responseBytes, WebJson)
                ?? throw Failure("install_link_response_invalid");
            if (result.Id is null || result.Id == Guid.Empty || result.TenantId != context.TenantId ||
                !string.Equals(result.RuntimeId, context.CandidateRuntimeId, StringComparison.Ordinal) ||
                !string.Equals(result.ArtifactVersion, context.CandidateVersion, StringComparison.Ordinal) ||
                !string.Equals(result.ArtifactSha256, context.CandidateSha256, StringComparison.Ordinal) ||
                result.ExpiresAtUtc is null || result.MaxUses != 1 || result.RemainingUses != 1 ||
                result.InstallAsService != true || result.SilentInstall != true || result.Replay != false)
            {
                throw Failure("install_link_receipt_mismatch");
            }

            if (result.ExpiresAtUtc.Value <= DateTimeOffset.UtcNow)
            {
                throw Failure("install_link_expired_before_execution");
            }

            var publicUrlText = result.PublicUrl ?? throw Failure("install_link_public_origin_invalid");
            const string publicInstallPrefix = "/clients/install/";
            if (!Uri.TryCreate(publicUrlText, UriKind.Absolute, out var publicUrl) ||
                !SameOrigin(context.PublicOrigin, publicUrl) || publicUrl.Query.Length != 0 ||
                publicUrl.Fragment.Length != 0 ||
                !publicUrl.AbsolutePath.StartsWith(publicInstallPrefix, StringComparison.Ordinal) ||
                !Regex.IsMatch(publicUrl.AbsolutePath[publicInstallPrefix.Length..],
                    @"^[A-Za-z0-9_-]{43}\.ps1$", RegexOptions.CultureInvariant) ||
                !string.Equals(publicUrl.GetLeftPart(UriPartial.Authority), context.PublicOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                throw Failure("install_link_public_origin_invalid");
            }

            var installCommand = result.InstallCommand ?? throw Failure("install_command_contract_invalid");
            var expectedCommand = $"Invoke-WebRequest -UseBasicParsing -ErrorAction Stop '{publicUrlText}' | Invoke-Expression";
            if (!string.Equals(installCommand, expectedCommand, StringComparison.Ordinal))
            {
                throw Failure("install_command_contract_invalid");
            }

            return new IssuedInstallLink(result.Id.Value, installCommand);
        }
        catch (JsonException)
        {
            throw Failure("install_link_response_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private static async Task VerifyInstallGrantConsumedAsync(
        HttpClient client,
        HostedContext context,
        Guid grantId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"/api/v1/client-install-links/{grantId:D}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Failure($"install_grant_receipt_status_{(int)response.StatusCode}");
        }

        using var document = await ReadJsonDocumentAsync(response, MaximumJsonResponseBytes, cancellationToken);
        var metadata = document.RootElement;
        if (!Guid.TryParse(ReadRequiredString(metadata, "id"), out var returnedId) || returnedId != grantId ||
            ReadRequiredInt32(metadata, "tenantId") != context.TenantId ||
            !string.Equals(ReadRequiredString(metadata, "runtimeId"), context.CandidateRuntimeId, StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(metadata, "artifactVersion"), context.CandidateVersion, StringComparison.Ordinal) ||
            ReadRequiredInt32(metadata, "maxUses") != 1 || ReadRequiredInt32(metadata, "uses") != 1 ||
            ReadRequiredBoolean(metadata, "isActive"))
        {
            throw Failure("install_grant_receipt_mismatch");
        }
    }

    private static async Task<string> WriteProtectedInstallCommandAsync(
        HostedContext context,
        string installCommand,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(context.Root, $"installer-{context.RunId:D}.ps1");
        EnsureDescendantPath(context.Root, path);
        EnsureNoReparseChain(context.Root, path, mustExist: false);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw Failure("installer_command_file_already_exists");
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(installCommand);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        EnsureNoReparseChain(context.Root, path, mustExist: true);
        ValidateTrustedLeafAcl(path, context.TrustedSids);
        return path;
    }

    private static Process StartInstaller(string commandPath)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            throw Failure("powershell_system_path_unavailable");
        }

        var powershellPath = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershellPath))
        {
            throw Failure("windows_powershell_unavailable");
        }

        var start = new ProcessStartInfo(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(commandPath);

        // The link command must use only the shipped install/state defaults.
        start.Environment.Remove("NetRatel_ROOT");
        start.Environment.Remove("NetRatel_STATE");
        start.Environment.Remove("NetRatel_LOG_DIR");
        start.Environment.Remove("NetRatel_UPDATE_ROOT");
        start.Environment.Remove("NetRatel_UPDATE_STATE");
        start.Environment.Remove("NetRatel_UPDATE_REQUEST");

        return Process.Start(start) ?? throw Failure("installer_process_start_failed");
    }

    private static async Task WaitForInstallerAsync(
        Process process,
        Task stdoutDrain,
        Task stderrDrain,
        CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutDrain, stderrDrain)
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    private static async Task StopOwnedInstallerAsync(Process process, Task? stdoutDrain, Task? stderrDrain)
    {
        var stopFailed = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The owned installer exited between the status check and Kill.
            stopFailed = !process.HasExited;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            stopFailed = !process.HasExited;
        }

        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(cleanupTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            var closeCode = CloseInstallerOutput(process);
            var outputCode = await ObserveClosedInstallerOutputAsync(stdoutDrain, stderrDrain);
            if (outputCode is not null) throw Failure(outputCode);
            if (closeCode is not null) throw Failure(closeCode);
            throw Failure(stopFailed ? "installer_process_stop_failed" : "installer_process_reap_timeout");
        }

        if (stdoutDrain is null && stderrDrain is null)
        {
            if (stopFailed) throw Failure("installer_process_stop_failed");
            return;
        }

        var readers = new[] { stdoutDrain, stderrDrain }.Where(task => task is not null).Cast<Task>().ToArray();
        try
        {
            await Task.WhenAll(readers).WaitAsync(cleanupTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            var closeCode = CloseInstallerOutput(process);
            var outputCode = await ObserveClosedInstallerOutputAsync(stdoutDrain, stderrDrain);
            if (outputCode is not null) throw Failure(outputCode);
            if (closeCode is not null) throw Failure(closeCode);
            throw Failure("installer_output_drain_timeout");
        }
        catch (IOException)
        {
            var closeCode = CloseInstallerOutput(process);
            var outputCode = await ObserveClosedInstallerOutputAsync(stdoutDrain, stderrDrain);
            if (outputCode is not null) throw Failure(outputCode);
            if (closeCode is not null) throw Failure(closeCode);
        }
        catch (ObjectDisposedException)
        {
            // The owning process was reaped and its redirected pipes were closed.
            if (!process.HasExited) throw Failure("installer_output_closed_before_exit");
        }

        if (stopFailed) throw Failure("installer_process_stop_failed");
    }

    private static async Task<string?> ObserveClosedInstallerOutputAsync(Task? stdoutDrain, Task? stderrDrain)
    {
        var readers = new[] { stdoutDrain, stderrDrain }.Where(task => task is not null).Cast<Task>().ToArray();
        if (readers.Length == 0) return null;
        try
        {
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(3));
            return null;
        }
        catch (TimeoutException)
        {
            ObserveTasksOnCompletion(readers);
            return "installer_output_close_timeout";
        }
        catch (IOException)
        {
            // Closing the redirected pipe after reaping the owned PowerShell process is expected.
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return ClassifyException(exception);
        }
    }

    private static string? CloseInstallerOutput(Process process)
    {
        string? failureCode = null;
        try { process.StandardOutput.Close(); }
        catch (InvalidOperationException)
        {
            if (!process.HasExited) failureCode = "installer_stdout_close_failed";
        }
        catch (IOException) { failureCode = "installer_stdout_close_failed"; }
        try { process.StandardError.Close(); }
        catch (InvalidOperationException)
        {
            if (!process.HasExited) failureCode ??= "installer_stderr_close_failed";
        }
        catch (IOException) { failureCode ??= "installer_stderr_close_failed"; }
        return failureCode;
    }

    private static async Task DrainAndDiscardAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) is > 0)
            {
                Array.Clear(buffer);
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static async Task<string?> ObserveReadinessTaskAsync(Task<ReadinessSnapshot> readinessTask)
    {
        try
        {
            var snapshot = await readinessTask.WaitAsync(TimeSpan.FromSeconds(5));
            snapshot.ClearSensitiveBuffers();
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            ObserveReadinessTaskOnCompletion(readinessTask);
            return "readiness_observer_stop_timeout";
        }
        catch (Exception exception)
        {
            return ClassifyException(exception);
        }
    }

    private static void ObserveReadinessTaskOnCompletion(Task<ReadinessSnapshot> readinessTask)
    {
        _ = readinessTask.ContinueWith(
            static completedTask =>
            {
                if (completedTask.IsCompletedSuccessfully)
                {
                    completedTask.Result.ClearSensitiveBuffers();
                }
                else if (completedTask.IsFaulted)
                {
                    _ = completedTask.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static ValidatedReadiness ValidateReadinessSnapshot(
        ReadinessSnapshot snapshot,
        HostedContext context,
        DateTimeOffset observerStartedAtUtc)
    {
        using var requestDocument = ParseJsonDocumentAllowingUtf8Bom(
            snapshot.RequestBytes, new JsonDocumentOptions { MaxDepth = 8 });
        using var readyDocument = JsonDocument.Parse(snapshot.ReadyBytes, new JsonDocumentOptions { MaxDepth = 8 });
        var request = requestDocument.RootElement;
        var ready = readyDocument.RootElement;
        var now = DateTimeOffset.UtcNow;

        var requestSchema = ReadRequiredString(request, "schema");
        var attemptIdText = ReadRequiredString(request, "attemptId");
        var nonce = ReadRequiredString(request, "nonce");
        var requestedAt = ReadRequiredDateTimeOffset(request, "requestedAtUtc");
        var expiresAt = ReadRequiredDateTimeOffset(request, "expiresAtUtc");
        if (!string.Equals(requestSchema, RequestSchema, StringComparison.Ordinal) ||
            !Guid.TryParseExact(attemptIdText, "D", out var attemptId) || attemptId == Guid.Empty ||
            !IsValidReadinessNonce(nonce) || requestedAt < observerStartedAtUtc.AddSeconds(-5) ||
            requestedAt > now.AddSeconds(5) || expiresAt <= requestedAt ||
            expiresAt > requestedAt.AddMinutes(10) || now > expiresAt)
        {
            throw Failure("readiness_challenge_invalid_or_stale");
        }

        var readyAttempt = ReadRequiredString(ready, "attemptId");
        var readyNonce = ReadRequiredString(ready, "nonce");
        var readyAgentIdText = ReadRequiredString(ready, "agentId");
        var connectionIdText = ReadRequiredString(ready, "connectionId");
        var stage = ReadRequiredString(ready, "stage");
        var agentId = ParseNonEmptyGuid(readyAgentIdText, "readiness_agent_id_invalid");
        var connectionId = ParseNonEmptyGuid(connectionIdText, "readiness_connection_id_invalid");
        var processId = ReadRequiredInt32(ready, "processId");
        var sessionId = ReadRequiredInt32(ready, "sessionId");
        var userSid = ReadRequiredString(ready, "userSid");
        var tenantId = ReadRequiredInt32(ready, "tenantId");
        var epoch = ReadRequiredUInt64(ready, "connectionEpoch");
        var heartbeatSequence = ReadRequiredUInt64(ready, "heartbeatSequence");
        var recordRequestedAt = ReadRequiredDateTimeOffset(ready, "requestedAtUtc");
        var observedAt = ReadRequiredDateTimeOffset(ready, "observedAtUtc");
        var processStartedAt = ReadRequiredDateTimeOffset(ready, "processStartedAtUtc");

        if (!string.Equals(ReadRequiredString(ready, "schema"), ReadySchema, StringComparison.Ordinal) ||
            !string.Equals(readyAttempt, attemptIdText, StringComparison.Ordinal) ||
            !string.Equals(readyNonce, nonce, StringComparison.Ordinal) ||
            !string.Equals(stage, "heartbeat_ready", StringComparison.Ordinal) ||
            processId <= 0 || sessionId != 0 || !string.Equals(userSid, LocalSystemSid, StringComparison.Ordinal) ||
            tenantId != context.TenantId || epoch == 0 || heartbeatSequence < 2 ||
            recordRequestedAt != requestedAt || processStartedAt <= requestedAt ||
            observedAt < requestedAt || observedAt < processStartedAt.AddSeconds(-2) ||
            observedAt > now.AddSeconds(5) || now - observedAt > TimeSpan.FromSeconds(30))
        {
            throw Failure("readiness_record_invalid_or_stale");
        }

        if (!snapshot.ReadinessDirectoryProtected || !snapshot.RequestFileProtected || !snapshot.ReadyFileProtected)
        {
            throw Failure("readiness_evidence_acl_untrusted");
        }

        return new ValidatedReadiness(
            attemptId,
            processId,
            processStartedAt,
            sessionId,
            userSid,
            agentId,
            tenantId,
            epoch,
            connectionId,
            heartbeatSequence,
            requestedAt,
            observedAt);
    }

    private static async Task WaitForOnlineDirectoryIdentityAsync(
        HttpClient client,
        HostedContext context,
        ValidatedReadiness readiness,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        var hadTransientProbeFailure = false;
        while (!overallTimeout.IsCancellationRequested)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(overallTimeout.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var response = await client.GetAsync(
                    $"/api/v2/client-presence/?tenantId={context.TenantId}&online=true&limit=100",
                    HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    hadTransientProbeFailure = false;
                    using var document = await ReadJsonDocumentAsync(response, MaximumJsonResponseBytes, requestTimeout.Token);
                    if (DirectoryContainsCurrentIdentity(document.RootElement, context, readiness))
                    {
                        return;
                    }
                }
                else if (response.StatusCode != HttpStatusCode.ServiceUnavailable &&
                         (int)response.StatusCode < 500)
                {
                    throw Failure($"directory_status_{(int)response.StatusCode}");
                }
                else if ((int)response.StatusCode >= 500)
                {
                    hadTransientProbeFailure = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (requestTimeout.IsCancellationRequested)
            {
                // A slow single probe must not consume the overall visibility window.
                hadTransientProbeFailure = true;
            }
            catch (HttpRequestException exception) when (
                exception.StatusCode is null || (int)exception.StatusCode.Value >= 500)
            {
                // A public route can briefly be unavailable while the authenticated client registers.
                hadTransientProbeFailure = true;
            }
            catch (HttpRequestException exception) when (exception.StatusCode is not null)
            {
                throw Failure($"directory_status_{(int)exception.StatusCode.Value}");
            }

            try
            {
                await timer.WaitForNextTickAsync(overallTimeout.Token);
            }
            catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw Failure(hadTransientProbeFailure
            ? "online_directory_identity_probe_unavailable"
            : "online_directory_identity_not_visible");
    }

    private static bool DirectoryContainsCurrentIdentity(
        JsonElement document,
        HostedContext context,
        ValidatedReadiness readiness)
    {
        if (!string.Equals(ReadRequiredString(document, "mode"), "akka", StringComparison.Ordinal) ||
            !document.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (ReadRequiredInt32(item, "tenantId") == context.TenantId &&
                Guid.TryParse(ReadRequiredString(item, "agentId"), out var agentId) &&
                agentId == readiness.AgentId &&
                ReadRequiredBoolean(item, "online") && ReadRequiredBoolean(item, "isEnabled") &&
                ReadRequiredBoolean(item, "isAuthoritative") &&
                string.Equals(ReadRequiredString(item, "source"), "gateway", StringComparison.Ordinal) &&
                string.Equals(ReadRequiredString(item, "authority"), "akka", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateInstalledCredentialFile(HostedContext context)
    {
        if (!File.Exists(context.CredentialPath) || Directory.Exists(context.CredentialPath))
        {
            throw Failure("system_enrollment_credential_missing");
        }

        EnsureNoReparseChain(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            context.CredentialPath,
            mustExist: true);
        EnsureNoReparseChain(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            context.StateRoot,
            mustExist: true);
        ValidateTrustedLeafAcl(context.CredentialPath, context.TrustedSids);
    }

    private static async Task<ServiceEvidence> ValidateCurrentSystemServiceAsync(
        HostedContext context,
        ValidatedReadiness readiness,
        CancellationToken cancellationToken)
    {
        using var serviceKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}", writable: false);
        if (serviceKey is null)
        {
            throw Failure("service_registration_missing");
        }

        var registeredImage = serviceKey.GetValue("ImagePath") as string;
        var startName = serviceKey.GetValue("ObjectName") as string;
        var expectedExecutable = Path.GetFullPath(Path.Combine(
            context.InstallRoot, "versions", context.CandidateVersion, "NetRatel.Client.exe"));
        EnsureNoReparseChain(context.InstallRoot, expectedExecutable, mustExist: true);
        var expectedImage = $"\"{expectedExecutable}\" --service";
        if (!string.Equals(registeredImage, expectedImage, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(startName, "LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("service_registration_identity_mismatch");
        }

        using var service = new ServiceController(ServiceName);
        service.Refresh();
        if (service.Status != ServiceControllerStatus.Running)
        {
            throw Failure("service_not_running");
        }

        var processId = await ReadServiceProcessIdAsync(cancellationToken);
        if (processId <= 0 || processId != readiness.ProcessId)
        {
            throw Failure("service_readiness_pid_mismatch");
        }

        using var process = Process.GetProcessById(processId);
        process.Refresh();
        var processPath = process.MainModule?.FileName;
        var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
        if (process.SessionId != 0 || string.IsNullOrWhiteSpace(processPath) ||
            !PathEquals(Path.GetFullPath(processPath), expectedExecutable) ||
            Math.Abs((actualStart - readiness.ProcessStartedAtUtc).TotalSeconds) > 2)
        {
            throw Failure("service_process_identity_mismatch");
        }

        var processSid = ReadProcessUserSid(process);
        if (!string.Equals(processSid, LocalSystemSid, StringComparison.Ordinal))
        {
            throw Failure("service_process_not_local_system");
        }

        return new ServiceEvidence(processId, actualStart, processSid, process.SessionId);
    }

    private static async Task<int> ReadServiceProcessIdAsync(CancellationToken cancellationToken)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var scPath = Path.Combine(systemDirectory, "sc.exe");
        if (string.IsNullOrWhiteSpace(systemDirectory) || !File.Exists(scPath))
        {
            throw Failure("service_pid_query_tool_unavailable");
        }

        using var process = Process.Start(new ProcessStartInfo(scPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "queryex", ServiceName }
        }) ?? throw Failure("service_pid_query_start_failed");
        var outputTask = ReadBoundedTextAsync(process.StandardOutput, 16 * 1024, CancellationToken.None);
        var errorDrain = DrainAndDiscardAsync(process.StandardError, CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            var cleanupCode = await StopAndObserveOwnedProcessAsync(process, outputTask, errorDrain);
            if (cleanupCode is not null) throw Failure(cleanupCode);
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure("service_pid_query_timeout");
        }

        string output;
        try
        {
            await Task.WhenAll(outputTask, errorDrain).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            output = await outputTask;
        }
        catch (TimeoutException)
        {
            var cleanupCode = await StopAndObserveOwnedProcessAsync(process, outputTask, errorDrain);
            throw Failure(cleanupCode ?? "service_pid_query_output_timeout");
        }
        catch (OperationCanceledException)
        {
            var cleanupCode = await StopAndObserveOwnedProcessAsync(process, outputTask, errorDrain);
            if (cleanupCode is not null) throw Failure(cleanupCode);
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure("service_pid_query_output_timeout");
        }
        if (process.ExitCode != 0)
        {
            throw Failure("service_pid_query_failed");
        }

        var match = Regex.Match(output, @"(?im)^\s*PID\s*:\s*(?<pid>[0-9]+)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var processId))
        {
            throw Failure("service_pid_missing");
        }

        return processId;
    }

    private static async Task<string?> StopAndObserveOwnedProcessAsync(Process process, params Task[] outputTasks)
    {
        var stopFailed = !TryKillOwnedProcess(process);
        var closeCode = CloseProcessOutput(process);
        var observeCode = await ObserveProcessExitAndOutputAsync(process, outputTasks);
        if (stopFailed) return "owned_process_stop_failed";
        return closeCode ?? observeCode;
    }

    private static bool TryKillOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? CloseProcessOutput(Process process)
    {
        string? failureCode = null;
        try { process.StandardOutput.Close(); }
        catch (InvalidOperationException)
        {
            if (!process.HasExited) failureCode = "owned_process_stdout_close_failed";
        }
        catch (IOException) { failureCode = "owned_process_stdout_close_failed"; }
        try { process.StandardError.Close(); }
        catch (InvalidOperationException)
        {
            if (!process.HasExited) failureCode ??= "owned_process_stderr_close_failed";
        }
        catch (IOException) { failureCode ??= "owned_process_stderr_close_failed"; }
        return failureCode;
    }

    private static async Task<string?> ObserveProcessExitAndOutputAsync(Process process, Task[] outputTasks)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return "owned_process_reap_timeout";
        }

        try
        {
            await Task.WhenAll(outputTasks).WaitAsync(TimeSpan.FromSeconds(3));
            return null;
        }
        catch (TimeoutException)
        {
            ObserveTasksOnCompletion(outputTasks);
            return "owned_process_output_close_timeout";
        }
        catch (IOException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return ClassifyException(exception);
        }
    }

    private static void ObserveTasksOnCompletion(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            _ = task.ContinueWith(
                static completedTask =>
                {
                    if (completedTask.IsFaulted)
                    {
                        _ = completedTask.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static string ReadProcessUserSid(Process process)
    {
        using var processHandle = OpenProcess(0x1000, inheritHandle: false, process.Id);
        if (processHandle.IsInvalid)
        {
            throw Failure("service_process_token_unavailable");
        }

        if (!OpenProcessToken(processHandle, 0x0008, out var tokenHandle) || tokenHandle.IsInvalid)
        {
            tokenHandle?.Dispose();
            throw Failure("service_process_token_unavailable");
        }

        using (tokenHandle)
        using (var identity = new WindowsIdentity(tokenHandle.DangerousGetHandle()))
        {
            return identity.User?.Value ?? throw Failure("service_process_sid_unavailable");
        }
    }

    private static async Task WriteIdentityReceiptAsync(
        HostedContext context,
        ValidatedReadiness readiness,
        ServiceEvidence service,
        CancellationToken cancellationToken)
    {
        if (File.Exists(context.IdentityReceiptPath) || Directory.Exists(context.IdentityReceiptPath))
        {
            throw Failure("identity_receipt_already_exists");
        }

        var receipt = new IdentityReceipt(
            1,
            Scope,
            context.RunId.ToString("D"),
            context.SourceSha,
            context.TestMergeSha,
            context.ProductVersion,
            readiness.AgentId.ToString("D"),
            readiness.TenantId,
            readiness.ConnectionEpoch,
            readiness.ConnectionId.ToString("D"),
            readiness.HeartbeatSequence,
            string.Equals(service.UserSid, LocalSystemSid, StringComparison.Ordinal),
            service.SessionId == 0,
            service.ProcessId == readiness.ProcessId,
            IsFreshReadiness(readiness, DateTimeOffset.UtcNow),
            true);
        if (!receipt.LocalSystem || !receipt.SessionZero || !receipt.CurrentServiceProcessMatched ||
            !receipt.FreshnessVerified || !receipt.OnlineDirectoryVerified)
        {
            throw Failure("identity_receipt_conditions_unmet");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, WebJson);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(context.IdentityReceiptPath)!,
            $".identity-{context.RunId:N}-{Guid.NewGuid():N}.tmp");
        try
        {
            EnsureDescendantPath(context.Root, temporaryPath);
            EnsureNoReparseChain(context.Root, context.IdentityReceiptPath, mustExist: false);
            EnsureNoReparseChain(context.Root, temporaryPath, mustExist: false);
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            EnsureNoReparsePoint(temporaryPath, mustExist: true);
            ValidateTrustedLeafAcl(temporaryPath, context.TrustedSids);
            File.Move(temporaryPath, context.IdentityReceiptPath, overwrite: false);
            EnsureNoReparsePoint(context.IdentityReceiptPath, mustExist: true);
            ValidateTrustedLeafAcl(context.IdentityReceiptPath, context.TrustedSids);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsFreshReadiness(ValidatedReadiness readiness, DateTimeOffset now) =>
        readiness.RequestedAtUtc <= now.AddSeconds(5) &&
        readiness.ObservedAtUtc <= now.AddSeconds(5) &&
        now - readiness.ObservedAtUtc <= TimeSpan.FromSeconds(30);

    private static async Task<string> ReadGitHeadShaAsync(string workspace, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(Path.GetFullPath(workspace));
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("HEAD");
        using var process = Process.Start(start) ?? throw Failure("checkout_sha_query_start_failed");
        var outputTask = ReadBoundedTextAsync(process.StandardOutput, 256, CancellationToken.None);
        var errorTask = DrainAndDiscardAsync(process.StandardError, CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            var cleanupCode = await StopAndObserveOwnedProcessAsync(process, outputTask, errorTask);
            if (cleanupCode is not null) throw Failure(cleanupCode);
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure("checkout_sha_query_timeout");
        }

        try
        {
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var cleanupCode = await StopAndObserveOwnedProcessAsync(process, outputTask, errorTask);
            if (cleanupCode is not null) throw Failure(cleanupCode);
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure("checkout_sha_query_output_timeout");
        }
        catch (TimeoutException)
        {
            var cleanupCode = await StopAndObserveOwnedProcessAsync(process, outputTask, errorTask);
            throw Failure(cleanupCode ?? "checkout_sha_query_output_timeout");
        }

        var head = outputTask.Result.Trim();
        if (process.ExitCode != 0 || !IsLowerHex(head, 40))
        {
            throw Failure("checkout_sha_query_failed");
        }

        return head;
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedStreamAsync(await response.Content.ReadAsStreamAsync(cancellationToken), maximumBytes, cancellationToken);
        try
        {
            // The ReadOnlyMemory<byte> overload can retain the caller's backing array until the
            // JsonDocument is disposed. Parsing a stream gives the document owned storage before
            // the bounded response buffer is zeroed below.
            using var stream = new MemoryStream(bytes, writable: false);
            return await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 32 },
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static JsonDocument ParseJsonDocumentAllowingUtf8Bom(
        byte[] utf8Json,
        JsonDocumentOptions options)
    {
        var start = utf8Json.Length >= 3 &&
                    utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF
            ? 3
            : 0;
        return JsonDocument.Parse(utf8Json.AsMemory(start), options);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedStreamAsync(stream, maximumBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedStreamAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            throw Failure("bounded_input_limit_invalid");
        }

        var buffer = new byte[maximumBytes];
        var offset = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                offset += read;
                if (offset == maximumBytes)
                {
                    var extra = new byte[1];
                    try
                    {
                        if (await stream.ReadAsync(extra.AsMemory(), cancellationToken) != 0)
                        {
                            throw Failure("bounded_input_size_exceeded");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(extra);
                    }

                    break;
                }
            }

            return buffer.AsSpan(0, offset).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 256));
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                if (output.Length + read > maximumCharacters)
                {
                    throw Failure("bounded_process_output_exceeded");
                }

                output.Append(buffer, 0, read);
            }
        }
        finally
        {
            Array.Clear(buffer);
        }

        return output.ToString();
    }

    private static string NormalizeContainedPath(string root, string value)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw Failure("context_path_not_absolute");
        }

        var fullPath = Path.GetFullPath(value);
        EnsureDescendantPath(root, fullPath);
        return fullPath;
    }

    private static void ValidatePathWithinRoot(
        string root,
        string path,
        bool isDirectory,
        bool mustExist,
        IReadOnlySet<string> trustedSids)
    {
        EnsureDescendantPath(root, path);
        EnsureNoReparseChain(root, path, mustExist);
        var exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
        if (mustExist && !exists)
        {
            throw Failure("protected_context_path_missing");
        }

        if ((File.Exists(path) && isDirectory) || (Directory.Exists(path) && !isDirectory))
        {
            throw Failure("protected_context_path_type_mismatch");
        }

        var checkedPath = exists ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(checkedPath) ||
            !(Directory.Exists(checkedPath) || File.Exists(checkedPath)))
        {
            throw Failure("protected_context_parent_missing");
        }

        ValidateTrustedLeafAcl(checkedPath, trustedSids);
    }

    private static void EnsureDescendantPath(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == "." ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw Failure("protected_context_path_outside_root");
        }
    }

    private static void EnsureNoReparsePoint(string path, bool mustExist)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        if (!exists)
        {
            if (mustExist) throw Failure("protected_path_missing");
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            {
                throw Failure("protected_path_parent_missing");
            }

            path = parent;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure("protected_path_reparse_point");
        }
    }

    private static void EnsureNoReparseChain(string root, string path, bool mustExist)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        EnsureDescendantPath(fullRoot, fullPath);
        EnsureNoReparsePoint(fullRoot, mustExist: true);

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = fullRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var exists = File.Exists(current) || Directory.Exists(current);
            if (!exists)
            {
                if (mustExist)
                {
                    throw Failure("protected_path_missing");
                }

                return;
            }

            EnsureNoReparsePoint(current, mustExist: true);
            if (!string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(current))
            {
                throw Failure("protected_path_parent_not_directory");
            }
        }

        if (mustExist && !(File.Exists(fullPath) || Directory.Exists(fullPath)))
        {
            throw Failure("protected_path_missing");
        }
    }

    private static string NormalizeAbsolutePath(string value)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw Failure("context_path_not_absolute");
        }

        return Path.GetFullPath(value);
    }

    private static void ValidateProtectedRootAcl(string root, IReadOnlySet<string> trustedSids)
    {
        var security = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!security.AreAccessRulesProtected || owner is null || !trustedSids.Contains(owner.Value))
        {
            throw Failure("protected_root_owner_or_inheritance_invalid");
        }

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        foreach (var rule in rules)
        {
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow)
            {
                throw Failure("protected_root_acl_shape_invalid");
            }

            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (!trustedSids.Contains(sid) && (rule.FileSystemRights & MutationRights) != 0)
            {
                throw Failure("protected_root_untrusted_writer");
            }
        }
    }

    private static void ValidateTrustedLeafAcl(string path, IReadOnlySet<string> trustedSids)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !trustedSids.Contains(owner.Value))
        {
            throw Failure("protected_leaf_owner_invalid");
        }

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        foreach (var rule in rules)
        {
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (!trustedSids.Contains(sid) && rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & MutationRights) != 0)
            {
                throw Failure("protected_leaf_untrusted_writer");
            }
        }
    }

    private static IReadOnlySet<string> TrustedSids(string? currentUserSid = null)
    {
        if (currentUserSid is null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            currentUserSid = identity.User?.Value ?? throw Failure("administrator_sid_unavailable");
        }
        return new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            LocalSystemSid,
            currentUserSid
        };
    }

    private static Uri ParsePublicOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns ||
            !uri.Host.Contains('.', StringComparison.Ordinal) || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath.TrimEnd('/').Length != 0 ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("public_origin_invalid");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    private static void ValidatePrivateServiceOrigin(string value, string requiredScheme)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, requiredScheme, StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath.TrimEnd('/').Length != 0 ||
            !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw Failure("private_service_origin_invalid");
        }
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port && right.UserInfo.Length == 0;

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && (length == 40 ? LowerSha40 : LowerSha64).IsMatch(value);

    private static bool IsAtomicReplaceRace(IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError is 32 or 33;
    }

    private static bool IsValidReadinessNonce(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            try { return bytes.Length == 32; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Guid ParseNonEmptyGuid(string value, string failureCode)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw Failure(failureCode);
        }

        return parsed;
    }

    private static JsonElement GetRequiredProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            throw Failure("response_contract_invalid");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement parent, string name)
    {
        var value = GetRequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Failure("response_contract_invalid");
        }

        return value.GetString()!;
    }

    private static int ReadRequiredInt32(JsonElement parent, string name)
    {
        var value = GetRequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Failure("response_contract_invalid");
        }

        return result;
    }

    private static long ReadRequiredInt64(JsonElement parent, string name)
    {
        var value = GetRequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw Failure("response_contract_invalid");
        }

        return result;
    }

    private static ulong ReadRequiredUInt64(JsonElement parent, string name)
    {
        var value = GetRequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var result))
        {
            throw Failure("readiness_numeric_field_invalid");
        }

        return result;
    }

    private static bool ReadRequiredBoolean(JsonElement parent, string name)
    {
        var value = GetRequiredProperty(parent, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Failure("response_contract_invalid");
        }

        return value.GetBoolean();
    }

    private static DateTimeOffset ReadRequiredDateTimeOffset(JsonElement parent, string name)
    {
        var value = GetRequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var result))
        {
            throw Failure("response_timestamp_invalid");
        }

        return result.ToUniversalTime();
    }

    private static string ClassifyException(Exception exception) => exception switch
    {
        HostedOnboardingFailure failure => failure.Code,
        OperationCanceledException => "timeout",
        TimeoutException => "timeout",
        HttpRequestException => "http_transport",
        UnauthorizedAccessException => "access_denied",
        System.Security.SecurityException => "security_boundary",
        IOException => "io_failure",
        JsonException => "json_invalid",
        CryptographicException => "crypto_failure",
        System.ComponentModel.Win32Exception => "windows_api_failure",
        _ => "unexpected_failure"
    };

    private static HostedOnboardingFailure Failure(string code) => new(code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    private sealed record HostedContext(
        string Root,
        IReadOnlySet<string> TrustedSids,
        string SourceSha,
        string TestMergeSha,
        string ProductVersion,
        Guid RunId,
        Uri PublicOrigin,
        int TenantId,
        string OperatorEmail,
        string OperatorPasswordFile,
        string CandidateArchive,
        string CandidateRuntimeId,
        string CandidateVersion,
        string CandidateSha256,
        string InstallRoot,
        string StateRoot,
        string CredentialPath,
        string IdentityReceiptPath,
        string ControlRequestPath,
        string ControlAckPath,
        string SafeEvidenceDirectory);

    private sealed record ReadinessSnapshot(
        byte[] RequestBytes,
        byte[] ReadyBytes,
        bool ReadinessDirectoryProtected,
        bool RequestFileProtected,
        bool ReadyFileProtected)
    {
        public void ClearSensitiveBuffers()
        {
            CryptographicOperations.ZeroMemory(RequestBytes);
            CryptographicOperations.ZeroMemory(ReadyBytes);
        }
    }

    private sealed record ValidatedReadiness(
        Guid AttemptId,
        int ProcessId,
        DateTimeOffset ProcessStartedAtUtc,
        int SessionId,
        string UserSid,
        Guid AgentId,
        int TenantId,
        ulong ConnectionEpoch,
        Guid ConnectionId,
        ulong HeartbeatSequence,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset ObservedAtUtc);

    private sealed record ServiceEvidence(
        int ProcessId,
        DateTimeOffset ProcessStartedAtUtc,
        string UserSid,
        int SessionId);

    private sealed record IdentityReceipt(
        int SchemaVersion,
        string Scope,
        string RunId,
        string SourceSha,
        string TestMergeSha,
        string ProductVersion,
        string AgentId,
        int TenantId,
        ulong ConnectionEpoch,
        string ConnectionId,
        ulong HeartbeatSequence,
        bool LocalSystem,
        bool SessionZero,
        bool CurrentServiceProcessMatched,
        bool FreshnessVerified,
        bool OnlineDirectoryVerified);

    private sealed record LocalLoginRequest(string Email, string Password, bool RememberMe);

    private sealed record IssuedInstallLink(Guid Id, string InstallCommand);

    private sealed record ClientInstallLinkResponse(
        Guid? Id,
        int? TenantId,
        string? RuntimeId,
        string? ArtifactVersion,
        string? ArtifactSha256,
        DateTimeOffset? ExpiresAtUtc,
        int? MaxUses,
        int? RemainingUses,
        bool? InstallAsService,
        bool? SilentInstall,
        string? PublicUrl,
        string? InstallCommand,
        bool? Replay);

    private sealed class HostedOnboardingFailure(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    private sealed class ReadinessObserver : IDisposable
    {
        private readonly string _commonApplicationData;
        private readonly string _netRatelDirectory;
        private readonly string _stateRoot;
        private readonly string _readinessDirectory;
        private readonly string _requestPath;
        private readonly string _readyPath;
        private readonly IReadOnlySet<string> _trustedSids;
        private readonly object _gate = new();
        private readonly TaskCompletionSource<ReadinessSnapshot> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FileSystemWatcher? _bootstrapWatcher;
        private FileSystemWatcher? _productWatcher;
        private byte[]? _requestBytes;
        private bool _disposed;

        public ReadinessObserver(string stateRoot, string commonApplicationData, IReadOnlySet<string> trustedSids)
        {
            StartedAtUtc = DateTimeOffset.UtcNow;
            _commonApplicationData = Path.GetFullPath(commonApplicationData);
            _netRatelDirectory = Path.GetFullPath(Path.Combine(_commonApplicationData, "NetRatel"));
            _stateRoot = Path.GetFullPath(stateRoot);
            _readinessDirectory = Path.GetFullPath(Path.Combine(_stateRoot, "install-readiness"));
            _requestPath = Path.Combine(_readinessDirectory, "request.json");
            _readyPath = Path.Combine(_readinessDirectory, "ready.json");
            _trustedSids = trustedSids;
            EnsureDescendantPath(_commonApplicationData, _readinessDirectory);
            EnsureNoReparsePoint(_commonApplicationData, mustExist: true);

            if (Directory.Exists(_netRatelDirectory))
            {
                StartProductWatcher();
            }
            else
            {
                StartBootstrapWatcher();
            }

            TryCaptureCurrentEvidence();
        }

        public DateTimeOffset StartedAtUtc { get; }

        public async Task<ReadinessSnapshot> WaitForTwoAcknowledgedHeartbeatsAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (!_completed.Task.IsCompleted)
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    throw Failure("readiness_observer_stopped");
                }

                TryCaptureCurrentEvidence();
            }

            return await _completed.Task.WaitAsync(cancellationToken);
        }

        private void StartBootstrapWatcher()
        {
            var watcher = new FileSystemWatcher(_commonApplicationData, "NetRatel")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                InternalBufferSize = 16 * 1024,
                EnableRaisingEvents = false
            };
            watcher.Created += OnBootstrapPathChanged;
            watcher.Renamed += OnBootstrapPathRenamed;
            watcher.Error += OnWatcherError;
            lock (_gate)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }

                _bootstrapWatcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
        }

        private void StartProductWatcher()
        {
            EnsureNoReparseChain(_commonApplicationData, _netRatelDirectory, mustExist: true);
            ValidateTrustedLeafAcl(_netRatelDirectory, _trustedSids);
            var watcher = new FileSystemWatcher(_netRatelDirectory, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024,
                EnableRaisingEvents = false
            };
            watcher.Created += OnProductFileChanged;
            watcher.Changed += OnProductFileChanged;
            watcher.Renamed += OnProductFileRenamed;
            watcher.Error += OnWatcherError;
            FileSystemWatcher? previousProduct;
            FileSystemWatcher? previousBootstrap;
            lock (_gate)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }

                previousProduct = _productWatcher;
                previousBootstrap = _bootstrapWatcher;
                _productWatcher = watcher;
                _bootstrapWatcher = null;
                watcher.EnableRaisingEvents = true;
            }

            previousProduct?.Dispose();
            previousBootstrap?.Dispose();
        }

        private void OnBootstrapPathChanged(object sender, FileSystemEventArgs args)
        {
            if (PathEquals(args.FullPath, _netRatelDirectory)) ActivateProductWatcherIfPresent();
        }

        private void OnBootstrapPathRenamed(object sender, RenamedEventArgs args)
        {
            if (PathEquals(args.FullPath, _netRatelDirectory)) ActivateProductWatcherIfPresent();
        }

        private void OnProductFileChanged(object sender, FileSystemEventArgs args)
        {
            if (IsExpectedPath(args.FullPath)) TryCaptureCurrentEvidence();
        }

        private void OnProductFileRenamed(object sender, RenamedEventArgs args)
        {
            if (IsExpectedPath(args.FullPath) || IsExpectedPath(args.OldFullPath)) TryCaptureCurrentEvidence();
        }

        private void ActivateProductWatcherIfPresent()
        {
            if (!Directory.Exists(_netRatelDirectory)) return;
            try
            {
                StartProductWatcher();
                TryCaptureCurrentEvidence();
            }
            catch (HostedOnboardingFailure failure)
            {
                SetFailure(failure);
            }
            catch (Exception exception)
            {
                SetFailure(Failure(ClassifyException(exception)));
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            var exception = args.GetException();
            var code = exception is InternalBufferOverflowException
                ? "readiness_watcher_overflow"
                : "readiness_watcher_failed";
            SetFailure(Failure(code));
        }

        private void SetFailure(Exception exception)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _completed.TrySetException(exception);
                }
            }
        }

        private bool IsExpectedPath(string path) =>
            PathEquals(path, _requestPath) || PathEquals(path, _readyPath);

        private void TryCaptureCurrentEvidence()
        {
            lock (_gate)
            {
                if (_disposed || _completed.Task.IsCompleted) return;
                try
                {
                    if (!Directory.Exists(_netRatelDirectory) || !Directory.Exists(_stateRoot) ||
                        !Directory.Exists(_readinessDirectory))
                    {
                        return;
                    }

                    EnsureNoReparseChain(_commonApplicationData, _readinessDirectory, mustExist: true);
                    ValidateTrustedLeafAcl(_netRatelDirectory, _trustedSids);
                    ValidateTrustedLeafAcl(_stateRoot, _trustedSids);
                    ValidateTrustedLeafAcl(_readinessDirectory, _trustedSids);
                    var readinessAcl = new DirectoryInfo(_readinessDirectory)
                        .GetAccessControl(AccessControlSections.Access);
                    if (!readinessAcl.AreAccessRulesProtected)
                    {
                        throw Failure("readiness_directory_inheritance_unprotected");
                    }

                    if (File.Exists(_requestPath))
                    {
                        EnsureNoReparseChain(_commonApplicationData, _requestPath, mustExist: true);
                        ValidateTrustedLeafAcl(_requestPath, _trustedSids);
                        var requestBytes = TryReadEvidenceFile(_requestPath);
                        if (requestBytes is null)
                        {
                            ClearCachedRequest();
                            return;
                        }

                        ReplaceCachedRequest(requestBytes);
                    }
                    else
                    {
                        ClearCachedRequest();
                        return;
                    }

                    if (_requestBytes is null || !File.Exists(_readyPath)) return;
                    EnsureNoReparseChain(_commonApplicationData, _readyPath, mustExist: true);
                    ValidateTrustedLeafAcl(_readyPath, _trustedSids);
                    byte[]? readyBytes = TryReadEvidenceFile(_readyPath);
                    if (readyBytes is null) return;

                    byte[]? requestClone = null;
                    try
                    {
                        using var ready = JsonDocument.Parse(readyBytes, new JsonDocumentOptions { MaxDepth = 8 });
                        var root = ready.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                        {
                            throw Failure("readiness_record_invalid");
                        }

                        var stage = ReadRequiredString(root, "stage");
                        if (!string.Equals(stage, "heartbeat_ready", StringComparison.Ordinal))
                        {
                            return;
                        }

                        var sequence = ReadRequiredUInt64(root, "heartbeatSequence");
                        if (sequence == 0)
                        {
                            throw Failure("readiness_heartbeat_sequence_invalid");
                        }
                        if (sequence < 2)
                        {
                            return;
                        }

                        requestClone = _requestBytes.ToArray();
                        var snapshot = new ReadinessSnapshot(
                            requestClone,
                            readyBytes,
                            ReadinessDirectoryProtected: true,
                            RequestFileProtected: true,
                            ReadyFileProtected: true);
                        requestClone = null;
                        if (_completed.TrySetResult(snapshot))
                        {
                            readyBytes = null;
                        }
                        else
                        {
                            snapshot.ClearSensitiveBuffers();
                            readyBytes = null;
                        }
                    }
                    finally
                    {
                        if (requestClone is not null)
                        {
                            CryptographicOperations.ZeroMemory(requestClone);
                        }

                        if (readyBytes is not null)
                        {
                            CryptographicOperations.ZeroMemory(readyBytes);
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    ClearCachedRequest();
                }
                catch (DirectoryNotFoundException)
                {
                    ClearCachedRequest();
                }
                catch (IOException exception) when (IsAtomicReplaceRace(exception))
                {
                    // Atomic replace races with sharing/lock violations are retried on the next bounded tick.
                    return;
                }
                catch (HostedOnboardingFailure failure)
                {
                    _completed.TrySetException(failure);
                }
                catch (Exception exception)
                {
                    _completed.TrySetException(Failure(ClassifyException(exception)));
                }
            }
        }

        private byte[]? TryReadEvidenceFile(string path)
        {
            byte[]? bytes = null;
            var transferred = false;
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    4096, FileOptions.SequentialScan);
                if (stream.Length is <= 0 or > MaximumReadinessBytes)
                {
                    throw Failure("readiness_file_size_invalid");
                }
                bytes = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        return null;
                    }

                    offset += read;
                }

                transferred = true;
                return bytes;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException exception) when (IsAtomicReplaceRace(exception))
            {
                return null;
            }
            finally
            {
                if (!transferred && bytes is not null)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        private void ReplaceCachedRequest(byte[] newValue)
        {
            if (_requestBytes is not null) CryptographicOperations.ZeroMemory(_requestBytes);
            _requestBytes = newValue;
        }

        private void ClearCachedRequest()
        {
            if (_requestBytes is null) return;
            CryptographicOperations.ZeroMemory(_requestBytes);
            _requestBytes = null;
        }

        public void Dispose()
        {
            FileSystemWatcher? bootstrapWatcher;
            FileSystemWatcher? productWatcher;
            ReadinessSnapshot? completedSnapshot = null;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                ClearCachedRequest();
                if (_completed.Task.IsCompletedSuccessfully)
                {
                    completedSnapshot = _completed.Task.Result;
                }
                else if (_completed.Task.IsFaulted)
                {
                    _ = _completed.Task.Exception;
                }

                bootstrapWatcher = _bootstrapWatcher;
                productWatcher = _productWatcher;
                _bootstrapWatcher = null;
                _productWatcher = null;
            }

            completedSnapshot?.ClearSensitiveBuffers();
            bootstrapWatcher?.Dispose();
            productWatcher?.Dispose();
        }
    }
}
