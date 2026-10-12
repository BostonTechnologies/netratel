using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Updates;

namespace NetRatel.Client.Service.Offboarding;

/// <summary>Stages a typed uninstall for an independent native helper; the running agent never deletes itself.</summary>
public sealed class ClientOffboardingCoordinator
{
    public const string Capability = "uninstall-v1";
    private readonly GatewayClientOptions _gateway;
    private readonly IAgentCredentialStore _credentials;
    private readonly IAgentDeviceKeyStore _keys;
    private readonly AkkaClientAutoUpdateCoordinator _updates;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Installation _installation;

    private ClientOffboardingCoordinator(GatewayClientOptions gateway, IAgentCredentialStore credentials,
        IAgentDeviceKeyStore keys, AkkaClientAutoUpdateCoordinator updates, Installation installation, Action<string> log)
    {
        _gateway = gateway; _credentials = credentials; _keys = keys; _updates = updates;
        _installation = installation; _log = log;
    }

    public static bool HasPendingUninstall
    {
        get
        {
            try
            {
                var active = OffboardingState.Read<Guid>(Path.Combine(OffboardingState.Root, "active.json"));
                return active != Guid.Empty && OffboardingState.Read<OffboardingRequest>(OffboardingState.RequestPath(active)) is { } request &&
                    request.ReceiptDeadlineUtc > DateTimeOffset.UtcNow;
            }
            catch (Exception exception) when (OffboardingState.IsExpectedFailure(exception))
            {
                // An unreadable maintenance marker cannot authorize a competing update.
                return File.Exists(Path.Combine(OffboardingState.Root, "active.json"));
            }
        }
    }

    public static async Task<ClientOffboardingCoordinator?> TryCreateAsync(ClientOptions options, GatewayClientOptions gateway,
        IAgentCredentialStore credentials, IAgentDeviceKeyStore keys, AkkaClientAutoUpdateCoordinator updates,
        Action<string> log, CancellationToken cancellationToken)
    {
        if (!NativeServiceRecoveryPolicy.IsOwnedService || !OffboardingState.HasNativePrivileges ||
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
            File.Exists("/.dockerenv") || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NetRatel_CREDENTIAL_MACHINE_ID"))) return null;
        try
        {
            if (!Uri.TryCreate(gateway.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)) return null;
            await OffboardingState.InitializeWindowsTrustAsync(cancellationToken).ConfigureAwait(false);
            var installation = await ReadInstallationAsync(options, cancellationToken).ConfigureAwait(false);
            return new(gateway, credentials, keys, updates, installation, log);
        }
        catch (Exception exception) when (OffboardingState.IsExpectedFailure(exception))
        {
            log($"Uninstall capability unavailable: installation verification failed ({exception.GetType().Name}).");
            return null;
        }
    }

    public async Task<UninstallAcknowledgement> PrepareAsync(GatewayPresenceSession session, UninstallRequest request,
        CancellationToken cancellationToken)
    {
        var acknowledgement = new UninstallAcknowledgement { OperationId = request.OperationId };
        if (!IsValidRequest(session, request, DateTimeOffset.UtcNow))
        {
            acknowledgement.FailureCode = "uninstall_request_invalid";
            return acknowledgement;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operationId = Guid.Parse(request.OperationId);
            var credentials = await _credentials.LoadAsync(cancellationToken).ConfigureAwait(false);
            var key = await _keys.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (credentials is not { } installed || !Guid.TryParse(installed.AgentId, out var installedId) || installedId != session.AgentId ||
                key is null || !string.Equals(Fingerprint(key.PublicKey), request.ExpectedKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                acknowledgement.FailureCode = "uninstall_installation_changed";
                return acknowledgement;
            }
            OffboardingState.EnsurePrivateDirectory(OffboardingState.Root);
            var activePath = Path.Combine(OffboardingState.Root, "active.json");
            var active = OffboardingState.Read<Guid>(activePath);
            if (active != Guid.Empty && active != operationId &&
                OffboardingState.Read<OffboardingRequest>(OffboardingState.RequestPath(active)) is { } pending &&
                pending.ReceiptDeadlineUtc > DateTimeOffset.UtcNow)
            {
                acknowledgement.FailureCode = "uninstall_already_pending";
                return acknowledgement;
            }
            OffboardingState.EnsurePrivateDirectory(OffboardingState.DirectoryFor(operationId));
            // The helper uses this same lock throughout removal and receipt delivery.
            // A duplicate dispatch must never replace its binary or roll back its journal.
            using var operation = ClientOffboardingHelper.AcquireOperationLock(operationId);
            var stored = OffboardingState.Read<OffboardingRequest>(OffboardingState.RequestPath(operationId));
            if (stored is not null && !MatchesOperation(stored, session, request))
            {
                acknowledgement.FailureCode = "uninstall_operation_mismatch";
                return acknowledgement;
            }
            if (stored is not null && stored.ReceiptDeadlineUtc <= DateTimeOffset.UtcNow)
            {
                acknowledgement.FailureCode = "uninstall_receipt_expired";
                return acknowledgement;
            }
            // Stop downloads/activation offers, then let the independent helper acquire the
            // existing installer/updater lock before touching the service or installation.
            await _updates.DisposeAsync().ConfigureAwait(false);
            if (stored is null)
            {
                stored = new(operationId, session.TenantId, session.AgentId, request.CompletionToken, _gateway.Endpoint,
                    request.StartDeadlineUtc.ToDateTimeOffset(), DateTimeOffset.UtcNow.AddHours(24), request.ExpectedKeyFingerprint,
                    _installation.Root, _installation.Executable, _installation.ExecutableSha256, _installation.CredentialPath,
                    _installation.UpdateDirectory, _installation.ServiceImage, _installation.ServiceUnitSha256, _installation.UpdaterUnitSha256);
            }
            else if (!stored.RemovalStarted && !File.Exists(OffboardingState.ResultPath(operationId)))
                stored = stored with { StartDeadlineUtc = request.StartDeadlineUtc.ToDateTimeOffset() };
            await OffboardingState.WriteAsync(OffboardingState.RequestPath(operationId), stored, cancellationToken).ConfigureAwait(false);
            await OffboardingState.WriteAsync(activePath, operationId, cancellationToken).ConfigureAwait(false);
            // Registration is deliberately repeatable after an ambiguous native launch or
            // a crash during staging. Its failure never discards the operation credential.
            StageHelper(stored);
            await OffboardingPlatformScripts.RegisterAsync(stored, cancellationToken).ConfigureAwait(false);
            acknowledgement.Accepted = true;
            return acknowledgement;
        }
        catch (Exception exception) when (OffboardingState.IsExpectedFailure(exception))
        {
            _log($"Uninstall preparation could not be confirmed ({exception.GetType().Name}); any staged operation remains available for retry.");
            acknowledgement.FailureCode = "uninstall_preparation_failed";
            return acknowledgement;
        }
        finally { _gate.Release(); }
    }

    public async Task StartPreparedAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operationId, out var id)) return;
        try
        {
            var request = OffboardingState.Read<OffboardingRequest>(OffboardingState.RequestPath(id));
            if (request is null) return;
            await OffboardingPlatformScripts.StartAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (OffboardingState.IsExpectedFailure(exception))
        {
            // The durable boot-triggered helper and request remain available for resumption.
            _log($"Uninstall helper launch is pending ({exception.GetType().Name}).");
        }
    }

