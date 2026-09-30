using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Win32;
using System.ComponentModel;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Agents;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.RemoteSupport;
using NetRatel.Application.Artifacts;
using NetRatel.Client.Service.Updates;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Infrastructure.Artifacts;
using NetRatel.Infrastructure.Auth;
using NetRatel.Shared.Contracts.RemoteSupport;
using NetRatel.Tests.Akka;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class WindowsInstallerNativeTests
{
    private const int MaximumCapturedInstallerStreamCharacters = 128 * 1024;
    private const int MaximumCombinedInstallerDiagnosticCharacters = MaximumCapturedInstallerStreamCharacters * 2;
    private static readonly string[] SafePowerShellExceptionTypes =
    [
        "RuntimeException",
        "CommandNotFoundException",
        "ParameterBindingException",
        "ItemNotFoundException",
        "UnauthorizedAccessException",
        "IOException",
        "DirectoryNotFoundException",
        "FileNotFoundException",
        "InvalidDataException",
        "InvalidOperationException",
        "ArgumentException",
        "SecurityException",
        "CryptographicException",
        "Win32Exception",
        "WebException",
        "HttpRequestException",
        "ServiceCommandException",
        "ProcessCommandException",
        "NativeCommandError",
        "RemoteException"
    ];
    private const string UnownedServiceImageFailure =
        "The registered NetRatel.Client image is outside the configured NetRatel package layout; refusing to stop or rewrite it.";

    [Fact]
    public void InstallerFailureDiagnosticAcceptsPowerShellLineWrappingWithoutLeakingPathsOrSids()
    {
        const string privatePath = @"C:\untrusted\installer.ps1";
        const string privateSid = "S-1-5-21-111111111-222222222-333333333-1001";
        var output = string.Join("\r\n", new[]
        {
            "The service readiness path could not be securely verified",
            "(phase=state-preflight;",
            "role=default-update-state;",
            "scope=leaf;",
            "component=2;",
            "reason=replacement-access;",
            "normalization=not-attempted;",
            "exception=RuntimeException).",
            $"at {privatePath} for {privateSid}"
        });

        var diagnostic = GetSafeInstallerDiagnostic(output);

        Assert.Equal(
            "Installer preflight failure phase=state-preflight role=default-update-state scope=leaf component=2 reason=replacement-access normalization=not-attempted exception=RuntimeException",
            diagnostic);
        Assert.DoesNotContain(privatePath, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(privateSid, diagnostic, StringComparison.Ordinal);

        var permissionOutput = output.Replace(
            "exception=RuntimeException).",
            "exception=RuntimeException; aceRights=WriteData|Synchronize; aceRightsValue=1048578; aceInherited=true; aceInheritance=ContainerInherit|ObjectInherit; acePropagation=None).",
            StringComparison.Ordinal);
        var permissionDiagnostic = GetSafeInstallerDiagnostic(permissionOutput);
        Assert.Equal(
            "Installer preflight failure phase=state-preflight role=default-update-state scope=leaf component=2 reason=replacement-access normalization=not-attempted exception=RuntimeException aceRights=WriteData|Synchronize aceRightsValue=1048578 aceInherited=true aceInheritance=ContainerInherit|ObjectInherit acePropagation=None",
            permissionDiagnostic);
        Assert.DoesNotContain(privatePath, permissionDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(privateSid, permissionDiagnostic, StringComparison.Ordinal);
        var malformedPermissionOutput = permissionOutput.Replace(
            "aceRights=WriteData|Synchronize",
            "aceRights=WriteData|Synchronize; principal=" + privateSid,
            StringComparison.Ordinal);
        var malformedPermissionDiagnostic = GetSafeInstallerDiagnostic(malformedPermissionOutput);
        Assert.DoesNotContain(privateSid, malformedPermissionDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("principal=", malformedPermissionDiagnostic, StringComparison.Ordinal);

        const string privateEnrollment = "ENR-private-seeded-capability";
        const string privateSeededPath = @"C:\private\seeded repair.ps1";
        var seededStopOutput =
            "Installer stopped: phase=directory-preflight; lastCompleted=script-started; serviceState=not-installed; failureType=InvalidOperationException." +
            $"\r\nAt {privateSeededPath}:47 char:3 for {privateSid}; {privateEnrollment}";
        var seededStopDiagnostic = GetSafeSeededParentFailureEvidence(seededStopOutput);
        Assert.Equal(
            "installerStop phase=directory-preflight lastCompleted=script-started serviceState=not-installed exceptionType=InvalidOperationException handoffFailure=none-recognized",
            seededStopDiagnostic);
        Assert.True(!seededStopDiagnostic.Contains(privatePath, StringComparison.Ordinal) &&
            !seededStopDiagnostic.Contains(privateSeededPath, StringComparison.Ordinal) &&
            !seededStopDiagnostic.Contains(privateSid, StringComparison.Ordinal) &&
            !seededStopDiagnostic.Contains(privateEnrollment, StringComparison.Ordinal),
            "The bounded seeded diagnostic must redact paths, SIDs, and enrollment capabilities.");
        var classifiedHandoffDiagnostic = GetSafeSeededParentFailureEvidence(
            $"Installer handoff request is invalid, stale, or incomplete.\r\n{privateEnrollment} {privatePath} {privateSid}");
        Assert.Contains("handoffFailure=request-invalid", classifiedHandoffDiagnostic, StringComparison.Ordinal);
        Assert.True(!classifiedHandoffDiagnostic.Contains(privatePath, StringComparison.Ordinal) &&
            !classifiedHandoffDiagnostic.Contains(privateSid, StringComparison.Ordinal) &&
            !classifiedHandoffDiagnostic.Contains(privateEnrollment, StringComparison.Ordinal),
            "The handoff classification must redact paths, SIDs, and enrollment capabilities.");

        const string unownedServiceFailure = "The registered NetRatel.Client image is outside the configured NetRatel package layout; refusing to stop or rewrite it.";
        var wrappedUnownedServiceFailure = string.Join("\r\n", new[]
        {
            "The registered NetRatel.Client image is outside the configured NetRatel",
            "package layout; refusing to stop or rewrite it."
        });
        Assert.Equal(unownedServiceFailure, GetSafeInstallerDiagnostic(wrappedUnownedServiceFailure));
        AssertInstallerOutputContains(
            "outside the configured NetRatel package layout",
            wrappedUnownedServiceFailure,
            string.Empty,
            exitCode: 1);

        var tlsProbe = new NativeTlsProbePair(
            new NativeTlsProbeResult(NativeTlsProbeClass.TlsHandshakeFailure, 35, "none", true),
            new NativeTlsProbeResult(NativeTlsProbeClass.HttpResponse, 0, "404", false));
        Assert.Equal(
            "Download failed with exit code 35; default[class=TlsHandshakeFailure,curlExit=35,httpStatus=none,proxyUsed=True]; direct[class=HttpResponse,curlExit=0,httpStatus=404,proxyUsed=False]",
            GetSafeInstallerDiagnostic("Download failed with exit code 35.", tlsProbe));
        Assert.DoesNotContain(privatePath, GetSafeInstallerDiagnostic("Download failed with exit code 35.", tlsProbe), StringComparison.Ordinal);
        Assert.DoesNotContain(privateSid, GetSafeInstallerDiagnostic("Download failed with exit code 35.", tlsProbe), StringComparison.Ordinal);
        Assert.Equal(NativeTlsProbeClass.HttpResponse, ClassifyCurlResult(0, "404"));
        Assert.Equal(NativeTlsProbeClass.TlsHandshakeFailure, ClassifyCurlResult(35, "none"));
        Assert.Equal(NativeTlsProbeClass.TransportFailure, ClassifyCurlResult(0, "000"));
        Assert.Equal(NativeTlsProbeClass.TransportFailure, ClassifyCurlResult(60, "404"));
        Assert.Equal("none", ParseCurlHttpStatus("000"));
        Assert.Equal("404", ParseCurlHttpStatus("404"));

        const string privateMessage = "grant=ENR-private-capability password=private-password";
        const string privateInstallerPath = @"C:\private\task-root\install.ps1";
        var powerShellError = $"At {privateInstallerPath}:42 char:7\r\n+ throw '{privateMessage}'\r\nCategoryInfo : OperationStopped: (private source text:String) [], RuntimeException\r\nFullyQualifiedErrorId : RuntimeException";
        const string safePowerShellDiagnostic = "PowerShell failure type=RuntimeException line=42";
        Assert.Equal(safePowerShellDiagnostic, GetSafeInstallerDiagnostic(powerShellError));
        Assert.DoesNotContain(privateMessage, GetSafeInstallerDiagnostic(powerShellError), StringComparison.Ordinal);
        Assert.DoesNotContain(privateInstallerPath, GetSafeInstallerDiagnostic(powerShellError), StringComparison.Ordinal);

        var powerShell7ConciseError = $"At {privateInstallerPath}: line 57 char:1\r\n+ throw '{privateMessage}'\r\nRuntimeException: {privateMessage}";
        const string safePowerShell7Diagnostic = "PowerShell failure type=RuntimeException line=57";
        Assert.Equal(safePowerShell7Diagnostic, GetSafeInstallerDiagnostic(powerShell7ConciseError));
        Assert.DoesNotContain(privateMessage, GetSafeInstallerDiagnostic(powerShell7ConciseError), StringComparison.Ordinal);
        Assert.DoesNotContain(privateInstallerPath, GetSafeInstallerDiagnostic(powerShell7ConciseError), StringComparison.Ordinal);

        var oversizedMalformedOutput = new string('x', MaximumCombinedInstallerDiagnosticCharacters + 4096) +
            $"\r\nAt {privateInstallerPath}:43 char:7\r\nCategoryInfo : OperationStopped: (private source text:String) [], RuntimeException";
        Assert.Equal("no bounded installer preflight diagnostic was emitted", GetSafeInstallerDiagnostic(oversizedMalformedOutput));
    }

    [Theory]
    [InlineData(false, false)] // Flat archive using curl.exe.
    [InlineData(true, false)] // Wrapped archive using curl.exe.
    [InlineData(false, true)] // Windows PowerShell 5.1 fallback without curl.exe.
    [Trait("category", "hosted")]
    public async Task GeneratedPowerShellInstallerDownloadsVerifiesAndEnrollsNativePackage(
        bool includeBaseDirectory,
        bool forceWindowsPowerShellFallback)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The native installer test requires hosted Windows.");

        var packageDirectory = Environment.GetEnvironmentVariable("NETRATEL_NATIVE_CLIENT_DIRECTORY")
            ?? throw new InvalidOperationException("Set NETRATEL_NATIVE_CLIENT_DIRECTORY to the hosted Windows client package directory.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(packageDirectory, "netratel-client-manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        Assert.Equal("win-x64", manifest.RootElement.GetProperty("runtimeId").GetString());

        var credentialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel");
        Assert.False(Directory.Exists(credentialDirectory), "the disposable Windows runner must start without a NetRatel enrollment");
        var root = Path.Combine(Path.GetTempPath(), $"netratel-windows-installer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Process? installerProcess = null;
        try
        {
            var archivePath = Path.Combine(root, "client.zip");
            ZipFile.CreateFromDirectory(
                packageDirectory, archivePath, CompressionLevel.Optimal, includeBaseDirectory: includeBaseDirectory);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath, timeout.Token);
            var sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var apiBase = $"http://127.0.0.1:{port}";
            const string enrollmentCode = "ENR-SYNTHETIC-WINDOWS-INSTALLER";
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "win-x64", enrollmentCode, apiBase, DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: false, SilentInstall: true, version, sha256));
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, script, timeout.Token);

            var serving = ServePackageAndEnrollmentAsync(listener, archiveBytes, version, enrollmentCode, timeout.Token);
            var powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(forceWindowsPowerShellFallback ? powershellPath : "pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            var installRoot = Path.Combine(root, "client");
            start.Environment["NetRatel_ROOT"] = installRoot;
            start.Environment["NetRatel_STATE"] = Path.Combine(root, "state");
            start.Environment["NetRatel_LOG_DIR"] = Path.Combine(root, "logs");
            start.Environment["TEMP"] = root;
            if (forceWindowsPowerShellFallback)
            {
                Assert.True(File.Exists(powershellPath), "the Windows PowerShell 5.1 fallback requires the inbox PowerShell host");
                start.Environment["PATH"] = Path.GetDirectoryName(powershellPath)!;
            }
            installerProcess = Process.Start(start)!;
            var captured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
            var output = captured.StandardOutput;
            var error = captured.StandardError;
            await serving;
            Assert.True(installerProcess.ExitCode == 0,
                $"The Windows installer failed: exitCode={installerProcess.ExitCode}; {GetSafeInstallerDiagnostic(string.Concat(output, Environment.NewLine, error))}");
            AssertInstallerOutputHasSafeText(
                output, "NetRatel client installed and enrolled; no service readiness was requested.", "nonservice_installed");
            var installed = Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe");
            Assert.True(File.Exists(installed));
            Assert.True(File.Exists(Path.Combine(credentialDirectory, "agent.dat")) ||
                File.Exists(Path.Combine(root, "ProgramData", "NetRatel", "agent.dat")));
        }
        finally
        {
            if (installerProcess is { HasExited: false }) installerProcess.Kill(entireProcessTree: true);
            installerProcess?.Dispose();
            listener.Stop();
            DeleteCredentialFiles(credentialDirectory);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("category", "hosted")]
    [SupportedOSPlatform("windows")]
    public async Task GeneratedPowerShell51InstallerPreflightRepairsInheritedWritesOnDefaultProductDirectories()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The native Windows installer preflight test requires hosted Windows.");

        const string serviceName = "NetRatel.Client";
        const string enrollmentCode = "ENR-SYNTHETIC-PREFLIGHT-ONLY";
        AssertWindowsServiceAbsent(serviceName);

        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(string.IsNullOrWhiteSpace(commonApplicationData));
        Assert.False(string.IsNullOrWhiteSpace(programFiles));

        var programDataDirectory = new DirectoryInfo(commonApplicationData);
        var originalProgramDataSecurity = programDataDirectory.GetAccessControl();
        const AccessControlSections programDataSecuritySections =
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        var originalProgramDataSddl = originalProgramDataSecurity.GetSecurityDescriptorSddlForm(programDataSecuritySections);
        var productDataDirectory = Path.Combine(commonApplicationData, "NetRatel");
        var logsDirectory = Path.Combine(productDataDirectory, "logs");
        var updateDirectory = Path.Combine(productDataDirectory, "update");
        var productInstallDirectory = Path.Combine(programFiles, "NetRatel");
        var installRoot = Path.Combine(productInstallDirectory, "Client");

        Assert.False(Directory.Exists(productDataDirectory), "the disposable Windows runner must start without NetRatel identity or updater data");
        Assert.False(Directory.Exists(productInstallDirectory), "the disposable Windows runner must start without a default NetRatel package root");
        Assert.False(File.Exists(Path.Combine(productDataDirectory, "agent.dat")));
        Assert.False(File.Exists(Path.Combine(productDataDirectory, ".netratel-credential-machine-id")));

        var administratorSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var dataAcl = new DirectorySecurity();
        dataAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        dataAcl.SetOwner(administratorSid);
        var productRights = FileSystemRights.FullControl;
        var productInheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        dataAcl.AddAccessRule(new FileSystemAccessRule(
            administratorSid, productRights, productInheritance, PropagationFlags.None, AccessControlType.Allow));
        dataAcl.AddAccessRule(new FileSystemAccessRule(
            systemSid, productRights, productInheritance, PropagationFlags.None, AccessControlType.Allow));
        dataAcl.AddAccessRule(new FileSystemAccessRule(
            usersSid, FileSystemRights.WriteData, productInheritance, PropagationFlags.None, AccessControlType.Allow));

        var root = Path.Combine(Path.GetTempPath(), $"netratel-native-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var serverCancellation = new CancellationTokenSource();
        Process? installerProcess = null;
        Task? serving = null;
        var packageRequests = 0;
        var productDataCreated = false;
        var productInstallCreated = false;
        string? customFixtureRoot = null;
        var fixtureJunctions = new List<string>();
        try
        {
            new DirectoryInfo(productDataDirectory).Create(dataAcl);
            productDataCreated = true;
            Directory.CreateDirectory(logsDirectory);
            Directory.CreateDirectory(updateDirectory);
            SetDirectoryOwner(logsDirectory, administratorSid);
            SetDirectoryOwner(updateDirectory, administratorSid);
            AssertInheritedWriteOnlyDirectoryAcl(logsDirectory, usersSid);
            AssertInheritedWriteOnlyDirectoryAcl(updateDirectory, usersSid);

            var createdParentSecurity = new DirectoryInfo(productDataDirectory).GetAccessControl();
            var createdOwner = createdParentSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            Assert.Equal(administratorSid.Value, createdOwner?.Value);
            Assert.True(createdParentSecurity.AreAccessRulesProtected);

            listener.Start();
            var apiBase = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "win-x64", enrollmentCode, apiBase, DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: true, SilentInstall: true));
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, script, timeout.Token);

            productInstallCreated = true;
            serving = ServeInstallerAndPackageRejectionAsync(
                listener, script, enrollmentCode, () => Interlocked.Increment(ref packageRequests), serverCancellation.Token);
            var powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.True(File.Exists(powershellPath), "the native preflight regression requires Windows PowerShell 5.1");
            using var identity = WindowsIdentity.GetCurrent();
            var isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            Assert.True(isAdministrator, "the native installer preflight regression requires an elevated disposable Windows runner");
            var powershellReceipt = string.Empty;

            for (var run = 1; run <= 2; run++)
            {
                var start = new ProcessStartInfo(powershellPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-ExecutionPolicy");
                start.ArgumentList.Add("Bypass");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add($"(Invoke-WebRequest -UseBasicParsing -Uri '{apiBase}/install.ps1').Content | Invoke-Expression");
                start.Environment.Remove("NetRatel_ROOT");
                start.Environment.Remove("NetRatel_STATE");
                start.Environment.Remove("NetRatel_LOG_DIR");
                start.Environment["TEMP"] = root;
                installerProcess = Process.Start(start)
                    ?? throw new InvalidOperationException("Could not start Windows PowerShell 5.1 for the native ACL regression.");
                var captured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
                var output = captured.StandardOutput;
                var error = captured.StandardError;
                var combined = string.Concat(output, Environment.NewLine, error);
                var shellVersion = Regex.Match(output, @"(?m)^PowerShell version: (?<version>[^\r\n]+)").Groups["version"].Value;
                AssertInstallerOutputHasSafeText(output, "PowerShell edition: Desktop", "preflight_powershell_edition");
                AssertInstallerOutputHasSafeText(output, "PowerShell version: 5.", "preflight_powershell_version");
                Assert.DoesNotContain(enrollmentCode, combined, StringComparison.Ordinal);
                Assert.True(installerProcess.ExitCode != 0,
                    "the synthetic package endpoint must reject the download after preflight rather than install a package");
                Assert.True(Volatile.Read(ref packageRequests) == run,
                    $"the generated preflight run {run} did not reach the synthetic artifact request; diagnostic={GetSafeInstallerDiagnostic(combined)}");
                AssertInstallerOutputHasSafeText(output, "Downloading NetRatel Client package", $"preflight_reached_download_{run}");
                if (run == 1) powershellReceipt = shellVersion;
                installerProcess.Dispose();
                installerProcess = null;
            }

            var customFixturePath = CreateWindowsServiceFixtureRoot("custom-space");
            customFixtureRoot = customFixturePath;
            var customPathRoot = Path.Combine(customFixturePath, "custom paths with spaces");
            var customInstallRoot = Path.Combine(customPathRoot, "client root");
            var customStateDirectory = Path.Combine(customPathRoot, "state directory");
            var customLogsDirectory = Path.Combine(customPathRoot, "log directory");
            var customStart = new ProcessStartInfo(powershellPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            customStart.ArgumentList.Add("-NoProfile");
            customStart.ArgumentList.Add("-ExecutionPolicy");
            customStart.ArgumentList.Add("Bypass");
            customStart.ArgumentList.Add("-Command");
            customStart.ArgumentList.Add($"(Invoke-WebRequest -UseBasicParsing -Uri '{apiBase}/install.ps1').Content | Invoke-Expression");
            customStart.Environment["NetRatel_ROOT"] = customInstallRoot;
            customStart.Environment["NetRatel_STATE"] = customStateDirectory;
            customStart.Environment["NetRatel_LOG_DIR"] = customLogsDirectory;
            customStart.Environment["TEMP"] = root;
            installerProcess = Process.Start(customStart)
                ?? throw new InvalidOperationException("Could not start Windows PowerShell 5.1 for the custom-space path regression.");
            var customCaptured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
            var customOutput = customCaptured.StandardOutput;
            var customCombined = string.Concat(customOutput, Environment.NewLine, customCaptured.StandardError);
            Assert.DoesNotContain(enrollmentCode, customCombined, StringComparison.Ordinal);
            Assert.True(installerProcess.ExitCode != 0,
                "the custom-path synthetic package endpoint must reject the download after preflight");
            var customPackageRequestCount = Volatile.Read(ref packageRequests);
            if (customPackageRequestCount != 3)
            {
                WriteWindowsAclInventory("custom-path-common-data-ancestor", commonApplicationData);
                if (customFixtureRoot is not null && Directory.Exists(customFixtureRoot))
                    WriteWindowsAclInventory("custom-path-fixture-root", customFixtureRoot);
                if (Directory.Exists(customPathRoot))
                    WriteWindowsAclInventory("custom-path-spaces-ancestor", customPathRoot);
                if (Directory.Exists(customInstallRoot))
                    WriteWindowsAclInventory("custom-path-install-root", customInstallRoot);
                if (Directory.Exists(customStateDirectory))
                    WriteWindowsAclInventory("custom-path-state-root", customStateDirectory);
                if (Directory.Exists(customLogsDirectory))
                    WriteWindowsAclInventory("custom-path-log-root", customLogsDirectory);
            }
            Assert.True(customPackageRequestCount == 3,
                $"The custom-path preflight did not reach the synthetic artifact request; requests={customPackageRequestCount}; diagnostic={GetSafeInstallerDiagnostic(customCombined)}");
            AssertInstallerOutputHasSafeText(customOutput, "Downloading NetRatel Client package", "custom_space_preflight_reached_download");
            AssertInstallerOutputHasSafeText(customOutput, "PowerShell edition: Desktop", "custom_space_powershell_edition");
            AssertInstallerOutputHasSafeText(customOutput, "PowerShell version: 5.", "custom_space_powershell_version");
            installerProcess.Dispose();
            installerProcess = null;

            var seededScriptPath = Path.Combine(root, "seeded repair.ps1");
            await File.WriteAllTextAsync(seededScriptPath, GetSeededWindowsScript(), timeout.Token);
            var seededStart = new ProcessStartInfo(powershellPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            seededStart.ArgumentList.Add("-NoProfile");
            seededStart.ArgumentList.Add("-ExecutionPolicy");
            seededStart.ArgumentList.Add("Bypass");
            seededStart.ArgumentList.Add("-File");
            seededStart.ArgumentList.Add(seededScriptPath);
            seededStart.ArgumentList.Add("-ApiBase");
            seededStart.ArgumentList.Add(apiBase);
            seededStart.ArgumentList.Add("-TenantId");
            seededStart.ArgumentList.Add("4098");
            seededStart.ArgumentList.Add("-EnrollmentCode");
            seededStart.ArgumentList.Add("ENR-SYNTHETIC-HANDOFF-ONLY");
            seededStart.ArgumentList.Add("-Runtime");
            seededStart.ArgumentList.Add("win-x64");
            seededStart.ArgumentList.Add("-Version");
            seededStart.ArgumentList.Add("latest");
            seededStart.Environment.Remove("NetRatel_ROOT");
            seededStart.Environment.Remove("NetRatel_STATE");
            seededStart.Environment.Remove("NetRatel_LOG_DIR");
            seededStart.Environment.Remove("NetRatel_UPDATE_ROOT");
            seededStart.Environment.Remove("NetRatel_UPDATE_STATE");
            seededStart.Environment["TEMP"] = root;
            installerProcess = Process.Start(seededStart)
                ?? throw new InvalidOperationException("Could not start the saved API-seeded installer in Windows PowerShell 5.1.");
            var seededCaptured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
            var seededOutput = seededCaptured.StandardOutput;
            var seededCombined = string.Concat(seededOutput, Environment.NewLine, seededCaptured.StandardError);
            var seededFailureEvidence = GetSafeSeededParentFailureEvidence(seededCombined);
            Assert.True(installerProcess.ExitCode == 0,
                $"The saved API-seeded parent did not hand off successfully: {GetSafeInstallerDiagnostic(seededCombined)}; {seededFailureEvidence}");
            Assert.DoesNotContain("ENR-SYNTHETIC-HANDOFF-ONLY", seededCombined, StringComparison.Ordinal);
            AssertInstallerOutputHasSafeText(seededOutput, "handed off to independent installer process", "seeded_parent_handoff");
            installerProcess.Dispose();
            installerProcess = null;

            var seededHandoffDirectory = Path.Combine(updateDirectory, "install-handoffs");
            var seededResultPath = Assert.Single(Directory.EnumerateFiles(seededHandoffDirectory, "handoff-*.result.json"));
            string seededHandoffId;
            using (var handedOffResult = JsonDocument.Parse(await File.ReadAllTextAsync(seededResultPath, timeout.Token)))
            {
                seededHandoffId = handedOffResult.RootElement.GetProperty("handoffId").GetString()!;
                Assert.Matches("^[a-f0-9]{32}$", seededHandoffId);
                Assert.True(Guid.TryParseExact(seededHandoffId, "N", out var observedHandoffId));
                Assert.NotEqual(Guid.Empty, observedHandoffId);
            }
            var seededRequestPath = Path.Combine(seededHandoffDirectory, $"handoff-{seededHandoffId}.json");
            var seededChildScriptPath = Path.Combine(seededHandoffDirectory, $"handoff-{seededHandoffId}.ps1");
            JsonDocument? terminalResult = null;
            await WaitUntilAsync(() =>
            {
                if (Volatile.Read(ref packageRequests) < 4 || !File.Exists(seededResultPath)) return false;
                try
                {
                    var observed = JsonDocument.Parse(File.ReadAllText(seededResultPath));
                    if (observed.RootElement.GetProperty("state").GetString() == "failed")
                    {
                        terminalResult = observed;
                        return true;
                    }
                    observed.Dispose();
                    return false;
                }
                catch (Exception exception) when (exception is JsonException or IOException) { return false; }
            }, TimeSpan.FromSeconds(45), timeout.Token);
            var completedTerminalResult = terminalResult ?? throw new InvalidOperationException(
                "The detached child must reach the synthetic artifact rejection and publish a terminal failure result.");
            using (completedTerminalResult)
            {
                Assert.Equal("failed", completedTerminalResult.RootElement.GetProperty("state").GetString());
                Assert.Equal("installer_failed", completedTerminalResult.RootElement.GetProperty("failureCode").GetString());
            }
            Assert.Equal(4, Volatile.Read(ref packageRequests));
            await WaitUntilAsync(
                () => !File.Exists(seededRequestPath) && !File.Exists(seededChildScriptPath),
                TimeSpan.FromSeconds(15), timeout.Token);
            Assert.False(File.Exists(seededRequestPath),
                "the detached child must consume only its exact request file after acquiring the update lock");
            Assert.False(File.Exists(seededChildScriptPath),
                "the detached child must remove only its exact saved installer after acquiring the update lock");

            const AccessControlSections fixtureAclSections =
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            var ownerFixtureRoot = Path.Combine(customFixturePath, "untrusted owner fixture");
            Directory.CreateDirectory(ownerFixtureRoot);
            var untrustedOwnerInstallRoot = Path.Combine(ownerFixtureRoot, "client root");
            Directory.CreateDirectory(untrustedOwnerInstallRoot);
            try
            {
                SetDirectoryOwner(untrustedOwnerInstallRoot, usersSid);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"The native untrusted-owner fixture could not set the synthetic owner before installer execution (exception={exception.GetType().Name}).");
            }

            var untrustedOwnerAcl = new DirectoryInfo(untrustedOwnerInstallRoot).GetAccessControl();
            var observedOwner = untrustedOwnerAcl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            Assert.True(observedOwner is not null && usersSid.Equals(observedOwner),
                "The fixture leaf owner must be BUILTIN Users before installer preflight.");
            var untrustedOwnerAclBeforePreflight = untrustedOwnerAcl.GetSecurityDescriptorSddlForm(fixtureAclSections);
            var ownerDiagnostic = await AssertRejectedPathWithCustomStateAsync(
                "untrusted-owner", untrustedOwnerInstallRoot, ownerFixtureRoot, expectedReason: "untrusted-owner");
            var untrustedOwnerAclAfterPreflight = new DirectoryInfo(untrustedOwnerInstallRoot).GetAccessControl()
                .GetSecurityDescriptorSddlForm(fixtureAclSections);
            Assert.True(string.Equals(untrustedOwnerAclBeforePreflight, untrustedOwnerAclAfterPreflight, StringComparison.Ordinal),
                "Installer preflight must not rewrite the untrusted-owner fixture ACL.");
            Assert.Contains("scope=leaf", ownerDiagnostic, StringComparison.Ordinal);

            var leafReparseFixtureRoot = Path.Combine(customFixturePath, "leaf reparse fixture");
            Directory.CreateDirectory(leafReparseFixtureRoot);
            var leafReparseTarget = Path.Combine(leafReparseFixtureRoot, "leaf sentinel target");
            Directory.CreateDirectory(leafReparseTarget);
            var leafSentinelPath = Path.Combine(leafReparseTarget, "sentinel.txt");
            const string sentinelContents = "synthetic reparse target remains unchanged";
            await File.WriteAllTextAsync(leafSentinelPath, sentinelContents, timeout.Token);
            var leafReparseTargetAcl = new DirectoryInfo(leafReparseTarget).GetAccessControl()
                .GetSecurityDescriptorSddlForm(fixtureAclSections);
            var leafReparseInstallRoot = Path.Combine(leafReparseFixtureRoot, "install-root junction");
            fixtureJunctions.Add(leafReparseInstallRoot);
            CreateWindowsDirectoryJunction(leafReparseInstallRoot, leafReparseTarget);
            var leafReparseAttributes = File.GetAttributes(leafReparseInstallRoot);
            Assert.True((leafReparseAttributes & FileAttributes.ReparsePoint) != 0);
            var leafReparseDiagnostic = await AssertRejectedPathWithCustomStateAsync(
                "leaf-reparse", leafReparseInstallRoot, leafReparseFixtureRoot, expectedReason: "reparse-point");
            Assert.Contains("scope=leaf", leafReparseDiagnostic, StringComparison.Ordinal);
            Assert.True((File.GetAttributes(leafReparseInstallRoot) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(sentinelContents, await File.ReadAllTextAsync(leafSentinelPath, timeout.Token));
            var leafTargetAclAfterPreflight = new DirectoryInfo(leafReparseTarget).GetAccessControl()
                .GetSecurityDescriptorSddlForm(fixtureAclSections);
            Assert.True(string.Equals(leafReparseTargetAcl, leafTargetAclAfterPreflight, StringComparison.Ordinal),
                "Installer preflight must not rewrite the leaf junction target ACL.");

            var ancestorReparseFixtureRoot = Path.Combine(customFixturePath, "ancestor reparse fixture");
            Directory.CreateDirectory(ancestorReparseFixtureRoot);
            var ancestorReparseTarget = Path.Combine(ancestorReparseFixtureRoot, "ancestor sentinel target");
            Directory.CreateDirectory(ancestorReparseTarget);
            var ancestorSentinelPath = Path.Combine(ancestorReparseTarget, "sentinel.txt");
            await File.WriteAllTextAsync(ancestorSentinelPath, sentinelContents, timeout.Token);
            var ancestorReparseTargetAcl = new DirectoryInfo(ancestorReparseTarget).GetAccessControl()
                .GetSecurityDescriptorSddlForm(fixtureAclSections);
            var ancestorReparsePath = Path.Combine(ancestorReparseFixtureRoot, "install ancestor junction");
            fixtureJunctions.Add(ancestorReparsePath);
            CreateWindowsDirectoryJunction(ancestorReparsePath, ancestorReparseTarget);
            var ancestorReparseInstallRoot = Path.Combine(ancestorReparsePath, "client root");
            var ancestorReparseDiagnostic = await AssertRejectedPathWithCustomStateAsync(
                "ancestor-reparse", ancestorReparseInstallRoot, ancestorReparseFixtureRoot, expectedReason: "reparse-point");
            Assert.Contains("scope=ancestor", ancestorReparseDiagnostic, StringComparison.Ordinal);
            Assert.Contains("reason=reparse-point", ancestorReparseDiagnostic, StringComparison.Ordinal);
            Assert.True((File.GetAttributes(ancestorReparsePath) & FileAttributes.ReparsePoint) != 0);
            Assert.False(Directory.Exists(ancestorReparseInstallRoot),
                "The installer must not create a product directory beneath a reparse ancestor.");
            Assert.Equal(sentinelContents, await File.ReadAllTextAsync(ancestorSentinelPath, timeout.Token));
            var ancestorTargetAclAfterPreflight = new DirectoryInfo(ancestorReparseTarget).GetAccessControl()
                .GetSecurityDescriptorSddlForm(fixtureAclSections);
            Assert.True(string.Equals(ancestorReparseTargetAcl, ancestorTargetAclAfterPreflight, StringComparison.Ordinal),
                "Installer preflight must not rewrite the ancestor junction target ACL.");
            Console.WriteLine(
                $"Native installer rejected-path receipt: os={Environment.OSVersion.VersionString}; powershell={powershellReceipt}; " +
                "roles=install-root-untrusted-owner,install-root-leaf-reparse,install-root-ancestor-reparse; " +
                "artifactRequests=0; sentinelUnchanged=true; runAsAdministrator=true");

            Console.WriteLine(
                $"Native installer preflight receipt: os={Environment.OSVersion.VersionString}; " +
                $"powershell={powershellReceipt}; roles=install-root-default,logs-default,update-state-default; " +
                $"syntheticInheritedAce=BUILTIN\\Users:WriteData(0x{(int)FileSystemRights.WriteData:X}); runs=2; runAsAdministrator={isAdministrator}");
            Console.WriteLine(
                "Native installer custom-path receipt: roles=install-root-custom,logs-custom,update-state-custom; " +
                "customPathContainsSpaces=true; artifactRequests=1; runAsAdministrator=true");
            Console.WriteLine(
                "Native API-seeded child receipt: shell=WindowsPowerShell-5.1; roles=default-update-state,seed-handoff-files; " +
                "syntheticArtifactResponse=404; terminalState=installer_failed; handoffRequestAndScriptRemoved=true");
            AssertWindowsServiceAbsent(serviceName);
            Assert.Equal(4, Volatile.Read(ref packageRequests));

            AssertProtectedOwnedDirectoryAcl(logsDirectory, usersSid);
            AssertProtectedOwnedDirectoryAcl(updateDirectory, usersSid);
            var updateLockPath = Path.Combine(updateDirectory, "update.lock");
            Assert.True(File.Exists(updateLockPath), "the default updater state must retain its established lock leaf for an idempotent no-service retry");
            Assert.Equal(0, new FileInfo(updateLockPath).Length);
            Assert.True(Directory.Exists(Path.Combine(installRoot, "updater")));
            Assert.True(Directory.Exists(Path.Combine(installRoot, "versions")));
            Assert.True(Directory.Exists(Path.Combine(installRoot, "staging")));
            Assert.True(Directory.Exists(Path.Combine(installRoot, "failed")));
            AssertProtectedOwnedDirectoryAcl(customInstallRoot, usersSid);
            AssertProtectedOwnedDirectoryAcl(Path.Combine(customInstallRoot, "updater"), usersSid);
            AssertProtectedOwnedDirectoryAcl(Path.Combine(customInstallRoot, "versions"), usersSid);
            AssertProtectedOwnedDirectoryAcl(Path.Combine(customInstallRoot, "staging"), usersSid);
            AssertProtectedOwnedDirectoryAcl(Path.Combine(customInstallRoot, "failed"), usersSid);
            AssertProtectedOwnedDirectoryAcl(customStateDirectory, usersSid);
            AssertProtectedOwnedDirectoryAcl(customLogsDirectory, usersSid);
            Assert.Equal(0, new FileInfo(Path.Combine(customStateDirectory, "update.lock")).Length);

            async Task<string> AssertRejectedPathWithCustomStateAsync(
                string role,
                string installPath,
                string fixturePath,
                string expectedReason)
            {
                var statePath = Path.Combine(fixturePath, "state directory");
                var logPath = Path.Combine(fixturePath, "log directory");
                return await AssertRejectedCustomPathAsync(role, installPath, statePath, logPath, expectedReason);
            }

            async Task<string> AssertRejectedCustomPathAsync(
                string role,
                string installPath,
                string statePath,
                string logPath,
                string expectedReason)
            {
                var packageRequestsBeforePreflight = Volatile.Read(ref packageRequests);
                var start = new ProcessStartInfo(powershellPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-ExecutionPolicy");
                start.ArgumentList.Add("Bypass");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add($"(Invoke-WebRequest -UseBasicParsing -Uri '{apiBase}/install.ps1').Content | Invoke-Expression");
                start.Environment.Remove("NetRatel_UPDATE_ROOT");
                start.Environment.Remove("NetRatel_UPDATE_STATE");
                start.Environment["NetRatel_ROOT"] = installPath;
                start.Environment["NetRatel_STATE"] = statePath;
                start.Environment["NetRatel_LOG_DIR"] = logPath;
                start.Environment["TEMP"] = root;
                installerProcess = Process.Start(start)
                    ?? throw new InvalidOperationException("Could not start Windows PowerShell 5.1 for the native installer path rejection.");
                var captured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
                var combined = string.Concat(captured.StandardOutput, Environment.NewLine, captured.StandardError);
                var exitCode = installerProcess.ExitCode;
                installerProcess.Dispose();
                installerProcess = null;

                var diagnostic = GetSafeInstallerDiagnostic(combined);
                Assert.True(exitCode != 0,
                    $"The {role} path fixture was not rejected; diagnostic={diagnostic}");
                Assert.DoesNotContain(enrollmentCode, combined, StringComparison.Ordinal);
                Assert.Contains("PowerShell edition: Desktop", captured.StandardOutput, StringComparison.Ordinal);
                Assert.Contains("PowerShell version: 5.", captured.StandardOutput, StringComparison.Ordinal);
                Assert.Contains("role=install-root", diagnostic, StringComparison.Ordinal);
                Assert.Contains($"reason={expectedReason}", diagnostic, StringComparison.Ordinal);
                Assert.True(!diagnostic.Contains(usersSid.Value, StringComparison.Ordinal),
                    "The safe structured diagnostic must not expose the synthetic owner SID.");
                Assert.True(!diagnostic.Contains(installPath, StringComparison.OrdinalIgnoreCase),
                    "The safe structured diagnostic must not expose the fixture path.");
                var packageRequestsAfterPreflight = Volatile.Read(ref packageRequests);
                Assert.True(packageRequestsAfterPreflight == packageRequestsBeforePreflight,
                    $"The {role} path changed the artifact request count; before={packageRequestsBeforePreflight}; " +
                    $"after={packageRequestsAfterPreflight}; diagnostic={diagnostic}");
                AssertWindowsServiceAbsent(serviceName);
                return diagnostic;
            }
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Stop();
            if (installerProcess is { HasExited: false }) installerProcess.Kill(entireProcessTree: true);
            installerProcess?.Dispose();
            if (serving is not null)
            {
                try { await serving; }
                catch (Exception exception) when (serverCancellation.IsCancellationRequested &&
                    (exception is OperationCanceledException or SocketException))
                {
                    Debug.WriteLine($"Synthetic installer source listener stopped during cleanup: {exception.GetType().Name}.");
                }
            }
            foreach (var junctionPath in fixtureJunctions)
                DeleteWindowsDirectoryJunctionIfPresent(junctionPath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (customFixtureRoot is not null && Directory.Exists(customFixtureRoot))
                Directory.Delete(customFixtureRoot, recursive: true);
            if (productDataCreated)
            {
                if (Directory.Exists(updateDirectory)) Directory.Delete(updateDirectory, recursive: true);
                if (Directory.Exists(logsDirectory)) Directory.Delete(logsDirectory, recursive: true);
                if (Directory.Exists(productDataDirectory)) Directory.Delete(productDataDirectory, recursive: true);
            }
            if (productInstallCreated && Directory.Exists(productInstallDirectory)) Directory.Delete(productInstallDirectory, recursive: true);

            var currentProgramDataSddl = programDataDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(programDataSecuritySections);
            var programDataAclWasChanged = !string.Equals(currentProgramDataSddl, originalProgramDataSddl, StringComparison.Ordinal);
            if (programDataAclWasChanged)
                programDataDirectory.SetAccessControl(originalProgramDataSecurity);
            Assert.False(programDataAclWasChanged, "the generated installer changed the shared ProgramData ACL; the original ACL was restored");
            Assert.Equal(originalProgramDataSddl, programDataDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(programDataSecuritySections));
        }
    }

    [Fact]
    [Trait("category", "hosted")]
    [SupportedOSPlatform("windows")]
    public async Task GeneratedServiceInstallerRejectsAnUnownedServiceImageBeforeStoppingOrReplacingFiles()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The native Windows service test requires hosted Windows.");

        const string serviceName = "NetRatel.Client";
        AssertWindowsServiceAbsent(serviceName);
        var root = CreateWindowsServiceFixtureRoot("unowned-service");
        var unownedImage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
        var unownedImagePath = $"\"{unownedImage}\" --service";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var packageServerCancellation = new CancellationTokenSource();
        Process? installerProcess = null;
        Task<bool>? serving = null;
        try
        {
            var packageDirectory = Environment.GetEnvironmentVariable("NETRATEL_NATIVE_CLIENT_DIRECTORY")
                ?? throw new InvalidOperationException("Set NETRATEL_NATIVE_CLIENT_DIRECTORY to the hosted Windows client package directory.");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(packageDirectory, "netratel-client-manifest.json"), timeout.Token));
            var version = manifest.RootElement.GetProperty("version").GetString()!;
            var archivePath = Path.Combine(root, "client.zip");
            ZipFile.CreateFromDirectory(packageDirectory, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath, timeout.Token);
            var sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();

            CreateWindowsService(serviceName, unownedImagePath);
            var beforeImage = ReadWindowsServiceImagePath(serviceName);
            using (var service = new ServiceController(serviceName))
                Assert.Equal(ServiceControllerStatus.Stopped, service.Status);

            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            const string enrollmentCode = "ENR-SYNTHETIC-UNOWNED-SERVICE";
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "win-x64", enrollmentCode, $"http://127.0.0.1:{port}", DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: true, SilentInstall: true, version, sha256));
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, script, timeout.Token);
            var servingTask = ServePackageOnlyAsync(listener, archiveBytes, version, enrollmentCode, packageServerCancellation.Token);
            serving = servingTask;

            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            var installRoot = Path.Combine(root, "client");
            var installedVersion = Path.Combine(installRoot, "versions", version);
            Directory.CreateDirectory(installedVersion);
            var sentinel = Path.Combine(installedVersion, "existing-install.txt");
            await File.WriteAllTextAsync(sentinel, "existing package remains untouched", timeout.Token);
            start.Environment["NetRatel_ROOT"] = installRoot;
            start.Environment["NetRatel_STATE"] = Path.Combine(root, "state");
            start.Environment["NetRatel_LOG_DIR"] = Path.Combine(root, "logs");
            start.Environment["TEMP"] = root;

            installerProcess = Process.Start(start)!;
            var captured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
            packageServerCancellation.Cancel();
            var packageWasRequested = await servingTask;
            serving = null;
            var output = captured.StandardOutput;
            var error = captured.StandardError;
            Assert.NotEqual(0, installerProcess.ExitCode);
            Assert.False(packageWasRequested, "The installer must reject the unowned service image before requesting the candidate package.");
            AssertInstallerOutputContains(
                "outside the configured NetRatel package layout", output, error, installerProcess.ExitCode);
            Assert.True(File.Exists(sentinel));
            Assert.Equal("existing package remains untouched", await File.ReadAllTextAsync(sentinel, timeout.Token));
            Assert.Equal(beforeImage, ReadWindowsServiceImagePath(serviceName));
            using var unchangedService = new ServiceController(serviceName);
            Assert.Equal(ServiceControllerStatus.Stopped, unchangedService.Status);
        }
        finally
        {
            packageServerCancellation.Cancel();
            listener.Stop();
            try
            {
                if (installerProcess is not null)
                {
                    try
                    {
                        if (!installerProcess.HasExited) installerProcess.Kill(entireProcessTree: true);
                        await installerProcess.WaitForExitAsync(CancellationToken.None);
                    }
                    finally { installerProcess.Dispose(); }
                }
                if (serving is not null) await serving;
            }
            finally
            {
                var serviceRemoved = RemoveWindowsServiceIfRegisteredImagePathEquals(serviceName, unownedImagePath);
                Assert.True(serviceRemoved, "The unowned service registration changed; retaining its files for inspection.");
                if (serviceRemoved && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    [Fact]
    [Trait("category", "hosted")]
    [SupportedOSPlatform("windows")]
    public async Task GeneratedServiceInstallerRetainsIdentityWhenAgentIsDisabledAndNeverReportsReady()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The native Windows service test requires hosted Windows.");

        var packageDirectory = Environment.GetEnvironmentVariable("NETRATEL_NATIVE_CLIENT_DIRECTORY")
            ?? throw new InvalidOperationException("Set NETRATEL_NATIVE_CLIENT_DIRECTORY to the hosted Windows client package directory.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(packageDirectory, "netratel-client-manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        Assert.Equal("win-x64", manifest.RootElement.GetProperty("runtimeId").GetString());

        const string serviceName = "NetRatel.Client";
        var credentialDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel");
        var credentialPath = Path.Combine(credentialDirectory, "agent.dat");
        AssertWindowsServiceAbsent(serviceName);
        Assert.False(Directory.Exists(credentialDirectory), "the disposable Windows runner must start without NetRatel credential files");

        var root = CreateWindowsServiceFixtureRoot("disabled-service");
        var installRoot = Path.Combine(root, "client");
        using var certificate = CreateNativeGatewayCertificate();
        using var trustedCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        using var fixture = new NativeGatewayHostFixture(4098, Guid.NewGuid(), "ENR-SYNTHETIC-WINDOWS-SERVICE", agentDisabled: true);
        WebApplication? app = null;
        var trusted = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Process? installerProcess = null;
        try
        {
            AddLocalMachineTrust(trustedCertificate);
            trusted = true;
            app = BuildNativeGatewayHost(certificate, fixture);
            await app.StartAsync(timeout.Token);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            Assert.NotNull(addresses);
            var apiBase = Assert.Single(addresses!, address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            var tlsProbe = await ProbeLoopbackHttpsAsync(apiBase, timeout.Token);

            var stagedPackage = Path.Combine(root, "package");
            CopyDirectory(packageDirectory, stagedPackage);
            // This file represents a pre-#118 installation. The service Environment value
            // must remain authoritative after install and after every service restart.
            await File.WriteAllTextAsync(Path.Combine(stagedPackage, "clientsettings.json"), """
                {
                  "apiBaseUrl": "http://stale.invalid"
                }
                """, timeout.Token);

            var archivePath = Path.Combine(root, "client.zip");
            ZipFile.CreateFromDirectory(stagedPackage, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath, timeout.Token);
            var sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            fixture.SetArtifact(archiveBytes, version, sha256);
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "win-x64", fixture.EnrollmentCode, apiBase, DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: true, SilentInstall: true, version, sha256,
                ReadinessTimeoutSeconds: 10));
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, script, timeout.Token);

            var powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershellPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            var logDirectory = Path.Combine(root, "logs");
            start.Environment["NetRatel_ROOT"] = installRoot;
            start.Environment["NetRatel_STATE"] = Path.Combine(root, "state");
            start.Environment["NetRatel_LOG_DIR"] = logDirectory;
            start.Environment["TEMP"] = root;
            installerProcess = Process.Start(start)!;
            var captured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
            var output = captured.StandardOutput;
            var error = captured.StandardError;

            Assert.NotEqual(0, installerProcess.ExitCode);
            AssertInstallerOutputHasSafeText(output, "PowerShell edition: Desktop", "powershell_edition");
            AssertInstallerOutputHasSafeText(output, "PowerShell version: 5.", "powershell_version");
            AssertInstallerOutputContains("did not reach gateway heartbeat readiness", output, error, installerProcess.ExitCode, tlsProbe);
            AssertInstallerOutputLacksSafeText(output, "authenticated gateway heartbeat readiness was verified", "unexpected_ready_message");
            Assert.True(File.Exists(credentialPath), "a service that enrolled but failed gateway admission retains its identity for repair");
            Assert.Equal(1, fixture.EnrollmentRequests);
            Assert.True(fixture.TokenRequests >= 1);

            var installed = Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe");
            Assert.True(File.Exists(installed));
            Assert.True(new FileInfo(credentialPath).Length > 0);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(installed)!, "netratel.enroll.json")));

            using (var service = new ServiceController(serviceName))
            {
                Assert.Equal(ServiceControllerStatus.Running, service.Status);
            }

            var serviceEnvironment = ReadWindowsServiceEnvironment(serviceName);
            Assert.Contains($"NetRatelCLIENT__Client__ApiBaseUrl={apiBase}", serviceEnvironment);

            var tokensBeforeRestart = fixture.TokenRequests;
            Assert.True(ServiceExecutableMatches(
                ReadWindowsServiceImagePath(serviceName), Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe")));
            Assert.Equal(2, ReadWindowsServiceStartType(serviceName));
            StopWindowsService(serviceName);
            StartWindowsService(serviceName);
            await WaitUntilAsync(
                () => fixture.TokenRequests > tokensBeforeRestart,
                TimeSpan.FromSeconds(45),
                timeout.Token);

            using (var restarted = new ServiceController(serviceName))
            {
                Assert.Equal(ServiceControllerStatus.Running, restarted.Status);
            }

            Assert.True(File.Exists(credentialPath), "the LocalSystem credential must survive a service restart");
            Assert.Contains($"NetRatelCLIENT__Client__ApiBaseUrl={apiBase}", ReadWindowsServiceEnvironment(serviceName));
            Assert.Equal(1, fixture.EnrollmentRequests);
            Assert.Equal(1, fixture.DownloadRequests);
            Assert.All(fixture.TokenAgentIds, value => Assert.Equal(fixture.AgentId.ToString("D"), value));
            Assert.All(fixture.RefreshTokens, value => Assert.Equal(NativeGatewayHostFixture.RefreshToken, value));
            Assert.True(fixture.TokenRequests >= 2);

            Assert.True(ServiceExecutableMatches(
                ReadWindowsServiceImagePath(serviceName), Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe")));
            StopWindowsService(serviceName);
            var logs = Directory.Exists(logDirectory)
                ? string.Join(Environment.NewLine, Directory.EnumerateFiles(logDirectory, "*.log").Select(File.ReadAllText))
                : string.Empty;
            Assert.Contains($"API={apiBase}", logs);
            Assert.Contains("Agent disabled by administrator", logs);
        }
        finally
        {
            timeout.Cancel();
            if (installerProcess is { HasExited: false }) installerProcess.Kill(entireProcessTree: true);
            installerProcess?.Dispose();
            var serviceRemoved = false;
            try
            {
                serviceRemoved = RemoveWindowsServiceIfRegisteredImagePathEquals(
                    serviceName, GetExpectedServiceImagePath(Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe")));
            }
            finally
            {
                try
                {
                    if (app is not null)
                    {
                        try { await app.StopAsync(CancellationToken.None); }
                        finally { await app.DisposeAsync(); }
                    }
                }
                finally
                {
                    try
                    {
                        if (trusted) RemoveLocalMachineTrust(trustedCertificate);
                    }
                    finally
                    {
                        if (serviceRemoved)
                        {
                            DeleteCredentialFiles(credentialDirectory);
                            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                        }
                    }
                }
            }
            Assert.True(serviceRemoved, "The service registration changed; retaining its package and credentials for inspection.");
        }
    }

    [Fact]
    [Trait("category", "hosted")]
    [SupportedOSPlatform("windows")]
    public async Task GeneratedServiceInstallerRequiresLocalSystemGatewayAdmissionAndAcknowledgedHeartbeats()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The native Windows gateway service test requires hosted Windows.");

        var packageDirectory = Environment.GetEnvironmentVariable("NETRATEL_NATIVE_CLIENT_DIRECTORY")
            ?? throw new InvalidOperationException("Set NETRATEL_NATIVE_CLIENT_DIRECTORY to the hosted Windows client package directory.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(packageDirectory, "netratel-client-manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        Assert.Equal("win-x64", manifest.RootElement.GetProperty("runtimeId").GetString());

        const string serviceName = "NetRatel.Client";
        const int tenantId = 4098;
        var agentId = Guid.NewGuid();
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(string.IsNullOrWhiteSpace(commonApplicationData));
        Assert.False(string.IsNullOrWhiteSpace(programFiles));
        var credentialDirectory = Path.Combine(commonApplicationData, "NetRatel");
        var credentialPath = Path.Combine(credentialDirectory, "agent.dat");
        var stateDirectory = Path.Combine(credentialDirectory, "update");
        var logDirectory = Path.Combine(credentialDirectory, "logs");
        var productInstallDirectory = Path.Combine(programFiles, "NetRatel");
        var installRoot = Path.Combine(productInstallDirectory, "Client");
        var programDataDirectory = new DirectoryInfo(commonApplicationData);
        var programFilesDirectory = new DirectoryInfo(programFiles);
        const AccessControlSections parentSecuritySections =
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        var originalProgramDataSecurity = programDataDirectory.GetAccessControl();
        var originalProgramDataSddl = originalProgramDataSecurity.GetSecurityDescriptorSddlForm(parentSecuritySections);
        var originalProgramFilesSecurity = programFilesDirectory.GetAccessControl();
        var originalProgramFilesSddl = originalProgramFilesSecurity.GetSecurityDescriptorSddlForm(parentSecuritySections);
        AssertWindowsServiceAbsent(serviceName);
        Assert.False(Directory.Exists(credentialDirectory), "the disposable Windows runner must start without NetRatel credential files");
        Assert.False(Directory.Exists(stateDirectory), "the disposable Windows runner must start without NetRatel updater state");
        Assert.False(Directory.Exists(logDirectory), "the disposable Windows runner must start without the default NetRatel log directory");
        Assert.False(Directory.Exists(productInstallDirectory), "the disposable Windows runner must start without the default NetRatel installation root");

        var root = CreateWindowsServiceFixtureRoot("real-gateway");
        using var certificate = CreateNativeGatewayCertificate();
        using var trustedCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        WebApplication? app = null;
        Process? installerProcess = null;
        var trusted = false;
        DirectorySecurity? originalStateParentAcl = null;
        var legacyStateParentAclInjected = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var fixture = new NativeGatewayHostFixture(tenantId, agentId, "ENR-SYNTHETIC-SYSTEM-GATEWAY");
        fixture.AgentStore.FirstAdmissionObserver = () =>
        {
            AssertProtectedServiceReadinessRequest(stateDirectory);
            AssertSystemServiceReadinessResponse(stateDirectory);
        };
        Process? repairInstallerProcess = null;
        Process? unsafeRepairInstallerProcess = null;
        try
        {
            AddLocalMachineTrust(trustedCertificate);
            trusted = true;
            app = BuildNativeGatewayHost(certificate, fixture);
            await app.StartAsync(timeout.Token);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            Assert.NotNull(addresses);
            var apiBase = Assert.Single(addresses!, address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            var tlsProbe = await ProbeLoopbackHttpsAsync(apiBase, timeout.Token);

            var archivePath = Path.Combine(root, "client.zip");
            ZipFile.CreateFromDirectory(packageDirectory, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath, timeout.Token);
            var sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            fixture.SetArtifact(archiveBytes, version, sha256);

            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                tenantId, "win-x64", fixture.EnrollmentCode, apiBase, DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: true, SilentInstall: true, version, sha256,
                ReadinessTimeoutSeconds: 90));
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, script, timeout.Token);

            var powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershellPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.Environment.Remove("NetRatel_ROOT");
            start.Environment.Remove("NetRatel_STATE");
            start.Environment.Remove("NetRatel_LOG_DIR");
            start.Environment["TEMP"] = root;
            installerProcess = Process.Start(start)!;
            var captured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
            var output = captured.StandardOutput;
            var error = captured.StandardError;

            Assert.True(installerProcess.ExitCode == 0,
                $"The real HTTPS gateway installer failed; installerExit={installerProcess.ExitCode}; {GetSafeInstallerDiagnostic(string.Concat(output, Environment.NewLine, error), tlsProbe)}");
            AssertInstallerOutputHasSafeText(output, "Gateway heartbeat ready:", "gateway_ready_message");
            AssertInstallerOutputHasSafeText(output, $"agentId={agentId:D}", "agent_id_message");
            AssertInstallerOutputHasSafeText(output, $"tenantId={tenantId}", "tenant_id_message");
            Assert.Equal(1, fixture.AgentStore.FirstAdmissionObservations);
            Assert.Empty(fixture.AgentStore.FirstAdmissionObservationFailures);
            Assert.Equal(2, ReadWindowsServiceStartType(serviceName));
            Assert.True(File.Exists(credentialPath), "the LocalSystem service must retain its enrolled credentials");
            Assert.Equal(1, fixture.EnrollmentRequests);
            Assert.True(fixture.TokenRequests >= 1);
            Assert.True(fixture.AgentStore.AdmissionCalls >= 1,
                "AgentGatewayService must reach the active-agent store after JWT signature validation.");
            var serviceEnvironment = ReadWindowsServiceEnvironment(serviceName);
            var updateRequestPath = Path.Combine(stateDirectory, "request.json");
            var updateReadyPath = Path.Combine(stateDirectory, "ready.json");
            Assert.Contains($"NetRatel_UPDATE_ROOT={installRoot}", serviceEnvironment);
            Assert.Contains($"NetRatel_UPDATE_STATE={stateDirectory}", serviceEnvironment);
            Assert.Contains($"NetRatel_UPDATE_REQUEST={updateRequestPath}", serviceEnvironment);
            Assert.Contains($"NetRatelCLIENT__Client__AutoUpdate__StateDirectory={stateDirectory}", serviceEnvironment);
            Assert.Contains($"NetRatelCLIENT__Client__AutoUpdate__RequestPath={updateRequestPath}", serviceEnvironment);
            Assert.Contains($"NetRatelCLIENT__Client__AutoUpdate__ReadyPath={updateReadyPath}", serviceEnvironment);

            var presence = app.Services.GetRequiredService<IClientPresenceRouter>();
            var clientKey = new ClientKey(tenantId, agentId);
            var firstSnapshot = await WaitForPresenceAsync(presence, clientKey, minimumEpoch: 1, timeout.Token);
            var firstServiceProcessId = ReadWindowsServiceProcessId(serviceName);
            Assert.Equal(ClientPresenceStatus.Online, firstSnapshot.Status);
            Assert.True(firstSnapshot.IsAuthoritative);
            Assert.Equal("akka", firstSnapshot.Source);
            Assert.True(firstSnapshot.ConnectionEpoch.HasValue && firstSnapshot.ConnectionEpoch.Value >= 1);
            Assert.True(firstSnapshot.ConnectionId.HasValue && firstSnapshot.ConnectionId.Value != Guid.Empty);
            Assert.True(firstSnapshot.LastAcceptedSequence >= 2,
                "the presence actor must observe two heartbeat frames acknowledged by AgentGatewayService.");

            var admissionCallsBeforeInvalidTokens = fixture.AgentStore.AdmissionCalls;
            await AssertGatewayTokenRejectedAsync(apiBase, fixture.CreateAccessToken(includeRole: false), tenantId, agentId,
                StatusCode.PermissionDenied, timeout.Token);
            using (var unrelatedSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                await AssertGatewayTokenRejectedAsync(apiBase,
                    fixture.CreateAccessToken(signingKey: unrelatedSigningKey), tenantId, agentId,
                    StatusCode.Unauthenticated, timeout.Token);
            }
            Assert.Equal(admissionCallsBeforeInvalidTokens, fixture.AgentStore.AdmissionCalls);

            var tokensBeforeRestart = fixture.TokenRequests;
            Assert.True(ServiceExecutableMatches(
                ReadWindowsServiceImagePath(serviceName), Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe")));
            StopWindowsService(serviceName);
            StartWindowsService(serviceName);
            await WaitUntilAsync(() => fixture.TokenRequests > tokensBeforeRestart,
                TimeSpan.FromSeconds(45), timeout.Token);
            var restartedSnapshot = await WaitForPresenceAsync(
                presence, clientKey, checked(firstSnapshot.ConnectionEpoch!.Value + 1), timeout.Token);
            var restartedServiceProcessId = ReadWindowsServiceProcessId(serviceName);
            Assert.Equal(ClientPresenceStatus.Online, restartedSnapshot.Status);
            Assert.True(restartedSnapshot.LastAcceptedSequence >= 2);
            Assert.NotEqual(firstSnapshot.ConnectionId, restartedSnapshot.ConnectionId);
            Assert.NotEqual(firstServiceProcessId, restartedServiceProcessId);
            Assert.True(fixture.EnrollmentRequests == 1, "a service restart must reuse the installed Agent identity");
            Assert.All(fixture.TokenAgentIds, value => Assert.Equal(agentId.ToString("D"), value));
            Assert.All(fixture.RefreshTokens, value => Assert.Equal(NativeGatewayHostFixture.RefreshToken, value));

            var stateDirectoryInfo = new DirectoryInfo(stateDirectory);
            var stateAclBeforeUnsafeRepair = stateDirectoryInfo.GetAccessControl();
            var stateRulesBeforeUnsafeRepair = stateAclBeforeUnsafeRepair
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            var stateRuleFingerprintsBeforeUnsafeRepair = stateRulesBeforeUnsafeRepair
                .Select(GetWindowsAccessRuleFingerprint)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var usersSid = new System.Security.Principal.SecurityIdentifier("S-1-5-32-545");
            Assert.DoesNotContain(stateRulesBeforeUnsafeRepair, rule =>
                rule.IdentityReference.Value == usersSid.Value && rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.DeleteSubdirectoriesAndFiles) != 0);
            var unsafeStateAcl = new DirectoryInfo(stateDirectory).GetAccessControl();
            var injectedUnsafeStateRule = new FileSystemAccessRule(
                usersSid,
                FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow);
            unsafeStateAcl.AddAccessRule(injectedUnsafeStateRule);
            stateDirectoryInfo.SetAccessControl(unsafeStateAcl);
            try
            {
                var serviceProcessIdBeforeUnsafeRepair = ReadWindowsServiceProcessId(serviceName);
                var unsafeRepairStart = new ProcessStartInfo(powershellPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                unsafeRepairStart.ArgumentList.Add("-NoProfile");
                unsafeRepairStart.ArgumentList.Add("-ExecutionPolicy");
                unsafeRepairStart.ArgumentList.Add("Bypass");
                unsafeRepairStart.ArgumentList.Add("-File");
                unsafeRepairStart.ArgumentList.Add(scriptPath);
                unsafeRepairStart.Environment["NetRatel_ROOT"] = installRoot;
                unsafeRepairStart.Environment["NetRatel_LOG_DIR"] = logDirectory;
                unsafeRepairStart.Environment["TEMP"] = root;
                var tokensBeforeUnsafeRepair = fixture.TokenRequests;
                unsafeRepairInstallerProcess = Process.Start(unsafeRepairStart)
                    ?? throw new InvalidOperationException("Could not start the unsafe-state repair check.");
                var unsafeCaptured = await RunInstallerAndCaptureOutputAsync(unsafeRepairInstallerProcess, timeout.Token);
                var unsafeOutput = unsafeCaptured.StandardOutput;
                var unsafeError = unsafeCaptured.StandardError;
                Assert.NotEqual(0, unsafeRepairInstallerProcess.ExitCode);
                AssertInstallerOutputContains(
                    "service readiness path", unsafeOutput, unsafeError, unsafeRepairInstallerProcess.ExitCode);
                Assert.Equal(serviceProcessIdBeforeUnsafeRepair, ReadWindowsServiceProcessId(serviceName));
                Assert.Equal(tokensBeforeUnsafeRepair, fixture.TokenRequests);
                Assert.Equal(1, fixture.DownloadRequests);
                unsafeRepairInstallerProcess.Dispose();
                unsafeRepairInstallerProcess = null;
            }
            finally
            {
                var aclAfterUnsafeRepair = stateDirectoryInfo.GetAccessControl();
                var rulesAfterUnsafeRepair = aclAfterUnsafeRepair
                    .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(System.Security.Principal.SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .ToArray();
                var injectedUnsafeRule = Assert.Single(rulesAfterUnsafeRepair, rule =>
                    rule.IdentityReference.Value == usersSid.Value && rule.AccessControlType == AccessControlType.Allow &&
                    !rule.IsInherited &&
                    (rule.FileSystemRights & ~FileSystemRights.Synchronize) == FileSystemRights.DeleteSubdirectoriesAndFiles &&
                    rule.InheritanceFlags == InheritanceFlags.None && rule.PropagationFlags == PropagationFlags.None);
                aclAfterUnsafeRepair.RemoveAccessRuleSpecific(injectedUnsafeRule);
                stateDirectoryInfo.SetAccessControl(aclAfterUnsafeRepair);
                var rulesAfterRemoval = stateDirectoryInfo.GetAccessControl()
                    .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .ToArray();
                Assert.DoesNotContain(rulesAfterRemoval, rule =>
                    rule.IdentityReference.Value == usersSid.Value && rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & FileSystemRights.DeleteSubdirectoriesAndFiles) != 0);
                Assert.Equal(
                    stateRuleFingerprintsBeforeUnsafeRepair,
                    rulesAfterRemoval.Select(GetWindowsAccessRuleFingerprint).Order(StringComparer.Ordinal).ToArray());
            }

            originalStateParentAcl = new DirectoryInfo(credentialDirectory).GetAccessControl();
            var legacyStateParentAcl = new DirectoryInfo(credentialDirectory).GetAccessControl();
            legacyStateParentAcl.AddAccessRule(new FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier("S-1-5-32-545"),
                FileSystemRights.WriteData,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(credentialDirectory).SetAccessControl(legacyStateParentAcl);
            legacyStateParentAclInjected = true;
            var legacyStateAcl = new DirectoryInfo(stateDirectory).GetAccessControl();
            legacyStateAcl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new DirectoryInfo(stateDirectory).SetAccessControl(legacyStateAcl);
            Assert.Contains(
                new DirectoryInfo(stateDirectory).GetAccessControl()
                    .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
                    .Cast<FileSystemAccessRule>(),
                rule => rule.IsInherited && rule.IdentityReference.Value == "S-1-5-32-545" &&
                        (rule.FileSystemRights & FileSystemRights.WriteData) != 0);
            WriteWindowsAclInventory("credential-root-before-inherited-state-repair", credentialDirectory);
            WriteWindowsAclInventory("update-state-before-inherited-state-repair", stateDirectory);

            var repairStart = new ProcessStartInfo(powershellPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            repairStart.ArgumentList.Add("-NoProfile");
            repairStart.ArgumentList.Add("-ExecutionPolicy");
            repairStart.ArgumentList.Add("Bypass");
            repairStart.ArgumentList.Add("-File");
            repairStart.ArgumentList.Add(scriptPath);
            repairStart.Environment.Remove("NetRatel_ROOT");
            repairStart.Environment.Remove("NetRatel_STATE");
            repairStart.Environment.Remove("NetRatel_LOG_DIR");
            repairStart.Environment["TEMP"] = root;
            var tokensBeforeRepair = fixture.TokenRequests;
            repairInstallerProcess = Process.Start(repairStart)
                ?? throw new InvalidOperationException("Could not start the same-version repair installer.");
            var repairCaptured = await RunInstallerAndCaptureOutputAsync(repairInstallerProcess, timeout.Token);
            var repairOutput = repairCaptured.StandardOutput;
            var repairError = repairCaptured.StandardError;
            Assert.True(repairInstallerProcess.ExitCode == 0,
                $"The same-version service repair failed: exitCode={repairInstallerProcess.ExitCode}; {GetSafeInstallerDiagnostic(string.Concat(repairOutput, Environment.NewLine, repairError))}");
            AssertInstallerOutputHasSafeText(repairOutput, "Gateway heartbeat ready:", "repair_ready_message");
            AssertInstallerOutputHasSafeText(repairOutput, $"agentId={agentId:D}", "repair_agent_id_message");
            AssertInstallerOutputHasSafeText(repairOutput, $"tenantId={tenantId}", "repair_tenant_id_message");
            repairInstallerProcess.Dispose();
            repairInstallerProcess = null;

            var repairedServiceProcessId = ReadWindowsServiceProcessId(serviceName);
            var repairedSnapshot = await WaitForPresenceAsync(
                presence, clientKey, checked(restartedSnapshot.ConnectionEpoch!.Value + 1), timeout.Token);
            Assert.Equal(ClientPresenceStatus.Online, repairedSnapshot.Status);
            Assert.True(repairedSnapshot.LastAcceptedSequence >= 2);
            Assert.NotEqual(restartedSnapshot.ConnectionId, repairedSnapshot.ConnectionId);
            Assert.NotEqual(restartedServiceProcessId, repairedServiceProcessId);
            Assert.Equal(clientKey, repairedSnapshot.Client);
            Assert.Equal(1, fixture.EnrollmentRequests);
            Assert.Equal(2, fixture.DownloadRequests);
            Assert.True(fixture.TokenRequests > tokensBeforeRepair);
            Assert.All(fixture.TokenAgentIds, value => Assert.Equal(agentId.ToString("D"), value));
            var repairedStateAcl = new DirectoryInfo(stateDirectory).GetAccessControl();
            Assert.True(repairedStateAcl.AreAccessRulesProtected,
                "the owned legacy updater state directory must stop inheriting replacement rights before reuse");
            Assert.DoesNotContain(
                repairedStateAcl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
                    .Cast<FileSystemAccessRule>(),
                rule => rule.IdentityReference.Value == "S-1-5-32-545" &&
                        (rule.FileSystemRights & FileSystemRights.WriteData) != 0);
            new DirectoryInfo(credentialDirectory).SetAccessControl(originalStateParentAcl);
            legacyStateParentAclInjected = false;
            Assert.True(ServiceExecutableMatches(
                ReadWindowsServiceImagePath(serviceName), Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe")));
            Assert.Equal(2, ReadWindowsServiceStartType(serviceName));

            var installedExecutable = Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe");
            var originalExecutableBytes = await File.ReadAllBytesAsync(installedExecutable, timeout.Token);
            var updaterScript = Path.Combine(installRoot, "updater", "netratel-update.ps1");
            Assert.True(File.Exists(updaterScript), "The installed package must contain the supported direct updater script.");
            Assert.Equal("LocalSystem", ReadWindowsServiceStartName(serviceName));

            var updaterPackagePath = Path.Combine(stateDirectory, "staging", $"win-x64-{version}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(updaterPackagePath)!);
            await File.WriteAllBytesAsync(updaterPackagePath, archiveBytes, timeout.Token);
            var updaterAttemptId = Guid.NewGuid();
            var updaterReleaseId = Guid.NewGuid();
            var updaterNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            fixture.ArmUpdateActivation(updaterAttemptId, updaterReleaseId, updaterNonce, version);
            if (File.Exists(updateReadyPath)) File.Delete(updateReadyPath);
            if (File.Exists(Path.Combine(stateDirectory, "result.json"))) File.Delete(Path.Combine(stateDirectory, "result.json"));
            await WriteNativeUpdaterRequestAsync(
                updateRequestPath, updaterAttemptId, updaterReleaseId, updaterNonce, "win-x64", version, version,
                updaterPackagePath, sha256, updateReadyPath, stateDirectory, timeout.Token);

            Assert.False(File.Exists(updateReadyPath), "preflight must start without readiness evidence");
            Assert.False(File.Exists(Path.Combine(stateDirectory, "result.json")), "preflight must start without an updater result");
            var requestBytesBeforePreflight = await File.ReadAllBytesAsync(updateRequestPath, timeout.Token);
            var stagedPackageBytesBeforePreflight = await File.ReadAllBytesAsync(updaterPackagePath, timeout.Token);
            var credentialBytesBeforePreflight = await File.ReadAllBytesAsync(credentialPath, timeout.Token);
            var serviceImageBeforePreflight = ReadWindowsServiceImagePath(serviceName);
            var serviceProcessIdBeforePreflight = ReadWindowsServiceProcessId(serviceName);
            var tokensBeforePreflight = fixture.TokenRequests;
            var enrollmentsBeforePreflight = fixture.EnrollmentRequests;
            var requestAclBeforePreflight = new FileInfo(updateRequestPath).GetAccessControl();
            try
            {
                var untrustedRequestAcl = new FileInfo(updateRequestPath).GetAccessControl();
                untrustedRequestAcl.AddAccessRule(new FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier("S-1-5-32-545"),
                    FileSystemRights.WriteData,
                    AccessControlType.Allow));
                new FileInfo(updateRequestPath).SetAccessControl(untrustedRequestAcl);

                var unsafeRequestUpdate = await RunWindowsUpdaterAsync(
                    powershellPath, installRoot, stateDirectory, updateRequestPath, timeout.Token);
                Assert.NotEqual(0, unsafeRequestUpdate.ExitCode);
                Assert.Contains("untrusted principal write access",
                    $"{unsafeRequestUpdate.StandardOutput}\n{unsafeRequestUpdate.StandardError}",
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(serviceProcessIdBeforePreflight, ReadWindowsServiceProcessId(serviceName));
                Assert.Equal(serviceImageBeforePreflight, ReadWindowsServiceImagePath(serviceName));
                Assert.Equal(originalExecutableBytes, await File.ReadAllBytesAsync(installedExecutable, timeout.Token));
                Assert.Equal(stagedPackageBytesBeforePreflight, await File.ReadAllBytesAsync(updaterPackagePath, timeout.Token));
                Assert.Equal(credentialBytesBeforePreflight, await File.ReadAllBytesAsync(credentialPath, timeout.Token));
                Assert.Equal(requestBytesBeforePreflight, await File.ReadAllBytesAsync(updateRequestPath, timeout.Token));
                Assert.Equal(tokensBeforePreflight, fixture.TokenRequests);
                Assert.Equal(enrollmentsBeforePreflight, fixture.EnrollmentRequests);
                Assert.False(File.Exists(updateReadyPath), "an unsafe request ACL must not create an acceptance marker");
                Assert.False(File.Exists(Path.Combine(stateDirectory, "result.json")),
                    "a rejected preflight must not report an accepted update result");

                var preflightSnapshot = await WaitForPresenceAsync(
                    presence, clientKey, repairedSnapshot.ConnectionEpoch!.Value, timeout.Token);
                Assert.Equal(ClientPresenceStatus.Online, preflightSnapshot.Status);
                Assert.Equal(repairedSnapshot.ConnectionId, preflightSnapshot.ConnectionId);
                Assert.Equal(clientKey, preflightSnapshot.Client);
            }
            finally
            {
                new FileInfo(updateRequestPath).SetAccessControl(requestAclBeforePreflight);
            }

            var processIdBeforeDirectUpdate = ReadWindowsServiceProcessId(serviceName);
            var tokensBeforeDirectUpdate = fixture.TokenRequests;
            var directUpdate = await RunWindowsUpdaterAsync(powershellPath, installRoot, stateDirectory, updateRequestPath, timeout.Token);
            Assert.Equal(0, directUpdate.ExitCode);
            Assert.Contains($"NetRatel client update {version} accepted.", directUpdate.StandardOutput);
            using (var directResult = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stateDirectory, "result.json"), timeout.Token)))
            {
                Assert.Equal("Accepted", directResult.RootElement.GetProperty("state").GetString());
                Assert.Equal(updaterAttemptId, directResult.RootElement.GetProperty("attemptId").GetGuid());
            }

            var directReadmission = Assert.Single(fixture.ActivationReadmissions, observation => observation.AttemptId == updaterAttemptId);
            var directConfirmation = Assert.Single(fixture.ActivationConfirmations, observation => observation.AttemptId == updaterAttemptId);
            Assert.True(directReadmission.Accepted, directReadmission.Reason);
            Assert.True(directConfirmation.Accepted, directConfirmation.Reason);
            Assert.Equal(tenantId, directConfirmation.TenantId);
            Assert.Equal(agentId, directConfirmation.AgentId);
            Assert.Equal(directReadmission.ConnectionId, directConfirmation.ConnectionId);
            Assert.Equal(directReadmission.ConnectionEpoch, directConfirmation.ConnectionEpoch);
            using (var directReady = JsonDocument.Parse(await File.ReadAllTextAsync(updateReadyPath, timeout.Token)))
            {
                Assert.Equal(updaterAttemptId, directReady.RootElement.GetProperty("attemptId").GetGuid());
                Assert.Equal(updaterReleaseId, directReady.RootElement.GetProperty("releaseId").GetGuid());
                Assert.Equal(version, directReady.RootElement.GetProperty("version").GetString());
            }

            var directSnapshot = await WaitForPresenceAsync(
                presence, clientKey, checked(repairedSnapshot.ConnectionEpoch!.Value + 1), timeout.Token);
            Assert.Equal(ClientPresenceStatus.Online, directSnapshot.Status);
            Assert.True(directSnapshot.LastAcceptedSequence >= 2);
            Assert.NotEqual(repairedSnapshot.ConnectionId, directSnapshot.ConnectionId);
            Assert.NotEqual(processIdBeforeDirectUpdate, ReadWindowsServiceProcessId(serviceName));
            Assert.True(fixture.TokenRequests > tokensBeforeDirectUpdate);
            Assert.Equal(1, fixture.EnrollmentRequests);
            Assert.All(fixture.TokenAgentIds, value => Assert.Equal(agentId.ToString("D"), value));
            Assert.Equal(originalExecutableBytes, await File.ReadAllBytesAsync(installedExecutable, timeout.Token));
            Assert.True(ServiceExecutableMatches(ReadWindowsServiceImagePath(serviceName), installedExecutable));

            var failedCandidateVersion = "99.0.0-native-rollback";
            var failedCandidateBytes = CreateNativeClientPackageWithExecutable(
                failedCandidateVersion, "win-x64", Encoding.ASCII.GetBytes("not a Windows executable"));
            var failedCandidateSha = Convert.ToHexString(SHA256.HashData(failedCandidateBytes)).ToLowerInvariant();
            var failedCandidatePath = Path.Combine(stateDirectory, "staging", $"win-x64-{failedCandidateVersion}.zip");
            await File.WriteAllBytesAsync(failedCandidatePath, failedCandidateBytes, timeout.Token);
            var failedAttemptId = Guid.NewGuid();
            var failedReleaseId = Guid.NewGuid();
            var failedNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            fixture.ArmUpdateActivation(failedAttemptId, failedReleaseId, failedNonce, failedCandidateVersion);
            if (File.Exists(updateReadyPath)) File.Delete(updateReadyPath);
            await WriteNativeUpdaterRequestAsync(
                updateRequestPath, failedAttemptId, failedReleaseId, failedNonce, "win-x64", version, failedCandidateVersion,
                failedCandidatePath, failedCandidateSha, updateReadyPath, stateDirectory, timeout.Token);

            var processIdBeforeRollback = ReadWindowsServiceProcessId(serviceName);
            var tokensBeforeRollback = fixture.TokenRequests;
            var rollback = await RunWindowsUpdaterAsync(powershellPath, installRoot, stateDirectory, updateRequestPath, timeout.Token);
            Assert.Equal(1, rollback.ExitCode);
            Assert.Contains("Rollback completed", rollback.StandardOutput);
            using (var rollbackResult = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stateDirectory, "result.json"), timeout.Token)))
            {
                Assert.Equal("RolledBack", rollbackResult.RootElement.GetProperty("state").GetString());
                Assert.Equal(failedAttemptId, rollbackResult.RootElement.GetProperty("attemptId").GetGuid());
                Assert.Equal("post_cutover_failure", rollbackResult.RootElement.GetProperty("failureCode").GetString());
            }
            Assert.False(File.Exists(updateReadyPath), "a failed candidate must not inherit or fabricate readiness evidence");
            Assert.DoesNotContain(fixture.ActivationConfirmations,
                observation => observation.AttemptId == failedAttemptId && observation.Accepted);

            var rollbackSnapshot = await WaitForPresenceAsync(
                presence, clientKey, checked(directSnapshot.ConnectionEpoch!.Value + 1), timeout.Token);
            Assert.Equal(ClientPresenceStatus.Online, rollbackSnapshot.Status);
            Assert.True(rollbackSnapshot.LastAcceptedSequence >= 2);
            Assert.NotEqual(directSnapshot.ConnectionId, rollbackSnapshot.ConnectionId);
            Assert.Equal(clientKey, rollbackSnapshot.Client);
            Assert.NotEqual(processIdBeforeRollback, ReadWindowsServiceProcessId(serviceName));
            Assert.True(fixture.TokenRequests > tokensBeforeRollback);
            Assert.Equal(1, fixture.EnrollmentRequests);
            Assert.All(fixture.TokenAgentIds, value => Assert.Equal(agentId.ToString("D"), value));
            Assert.Equal(originalExecutableBytes, await File.ReadAllBytesAsync(installedExecutable, timeout.Token));
            Assert.True(ServiceExecutableMatches(ReadWindowsServiceImagePath(serviceName), installedExecutable));
            Assert.Equal("LocalSystem", ReadWindowsServiceStartName(serviceName));
            Console.WriteLine(
                $"Native default-path service receipt: os={Environment.OSVersion.VersionString}; " +
                "powershell=WindowsPowerShell-5.1; roles=install-root-default,logs-default,update-state-default; " +
                "freshInstall=true;sameVersionRepair=true; readiness=LocalSystem-session-0-two-acknowledged-heartbeats");
        }
        finally
        {
            if (repairInstallerProcess is not null)
            {
                try
                {
                    if (!repairInstallerProcess.HasExited) repairInstallerProcess.Kill(entireProcessTree: true);
                    await repairInstallerProcess.WaitForExitAsync(CancellationToken.None);
                }
                finally
                {
                    repairInstallerProcess.Dispose();
                }
            }
            if (unsafeRepairInstallerProcess is not null)
            {
                try
                {
                    if (!unsafeRepairInstallerProcess.HasExited) unsafeRepairInstallerProcess.Kill(entireProcessTree: true);
                    await unsafeRepairInstallerProcess.WaitForExitAsync(CancellationToken.None);
                }
                finally { unsafeRepairInstallerProcess.Dispose(); }
            }
            if (installerProcess is not null)
            {
                try
                {
                    if (!installerProcess.HasExited) installerProcess.Kill(entireProcessTree: true);
                    await installerProcess.WaitForExitAsync(CancellationToken.None);
                }
                finally { installerProcess.Dispose(); }
            }
            if (legacyStateParentAclInjected && originalStateParentAcl is not null && Directory.Exists(credentialDirectory))
            {
                new DirectoryInfo(credentialDirectory).SetAccessControl(originalStateParentAcl);
                legacyStateParentAclInjected = false;
            }
            var serviceRemoved = false;
            try
            {
                serviceRemoved = RemoveWindowsServiceIfRegisteredImagePathEquals(
                    serviceName, GetExpectedServiceImagePath(Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe")));
            }
            finally
            {
                try
                {
                    if (app is not null)
                    {
                        try { await app.StopAsync(CancellationToken.None); }
                        finally { await app.DisposeAsync(); }
                    }
                }
                finally
                {
                    try
                    {
                        if (trusted) RemoveLocalMachineTrust(trustedCertificate);
                    }
                    finally
                    {
                        if (serviceRemoved)
                        {
                            if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
                            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
                            DeleteCredentialFiles(credentialDirectory);
                            if (Directory.Exists(installRoot)) Directory.Delete(installRoot, recursive: true);
                            if (Directory.Exists(productInstallDirectory) && !Directory.EnumerateFileSystemEntries(productInstallDirectory).Any())
                                Directory.Delete(productInstallDirectory);
                            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                        }
                    }
                }
            }
            var currentProgramDataSddl = programDataDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(parentSecuritySections);
            var programDataAclWasChanged = !string.Equals(currentProgramDataSddl, originalProgramDataSddl, StringComparison.Ordinal);
            if (programDataAclWasChanged) programDataDirectory.SetAccessControl(originalProgramDataSecurity);
            Assert.False(programDataAclWasChanged, "the generated default-path service installer changed the shared ProgramData ACL; the original ACL was restored");
            Assert.Equal(originalProgramDataSddl, programDataDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(parentSecuritySections));

            var currentProgramFilesSddl = programFilesDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(parentSecuritySections);
            var programFilesAclWasChanged = !string.Equals(currentProgramFilesSddl, originalProgramFilesSddl, StringComparison.Ordinal);
            if (programFilesAclWasChanged) programFilesDirectory.SetAccessControl(originalProgramFilesSecurity);
            Assert.False(programFilesAclWasChanged, "the generated default-path service installer changed the shared Program Files ACL; the original ACL was restored");
            Assert.Equal(originalProgramFilesSddl, programFilesDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(parentSecuritySections));
            Assert.True(serviceRemoved, "The service registration changed; retaining its package and credentials for inspection.");
        }
    }

    [Fact]
    [Trait("category", "hosted")]
    [SupportedOSPlatform("windows")]
    public async Task GeneratedServiceInstallerUpgradesPublishedPreviousVersionAndPreservesIdentity()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The published Windows service upgrade test requires hosted Windows.");

        var fixtureDirectory = Environment.GetEnvironmentVariable("NETRATEL_RELEASE_FIXTURE_DIR")
            ?? throw new InvalidOperationException("NETRATEL_RELEASE_FIXTURE_DIR must contain the selected completed public release fixture.");
        var packageDirectory = Environment.GetEnvironmentVariable("NETRATEL_NATIVE_CLIENT_DIRECTORY")
            ?? throw new InvalidOperationException("Set NETRATEL_NATIVE_CLIENT_DIRECTORY to the hosted Windows client package directory.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var publishedPackage = await LoadVerifiedPublishedWindowsPackageAsync(fixtureDirectory, timeout.Token);

        using var candidateManifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(packageDirectory, "netratel-client-manifest.json"), timeout.Token));
        var candidateManifestRoot = candidateManifest.RootElement;
        var candidateVersion = candidateManifestRoot.GetProperty("version").GetString()
            ?? throw new InvalidDataException("The candidate client manifest has no version.");
        var candidateCommit = candidateManifestRoot.GetProperty("commitSha").GetString()
            ?? throw new InvalidDataException("The candidate client manifest has no commit identity.");
        Assert.Equal("NetRatel.Client", candidateManifestRoot.GetProperty("product").GetString());
        Assert.Equal("win-x64", candidateManifestRoot.GetProperty("runtimeId").GetString());
        Assert.Matches("^[0-9a-f]{40}$", candidateCommit);
        Assert.True(ClientUpdateVersioning.IsNewerVersion(candidateVersion, publishedPackage.Version),
            "The dynamically selected completed publication must be an earlier version than the candidate.");

        const string serviceName = "NetRatel.Client";
        const int tenantId = 4098;
        var agentId = Guid.NewGuid();
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(string.IsNullOrWhiteSpace(commonApplicationData));
        Assert.False(string.IsNullOrWhiteSpace(programFiles));
        var credentialDirectory = Path.Combine(commonApplicationData, "NetRatel");
        var credentialPath = Path.Combine(credentialDirectory, "agent.dat");
        var stateDirectory = Path.Combine(credentialDirectory, "update");
        var logDirectory = Path.Combine(credentialDirectory, "logs");
        var productInstallDirectory = Path.Combine(programFiles, "NetRatel");
        var installRoot = Path.Combine(productInstallDirectory, "Client");
        var programDataDirectory = new DirectoryInfo(commonApplicationData);
        var programFilesDirectory = new DirectoryInfo(programFiles);
        const AccessControlSections parentSecuritySections =
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        var originalProgramDataSecurity = programDataDirectory.GetAccessControl();
        var originalProgramDataSddl = originalProgramDataSecurity.GetSecurityDescriptorSddlForm(parentSecuritySections);
        var originalProgramFilesSecurity = programFilesDirectory.GetAccessControl();
        var originalProgramFilesSddl = originalProgramFilesSecurity.GetSecurityDescriptorSddlForm(parentSecuritySections);
        AssertWindowsServiceAbsent(serviceName);
        Assert.False(Directory.Exists(credentialDirectory), "the disposable Windows runner must start without NetRatel credential files");
        Assert.False(Directory.Exists(stateDirectory), "the disposable Windows runner must start without NetRatel updater state");
        Assert.False(Directory.Exists(logDirectory), "the disposable Windows runner must start without the default NetRatel log directory");
        Assert.False(Directory.Exists(productInstallDirectory), "the disposable Windows runner must start without the default NetRatel installation root");

        var root = CreateWindowsServiceFixtureRoot("published-upgrade");
        using var certificate = CreateNativeGatewayCertificate();
        using var trustedCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        WebApplication? app = null;
        Process? installerProcess = null;
        var trusted = false;
        using var fixture = new NativeGatewayHostFixture(tenantId, agentId, "ENR-SYNTHETIC-PUBLISHED-UPGRADE");
        fixture.AgentStore.FirstAdmissionObserver = () =>
        {
            AssertProtectedServiceReadinessRequest(stateDirectory);
            AssertSystemServiceReadinessResponse(stateDirectory);
        };
        var observedReadinessAttempts = new ConcurrentDictionary<Guid, byte>();
        fixture.AgentStore.AdmissionObserver = () =>
        {
            var readinessRequestPath = Path.Combine(stateDirectory, "install-readiness", "request.json");
            var readinessResponsePath = Path.Combine(stateDirectory, "install-readiness", "ready.json");
            if (!File.Exists(readinessRequestPath) || !File.Exists(readinessResponsePath)) return;

            var attemptId = ReadServiceReadinessAttemptId(stateDirectory);
            if (!observedReadinessAttempts.TryAdd(attemptId, 0)) return;

            try
            {
                AssertProtectedServiceReadinessRequest(stateDirectory);
                AssertSystemServiceReadinessResponse(stateDirectory);
            }
            catch
            {
                observedReadinessAttempts.TryRemove(attemptId, out _);
                throw;
            }
        };

        try
        {
            var candidateArchivePath = Path.Combine(root, "candidate.zip");
            ZipFile.CreateFromDirectory(packageDirectory, candidateArchivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var candidateArchiveBytes = await File.ReadAllBytesAsync(candidateArchivePath, timeout.Token);
            var candidateSha256 = Convert.ToHexString(SHA256.HashData(candidateArchiveBytes)).ToLowerInvariant();
            await AssertWindowsClientArchiveManifestAsync(
                candidateArchivePath, candidateVersion, candidateCommit, timeout.Token);

            AddLocalMachineTrust(trustedCertificate);
            trusted = true;
            app = BuildNativeGatewayHost(certificate, fixture);
            await app.StartAsync(timeout.Token);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            Assert.NotNull(addresses);
            var apiBase = Assert.Single(addresses!, address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            var tlsProbe = await ProbeLoopbackHttpsAsync(apiBase, timeout.Token);
            var powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGeneratedInstallerAsync(
                string version,
                string sha256,
                string scriptName)
            {
                var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                    tenantId, "win-x64", fixture.EnrollmentCode, apiBase, DateTimeOffset.UtcNow.AddHours(1),
                    InstallAsService: true, SilentInstall: true, version, sha256,
                    ReadinessTimeoutSeconds: 90));
                var scriptPath = Path.Combine(root, scriptName);
                await File.WriteAllTextAsync(scriptPath, script, timeout.Token);

                var start = new ProcessStartInfo(powershellPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-ExecutionPolicy");
                start.ArgumentList.Add("Bypass");
                start.ArgumentList.Add("-File");
                start.ArgumentList.Add(scriptPath);
                start.Environment.Remove("NetRatel_ROOT");
                start.Environment.Remove("NetRatel_STATE");
                start.Environment.Remove("NetRatel_LOG_DIR");
                start.Environment["TEMP"] = root;

                installerProcess = Process.Start(start)
                    ?? throw new InvalidOperationException("Could not start the generated Windows service installer.");
                var captured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
                var exitCode = installerProcess.ExitCode;
                installerProcess.Dispose();
                installerProcess = null;
                return (exitCode, captured.StandardOutput, captured.StandardError);
            }

            fixture.SetArtifact(publishedPackage.Bytes, publishedPackage.Version, publishedPackage.Sha256);
            var previousInstall = await RunGeneratedInstallerAsync(
                publishedPackage.Version, publishedPackage.Sha256, "install-published-previous.ps1");
            Assert.True(previousInstall.ExitCode == 0,
                $"The published previous-version service install failed; {GetSafeInstallerDiagnostic(string.Concat(previousInstall.StandardOutput, Environment.NewLine, previousInstall.StandardError), tlsProbe)}");
            AssertInstallerOutputHasSafeText(previousInstall.StandardOutput, "Gateway heartbeat ready:", "published_previous_ready_message");
            AssertInstallerOutputHasSafeText(previousInstall.StandardOutput, $"agentId={agentId:D}", "published_previous_agent_id_message");
            AssertInstallerOutputHasSafeText(previousInstall.StandardOutput, $"tenantId={tenantId}", "published_previous_tenant_id_message");
            Assert.Equal(1, fixture.AgentStore.FirstAdmissionObservations);
            Assert.Empty(fixture.AgentStore.FirstAdmissionObservationFailures);
            Assert.Equal(1, fixture.DownloadRequests);
            Assert.Equal(1, fixture.EnrollmentRequests);
            Assert.True(File.Exists(credentialPath), "the LocalSystem install must retain its enrolled credentials");

            var presence = app.Services.GetRequiredService<IClientPresenceRouter>();
            var clientKey = new ClientKey(tenantId, agentId);
            var previousSnapshot = await WaitForPresenceAsync(presence, clientKey, minimumEpoch: 1, timeout.Token);
            Assert.Equal(ClientPresenceStatus.Online, previousSnapshot.Status);
            Assert.True(previousSnapshot.IsAuthoritative);
            Assert.Equal("akka", previousSnapshot.Source);
            Assert.True(previousSnapshot.LastAcceptedSequence >= 2,
                "the published previous-version service must have two gateway-acknowledged heartbeat frames");

            var previousExecutable = Path.Combine(
                installRoot, "versions", publishedPackage.Version, "NetRatel.Client.exe");
            Assert.True(File.Exists(previousExecutable));
            Assert.True(ServiceExecutableMatches(ReadWindowsServiceImagePath(serviceName), previousExecutable));
            Assert.Equal("LocalSystem", ReadWindowsServiceStartName(serviceName));
            Assert.Equal(2, ReadWindowsServiceStartType(serviceName));
            var previousServiceProcessId = ReadWindowsServiceProcessId(serviceName);
            using (var previousServiceProcess = Process.GetProcessById(previousServiceProcessId))
            {
                Assert.Equal(0, previousServiceProcess.SessionId);
            }

            var installedPreviousExecutableBytes = await File.ReadAllBytesAsync(previousExecutable, timeout.Token);
            var credentialBytesBeforeUpgrade = await File.ReadAllBytesAsync(credentialPath, timeout.Token);
            var credentialSecurityBeforeUpgrade = AssertProtectedWindowsCredential(credentialPath);
            var tokensBeforeUpgrade = fixture.TokenRequests;

            fixture.SetArtifact(candidateArchiveBytes, candidateVersion, candidateSha256);
            var candidateInstall = await RunGeneratedInstallerAsync(
                candidateVersion, candidateSha256, "install-candidate-upgrade.ps1");
            Assert.True(candidateInstall.ExitCode == 0,
                $"The candidate service upgrade failed; {GetSafeInstallerDiagnostic(string.Concat(candidateInstall.StandardOutput, Environment.NewLine, candidateInstall.StandardError), tlsProbe)}");
            AssertInstallerOutputHasSafeText(candidateInstall.StandardOutput, "Gateway heartbeat ready:", "candidate_upgrade_ready_message");
            AssertInstallerOutputHasSafeText(candidateInstall.StandardOutput, $"agentId={agentId:D}", "candidate_upgrade_agent_id_message");
            AssertInstallerOutputHasSafeText(candidateInstall.StandardOutput, $"tenantId={tenantId}", "candidate_upgrade_tenant_id_message");

            var upgradedSnapshot = await WaitForPresenceAsync(
                presence, clientKey, checked(previousSnapshot.ConnectionEpoch!.Value + 1), timeout.Token);
            Assert.Equal(ClientPresenceStatus.Online, upgradedSnapshot.Status);
            Assert.True(upgradedSnapshot.IsAuthoritative);
            Assert.Equal("akka", upgradedSnapshot.Source);
            Assert.True(upgradedSnapshot.LastAcceptedSequence >= 2,
                "the candidate service must establish two newly acknowledged gateway heartbeat frames");
            Assert.Equal(clientKey, upgradedSnapshot.Client);
            Assert.NotEqual(previousSnapshot.ConnectionId, upgradedSnapshot.ConnectionId);
            Assert.True(upgradedSnapshot.ConnectionEpoch.HasValue &&
                upgradedSnapshot.ConnectionEpoch.Value > previousSnapshot.ConnectionEpoch!.Value);

            var candidateExecutable = Path.Combine(installRoot, "versions", candidateVersion, "NetRatel.Client.exe");
            Assert.True(File.Exists(candidateExecutable));
            Assert.True(File.Exists(previousExecutable), "the prior published package version must remain available after upgrade");
            Assert.Equal(installedPreviousExecutableBytes, await File.ReadAllBytesAsync(previousExecutable, timeout.Token));
            Assert.True(ServiceExecutableMatches(ReadWindowsServiceImagePath(serviceName), candidateExecutable));
            Assert.Equal("LocalSystem", ReadWindowsServiceStartName(serviceName));
            Assert.Equal(2, ReadWindowsServiceStartType(serviceName));
            var candidateServiceProcessId = ReadWindowsServiceProcessId(serviceName);
            Assert.NotEqual(previousServiceProcessId, candidateServiceProcessId);
            using (var candidateServiceProcess = Process.GetProcessById(candidateServiceProcessId))
            {
                Assert.Equal(0, candidateServiceProcess.SessionId);
            }

            Assert.Equal(credentialBytesBeforeUpgrade, await File.ReadAllBytesAsync(credentialPath, timeout.Token));
            Assert.Equal(credentialSecurityBeforeUpgrade, AssertProtectedWindowsCredential(credentialPath));
            Assert.True(fixture.EnrollmentRequests == 1,
                "the upgrade must reuse the installed Agent identity instead of enrolling a second agent");
            Assert.Equal(2, fixture.DownloadRequests);
            Assert.True(fixture.TokenRequests > tokensBeforeUpgrade,
                "the candidate LocalSystem process must authenticate and publish a new connection");
            Assert.All(fixture.TokenAgentIds, value => Assert.Equal(agentId.ToString("D"), value));
            Assert.All(fixture.RefreshTokens, value => Assert.Equal(NativeGatewayHostFixture.RefreshToken, value));

            Assert.Equal(2, observedReadinessAttempts.Count);
            Assert.Empty(fixture.AgentStore.AdmissionObservationFailures);
            Assert.False(File.Exists(Path.Combine(stateDirectory, "install-readiness", "request.json")),
                "the successful installer must remove its readiness request after the admission was observed");
            Assert.False(File.Exists(Path.Combine(stateDirectory, "install-readiness", "ready.json")),
                "the successful installer must remove its readiness response after the admission was observed");
            Console.WriteLine(
                $"Native published Windows upgrade receipt: os={Environment.OSVersion.VersionString}; " +
                $"powershell=WindowsPowerShell-5.1; previous={publishedPackage.Version}; candidate={candidateVersion}; " +
                "service=LocalSystem-session-0; priorAndCandidateHeartbeats=acknowledged>=2; identityPreserved=true; enrollments=1");
        }
        finally
        {
            if (installerProcess is not null)
            {
                try
                {
                    if (!installerProcess.HasExited) installerProcess.Kill(entireProcessTree: true);
                    await installerProcess.WaitForExitAsync(CancellationToken.None);
                }
                finally { installerProcess.Dispose(); }
            }

            var serviceRemoved = false;
            try
            {
                serviceRemoved = RemoveWindowsServiceIfRegisteredImagePathEquals(
                    serviceName,
                    GetExpectedServiceImagePath(Path.Combine(
                        installRoot, "versions", publishedPackage.Version, "NetRatel.Client.exe")));
                if (!serviceRemoved)
                {
                    serviceRemoved = RemoveWindowsServiceIfRegisteredImagePathEquals(
                        serviceName,
                        GetExpectedServiceImagePath(Path.Combine(
                            installRoot, "versions", candidateVersion, "NetRatel.Client.exe")));
                }
            }
            finally
            {
                try
                {
                    if (app is not null)
                    {
                        try { await app.StopAsync(CancellationToken.None); }
                        finally { await app.DisposeAsync(); }
                    }
                }
                finally
                {
                    try
                    {
                        if (trusted) RemoveLocalMachineTrust(trustedCertificate);
                    }
                    finally
                    {
                        if (serviceRemoved)
                        {
                            if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
                            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
                            DeleteCredentialFiles(credentialDirectory);
                            if (Directory.Exists(installRoot)) Directory.Delete(installRoot, recursive: true);
                            if (Directory.Exists(productInstallDirectory) && !Directory.EnumerateFileSystemEntries(productInstallDirectory).Any())
                                Directory.Delete(productInstallDirectory);
                            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                        }
                    }
                }
            }

            var currentProgramDataSddl = programDataDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(parentSecuritySections);
            var programDataAclWasChanged = !string.Equals(currentProgramDataSddl, originalProgramDataSddl, StringComparison.Ordinal);
            if (programDataAclWasChanged) programDataDirectory.SetAccessControl(originalProgramDataSecurity);
            Assert.False(programDataAclWasChanged,
                "the generated published-upgrade installer changed the shared ProgramData ACL; the original ACL was restored");
            Assert.Equal(originalProgramDataSddl, programDataDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(parentSecuritySections));

            var currentProgramFilesSddl = programFilesDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(parentSecuritySections);
            var programFilesAclWasChanged = !string.Equals(currentProgramFilesSddl, originalProgramFilesSddl, StringComparison.Ordinal);
            if (programFilesAclWasChanged) programFilesDirectory.SetAccessControl(originalProgramFilesSecurity);
            Assert.False(programFilesAclWasChanged,
                "the generated published-upgrade installer changed the shared Program Files ACL; the original ACL was restored");
            Assert.Equal(originalProgramFilesSddl, programFilesDirectory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(parentSecuritySections));
            Assert.True(serviceRemoved, "The service registration changed; retaining its package and credentials for inspection.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(string Version, string Sha256, byte[] Bytes)> LoadVerifiedPublishedWindowsPackageAsync(
        string fixtureDirectory,
        CancellationToken cancellationToken)
    {
        using var publication = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixtureDirectory, "publication.json"), cancellationToken));
        var record = publication.RootElement;
        var version = record.GetProperty("productVersion").GetString()
            ?? throw new InvalidDataException("The publication record has no product version.");
        Assert.Matches("^\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", version);
        Assert.Equal("complete", record.GetProperty("verification").GetProperty("state").GetString());

        var receipt = record.GetProperty("inputReceipt");
        Assert.Equal("BostonTechnologies/netratel", receipt.GetProperty("repository").GetString());
        Assert.Equal(".github/workflows/release-build.yml", receipt.GetProperty("workflow").GetString());
        Assert.True(receipt.GetProperty("runId").GetInt64() > 0);
        Assert.True(receipt.GetProperty("attempt").GetInt32() > 0);
        var commit = record.GetProperty("publicCommit").GetString()
            ?? throw new InvalidDataException("The publication record has no public commit identity.");
        Assert.Matches("^[0-9a-f]{40}$", commit);
        Assert.Equal(commit, receipt.GetProperty("headSha").GetString());

        var archiveName = $"netratel-client-{version}-win-x64.zip";
        var receiptFiles = receipt.GetProperty("files");
        Assert.True(receiptFiles.TryGetProperty(archiveName, out var receiptArchive),
            "The completed build receipt must declare the exact previous Windows archive.");
        var receiptSha256 = receiptArchive.GetProperty("sha256").GetString()
            ?? throw new InvalidDataException("The publication receipt has no archive checksum.");
        var publishedArtifacts = record.GetProperty("artifacts");
        Assert.True(publishedArtifacts.TryGetProperty(archiveName, out var publishedArchiveSha256),
            "The completed publication record must list the previous Windows archive asset.");
        Assert.Equal(receiptSha256.ToLowerInvariant(), publishedArchiveSha256.GetString()?.ToLowerInvariant());

        var archivePath = Path.Combine(fixtureDirectory, archiveName);
        Assert.True(File.Exists(archivePath), "The selected publication fixture must contain its declared Windows archive.");
        var checksumLines = File.ReadAllLines(Path.Combine(fixtureDirectory, "SHA256SUMS"))
            .Select(line => Regex.Match(line, "^(?<sha256>[a-fA-F0-9]{64})\\s+\\*?(?<name>[^\\s]+)$"))
            .Where(match => match.Success && string.Equals(match.Groups["name"].Value, archiveName, StringComparison.Ordinal))
            .Select(match => match.Groups["sha256"].Value.ToLowerInvariant())
            .ToArray();
        var checksumSha256 = Assert.Single(checksumLines);
        Assert.Equal(receiptSha256.ToLowerInvariant(), checksumSha256);

        var bytes = await File.ReadAllBytesAsync(archivePath, cancellationToken);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(receiptSha256.ToLowerInvariant(), actualSha256);
        await AssertWindowsClientArchiveManifestAsync(archivePath, version, commit, cancellationToken);
        return (version, actualSha256, bytes);
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertWindowsClientArchiveManifestAsync(
        string archivePath,
        string expectedVersion,
        string expectedCommit,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        const string manifestName = "netratel-client-manifest.json";
        var manifestEntry = Assert.Single(archive.Entries,
            entry => string.Equals(entry.FullName, manifestName, StringComparison.Ordinal) ||
                     string.Equals(entry.FullName, $"netratel-client-win-x64/{manifestName}", StringComparison.Ordinal));
        Assert.InRange(manifestEntry.Length, 1, 128 * 1024);
        const string wrappedPackagePrefix = "netratel-client-win-x64/";
        var isWrapped = manifestEntry.FullName.StartsWith(wrappedPackagePrefix, StringComparison.Ordinal);
        if (isWrapped)
        {
            Assert.All(archive.Entries, entry =>
                Assert.True(entry.FullName.StartsWith(wrappedPackagePrefix, StringComparison.Ordinal),
                    "a wrapped Windows package archive must not mix in other root entries"));
        }
        else
        {
            Assert.DoesNotContain(archive.Entries,
                entry => entry.FullName.StartsWith(wrappedPackagePrefix, StringComparison.Ordinal));
        }

        var packageDirectory = manifestEntry.FullName[..^manifestName.Length];
        await using var manifestStream = manifestEntry.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
        var manifestRoot = manifest.RootElement;
        Assert.Equal("netratel.client.manifest.v1", manifestRoot.GetProperty("schema").GetString());
        Assert.Equal("NetRatel.Client", manifestRoot.GetProperty("product").GetString());
        Assert.Equal(expectedVersion, manifestRoot.GetProperty("version").GetString());
        Assert.Equal("win-x64", manifestRoot.GetProperty("runtimeId").GetString());
        Assert.Equal(expectedCommit, manifestRoot.GetProperty("commitSha").GetString());
        var executable = manifestRoot.GetProperty("executable").GetString();
        Assert.Equal("NetRatel.Client.exe", executable);
        Assert.Contains(archive.Entries,
            entry => string.Equals(entry.FullName, $"{packageDirectory}{executable}", StringComparison.Ordinal));
    }

    [SupportedOSPlatform("windows")]
    private static string AssertProtectedWindowsCredential(string credentialPath)
    {
        var file = new FileInfo(credentialPath);
        Assert.True(file.Exists, "the LocalSystem service must persist its machine credential file");
        var security = file.GetAccessControl();
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        Assert.Equal(systemSid.Value, owner?.Value);
        var untrustedSids = new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value
        };
        const FileSystemRights replacementRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.DoesNotContain(rules, rule => rule.AccessControlType == AccessControlType.Allow &&
            untrustedSids.Contains(rule.IdentityReference.Value) && (rule.FileSystemRights & replacementRights) != 0);
        return security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
    }

    [SupportedOSPlatform("windows")]
    private static Guid ReadServiceReadinessAttemptId(string stateDirectory)
    {
        using var request = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(stateDirectory, "install-readiness", "request.json")));
        return request.RootElement.GetProperty("attemptId").GetGuid();
    }

    [Fact]
    [Trait("category", "hosted")]
    [SupportedOSPlatform("windows")]
    public async Task GeneratedServiceInstallerPreservesAnExistingAdminDpapiCredentialWhenLocalSystemCannotReadIt()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The native Windows credential-boundary test requires hosted Windows.");

        const string serviceName = "NetRatel.Client";
        const int tenantId = 4098;
        var agentId = Guid.NewGuid();
        var packageDirectory = Environment.GetEnvironmentVariable("NETRATEL_NATIVE_CLIENT_DIRECTORY")
            ?? throw new InvalidOperationException("Set NETRATEL_NATIVE_CLIENT_DIRECTORY to the hosted Windows client package directory.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(packageDirectory, "netratel-client-manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        Assert.Equal("win-x64", manifest.RootElement.GetProperty("runtimeId").GetString());

        AssertWindowsServiceAbsent(serviceName);
        var credentialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel");
        Assert.False(Directory.Exists(credentialDirectory), "the disposable Windows runner must start without NetRatel credential files");
        var root = CreateWindowsServiceFixtureRoot("admin-dpapi");
        var installRoot = Path.Combine(root, "client");
        var credentialPath = Path.Combine(credentialDirectory, "agent.dat");
        byte[]? originalCredentialBytes = null;
        X509Certificate2? certificate = null;
        X509Certificate2? trustedCertificate = null;
        using var fixture = new NativeGatewayHostFixture(tenantId, agentId, "ENR-SYNTHETIC-ADMIN-DPAPI");
        WebApplication? app = null;
        var trusted = false;
        var credentialDirectoryCreatedByTest = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Process? installerProcess = null;
        try
        {
            Directory.CreateDirectory(credentialDirectory);
            credentialDirectoryCreatedByTest = true;
            string publicKey;
            string privateKey;
            using (var deviceKeyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                publicKey = Convert.ToBase64String(deviceKeyPair.ExportSubjectPublicKeyInfo());
                privateKey = Convert.ToBase64String(deviceKeyPair.ExportPkcs8PrivateKey());
            }
            var adminProtectedCredential = ProtectedData.Protect(
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    AgentId = agentId.ToString("D"),
                    RefreshToken = "admin-only-refresh-token",
                    PublicKey = publicKey,
                    PrivateKey = privateKey,
                    KeyAlgorithm = "ecdsa-p256"
                }),
                Encoding.UTF8.GetBytes("netratel-agent-credential-v1"),
                DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(credentialPath, adminProtectedCredential, timeout.Token);
            originalCredentialBytes = await File.ReadAllBytesAsync(credentialPath, timeout.Token);
            var adminCredentialStore = new AgentCredentialStore();
            Assert.Equal((agentId.ToString("D"), "admin-only-refresh-token"), await adminCredentialStore.LoadAsync());
            var adminDeviceKey = await adminCredentialStore.GetOrCreateAsync(CancellationToken.None);
            Assert.Equal(publicKey, adminDeviceKey.PublicKey);
            using (var validatedDeviceKey = ECDsa.Create())
            {
                validatedDeviceKey.ImportPkcs8PrivateKey(Convert.FromBase64String(adminDeviceKey.PrivateKey), out _);
                Assert.Equal(adminDeviceKey.PublicKey, Convert.ToBase64String(validatedDeviceKey.ExportSubjectPublicKeyInfo()));
            }

            certificate = CreateNativeGatewayCertificate();
            trustedCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            AddLocalMachineTrust(trustedCertificate);
            trusted = true;
            app = BuildNativeGatewayHost(certificate, fixture);
            await app.StartAsync(timeout.Token);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            Assert.NotNull(addresses);
            var apiBase = Assert.Single(addresses!, address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            var tlsProbe = await ProbeLoopbackHttpsAsync(apiBase, timeout.Token);

            var archivePath = Path.Combine(root, "client.zip");
            ZipFile.CreateFromDirectory(packageDirectory, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath, timeout.Token);
            var sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            fixture.SetArtifact(archiveBytes, version, sha256);
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                tenantId, "win-x64", fixture.EnrollmentCode, apiBase, DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: true, SilentInstall: true, version, sha256,
                ReadinessTimeoutSeconds: 10));
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, script, timeout.Token);

            var powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershellPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.Environment["NetRatel_ROOT"] = installRoot;
            start.Environment["NetRatel_STATE"] = Path.Combine(root, "state");
            start.Environment["NetRatel_LOG_DIR"] = Path.Combine(root, "logs");
            start.Environment["TEMP"] = root;

            installerProcess = Process.Start(start) ?? throw new InvalidOperationException("Could not start the service installer.");
            var captured = await RunInstallerAndCaptureOutputAsync(installerProcess, timeout.Token);
            var output = captured.StandardOutput;
            var error = captured.StandardError;
            Assert.NotEqual(0, installerProcess.ExitCode);
            AssertInstallerOutputContains("did not reach gateway heartbeat readiness", output, error, installerProcess.ExitCode, tlsProbe);
            AssertInstallerOutputLacksSafeText(output, "authenticated gateway heartbeat readiness was verified", "unexpected_ready_message");
            Assert.Equal(originalCredentialBytes, await File.ReadAllBytesAsync(credentialPath, timeout.Token));
            Assert.Equal(0, fixture.EnrollmentRequests);
            Assert.Equal(0, fixture.TokenRequests);
            Assert.Equal(1, fixture.DownloadRequests);
            Assert.True(ServiceExecutableMatches(
                ReadWindowsServiceImagePath(serviceName), Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe")));
        }
        finally
        {
            timeout.Cancel();
            if (installerProcess is not null)
            {
                try
                {
                    if (!installerProcess.HasExited) installerProcess.Kill(entireProcessTree: true);
                    await installerProcess.WaitForExitAsync(CancellationToken.None);
                }
                finally { installerProcess.Dispose(); }
            }
            var serviceRemoved = false;
            try
            {
                serviceRemoved = RemoveWindowsServiceIfRegisteredImagePathEquals(
                    serviceName, GetExpectedServiceImagePath(Path.Combine(installRoot, "versions", version, "NetRatel.Client.exe")));
            }
            finally
            {
                try
                {
                    if (app is not null)
                    {
                        try { await app.StopAsync(CancellationToken.None); }
                        finally { await app.DisposeAsync(); }
                    }
                }
                finally
                {
                    try
                    {
                        if (trusted && trustedCertificate is not null) RemoveLocalMachineTrust(trustedCertificate);
                    }
                    finally
                    {
                        trustedCertificate?.Dispose();
                        certificate?.Dispose();
                        if (serviceRemoved)
                        {
                            if (credentialDirectoryCreatedByTest) DeleteCredentialFiles(credentialDirectory);
                            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                        }
                    }
                }
            }
            Assert.True(serviceRemoved, "The service registration changed; retaining its package and credentials for inspection.");
        }
    }

    private static async Task ServeInstallerAndPackageRejectionAsync(
        TcpListener listener,
        string installerScript,
        string enrollmentCode,
        Action recordPackageRequest,
        CancellationToken cancellationToken)
    {
        var installerBytes = Encoding.UTF8.GetBytes(installerScript);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, cancellationToken);
            var requestLine = request.Split("\r\n", 2, StringSplitOptions.None)[0];
            var isInstallerSource = requestLine.Contains("/install.ps1", StringComparison.Ordinal);
            if (!isInstallerSource)
            {
                Assert.Contains("/api/v1/client-artifacts/win-x64/latest/onboarding-download", requestLine, StringComparison.Ordinal);
                Assert.Contains("X-NetRatel-Tenant-Id: 4098", request, StringComparison.OrdinalIgnoreCase);
                Assert.Contains($"X-NetRatel-Enrollment-Code: {enrollmentCode}", request, StringComparison.OrdinalIgnoreCase);
                recordPackageRequest();
            }

            byte[] body = isInstallerSource ? installerBytes : Array.Empty<byte>();
            var statusLine = isInstallerSource ? "HTTP/1.1 200 OK" : "HTTP/1.1 404 Not Found";
            var headers = Encoding.ASCII.GetBytes(
                $"{statusLine}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            if (body.Length > 0) await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetDirectoryOwner(string path, SecurityIdentifier owner)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        security.SetOwner(owner);
        directory.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectoryJunction(string junctionPath, string targetPath)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Arguments = $"/d /c mklink /J \"{junctionPath}\" \"{targetPath}\""
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start cmd.exe to create the disposable junction fixture.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch (System.TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("cmd.exe timed out while creating the disposable junction fixture.");
        }

        _ = standardOutput.GetAwaiter().GetResult();
        _ = standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"cmd.exe could not create the disposable junction fixture (exitCode={process.ExitCode}).");
        }

        var attributes = File.GetAttributes(junctionPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
            (FileAttributes.Directory | FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The disposable junction fixture was not created as a directory reparse point.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteWindowsDirectoryJunctionIfPresent(string junctionPath)
    {
        if (!Directory.Exists(junctionPath) && !File.Exists(junctionPath)) return;

        var attributes = File.GetAttributes(junctionPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
            (FileAttributes.Directory | FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The disposable junction path changed type before nonrecursive cleanup.");
        }

        Directory.Delete(junctionPath, recursive: false);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertInheritedWriteOnlyDirectoryAcl(string path, SecurityIdentifier usersSid)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var administratorSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        Assert.Equal(administratorSid.Value, owner?.Value);
        Assert.False(security.AreAccessRulesProtected, "the legacy fixture must retain inherited access rules before preflight");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Value == usersSid.Value)
            .ToArray();
        var inheritedWriteRule = Assert.Single(rules);
        Assert.True(inheritedWriteRule.IsInherited);
        Assert.Equal(AccessControlType.Allow, inheritedWriteRule.AccessControlType);
        Assert.True((inheritedWriteRule.FileSystemRights & FileSystemRights.WriteData) != 0,
            $"the synthetic inherited ACE must grant WriteData; numeric rights=0x{(int)inheritedWriteRule.FileSystemRights:X}");
        Assert.Equal(PropagationFlags.None, inheritedWriteRule.PropagationFlags);
        Assert.True((inheritedWriteRule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0);
        var dangerousRights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        Assert.True((inheritedWriteRule.FileSystemRights & dangerousRights) == 0,
            $"the synthetic ACE must not grant replacement or ACL-control rights; numeric rights=0x{(int)inheritedWriteRule.FileSystemRights:X}");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertProtectedOwnedDirectoryAcl(string path, SecurityIdentifier usersSid)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var administratorSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        Assert.Equal(administratorSid.Value, owner?.Value);
        Assert.True(security.AreAccessRulesProtected, "the repaired product directory must not inherit write access");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.DoesNotContain(rules, rule => rule.IdentityReference.Value == usersSid.Value &&
            rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.WriteData) != 0);
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        Assert.Contains(rules, rule => rule.IdentityReference.Value == administratorSid.Value &&
            rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
        Assert.Contains(rules, rule => rule.IdentityReference.Value == systemSid.Value &&
            rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindowsAclInventory(string role, string path)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        var ownerSid = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var reparsePoint = (directory.Attributes & FileAttributes.ReparsePoint) != 0;
        Console.WriteLine(
            $"Windows ACL inventory: role={role}; owner={GetSafeWindowsPrincipalLabel(ownerSid?.Value)}; " +
            $"protected={security.AreAccessRulesProtected}; reparse={reparsePoint}");
        foreach (var rule in security.GetAccessRules(
                     includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                 .Cast<FileSystemAccessRule>()
                 .OrderBy(rule => rule.IsInherited)
                 .ThenBy(rule => rule.IdentityReference.Value, StringComparer.Ordinal)
                 .ThenBy(rule => rule.AccessControlType))
        {
            Console.WriteLine(
                $"Windows ACL ACE: role={role}; principal={GetSafeWindowsPrincipalLabel(rule.IdentityReference.Value)}; " +
                $"type={rule.AccessControlType}; rights=0x{unchecked((uint)(int)rule.FileSystemRights):X8}({rule.FileSystemRights}); " +
                $"inherited={rule.IsInherited}; inheritance={rule.InheritanceFlags}; propagation={rule.PropagationFlags}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetSafeWindowsPrincipalLabel(string? sid)
    {
        var trustedInstallerSid = new System.Security.Principal.NTAccount("NT SERVICE", "TrustedInstaller")
            .Translate(typeof(SecurityIdentifier)).Value;
        if (string.Equals(sid, trustedInstallerSid, StringComparison.Ordinal)) return "NT-SERVICE\\TrustedInstaller";

        return sid switch
        {
            "S-1-5-18" => "NT-AUTHORITY\\SYSTEM",
            "S-1-5-32-544" => "BUILTIN\\Administrators",
            "S-1-5-32-545" => "BUILTIN\\Users",
            "S-1-5-11" => "NT-AUTHORITY\\Authenticated-Users",
            "S-1-1-0" => "Everyone",
            "S-1-3-0" => "CREATOR-OWNER",
            null => "unknown",
            _ => "redacted-principal"
        };
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsAccessRuleFingerprint(FileSystemAccessRule rule)
        => string.Join('|',
            rule.IdentityReference.Value,
            (int)rule.AccessControlType,
            (int)rule.FileSystemRights,
            (int)rule.InheritanceFlags,
            (int)rule.PropagationFlags,
            rule.IsInherited);

    private static async Task ServePackageAndEnrollmentAsync(TcpListener listener, byte[] archive,
        string version, string enrollmentCode, CancellationToken ct)
    {
        for (var requestNumber = 0; requestNumber < 2; requestNumber++)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, ct);
            byte[] body;
            string contentType;
            if (requestNumber == 0)
            {
                Assert.StartsWith($"GET /api/v1/client-artifacts/win-x64/{version}/onboarding-download ", request);
                Assert.Contains($"X-NetRatel-Enrollment-Code: {enrollmentCode}", request, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("X-NetRatel-Tenant-Id: 4098", request, StringComparison.OrdinalIgnoreCase);
                body = archive;
                contentType = "application/zip";
            }
            else
            {
                Assert.StartsWith("POST /api/v1/agents/enroll ", request);
                Assert.Contains($"\"enrollmentCode\":\"{enrollmentCode}\"", request);
                body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    agentId = Guid.NewGuid(), refreshToken = "synthetic-native-installer-refresh-token", expiresInDays = 30
                });
                contentType = "application/json";
            }
            var artifactHeaders = requestNumber == 0
                ? $"X-NetRatel-Artifact-Rid: win-x64\r\nX-NetRatel-Artifact-Version: {version}\r\nX-NetRatel-Artifact-Sha256: {Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()}\r\nX-NetRatel-Artifact-Size: {archive.LongLength}\r\n"
                : string.Empty;
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n{artifactHeaders}Connection: close\r\n\r\n");
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }
    }

    private static async Task<bool> ServePackageOnlyAsync(TcpListener listener, byte[] archive, string version, string enrollmentCode, CancellationToken ct)
    {
        var accepted = false;
        try
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            accepted = true;
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, ct);
            Assert.StartsWith($"GET /api/v1/client-artifacts/win-x64/{version}/onboarding-download ", request);
            Assert.Contains($"X-NetRatel-Enrollment-Code: {enrollmentCode}", request, StringComparison.OrdinalIgnoreCase);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {archive.Length}\r\nX-NetRatel-Artifact-Rid: win-x64\r\nX-NetRatel-Artifact-Version: {version}\r\nX-NetRatel-Artifact-Sha256: {Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()}\r\nX-NetRatel-Artifact-Size: {archive.LongLength}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(archive, ct);
            await stream.FlushAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return accepted;
        }
    }

    private static void AssertInstallerOutputContains(
        string expected,
        string output,
        string error,
        int exitCode,
        NativeTlsProbePair? tlsProbe = null)
    {
        var combined = string.Concat(output, Environment.NewLine, error);
        if (combined.Contains(expected, StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(expected, "outside the configured NetRatel package layout", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = Regex.Replace(combined, @"\s+", " ").Trim();
            if (normalized.Contains(UnownedServiceImageFailure, StringComparison.OrdinalIgnoreCase)) return;
        }

        var safeDiagnostic = GetSafeInstallerDiagnostic(combined, tlsProbe);
        Assert.Fail($"Expected installer output to contain '{expected}'; exit code={exitCode}; diagnostic={safeDiagnostic}");
    }

    private static void AssertInstallerOutputHasSafeText(string output, string expected, string diagnosticCode)
    {
        if (output.Contains(expected, StringComparison.Ordinal)) return;

        Assert.Fail($"Installer output omitted an expected marker; code={diagnosticCode}; diagnostic={GetSafeInstallerDiagnostic(output)}");
    }

    private static void AssertInstallerOutputLacksSafeText(string output, string unexpected, string diagnosticCode)
    {
        if (!output.Contains(unexpected, StringComparison.OrdinalIgnoreCase)) return;

        Assert.Fail($"Installer output contained a forbidden success marker; code={diagnosticCode}; diagnostic={GetSafeInstallerDiagnostic(output)}");
    }

    private static string GetSafeInstallerDiagnostic(string combined, NativeTlsProbePair? tlsProbe = null)
    {
        if (combined.Length > MaximumCombinedInstallerDiagnosticCharacters)
            combined = combined[..MaximumCombinedInstallerDiagnosticCharacters];

        var protectedPathFailure = Regex.Match(
            combined,
            @"The\s+service\s+readiness\s+path\s+could\s+not\s+be\s+securely\s+verified\s+\(phase=(?<phase>[a-z0-9-]{1,64});\s*role=(?<role>[a-z0-9-]{1,64});\s*scope=(?<scope>ancestor|leaf);\s*component=(?<component>\d{1,3});\s*reason=(?<reason>[a-z0-9-]{1,64});\s*normalization=(?<normalization>attempted|not-attempted);\s*exception=(?<exception>[A-Za-z0-9]{1,64})(?:;\s*aceRights=(?<aceRights>[A-Za-z0-9|,+-]{1,128});\s*aceRightsValue=(?<aceRightsValue>-?\d{1,10});\s*aceInherited=(?<aceInherited>true|false);\s*aceInheritance=(?<aceInheritance>[A-Za-z|]{1,64});\s*acePropagation=(?<acePropagation>[A-Za-z|]{1,64}))?\)\.",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var whitespaceNormalized = Regex.Replace(combined, @"\s+", " ").Trim();
        var downloadFailure = Regex.Match(
            combined,
            @"Download\s+failed\s+with\s+exit\s+code\s+(?<code>\d+)\.",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var powerShellFailure = GetSafePowerShellFailureDiagnostic(combined);
        var permissionDetail = protectedPathFailure.Success && protectedPathFailure.Groups["aceRights"].Success
            ? $" aceRights={protectedPathFailure.Groups["aceRights"].Value} aceRightsValue={protectedPathFailure.Groups["aceRightsValue"].Value} aceInherited={protectedPathFailure.Groups["aceInherited"].Value} aceInheritance={protectedPathFailure.Groups["aceInheritance"].Value} acePropagation={protectedPathFailure.Groups["acePropagation"].Value}"
            : string.Empty;
        return protectedPathFailure.Success
            ? $"Installer preflight failure phase={protectedPathFailure.Groups["phase"].Value} role={protectedPathFailure.Groups["role"].Value} scope={protectedPathFailure.Groups["scope"].Value} component={protectedPathFailure.Groups["component"].Value} reason={protectedPathFailure.Groups["reason"].Value} normalization={protectedPathFailure.Groups["normalization"].Value} exception={protectedPathFailure.Groups["exception"].Value}{permissionDetail}"
            : whitespaceNormalized.Contains(UnownedServiceImageFailure, StringComparison.OrdinalIgnoreCase)
                ? UnownedServiceImageFailure
                : downloadFailure.Success
                    ? $"Download failed with exit code {downloadFailure.Groups["code"].Value}; {tlsProbe?.ToSafeDiagnostic() ?? "TLS probe unavailable"}"
                    : powerShellFailure ?? tlsProbe?.ToSafeDiagnostic() ?? "no bounded installer preflight diagnostic was emitted";
    }

    private static string GetSafeSeededParentFailureEvidence(string capturedOutput)
    {
        if (capturedOutput.Length > MaximumCombinedInstallerDiagnosticCharacters)
            capturedOutput = capturedOutput[..MaximumCombinedInstallerDiagnosticCharacters];

        var installerStop = Regex.Match(
            capturedOutput,
            @"Installer stopped: phase=(?<phase>[a-z0-9-]{1,64});\s*lastCompleted=(?<lastCompleted>[a-z0-9-]{1,64});\s*serviceState=(?<serviceState>[A-Za-z -]{1,32});\s*failureType=(?<exceptionType>[A-Za-z0-9]{1,64})\.",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var handoffFailure = GetSafeHandoffFailureClassification(capturedOutput);
        if (installerStop.Success)
        {
            var serviceState = NormalizeSafeInstallerServiceState(installerStop.Groups["serviceState"].Value);
            return $"installerStop phase={installerStop.Groups["phase"].Value} " +
                $"lastCompleted={installerStop.Groups["lastCompleted"].Value} serviceState={serviceState} " +
                $"exceptionType={installerStop.Groups["exceptionType"].Value} handoffFailure={handoffFailure}";
        }

        var shellFailure = GetSafePowerShellFailureDiagnostic(capturedOutput) ?? "PowerShell failure unavailable";
        return $"installerStop summary=not-emitted serviceState=unavailable handoffFailure={handoffFailure}; {shellFailure}";
    }

    private static string GetSafeHandoffFailureClassification(string capturedOutput)
    {
        (string Marker, string Classification)[] knownHandoffFailures =
        [
            ("Installer handoff validation failed.", "validation-failed"),
            ("Installer handoff request is outside its protected directory or has an invalid name.", "request-path-rejected"),
            ("Installer handoff path contains a reparse point or unexpected object type.", "path-rejected"),
            ("Installer handoff files exceed their bounded size limits.", "files-too-large"),
            ("Installer handoff request is invalid, stale, or incomplete.", "request-invalid"),
            ("Installer handoff origin process identity is invalid.", "origin-identity-invalid"),
            ("Installer handoff origin remained alive after its bounded wait.", "origin-still-running")
        ];
        foreach (var (marker, classification) in knownHandoffFailures)
        {
            if (capturedOutput.Contains(marker, StringComparison.Ordinal)) return classification;
        }

        return "none-recognized";
    }

    private static string NormalizeSafeInstallerServiceState(string candidate) => candidate.Trim().ToLowerInvariant() switch
    {
        "not-applicable" => "not-applicable",
        "not-installed" => "not-installed",
        "unknown" => "unknown",
        "running" => "running",
        "stopped" => "stopped",
        "start pending" => "start-pending",
        "stop pending" => "stop-pending",
        "continue pending" => "continue-pending",
        "pause pending" => "pause-pending",
        "paused" => "paused",
        _ => "other"
    };

    private static string? GetSafePowerShellFailureDiagnostic(string capturedOutput)
    {
        var lineMatch = Regex.Match(
            capturedOutput,
            @"\binstall\.ps1\s*:\s*(?:line\s+)?(?<line>[1-9][0-9]{0,5})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var lineNumber = lineMatch.Success && int.TryParse(
            lineMatch.Groups["line"].Value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedLine)
            ? parsedLine.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

        string? exceptionType = null;
        using (var reader = new StringReader(capturedOutput))
        {
            while (reader.ReadLine() is { } currentLine)
            {
                var line = currentLine.Trim();
                var separator = line.IndexOf(':');
                if (separator < 0) continue;
                var label = line[..separator].Trim();
                if (!label.Equals("CategoryInfo", StringComparison.OrdinalIgnoreCase) &&
                    !label.Equals("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = line[(separator + 1)..].Trim();
                var lastToken = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (lastToken is not null && NormalizeSafePowerShellExceptionType(lastToken) is { } safeType)
                {
                    exceptionType = safeType;
                    break;
                }
            }
        }

        if (exceptionType is null)
        {
            using var reader = new StringReader(capturedOutput);
            while (reader.ReadLine() is { } currentLine)
            {
                var conciseError = Regex.Match(
                    currentLine,
                    @"^\s*(?:System\.Management\.Automation\.)?(?<type>[A-Za-z][A-Za-z0-9]+(?:Exception|Error))\s*:",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!conciseError.Success) continue;

                var candidate = conciseError.Groups["type"].Value;
                exceptionType = NormalizeSafePowerShellExceptionType(candidate);
                if (exceptionType is null) continue;
                break;
            }
        }

        var hasPowerShellFailureMarker = lineMatch.Success ||
            capturedOutput.Contains("CategoryInfo", StringComparison.OrdinalIgnoreCase) ||
            capturedOutput.Contains("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase) ||
            exceptionType is not null;
        return hasPowerShellFailureMarker
            ? $"PowerShell failure type={exceptionType ?? "unclassified"} line={lineNumber ?? "unavailable"}"
            : null;
    }

    private static string? NormalizeSafePowerShellExceptionType(string candidate)
    {
        var separator = candidate.LastIndexOf('.');
        var shortName = separator >= 0 ? candidate[(separator + 1)..] : candidate;
        return SafePowerShellExceptionTypes.FirstOrDefault(
            allowed => string.Equals(allowed, shortName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(string StandardOutput, string StandardError)> RunInstallerAndCaptureOutputAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var standardOutputTask = ReadBoundedInstallerOutputAsync(process.StandardOutput, captureCancellation.Token);
        var standardErrorTask = ReadBoundedInstallerOutputAsync(process.StandardError, captureCancellation.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return (await standardOutputTask, await standardErrorTask);
        }
        catch
        {
            captureCancellation.Cancel();
            var processStopped = await TerminateInstallerProcessAsync(process);
            if (!processStopped)
            {
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
            }

            var outputObserved = await ObserveInstallerOutputTasksAsync(standardOutputTask, standardErrorTask);
            if (!outputObserved)
            {
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
                await ObserveInstallerOutputTasksAsync(standardOutputTask, standardErrorTask);
            }

            throw;
        }
    }

    private static async Task<bool> TerminateInstallerProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return process.HasExited;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    private static async Task<bool> ObserveInstallerOutputTasksAsync(
        Task<string> standardOutputTask,
        Task<string> standardErrorTask)
    {
        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch (OperationCanceledException) when (standardOutputTask.IsCanceled || standardErrorTask.IsCanceled)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.TimeoutException)
        {
            ObserveInstallerOutputTaskOnCompletion(standardOutputTask);
            ObserveInstallerOutputTaskOnCompletion(standardErrorTask);
            return false;
        }
    }

    private static void ObserveInstallerOutputTaskOnCompletion(Task<string> task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<string> ReadBoundedInstallerOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var captured = new StringBuilder(Math.Min(MaximumCapturedInstallerStreamCharacters, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return captured.ToString();

            var remaining = MaximumCapturedInstallerStreamCharacters - captured.Length;
            if (remaining > 0)
                captured.Append(buffer, 0, Math.Min(read, remaining));
        }
    }

    private static async Task<NativeTlsProbePair> ProbeLoopbackHttpsAsync(string apiBase, CancellationToken cancellationToken)
    {
        var probeUri = new Uri(new Uri(apiBase, UriKind.Absolute), "/__netratel_native_tls_probe");
        var defaultEnvironment = await RunCurlTlsProbeAsync(probeUri, bypassProxy: false, cancellationToken);
        var directLoopback = await RunCurlTlsProbeAsync(probeUri, bypassProxy: true, cancellationToken);
        return new NativeTlsProbePair(defaultEnvironment, directLoopback);
    }

    private static async Task<NativeTlsProbeResult> RunCurlTlsProbeAsync(
        Uri probeUri,
        bool bypassProxy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("curl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--disable");
        start.ArgumentList.Add("--verbose");
        start.ArgumentList.Add("--silent");
        start.ArgumentList.Add("--show-error");
        start.ArgumentList.Add("--connect-timeout");
        start.ArgumentList.Add("5");
        start.ArgumentList.Add("--max-time");
        start.ArgumentList.Add("10");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add("NUL");
        start.ArgumentList.Add("--write-out");
        start.ArgumentList.Add("%{http_code}");
        if (bypassProxy)
        {
            start.ArgumentList.Add("--noproxy");
            start.ArgumentList.Add("*");
        }

        start.ArgumentList.Add(probeUri.AbsoluteUri);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new NativeTlsProbeResult(NativeTlsProbeClass.CurlUnavailable, null, "none", false);
        }

        if (process is null)
        {
            return new NativeTlsProbeResult(NativeTlsProbeClass.CurlUnavailable, null, "none", false);
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var outputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var standardOutputTask = ReadBoundedOutputAsync(process.StandardOutput, maximumCharacters: 128, outputCancellation.Token);
            var standardErrorTask = ReadBoundedOutputAsync(process.StandardError, maximumCharacters: 8192, outputCancellation.Token);
            var timedOut = false;
            var processTerminated = true;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                processTerminated = await TerminateProbeProcessAsync(process);
            }

            var probeOutput = await DrainProbeOutputAsync(
                process,
                standardOutputTask,
                standardErrorTask,
                outputCancellation);
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (timedOut || !processTerminated || probeOutput is null)
            {
                return new NativeTlsProbeResult(
                    NativeTlsProbeClass.TimedOut,
                    process.HasExited ? process.ExitCode : null,
                    probeOutput is null ? "none" : ParseCurlHttpStatus(probeOutput.Value.StandardOutput),
                    probeOutput is null ? false : CurlUsedEnvironmentProxy(probeOutput.Value.StandardError));
            }

            var httpStatus = ParseCurlHttpStatus(probeOutput.Value.StandardOutput);
            var proxyUsed = CurlUsedEnvironmentProxy(probeOutput.Value.StandardError);
            var classification = ClassifyCurlResult(process.ExitCode, httpStatus);
            return new NativeTlsProbeResult(classification, process.ExitCode, httpStatus, proxyUsed);
        }
    }

    private static async Task<bool> TerminateProbeProcessAsync(Process process)
    {
        if (process.HasExited) return true;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return process.HasExited;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    private static async Task<(string StandardOutput, string StandardError)?> DrainProbeOutputAsync(
        Process process,
        Task<string> standardOutputTask,
        Task<string> standardErrorTask,
        CancellationTokenSource outputCancellation)
    {
        var drained = await TryAwaitProbeReadersAsync(standardOutputTask, standardErrorTask, TimeSpan.FromSeconds(5));
        if (drained)
        {
            if (standardOutputTask.IsCompletedSuccessfully && standardErrorTask.IsCompletedSuccessfully)
            {
                return (await standardOutputTask, await standardErrorTask);
            }

            return null;
        }

        outputCancellation.Cancel();
        process.StandardOutput.Dispose();
        process.StandardError.Dispose();
        var observed = await TryAwaitProbeReadersAsync(standardOutputTask, standardErrorTask, TimeSpan.FromSeconds(1));
        if (!observed)
        {
            ObserveProbeReaderFailure(standardOutputTask);
            ObserveProbeReaderFailure(standardErrorTask);
        }

        return null;
    }

    private static async Task<bool> TryAwaitProbeReadersAsync(
        Task<string> standardOutputTask,
        Task<string> standardErrorTask,
        TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(timeout);
            return true;
        }
        catch (System.TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (standardOutputTask.IsCanceled || standardErrorTask.IsCanceled)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static void ObserveProbeReaderFailure(Task<string> task)
    {
        _ = task.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<string> ReadBoundedOutputAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[512];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return output.ToString();
            }

            if (output.Length < maximumCharacters)
            {
                output.Append(buffer, 0, Math.Min(read, maximumCharacters - output.Length));
            }
        }
    }

    private static string ParseCurlHttpStatus(string standardOutput)
    {
        var value = standardOutput.Trim();
        return IsValidHttpStatus(value) ? value : "none";
    }

    private static NativeTlsProbeClass ClassifyCurlResult(int curlExitCode, string httpStatus) =>
        curlExitCode == 35
            ? NativeTlsProbeClass.TlsHandshakeFailure
            : curlExitCode == 0 && IsValidHttpStatus(httpStatus)
                ? NativeTlsProbeClass.HttpResponse
                : NativeTlsProbeClass.TransportFailure;

    private static bool IsValidHttpStatus(string value) =>
        Regex.IsMatch(value, @"^\d{3}$", RegexOptions.CultureInvariant) &&
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var statusCode) &&
        statusCode is >= 100 and <= 599;

    private static bool CurlUsedEnvironmentProxy(string standardError) =>
        Regex.IsMatch(
            standardError,
            @"(?im)^\*\s+Uses\s+proxy\s+env\s+variable\s+(?:HTTPS?_PROXY|ALL_PROXY)\b",
            RegexOptions.CultureInvariant);

    private enum NativeTlsProbeClass
    {
        HttpResponse,
        TlsHandshakeFailure,
        TransportFailure,
        TimedOut,
        CurlUnavailable
    }

    private readonly record struct NativeTlsProbeResult(
        NativeTlsProbeClass Classification,
        int? CurlExitCode,
        string HttpStatus,
        bool ProxyUsed)
    {
        public string ToSafeDiagnostic(string name)
        {
            var exitCode = CurlExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
            return $"{name}[class={Classification},curlExit={exitCode},httpStatus={HttpStatus},proxyUsed={ProxyUsed}]";
        }
    }

    private readonly record struct NativeTlsProbePair(
        NativeTlsProbeResult DefaultEnvironment,
        NativeTlsProbeResult DirectLoopback)
    {
        public string ToSafeDiagnostic() =>
            $"{DefaultEnvironment.ToSafeDiagnostic("default")}; {DirectLoopback.ToSafeDiagnostic("direct")}";
    }

    [SupportedOSPlatform("windows")]
    private static string CreateWindowsServiceFixtureRoot(string fixtureName)
    {
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonApplicationData))
        {
            throw new InvalidOperationException("The hosted Windows runner has no CommonApplicationData directory.");
        }

        var root = Path.Combine(
            commonApplicationData,
            $"NetRatelInstallerNative-{fixtureName}-{Guid.NewGuid():N}");
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException("The unique hosted Windows fixture path already exists.");
        }

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            localSystem,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var directory = new DirectoryInfo(root);
        var createdRoot = false;
        try
        {
            directory.Create(security);
            createdRoot = true;

            var actualSecurity = directory.GetAccessControl();
            var actualOwner = actualSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var actualRules = actualSecurity
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            var allowedSids = new HashSet<string>(StringComparer.Ordinal)
            {
                administrators.Value,
                localSystem.Value
            };
            if (!actualSecurity.AreAccessRulesProtected || actualOwner?.Value != administrators.Value ||
                actualRules.Any(rule => rule.IsInherited ||
                    rule.AccessControlType != AccessControlType.Allow ||
                    !allowedSids.Contains(rule.IdentityReference.Value)) ||
                !actualRules.Any(rule => rule.IdentityReference.Value == administrators.Value &&
                    (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) ||
                !actualRules.Any(rule => rule.IdentityReference.Value == localSystem.Value &&
                    (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl))
            {
                throw new InvalidOperationException("The hosted Windows fixture directory did not receive its protected administrator and SYSTEM ACL.");
            }

            return root;
        }
        catch (Exception fixtureFailure)
        {
            if (createdRoot)
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                }
                catch (Exception cleanupFailure)
                {
                    fixtureFailure.Data["FixtureRootCleanupException"] = cleanupFailure.GetType().Name;
                }
            }

            throw;
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[4096];
        var headerEnd = -1;
        var bodyLength = 0;
        while (bytes.Count < 32 * 1024)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            if (headerEnd < 0)
            {
                headerEnd = Encoding.ASCII.GetString(bytes.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    headerEnd += 4;
                    var headers = Encoding.ASCII.GetString(bytes.ToArray(), 0, headerEnd);
                    var length = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    if (length is not null) bodyLength = int.Parse(length.Split(':', 2)[1].Trim());
                }
            }
            if (headerEnd >= 0 && bytes.Count >= headerEnd + bodyLength) break;
        }
        Assert.True(headerEnd >= 0 && bytes.Count >= headerEnd + bodyLength, "the native client sent an incomplete HTTP request");
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static X509Certificate2 CreateNativeGatewayCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: true));
        var generatedCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(2));
        if (!OperatingSystem.IsWindows()) return generatedCertificate;

        // Schannel needs the server key in a Windows key container; keep it temporary by omitting PersistKeySet.
        var passwordBytes = RandomNumberGenerator.GetBytes(32);
        var password = Convert.ToBase64String(passwordBytes);
        byte[]? pkcs12 = null;
        try
        {
            pkcs12 = generatedCertificate.Export(X509ContentType.Pfx, password);
            return X509CertificateLoader.LoadPkcs12(pkcs12, password, X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            if (pkcs12 is not null) CryptographicOperations.ZeroMemory(pkcs12);
            CryptographicOperations.ZeroMemory(passwordBytes);
            generatedCertificate.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddLocalMachineTrust(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveLocalMachineTrust(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var installed in store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false))
        {
            store.Remove(installed);
            installed.Dispose();
        }
    }

    private static async Task<ClientPresenceSnapshot> WaitForPresenceAsync(
        IClientPresenceRouter presence,
        ClientKey client,
        long minimumEpoch,
        CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (true)
            {
                var snapshot = await presence.GetSnapshotAsync(client, linked.Token);
                if (snapshot.Status == ClientPresenceStatus.Online &&
                    snapshot.ConnectionEpoch.HasValue && snapshot.ConnectionEpoch.Value >= minimumEpoch &&
                    snapshot.LastAcceptedSequence >= 2)
                {
                    return snapshot;
                }

                if (!await timer.WaitForNextTickAsync(linked.Token)) break;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new System.TimeoutException("The real gateway presence actor did not acknowledge two heartbeats before the test deadline.");
        }

        throw new System.TimeoutException("The real gateway presence actor did not acknowledge two heartbeats before the test deadline.");
    }

    private static async Task AssertGatewayTokenRejectedAsync(
        string apiBase,
        string token,
        int tenantId,
        Guid agentId,
        StatusCode expectedStatus,
        CancellationToken ct)
    {
        using var channel = GrpcChannel.ForAddress(apiBase);
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {token}" } };
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var call = client.Connect(headers, cancellationToken: ct);
            await call.RequestStream.WriteAsync(new AgentFrame
            {
                ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
                TenantId = tenantId,
                ClientId = agentId.ToString("D"),
                ConnectionId = Guid.NewGuid().ToString("D"),
                OperationId = Guid.NewGuid().ToString("D"),
                Hello = new ConnectHello { AgentVersion = "native-auth-negative" }
            });
            await call.RequestStream.CompleteAsync();

            while (await call.ResponseStream.MoveNext(ct)) { }
        });
        Assert.Equal(expectedStatus, exception.StatusCode);
    }

    private static WebApplication BuildNativeGatewayHost(
        X509Certificate2 certificate,
        NativeGatewayHostFixture fixture)
    {
        const string authorizationPolicy = "AgentGatewayAccess";
        var options = new NetRatelAkkaOptions
        {
            ActorSystemName = $"NetRatelNative-{Guid.NewGuid():N}",
            HeartbeatIntervalSeconds = 5,
            MissedHeartbeatLimit = 4,
            HeartbeatGraceSeconds = 5,
            AskTimeoutSeconds = 5
        };
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, endpoint =>
        {
            endpoint.Protocols = HttpProtocols.Http1AndHttp2;
            endpoint.UseHttps(certificate);
        }));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICommandPersistenceStore>(new TestCommandPersistenceStore());
        builder.Services.AddSingleton<IJobObservationStore>(new UnusedJobObservationStore());
        builder.Services.AddSingleton<IRemoteSupportLifecycleStore>(new UnusedRemoteSupportLifecycleStore());
        builder.Services.AddNetRatelAkkaActors(options.ActorSystemName);
        builder.Services.AddSingleton<IAgentManagementService>(fixture.AgentStore);
        builder.Services.AddSingleton<IClientUpdateCatalog, EmptyNativeClientUpdateCatalog>();
        builder.Services.AddDbContext<OrchestratorDbContext>(db => db.UseInMemoryDatabase($"native-gateway-{Guid.NewGuid():N}"));
        builder.Services.AddSingleton<IClientUpdateActivationAuthority>(fixture);
        builder.Services.AddAuthentication("Agent")
            .AddJwtBearer("Agent", auth =>
            {
                auth.MapInboundClaims = false;
                auth.RequireHttpsMetadata = true;
                auth.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = NativeGatewayHostFixture.Issuer,
                    ValidateAudience = true,
                    ValidAudience = NativeGatewayHostFixture.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = fixture.ValidationSigningKey,
                    ValidateLifetime = true,
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(5),
                    NameClaimType = "sub",
                    RoleClaimType = "role"
                };
            });
        builder.Services.AddAuthorization(auth => auth.AddPolicy(authorizationPolicy, policy =>
        {
            policy.AddAuthenticationSchemes("Agent");
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => AgentGatewayIdentityResolver.TryResolve(context.User, out _, out _));
        }));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/v1/client-artifacts/win-x64/{version}/onboarding-download",
            (HttpContext context) => fixture.DownloadArtifactAsync(context));
        app.MapPost("/api/v1/agents/enroll", (HttpContext context) => fixture.EnrollAsync(context));
        app.MapPost("/api/v1/agents/token", (HttpContext context) => fixture.ExchangeTokenAsync(context));
        app.MapGrpcService<AgentGatewayService>().RequireAuthorization(authorizationPolicy);
        return app;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new System.TimeoutException("The hosted Windows installer condition was not observed before the timeout.");
            }

            await timer.WaitForNextTickAsync(ct);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunWindowsUpdaterAsync(
        string powerShellPath,
        string installRoot,
        string stateDirectory,
        string requestPath,
        CancellationToken cancellationToken)
    {
        var startInfo = AkkaClientAutoUpdateCoordinator.CreateWindowsUpdaterStartInfo(
            powerShellPath, installRoot, stateDirectory, requestPath);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the supported Windows updater launcher.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static Task WriteNativeUpdaterRequestAsync(
        string requestPath,
        Guid attemptId,
        Guid releaseId,
        string admissionNonce,
        string runtimeId,
        string fromVersion,
        string toVersion,
        string packagePath,
        string sha256,
        string readyPath,
        string stateDirectory,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
        {
            schema = "netratel.update.request.v2",
            attemptId,
            releaseId,
            admissionNonce,
            runtimeId,
            fromVersion,
            currentVersion = fromVersion,
            toVersion,
            version = toVersion,
            packagePath = Path.GetFullPath(packagePath),
            sha256,
            readyPath = Path.GetFullPath(readyPath),
            presencePath = Path.Combine(stateDirectory, "presence.json"),
            resultPath = Path.Combine(stateDirectory, "result.json"),
            logPath = (string?)null,
            createdAtUtc = DateTimeOffset.UtcNow
        }), cancellationToken);

    private static byte[] CreateNativeClientPackageWithExecutable(string version, string runtimeId, byte[] executableBytes)
    {
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("netratel-client-manifest.json");
            using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(JsonSerializer.Serialize(new
                {
                    schema = "netratel.client.manifest.v1",
                    product = "NetRatel.Client",
                    version,
                    runtimeId,
                    commitSha = new string('a', 40),
                    executable = "NetRatel.Client.exe"
                }));
            }

            var executable = archive.CreateEntry("NetRatel.Client.exe");
            using var stream = executable.Open();
            stream.Write(executableBytes);
        }

        return package.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static string[] ReadWindowsServiceEnvironment(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}");
        return key?.GetValue("Environment") as string[] ?? [];
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindowsServiceImagePath(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}");
        return key?.GetValue("ImagePath") as string ?? throw new InvalidOperationException("Service ImagePath was not registered.");
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindowsServiceStartName(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}");
        return key?.GetValue("ObjectName") as string ?? throw new InvalidOperationException("Service logon identity was not registered.");
    }

    [SupportedOSPlatform("windows")]
    private static int ReadWindowsServiceStartType(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}");
        return (int)(key?.GetValue("Start") ?? throw new InvalidOperationException("Service startup type was not registered."));
    }

    [SupportedOSPlatform("windows")]
    private static int ReadWindowsServiceProcessId(string serviceName)
    {
        using var query = Process.Start(new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "queryex", serviceName }
        }) ?? throw new InvalidOperationException("Could not start sc.exe to read the service process ID.");
        var outputTask = query.StandardOutput.ReadToEndAsync();
        var errorTask = query.StandardError.ReadToEndAsync();
        if (!query.WaitForExit(10_000))
        {
            query.Kill(entireProcessTree: true);
            query.WaitForExit();
            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            throw new System.TimeoutException("sc.exe did not return the NetRatel service process ID.");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        Assert.True(query.ExitCode == 0, $"sc.exe could not query the NetRatel service: {error}");
        var match = Regex.Match(output, @"(?im)^\s*PID\s*:\s*(?<pid>[0-9]+)\s*$");
        Assert.True(match.Success, $"sc.exe did not report a service PID: {output}");
        return int.Parse(match.Groups["pid"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsService(string serviceName, string imagePath)
    {
        using var create = Process.Start(new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "create", serviceName, "binPath=", imagePath, "start=", "demand" }
        }) ?? throw new InvalidOperationException("Could not start sc.exe.");
        create.WaitForExit();
        Assert.Equal(0, create.ExitCode);
    }

    [SupportedOSPlatform("windows")]
    private static void StopWindowsService(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status == ServiceControllerStatus.Stopped) return;
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The service was not installed or was removed during an installer failure.
            Debug.WriteLine($"Windows service '{serviceName}' was already absent during cleanup: {exception.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StartWindowsService(string serviceName)
    {
        using var service = new ServiceController(serviceName);
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertProtectedServiceReadinessRequest(string stateDirectory)
    {
        var requestPath = Path.Combine(stateDirectory, "install-readiness", "request.json");
        var requestFile = new FileInfo(requestPath);
        Assert.True(requestFile.Exists, "the installer must persist the current service-readiness challenge");

        var security = requestFile.GetAccessControl();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var trustedInstaller = new NTAccount("NT SERVICE", "TrustedInstaller")
            .Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
        Assert.NotNull(trustedInstaller);
        var allowedSids = new HashSet<string>(StringComparer.Ordinal)
        {
            administrators.Value,
            localSystem.Value,
            trustedInstaller!.Value
        };
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.True(security.AreAccessRulesProtected,
            "the published readiness request must not inherit access rules");
        Assert.Equal(administrators.Value, owner?.Value);
        Assert.Equal(allowedSids.Count, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Contains(rule.IdentityReference.Value, allowedSids);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        });
        Assert.All(allowedSids, sid => Assert.Contains(rules, rule => rule.IdentityReference.Value == sid));

        using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
        Assert.Equal("netratel.install-readiness.request.v1", request.RootElement.GetProperty("schema").GetString());
        Assert.True(Guid.TryParse(request.RootElement.GetProperty("attemptId").GetString(), out var attemptId));
        Assert.NotEqual(Guid.Empty, attemptId);
        Assert.Equal(32, Convert.FromBase64String(request.RootElement.GetProperty("nonce").GetString()!).Length);
        var requestedAt = request.RootElement.GetProperty("requestedAtUtc").GetDateTimeOffset();
        var expiresAt = request.RootElement.GetProperty("expiresAtUtc").GetDateTimeOffset();
        Assert.True(requestedAt <= DateTimeOffset.UtcNow);
        Assert.True(expiresAt > requestedAt);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertSystemServiceReadinessResponse(string stateDirectory)
    {
        var readinessDirectory = Path.Combine(stateDirectory, "install-readiness");
        var requestPath = Path.Combine(readinessDirectory, "request.json");
        var readyFile = new FileInfo(Path.Combine(readinessDirectory, "ready.json"));
        Assert.True(readyFile.Exists, "the LocalSystem service must publish its current readiness response");

        var security = readyFile.GetAccessControl();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var trustedInstaller = new NTAccount("NT SERVICE", "TrustedInstaller")
            .Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
        Assert.NotNull(trustedInstaller);
        var allowedSids = new HashSet<string>(StringComparer.Ordinal)
        {
            administrators.Value,
            localSystem.Value,
            trustedInstaller!.Value
        };
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.Equal(localSystem.Value, owner?.Value);
        Assert.Equal(allowedSids.Count, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.True(rule.IsInherited, "the SYSTEM-created response must inherit only the protected readiness-directory ACL");
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Contains(rule.IdentityReference.Value, allowedSids);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        });
        Assert.All(allowedSids, sid => Assert.Contains(rules, rule => rule.IdentityReference.Value == sid));

        using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
        using var ready = JsonDocument.Parse(File.ReadAllText(readyFile.FullName));
        Assert.Equal(request.RootElement.GetProperty("attemptId").GetString(), ready.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(request.RootElement.GetProperty("nonce").GetString(), ready.RootElement.GetProperty("nonce").GetString());
        Assert.Equal("S-1-5-18", ready.RootElement.GetProperty("userSid").GetString());
        Assert.Equal(0, ready.RootElement.GetProperty("sessionId").GetInt32());
        Assert.Equal(ReadWindowsServiceProcessId("NetRatel.Client"), ready.RootElement.GetProperty("processId").GetInt32());
    }

    private static string GetSeededWindowsScript()
    {
        var seedField = typeof(AgentUpdateScriptSeedService).GetField("WindowsScript", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The API-seeded Windows installer script could not be resolved.");
        var seed = seedField.GetValue(null)
            ?? throw new InvalidOperationException("The API-seeded Windows installer script is unavailable.");
        var contentProperty = seed.GetType().GetProperty("Content", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The API-seeded Windows installer script content could not be resolved.");
        var seededContent = (string)contentProperty.GetValue(seed)!;
        const string manifestBoundary = "#| END";
        var manifestEnd = seededContent.IndexOf(manifestBoundary, StringComparison.Ordinal);
        Assert.True(manifestEnd >= 0, "the seeded PowerShell script must contain its manifest boundary");
        Assert.Equal(manifestEnd, seededContent.LastIndexOf(manifestBoundary, StringComparison.Ordinal));
        return seededContent[(manifestEnd + manifestBoundary.Length)..].TrimStart('\r', '\n');
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsServiceAbsent(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}");
        Assert.Null(key);
    }

    [SupportedOSPlatform("windows")]
    private static bool RemoveWindowsServiceIfRegisteredImagePathEquals(string serviceName, string expectedImagePath)
    {
        using (var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}"))
        {
            if (key is null) return true;
            var currentImagePath = key.GetValue("ImagePath") as string;
            if (!RegisteredImagePathEquals(currentImagePath, expectedImagePath)) return false;
        }

        using (var service = new ServiceController(serviceName))
        {
            if (service.Status != ServiceControllerStatus.Stopped) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            Assert.Equal(ServiceControllerStatus.Stopped, service.Status);
        }

        if (!RegisteredImagePathEquals(ReadWindowsServiceImagePath(serviceName), expectedImagePath)) return false;

        using var delete = Process.Start(new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "delete", serviceName }
        }) ?? throw new InvalidOperationException("Could not start sc.exe to remove the service created by this test.");
        delete.WaitForExit();
        Assert.Equal(0, delete.ExitCode);
        return true;
    }

    private static bool RegisteredImagePathEquals(string? currentImagePath, string expectedImagePath)
        => string.Equals(currentImagePath, expectedImagePath, StringComparison.OrdinalIgnoreCase);

    private static string GetExpectedServiceImagePath(string executablePath)
        => $"\"{Path.GetFullPath(executablePath)}\" --service";

    private static bool ServiceExecutableMatches(string? currentImagePath, string expectedExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(currentImagePath)) return false;
        var currentExecutable = ExtractWindowsExecutablePath(currentImagePath);
        var expectedExecutable = Path.GetFullPath(expectedExecutablePath);
        return string.Equals(Path.GetFullPath(currentExecutable), expectedExecutable, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractWindowsExecutablePath(string imagePath)
    {
        var trimmed = imagePath.TrimStart();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1) return string.Empty;
            return trimmed[1..closingQuote];
        }

        var separator = trimmed.IndexOf(' ');
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private static void DeleteCredentialFiles(string credentialDirectory)
    {
        foreach (var fileName in new[] { "agent.dat", ".netratel-credential-machine-id" })
        {
            var path = Path.Combine(credentialDirectory, fileName);
            if (File.Exists(path)) File.Delete(path);
        }

        if (Directory.Exists(credentialDirectory) && !Directory.EnumerateFileSystemEntries(credentialDirectory).Any())
        {
            Directory.Delete(credentialDirectory);
        }
    }

    private sealed class NativeGatewayHostFixture : IDisposable, IClientUpdateActivationAuthority
    {
        private readonly ECDsa _jwtSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly bool _agentDisabled;
        private readonly object _activationGate = new();
        private byte[] _artifactBytes = [];
        private string _artifactVersion = string.Empty;
        private string _artifactSha256 = string.Empty;
        private string? _devicePublicKey;
        private int _downloadRequests;
        private int _enrollmentRequests;
        private int _tokenRequests;
        private NativeUpdateActivationTicket? _activationTicket;

        public NativeGatewayHostFixture(int tenantId, Guid agentId, string enrollmentCode, bool agentDisabled = false)
        {
            TenantId = tenantId;
            AgentId = agentId;
            EnrollmentCode = enrollmentCode;
            _agentDisabled = agentDisabled;
            AgentStore = new NativeActiveAgentStore(tenantId, agentId);
            ValidationSigningKey = new ECDsaSecurityKey(_jwtSigningKey) { KeyId = SigningKeyId };
        }

        public const string RefreshToken = "native-system-gateway-refresh-token";
        public const string Issuer = "https://netratel-native-gateway.example";
        public const string Audience = "netratel-agent";
        private const string SigningKeyId = "netratel-native-gateway-key";
        public int TenantId { get; }
        public Guid AgentId { get; }
        public string EnrollmentCode { get; }
        public NativeActiveAgentStore AgentStore { get; }
        public SecurityKey ValidationSigningKey { get; }
        public int DownloadRequests => Volatile.Read(ref _downloadRequests);
        public int EnrollmentRequests => Volatile.Read(ref _enrollmentRequests);
        public int TokenRequests => Volatile.Read(ref _tokenRequests);
        public ConcurrentQueue<string> TokenAgentIds { get; } = new();
        public ConcurrentQueue<string> RefreshTokens { get; } = new();
        public ConcurrentQueue<NativeUpdateActivationObservation> ActivationReadmissions { get; } = new();
        public ConcurrentQueue<NativeUpdateActivationObservation> ActivationConfirmations { get; } = new();

        public void ArmUpdateActivation(Guid attemptId, Guid releaseId, string admissionNonce, string targetVersion,
            bool rejectReadmission = false)
        {
            if (attemptId == Guid.Empty || releaseId == Guid.Empty || admissionNonce.Length != 64 ||
                admissionNonce.Any(character => !Uri.IsHexDigit(character)) || string.IsNullOrWhiteSpace(targetVersion))
            {
                throw new ArgumentException("The native update activation ticket is invalid.");
            }

            lock (_activationGate)
            {
                _activationTicket = new NativeUpdateActivationTicket(
                    attemptId, releaseId, admissionNonce, targetVersion, rejectReadmission);
            }
        }

        public Task<ClientUpdateActivationResult> MarkReadmittedAsync(
            AuthenticatedAgentIdentity identity,
            Guid attemptId,
            Guid releaseId,
            string nonce,
            string agentVersion,
            Guid connectionId,
            long connectionEpoch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_activationGate)
            {
                var ticket = _activationTicket;
                var reason = ticket is null ? "attempt_not_found" :
                    identity.TenantId != TenantId || identity.AgentId != AgentId ? "agent_mismatch" :
                    attemptId != ticket.AttemptId ? "attempt_mismatch" :
                    releaseId != ticket.ReleaseId ? "release_mismatch" :
                    !FixedTimeEquals(ticket.AdmissionNonce, nonce) ? "nonce_mismatch" :
                    !string.Equals(RemoveBuildMetadata(agentVersion), RemoveBuildMetadata(ticket.TargetVersion), StringComparison.OrdinalIgnoreCase)
                        ? "version_mismatch" :
                    connectionId == Guid.Empty || connectionEpoch <= 0 ? "connection_mismatch" :
                    ticket.RejectReadmission ? "native_fixture_rejected" :
                    ticket.ConfirmationId.HasValue ? "attempt_already_confirmed" :
                    null;
                if (reason is not null)
                {
                    ActivationReadmissions.Enqueue(new(attemptId, releaseId, identity.TenantId, identity.AgentId,
                        connectionId, connectionEpoch, false, reason));
                    return Task.FromResult(new ClientUpdateActivationResult(false, reason));
                }

                ticket!.ConnectionId = connectionId;
                ticket.ConnectionEpoch = connectionEpoch;
                ticket.Readmitted = true;
                ActivationReadmissions.Enqueue(new(attemptId, releaseId, identity.TenantId, identity.AgentId,
                    connectionId, connectionEpoch, true, "accepted"));
                return Task.FromResult(new ClientUpdateActivationResult(true, "accepted"));
            }
        }

        public Task<ClientUpdateActivationResult> ConfirmAsync(
            AuthenticatedAgentIdentity identity,
            Guid attemptId,
            Guid connectionId,
            long connectionEpoch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_activationGate)
            {
                var ticket = _activationTicket;
                var reason = ticket is null ? "attempt_not_found" :
                    identity.TenantId != TenantId || identity.AgentId != AgentId ? "agent_mismatch" :
                    attemptId != ticket.AttemptId ? "attempt_mismatch" :
                    !ticket.Readmitted ? "not_readmitted" :
                    ticket.ConnectionId != connectionId ? "presence_connection_mismatch" :
                    ticket.ConnectionEpoch != connectionEpoch ? "presence_epoch_mismatch" :
                    ticket.ConfirmationId.HasValue ? "already_confirmed" :
                    null;
                if (reason is not null)
                {
                    ActivationConfirmations.Enqueue(new(attemptId, ticket?.ReleaseId ?? Guid.Empty,
                        identity.TenantId, identity.AgentId, connectionId, connectionEpoch, false, reason));
                    return Task.FromResult(new ClientUpdateActivationResult(false, reason));
                }

                ticket!.ConfirmationId = Guid.NewGuid();
                ActivationConfirmations.Enqueue(new(attemptId, ticket.ReleaseId, identity.TenantId, identity.AgentId,
                    connectionId, connectionEpoch, true, "accepted"));
                return Task.FromResult(new ClientUpdateActivationResult(true, "accepted", ticket.ConfirmationId));
            }
        }

        private static string RemoveBuildMetadata(string value)
        {
            var separator = value.IndexOf('+');
            return separator >= 0 ? value[..separator] : value;
        }

        private static bool FixedTimeEquals(string expected, string actual)
        {
            var expectedBytes = Encoding.ASCII.GetBytes(expected);
            var actualBytes = Encoding.ASCII.GetBytes(actual);
            return expectedBytes.Length == actualBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        public void SetArtifact(byte[] bytes, string version, string sha256)
        {
            _artifactBytes = bytes;
            _artifactVersion = version;
            _artifactSha256 = sha256;
        }

        public async Task DownloadArtifactAsync(HttpContext context)
        {
            if (!string.Equals(context.Request.RouteValues["version"]?.ToString(), _artifactVersion, StringComparison.Ordinal) ||
                !string.Equals(context.Request.Headers["X-NetRatel-Tenant-Id"].ToString(), TenantId.ToString(), StringComparison.Ordinal) ||
                !string.Equals(context.Request.Headers["X-NetRatel-Enrollment-Code"].ToString(), EnrollmentCode, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            Interlocked.Increment(ref _downloadRequests);
            context.Response.Headers["X-NetRatel-Artifact-Rid"] = "win-x64";
            context.Response.Headers["X-NetRatel-Artifact-Version"] = _artifactVersion;
            context.Response.Headers["X-NetRatel-Artifact-Sha256"] = _artifactSha256;
            context.Response.Headers["X-NetRatel-Artifact-Size"] = _artifactBytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.ContentType = "application/zip";
            await context.Response.Body.WriteAsync(_artifactBytes, context.RequestAborted);
        }

        public async Task EnrollAsync(HttpContext context)
        {
            Interlocked.Increment(ref _enrollmentRequests);
            var body = await ReadBodyAsync(context);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var enrollmentCode = root.GetProperty("enrollmentCode").GetString() ?? string.Empty;
            var publicKey = root.GetProperty("publicKey").GetString() ?? string.Empty;
            var keyAlgorithm = root.GetProperty("keyAlgorithm").GetString() ?? string.Empty;
            var deviceInfoJson = root.GetProperty("deviceInfoJson").GetString();
            var scopes = root.GetProperty("requestedScopes").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
            var bodyHash = PopSignatureService.ComputeEnrollmentBodyHash(
                enrollmentCode, publicKey, keyAlgorithm, deviceInfoJson, scopes);
            if (!string.Equals(enrollmentCode, EnrollmentCode, StringComparison.Ordinal) ||
                !VerifyPop(context.Request, "/api/v1/agents/enroll", bodyHash, publicKey, keyAlgorithm))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            _devicePublicKey = publicKey;
            AgentStore.MarkEnrolled();
            await context.Response.WriteAsJsonAsync(new
            {
                agentId = AgentId.ToString("D"),
                refreshToken = RefreshToken,
                expiresInDays = 30
            }, context.RequestAborted);
        }

        public async Task ExchangeTokenAsync(HttpContext context)
        {
            var body = await ReadBodyAsync(context);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var agentIdText = root.GetProperty("agentId").GetString() ?? string.Empty;
            var refreshToken = root.GetProperty("refreshToken").GetString() ?? string.Empty;
            var scopes = root.GetProperty("requestedScopes").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
            if (!AgentStore.IsEnrolled || !string.Equals(agentIdText, AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(refreshToken, RefreshToken, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(_devicePublicKey) ||
                !Guid.TryParse(agentIdText, out var parsedAgentId) ||
                !VerifyPop(context.Request, "/api/v1/agents/token",
                    PopSignatureService.ComputeTokenBodyHash(parsedAgentId, refreshToken, scopes),
                    _devicePublicKey, "ecdsa-p256"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            TokenAgentIds.Enqueue(agentIdText);
            RefreshTokens.Enqueue(refreshToken);
            Interlocked.Increment(ref _tokenRequests);
            if (_agentDisabled)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Agent disabled",
                    detail = "synthetic disabled state",
                    code = "agent_disabled"
                }, context.RequestAborted);
                return;
            }

            await context.Response.WriteAsJsonAsync(new
            {
                accessToken = CreateAccessToken(),
                expiresIn = 3600,
                refreshToken
            }, context.RequestAborted);
        }

        public string CreateAccessToken(bool includeRole = true, ECDsa? signingKey = null)
        {
            signingKey ??= _jwtSigningKey;
            var claims = new List<Claim>
            {
                new("sub", AgentId.ToString("D")),
                new("agent_id", AgentId.ToString("D")),
                new("tenant_id", TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("scope", "netratel:connect")
            };
            if (includeRole) claims.Add(new Claim("role", "agent"));
            var key = new ECDsaSecurityKey(signingKey) { KeyId = SigningKeyId };
            var credentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                Issuer,
                Audience,
                claims,
                now.AddSeconds(-1),
                now.AddHours(1),
                credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private static async Task<string> ReadBodyAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadToEndAsync(context.RequestAborted);
        }

        private static bool VerifyPop(HttpRequest request, string path, string bodyHash, string publicKey, string keyAlgorithm)
        {
            var timestampText = request.Headers["X-NetRatel-Timestamp"].ToString();
            var nonce = request.Headers["X-NetRatel-Nonce"].ToString();
            var signature = request.Headers["X-NetRatel-Signature"].ToString();
            if (!DateTimeOffset.TryParse(timestampText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp) ||
                Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalMinutes) > 5 ||
                string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            var message = PopSignatureService.BuildSigningMessage("POST", path, timestamp, nonce, bodyHash);
            return PopSignatureService.VerifySignature(keyAlgorithm, publicKey, signature, message);
        }

        public void Dispose() => _jwtSigningKey.Dispose();
    }

    private sealed class NativeActiveAgentStore(int tenantId, Guid agentId) : IAgentManagementService
    {
        private int _isEnrolled;
        private int _admissionCalls;
        private int _firstAdmissionObserved;
        private int _firstAdmissionObservationCount;

        public bool IsEnrolled => Volatile.Read(ref _isEnrolled) != 0;
        public int AdmissionCalls => Volatile.Read(ref _admissionCalls);
        public int FirstAdmissionObservations => Volatile.Read(ref _firstAdmissionObservationCount);
        public Action? FirstAdmissionObserver { get; set; }
        public ConcurrentQueue<string> FirstAdmissionObservationFailures { get; } = new();
        public Action? AdmissionObserver { get; set; }
        public ConcurrentQueue<string> AdmissionObservationFailures { get; } = new();
        public void MarkEnrolled() => Interlocked.Exchange(ref _isEnrolled, 1);

        public Task<AgentDetailDto?> GetAsync(int requestedTenantId, Guid requestedAgentId, CancellationToken ct)
        {
            Interlocked.Increment(ref _admissionCalls);
            var observer = FirstAdmissionObserver;
            if (observer is not null && Interlocked.Exchange(ref _firstAdmissionObserved, 1) == 0)
            {
                try
                {
                    observer();
                    Interlocked.Increment(ref _firstAdmissionObservationCount);
                }
                catch (Exception exception) { FirstAdmissionObservationFailures.Enqueue(exception.GetType().Name); }
            }
            var admissionObserver = AdmissionObserver;
            if (admissionObserver is not null)
            {
                try { admissionObserver(); }
                catch (Exception exception) { AdmissionObservationFailures.Enqueue(exception.GetType().Name); }
            }
            AgentDetailDto? agent = IsEnrolled && requestedTenantId == tenantId && requestedAgentId == agentId
                ? new AgentDetailDto(tenantId, agentId, "Hosted SYSTEM gateway agent", true, null,
                    DateTimeOffset.UtcNow, "native-fixture", null, null, null)
                : null;
            return Task.FromResult(agent);
        }

        public Task<AgentListResponse> ListAsync(int requestedTenantId, AgentListQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task DisableAsync(int requestedTenantId, Guid requestedAgentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task EnableAsync(int requestedTenantId, Guid requestedAgentId, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(int requestedTenantId, Guid requestedAgentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class EmptyNativeClientUpdateCatalog : IClientUpdateCatalog
    {
        public ClientUpdateOfferSnapshot? GetOffer(int tenantId, Guid agentId, string runtimeId, string currentVersion, string channel) => null;
        public ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision) => new(0, false, false, false, null);
        public long Revision => 0;
        public DateTimeOffset RefreshedAtUtc => DateTimeOffset.UtcNow;
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NativeUpdateActivationTicket(
        Guid attemptId,
        Guid releaseId,
        string admissionNonce,
        string targetVersion,
        bool rejectReadmission)
    {
        public Guid AttemptId { get; } = attemptId;
        public Guid ReleaseId { get; } = releaseId;
        public string AdmissionNonce { get; } = admissionNonce;
        public string TargetVersion { get; } = targetVersion;
        public bool RejectReadmission { get; } = rejectReadmission;
        public Guid? ConnectionId { get; set; }
        public long? ConnectionEpoch { get; set; }
        public Guid? ConfirmationId { get; set; }
        public bool Readmitted { get; set; }
    }

    private sealed record NativeUpdateActivationObservation(
        Guid AttemptId,
        Guid ReleaseId,
        int TenantId,
        Guid AgentId,
        Guid ConnectionId,
        long ConnectionEpoch,
        bool Accepted,
        string Reason);

    private sealed class UnusedJobObservationStore : IJobObservationStore
    {
        public Task<JobObservationWriteResult> RecordAsync(IJobObservation observation, CancellationToken cancellationToken) =>
            Task.FromResult(new JobObservationWriteResult(observation.JobRunId, JobObservationWriteDisposition.Stored,
                JobCommandCorrelationStatus.NotProvided, null));
        public Task<IReadOnlyList<PersistedJobObservation>> ReplayAsync(ulong jobRunId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersistedJobObservation>>([]);
        public Task<JobObservationDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new JobObservationDiagnostics(0, 0, 0, 0, 0, null, null, "native-test", "akka"));
        public void RecordRecoverySucceeded() { }
    }

    private sealed class UnusedRemoteSupportLifecycleStore : IRemoteSupportLifecycleStore
    {
        public Task<RemoteSupportLifecycleOpenResult> OpenAsync(RemoteSupportOpenSessionCommand command, Guid remoteSupportSessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteSupportSessionSnapshot?> LoadAsync(RemoteSupportSessionKey session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteSupportLifecycleTransitionResult> TransitionAsync(RemoteSupportLifecycleTransition transition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteSupportAuditEvent>> ReadAuditAsync(RemoteSupportSessionKey session, long afterAuditSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

}
