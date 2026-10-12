using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Offboarding;

internal sealed record OffboardingRequest(
    Guid OperationId, int TenantId, Guid AgentId, string CompletionToken, string GatewayEndpoint,
    DateTimeOffset StartDeadlineUtc, DateTimeOffset ReceiptDeadlineUtc, string ExpectedKeyFingerprint,
    string InstallRoot, string InstalledExecutable, string ExecutableSha256, string CredentialPath,
    string UpdateDirectory, string ServiceImage, string? ServiceUnitSha256, string? UpdaterUnitSha256,
    bool RemovalStarted = false);

internal sealed record OffboardingResult(bool ServiceRemoved, bool FilesRemoved, string FailureCode)
{
    public bool Success => ServiceRemoved && FilesRemoved && string.IsNullOrEmpty(FailureCode);
}

/// <summary>The temporary helper is outside both the installation and credential directories.</summary>
internal static class OffboardingState
{
    private static HashSet<string> _windowsTrustedSids = ["S-1-5-18", "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel-Offboarding")
        : "/var/lib/netratel-offboarding";
    internal static string DirectoryFor(Guid operationId) => Path.Combine(Root, operationId.ToString("N"));
    internal static string RequestPath(Guid operationId) => Path.Combine(DirectoryFor(operationId), "request.json");
    internal static string ResultPath(Guid operationId) => Path.Combine(DirectoryFor(operationId), "result.json");
    internal static string HelperName => OperatingSystem.IsWindows() ? "NetRatel.Client.exe" : "NetRatel.Client";
    internal static string UnitName(Guid operationId) => "netratel-offboarding-" + operationId.ToString("N") + ".service";
    internal static string TaskName(Guid operationId) => "NetRatel.Offboarding." + operationId.ToString("N");

    internal static bool HasNativePrivileges => OperatingSystem.IsWindows()
        ? WindowsIdentity.GetCurrent().IsSystem
        : OperatingSystem.IsLinux() && geteuid() == 0;

    internal static async Task InitializeWindowsTrustAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Match the installer: package files can be owned by a local administrator
        // user as well as SYSTEM, Administrators or TrustedInstaller.
        var output = await OffboardingProcess.RunAsync(OffboardingProcess.PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; $group=Get-CimInstance Win32_Group -Filter \"SID='S-1-5-32-544'\" -OperationTimeoutSec 10; $members=@(Get-CimAssociatedInstance -InputObject $group -Association Win32_GroupUser -ResultClassName Win32_UserAccount -OperationTimeoutSec 10 | Select-Object -ExpandProperty SID); ConvertTo-Json -InputObject $members -Compress"],
            cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var trusted = new HashSet<string>(_windowsTrustedSids, StringComparer.Ordinal);
        foreach (var sid in JsonSerializer.Deserialize<string[]>(output) ?? [])
            trusted.Add(new SecurityIdentifier(sid).Value);
        _windowsTrustedSids = trusted;
    }

    internal static void EnsurePrivateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            AssertProtectedPath(path, privateLeaf: true);
            return;
        }
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("offboarding_path_invalid");
        AssertProtectedPath(parent);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else throw new PlatformNotSupportedException();
        AssertProtectedPath(path, privateLeaf: true);
    }

    internal static void AssertProtectedPath(string path, bool privateLeaf = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("offboarding_path_invalid");
        FileSystemInfo leaf = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!leaf.Exists) throw new IOException("offboarding_path_missing");
        for (FileSystemInfo? entry = leaf; entry is not null; entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("offboarding_path_link");
            if (OperatingSystem.IsWindows())
            {
                FileSystemSecurity acl = entry is FileInfo windowsFile ? windowsFile.GetAccessControl() : ((DirectoryInfo)entry).GetAccessControl();
                static bool Trusted(string? sid) => sid is not null && _windowsTrustedSids.Contains(sid);
                if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier))?.Value)) throw new IOException("offboarding_path_owner");
                var dangerous = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if (entry is FileInfo) dangerous |= FileSystemRights.Write;
                if (ReferenceEquals(entry, leaf) && privateLeaf) dangerous = FileSystemRights.FullControl;
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow &&
                        !(entry is DirectoryInfo && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) &&
                        (rule.FileSystemRights & dangerous) != 0 && !Trusted(rule.IdentityReference.Value))
                        throw new IOException("offboarding_path_permissions");
            }
            else if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(entry.FullName);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 ||
                    ReferenceEquals(entry, leaf) && privateLeaf && (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute)) != 0 ||
                    statx(-100, entry.FullName, 0x100, 0x8, out var owner) != 0 || (owner.Mask & 0x8) == 0 || owner.UserId != 0)
                    throw new IOException("offboarding_path_permissions");
            }
            else throw new PlatformNotSupportedException();
        }
    }

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path)!;
        AssertProtectedPath(parent, privateLeaf: true);
        if (File.Exists(path)) AssertProtectedPath(path, privateLeaf: true);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        AssertProtectedPath(path, privateLeaf: true);
        if (new FileInfo(path).Length > 32768) throw new IOException("offboarding_state_too_large");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    }

    internal static bool IsExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        InvalidOperationException or Win32Exception or JsonException or TimeoutException or
        System.Collections.Generic.KeyNotFoundException or FormatException or System.Security.Cryptography.CryptographicException;

    [DllImport("libc")] private static extern uint geteuid();
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint UserId;
    }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int directory, string path, int flags, uint mask, out Statx value);
}

internal static class OffboardingProcess
{
    internal static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("offboarding_process_start_failed");
        var output = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var error = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("offboarding_native_command_failed");
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch (OperationCanceledException) { Debug.WriteLine("Offboarding command capture cancelled with its process."); }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new IOException("offboarding_native_command_timeout");
        }
    }
}