    internal static bool IsValidRequest(GatewayPresenceSession session, UninstallRequest request, DateTimeOffset now)
    {
        if (session.TenantId <= 0 || session.AgentId == Guid.Empty || !Guid.TryParse(request.OperationId, out var id) || id == Guid.Empty ||
            request.CompletionToken.Length is < 32 or > 512 || request.CompletionToken.Any(char.IsControl) ||
            request.ExpectedKeyFingerprint.Length != 64 || request.ExpectedKeyFingerprint.Any(c => !char.IsAsciiHexDigit(c)) ||
            request.StartDeadlineUtc is null) return false;
        try
        {
            var deadline = request.StartDeadlineUtc.ToDateTimeOffset();
            return deadline > now && deadline <= now.AddMinutes(6);
        }
        catch (InvalidOperationException) { return false; }
    }

    internal static string Fingerprint(string publicKey) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey))).ToLowerInvariant();

    internal static bool MatchesOperation(OffboardingRequest stored, GatewayPresenceSession session, UninstallRequest request) =>
        Guid.TryParse(request.OperationId, out var operationId) && stored.OperationId == operationId &&
        stored.TenantId == session.TenantId && stored.AgentId == session.AgentId && stored.CompletionToken == request.CompletionToken &&
        string.Equals(stored.ExpectedKeyFingerprint, request.ExpectedKeyFingerprint, StringComparison.OrdinalIgnoreCase);

    private static void StageHelper(OffboardingRequest request)
    {
        var directory = OffboardingState.DirectoryFor(request.OperationId);
        var target = Path.Combine(directory, OffboardingState.HelperName);
        if (!File.Exists(target))
        {
            OffboardingState.AssertProtectedPath(request.InstalledExecutable);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(request.InstalledExecutable, temporary);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (!string.Equals(ClientOffboardingHelper.HashFile(temporary), request.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("offboarding_helper_copy_mismatch");
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (!string.Equals(ClientOffboardingHelper.HashFile(target), request.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("offboarding_helper_copy_mismatch");
        OffboardingPlatformScripts.Write(request);
    }

    private sealed record Installation(string Root, string Executable, string ExecutableSha256, string CredentialPath,
        string UpdateDirectory, string ServiceImage, string? ServiceUnitSha256, string? UpdaterUnitSha256);

    private static async Task<Installation> ReadInstallationAsync(ClientOptions options, CancellationToken cancellationToken)
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetRatel", "Client") : "/opt/netratel/client";
        var update = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel", "update") : "/var/lib/netratel/update";
        if (!string.IsNullOrEmpty(options.AutoUpdate.StateDirectory) && !PathsEqual(options.AutoUpdate.StateDirectory, update) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NetRatel_UPDATE_ROOT")) &&
            !PathsEqual(Environment.GetEnvironmentVariable("NetRatel_UPDATE_ROOT")!, root))
            throw new IOException("offboarding_custom_installation_unsupported");
        var executable = Environment.ProcessPath ?? throw new IOException("offboarding_executable_unknown");
        var versionDirectory = Path.GetDirectoryName(executable)!;
        if (!PathsEqual(Path.GetDirectoryName(versionDirectory)!, Path.Combine(root, "versions")) ||
            Path.GetFileName(executable) != OffboardingState.HelperName) throw new IOException("offboarding_installation_layout_unsupported");
        var manifestPath = Path.Combine(versionDirectory, "netratel-client-manifest.json");
        OffboardingState.AssertProtectedPath(executable);
        OffboardingState.AssertProtectedPath(manifestPath);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        var metadata = manifest.RootElement;
        if (!IsStandaloneManifest(metadata, OperatingSystem.IsWindows() ? "win-x64" : "linux-x64", OffboardingState.HelperName))
            throw new IOException("offboarding_installation_manifest_invalid");
        var credential = Auth.AgentCredentialStore.DefaultPath();
        OffboardingState.AssertProtectedPath(credential);
        OffboardingState.AssertProtectedPath(update);
        if (File.Exists(Path.Combine(update, "update.lock"))) OffboardingState.AssertProtectedPath(Path.Combine(update, "update.lock"));
        string image;
        string? unitHash = null, updaterHash = null;
        if (OperatingSystem.IsWindows())
        {
            var value = await OffboardingProcess.RunAsync(OffboardingProcess.PowerShell,
                ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; Get-CimInstance Win32_Service -Filter \"Name='NetRatel.Client'\" | Select-Object PathName,ProcessId,StartName | ConvertTo-Json -Compress"], cancellationToken).ConfigureAwait(false);
            using var service = JsonDocument.Parse(value);
            image = service.RootElement.GetProperty("PathName").GetString() ?? string.Empty;
            if (!WindowsRecoveryPolicyAdapter.IsOwnedImage(image, executable) ||
                service.RootElement.GetProperty("ProcessId").GetInt32() != Environment.ProcessId ||
                service.RootElement.GetProperty("StartName").GetString() is not ("LocalSystem" or "NT AUTHORITY\\SYSTEM")) throw new IOException("offboarding_service_not_owned");
        }
        else
        {
            image = "/etc/systemd/system/netratel-client.service";
            OffboardingState.AssertProtectedPath(image);
            var fragment = await OffboardingProcess.RunAsync("/usr/bin/systemctl",
                ["show", "netratel-client.service", "--property=FragmentPath", "--value"], cancellationToken).ConfigureAwait(false);
            if (fragment.Trim() != image) throw new IOException("offboarding_service_not_owned");
            unitHash = ClientOffboardingHelper.HashFile(image);
            var updater = "/etc/systemd/system/netratel-update.service";
            if (File.Exists(updater))
            {
                OffboardingState.AssertProtectedPath(updater);
                if (!IsOwnedLinuxUpdaterUnit(File.ReadAllText(updater), root))
                    throw new IOException("offboarding_updater_not_owned");
                updaterHash = ClientOffboardingHelper.HashFile(updater);
            }
        }
        return new(root, executable, ClientOffboardingHelper.HashFile(executable), credential,
            update, image, unitHash, updaterHash);
    }

    internal static bool PathsEqual(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool IsOwnedLinuxUpdaterUnit(string content, string installationRoot)
    {
        var commands = content.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("ExecStart=", StringComparison.Ordinal)).ToArray();
        var expected = installationRoot + "/updater/netratel-update.sh";
        return commands.Length == 1 && (commands[0] == "ExecStart=" + expected || commands[0] == "ExecStart=\"" + expected + "\"");
    }

    internal static bool IsStandaloneManifest(JsonElement metadata, string runtimeId, string executable)
    {
        bool Text(string name, string expected) => metadata.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() == expected;
        return metadata.ValueKind == JsonValueKind.Object && Text("schema", "netratel.client.manifest.v1") &&
            Text("product", "NetRatel.Client") && Text("executable", executable) && Text("runtimeId", runtimeId) &&
            metadata.TryGetProperty("selfContained", out var contained) && contained.ValueKind == JsonValueKind.True &&
            metadata.TryGetProperty("singleFile", out var single) && single.ValueKind == JsonValueKind.True;
    }
}
