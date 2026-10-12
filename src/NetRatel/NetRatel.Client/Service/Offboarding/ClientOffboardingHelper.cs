using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Auth;
using NetRatel.Shared.Client;

namespace NetRatel.Client.Service.Offboarding;

/// <summary>Runs before normal startup, without enrollment, presence, logging initialization, or an actor system.</summary>
public static class ClientOffboardingHelper
{
    public const string Command = "--offboarding-helper";

    public static async Task<int> RunAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!OffboardingState.HasNativePrivileges || !Guid.TryParse(operationId, out var id) || id == Guid.Empty) return 2;
        try
        {
            await OffboardingState.InitializeWindowsTrustAsync(cancellationToken).ConfigureAwait(false);
            using var operation = AcquireOperationLock(id);
            var request = OffboardingState.Read<OffboardingRequest>(OffboardingState.RequestPath(id));
            if (request is null || request.OperationId != id) return 2;
            ValidateRequestPaths(request);
            var executable = Environment.ProcessPath ?? throw new IOException("offboarding_helper_path_unknown");
            if (!ClientOffboardingCoordinator.PathsEqual(executable, Path.Combine(OffboardingState.DirectoryFor(id), OffboardingState.HelperName)) ||
                !string.Equals(HashFile(executable), request.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("offboarding_helper_identity_mismatch");
            if (DateTimeOffset.UtcNow >= request.ReceiptDeadlineUtc)
                return await RetainExpiredJournalAsync(request, cancellationToken).ConfigureAwait(false);
            // A previous helper may already have removed the service. A persisted result
            // is authoritative for this operation and is resent without repeating removal.
            var result = await EnsureResultAsync(request, OffboardingState.Read<OffboardingResult>(OffboardingState.ResultPath(id)),
                RemoveAsync, (value, token) => OffboardingState.WriteAsync(OffboardingState.ResultPath(id), value, token),
                cancellationToken).ConfigureAwait(false);

            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            using var channel = GrpcChannel.ForAddress(request.GatewayEndpoint, new GrpcChannelOptions { HttpHandler = handler });
            var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentControlGateway.AgentControlGatewayClient(channel);
            var delay = TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < request.ReceiptDeadlineUtc)
            {
                try
                {
                    var acknowledgement = await client.CompleteUninstallAsync(CreateReceipt(request, result),
                        deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
                    if (acknowledgement.Accepted) return 0;
                    Console.Error.WriteLine("Offboarding completion was not accepted; retrying the persisted result until its deadline.");
                }
                catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or
                    StatusCode.Internal or StatusCode.ResourceExhausted or StatusCode.Cancelled && !cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Offboarding completion delivery is pending; retrying the persisted result.");
                }
                var remaining = request.ReceiptDeadlineUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var wait = TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, delay.TotalMilliseconds * (1 + Random.Shared.NextDouble() * .1)));
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
            }
            return await RetainExpiredJournalAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (OffboardingState.IsExpectedFailure(exception) || exception is RpcException)
        {
            Console.Error.WriteLine($"Offboarding helper did not complete ({exception.GetType().Name}); its journal is retained.");
            return 1;
        }
    }

    private static async Task<int> RetainExpiredJournalAsync(OffboardingRequest request, CancellationToken cancellationToken)
    {
        // Reload the journal to preserve removal progress written before a crash.
        var current = OffboardingState.Read<OffboardingRequest>(OffboardingState.RequestPath(request.OperationId)) ?? request;
        await OffboardingState.WriteAsync(OffboardingState.RequestPath(request.OperationId), current with { CompletionToken = string.Empty }, cancellationToken).ConfigureAwait(false);
        Console.Error.WriteLine("Offboarding completion delivery expired; the operation journal is retained.");
        return 2;
    }

    internal static FileStream AcquireOperationLock(Guid operationId)
    {
        var path = Path.Combine(OffboardingState.DirectoryFor(operationId), "operation.lock");
        OffboardingState.AssertProtectedPath(Path.GetDirectoryName(path)!, privateLeaf: true);
        if (File.Exists(path)) OffboardingState.AssertProtectedPath(path, privateLeaf: true);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    internal static async Task<OffboardingResult> EnsureResultAsync(OffboardingRequest request, OffboardingResult? previous,
        Func<OffboardingRequest, CancellationToken, Task<OffboardingResult>> remove,
        Func<OffboardingResult, CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        if (previous is not null) return previous;
        var result = await remove(request, cancellationToken).ConfigureAwait(false);
        await save(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static UninstallReceipt CreateReceipt(OffboardingRequest request, OffboardingResult result) => new()
    {
        TenantId = request.TenantId, ClientId = request.AgentId.ToString("D"), OperationId = request.OperationId.ToString("D"),
        CompletionToken = request.CompletionToken, Success = result.Success, ServiceRemoved = result.ServiceRemoved,
        FilesRemoved = result.FilesRemoved, FailureCode = result.FailureCode
    };

    private static async Task<OffboardingResult> RemoveAsync(OffboardingRequest request, CancellationToken cancellationToken)
    {
        var serviceRemoved = false;
        try
        {
            if (!request.RemovalStarted && DateTimeOffset.UtcNow >= request.StartDeadlineUtc)
                return new(false, false, "uninstall_start_expired");
            if (request.ReceiptDeadlineUtc <= DateTimeOffset.UtcNow)
                return new(false, false, "uninstall_receipt_expired");

            if (!File.Exists(request.CredentialPath))
            {
                // The process may have crashed between final cleanup and writing result.json.
                // This path only verifies a completed removal; it cannot delete a new installation.
                if (!request.RemovalStarted || Directory.Exists(request.InstallRoot) || !await IsServiceAbsentAsync(cancellationToken).ConfigureAwait(false))
                    return new(false, false, "uninstall_installation_identity_missing");
                var complete = OwnedDataFiles(request).All(path => !File.Exists(path));
                return new(true, complete, complete ? string.Empty : "uninstall_credential_files_remain");
            }

            OffboardingState.AssertProtectedPath(request.CredentialPath);
            var credentials = new AgentCredentialStore(request.CredentialPath, legacyPathOverride: string.Empty);
            var identity = await credentials.LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
            var key = await ((IAgentDeviceKeyStore)credentials).LoadAsync(cancellationToken).ConfigureAwait(false);
            if (identity is not { } stored || !Guid.TryParse(stored.AgentId, out var agentId) || agentId != request.AgentId || key is null ||
                !string.Equals(ClientOffboardingCoordinator.Fingerprint(key.PublicKey), request.ExpectedKeyFingerprint, StringComparison.OrdinalIgnoreCase))
                return new(false, false, "uninstall_installation_changed");

            if (Directory.Exists(request.InstallRoot))
            {
                AssertOwnedTree(request.InstallRoot, request.InstallRoot);
                if (File.Exists(request.InstalledExecutable)
                    ? !string.Equals(HashFile(request.InstalledExecutable), request.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
                    : !request.RemovalStarted || !await IsServiceAbsentAsync(cancellationToken).ConfigureAwait(false))
                    return new(false, false, "uninstall_installed_binary_changed");
            }
            ValidateCredentialFiles(request);
            ValidateWindowsLaunchers(request);
            ValidateLinuxUnits(request);
            await OffboardingState.WriteAsync(OffboardingState.RequestPath(request.OperationId), request with { RemovalStarted = true }, cancellationToken).ConfigureAwait(false);
            OffboardingPlatformScripts.Write(request);
            await OffboardingPlatformScripts.RemoveServiceAsync(request, cancellationToken).ConfigureAwait(false);
            serviceRemoved = await IsServiceAbsentAsync(cancellationToken).ConfigureAwait(false);
            if (!serviceRemoved) return new(false, false, "uninstall_service_still_registered");
            await StopOwnedProcessesAsync(request.InstallRoot, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(request.InstallRoot)) DeleteOwnedTree(request.InstallRoot, request.InstallRoot);
            // Logs are retained. Only the exact installation credential payload and its
            // refresh journal are removed; unrelated application data is never traversed.
            foreach (var path in OwnedDataFiles(request)) if (File.Exists(path)) File.Delete(path);
            var filesRemoved = !Directory.Exists(request.InstallRoot) && OwnedDataFiles(request).All(path => !File.Exists(path));
            return new(serviceRemoved, filesRemoved, filesRemoved ? string.Empty : "uninstall_files_remain");
        }
        catch (Exception exception) when (OffboardingState.IsExpectedFailure(exception))
        {
            Console.Error.WriteLine($"Offboarding removal failed ({exception.GetType().Name}).");
            return new(serviceRemoved, false, "uninstall_native_removal_failed");
        }
    }

    private static string[] CredentialFiles(OffboardingRequest request) =>
        [request.CredentialPath + ".native-refresh", request.CredentialPath + ".native-refresh.lock",
            Path.Combine(Path.GetDirectoryName(request.CredentialPath)!, ".netratel-credential-machine-id"), request.CredentialPath];

    private static string[] WindowsLauncherFiles(OffboardingRequest request) => OperatingSystem.IsWindows()
        ? [Path.Combine(Path.GetDirectoryName(request.CredentialPath)!, "remote-desktop", "netratel-remote-desktop-user-helper.cmd"),
            Path.Combine(Path.GetDirectoryName(request.CredentialPath)!, "remote-desktop", "netratel-remote-desktop-user-helper.vbs")]
        : [];

    private static string[] OwnedDataFiles(OffboardingRequest request) => [.. WindowsLauncherFiles(request), .. CredentialFiles(request)];

    private static void ValidateWindowsLaunchers(OffboardingRequest request)
    {
        foreach (var file in WindowsLauncherFiles(request))
        {
            if (!File.Exists(file)) continue;
            OffboardingState.AssertProtectedPath(file);
            var lines = File.ReadAllLines(file);
            const string commandSuffix = "\" --remote-desktop-user-helper";
            const string hiddenPrefix = "shell.Run \"\"\"";
            const string hiddenSuffix = "\"\" --remote-desktop-user-helper\", 0, False";
            string executable;
            if (file.EndsWith(".cmd", StringComparison.Ordinal) && lines.Length == 3 && lines[0] == "@echo off" && lines[1] == "setlocal" &&
                lines[2].StartsWith('"') && lines[2].EndsWith(commandSuffix, StringComparison.Ordinal))
                executable = lines[2][1..^commandSuffix.Length];
            else if (file.EndsWith(".vbs", StringComparison.Ordinal) && lines.Length == 2 && lines[0] == "Set shell = CreateObject(\"WScript.Shell\")" &&
                lines[1].StartsWith(hiddenPrefix, StringComparison.Ordinal) && lines[1].EndsWith(hiddenSuffix, StringComparison.Ordinal))
                executable = lines[1][hiddenPrefix.Length..^hiddenSuffix.Length];
            else throw new IOException("offboarding_user_helper_launcher_changed");
            // An update can leave a launcher pointing at an earlier owned version.
            if (executable.Contains('"') || executable.Any(char.IsControl) || Path.GetFileName(executable) != "NetRatel.Client.exe" ||
                !ClientOffboardingCoordinator.PathsEqual(Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!, Path.Combine(request.InstallRoot, "versions")))
                throw new IOException("offboarding_user_helper_launcher_changed");
        }
    }

    private static void ValidateCredentialFiles(OffboardingRequest request)
    {
        foreach (var file in CredentialFiles(request)) if (File.Exists(file)) OffboardingState.AssertProtectedPath(file);
    }

    private static void ValidateRequestPaths(OffboardingRequest request)
    {
        var expectedRoot = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetRatel", "Client") : "/opt/netratel/client";
        var expectedData = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel") : "/var/lib/netratel";
        if (request.TenantId <= 0 || request.AgentId == Guid.Empty ||
            !ClientOffboardingCoordinator.PathsEqual(request.InstallRoot, expectedRoot) ||
            !ClientOffboardingCoordinator.PathsEqual(request.CredentialPath, Path.Combine(expectedData, "agent.dat")) ||
            !ClientOffboardingCoordinator.PathsEqual(request.UpdateDirectory, Path.Combine(expectedData, "update")) ||
            !ClientOffboardingCoordinator.PathsEqual(Path.GetDirectoryName(Path.GetDirectoryName(request.InstalledExecutable)!)!, Path.Combine(expectedRoot, "versions")) ||
            Path.GetFileName(request.InstalledExecutable) != OffboardingState.HelperName ||
            (request.ReceiptDeadlineUtc > DateTimeOffset.UtcNow && request.CompletionToken.Length is < 32 or > 512) || request.CompletionToken.Any(char.IsControl) ||
            request.ExpectedKeyFingerprint.Length != 64 || request.ExpectedKeyFingerprint.Any(c => !char.IsAsciiHexDigit(c)) ||
            !Uri.TryCreate(request.GatewayEndpoint, UriKind.Absolute, out var gateway) || gateway.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(gateway.UserInfo) || !string.IsNullOrEmpty(gateway.Query) || !string.IsNullOrEmpty(gateway.Fragment))
            throw new IOException("offboarding_request_invalid");
    }

    private static void ValidateLinuxUnits(OffboardingRequest request)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var (path, hash) in new[] { ("/etc/systemd/system/netratel-client.service", request.ServiceUnitSha256),
            ("/etc/systemd/system/netratel-update.service", request.UpdaterUnitSha256) })
        {
            if (!File.Exists(path)) continue;
            OffboardingState.AssertProtectedPath(path);
            if (!string.Equals(HashFile(path), hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("offboarding_service_unit_changed");
        }
        var directory = "/etc/systemd/system/netratel-client.service.d";
        if (!Directory.Exists(directory)) return;
        OffboardingState.AssertProtectedPath(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            OffboardingState.AssertProtectedPath(path);
            if (Path.GetFileName(path) != "50-netratel-recovery.conf" || File.ReadAllText(path) != ManagedServiceRecovery.LinuxDropIn)
                throw new IOException("offboarding_service_override_present");
        }
    }

    private static void AssertOwnedTree(string path, string root)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (path != Path.Combine(root, "current") || target is null || !IsWithin(target, Path.Combine(root, "versions")))
                throw new IOException("offboarding_installation_link");
            return;
        }
        OffboardingState.AssertProtectedPath(path);
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) AssertOwnedTree(child, root);
    }

    private static void DeleteOwnedTree(string path, string root)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            AssertOwnedTree(path, root);
            Directory.Delete(path);
            return;
        }
        OffboardingState.AssertProtectedPath(path);
        if (!Directory.Exists(path)) { File.Delete(path); return; }
        // Remove the verified current link before its version target, keeping crash
        // recovery able to inspect a partially removed installation safely.
        foreach (var child in Directory.EnumerateFileSystemEntries(path).OrderBy(child => child == Path.Combine(root, "current") ? 0 : 1))
            DeleteOwnedTree(child, root);
        Directory.Delete(path);
    }

    private static async Task StopOwnedProcessesAsync(string installationRoot, CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    // Exited and protected OS processes cannot be NetRatel's owned helper.
                    Debug.WriteLine($"Offboarding process inspection skipped: {exception.GetType().Name}");
                    continue;
                }
                if (executable is null || !IsWithin(executable, installationRoot)) continue;
                try
                {
                    process.Kill();
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    continue;
                }
            }
        }
    }

    private static bool IsWithin(string path, string directory) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<bool> IsServiceAbsentAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await OffboardingProcess.RunAsync(OffboardingProcess.PowerShell,
                ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; if ($null -ne (Get-CimInstance Win32_Service -Filter \"Name='NetRatel.Client'\" -OperationTimeoutSec 10)) { exit 3 }; Write-Output 'absent'"], cancellationToken).ConfigureAwait(false);
            return result.Trim() == "absent";
        }
        return (await OffboardingProcess.RunAsync("/usr/bin/systemctl", ["show", "netratel-client.service", "--property=LoadState", "--value"],
            cancellationToken).ConfigureAwait(false)).Trim() == "not-found";
    }

    internal static string HashFile(string path)
    {
        OffboardingState.AssertProtectedPath(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
