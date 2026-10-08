using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.ClientAuth;

namespace NetRatel.Client.Service.Auth;

public sealed class AgentCredentialStore : IAgentCredentialStore, IAgentDeviceKeyStore, IAgentRefreshExchangeStore
{
    private const string CredentialMachineIdentityEnvironmentVariable = "NetRatel_CREDENTIAL_MACHINE_ID";
    private const string CredentialMachineIdentityFileName = ".netratel-credential-machine-id";
    private static readonly CredentialProtectionProfile CurrentProtection = new(
        Encoding.UTF8.GetBytes("netratel-agent-credential-v1"),
        LinuxUser: null,
        KeyLabel: "netratel-agent-store-v1");
    private static readonly CredentialProtectionProfile LegacyStoProtection = new(
        Encoding.UTF8.GetBytes("sto-agent-credential-v1"),
        LinuxUser: "sto",
        KeyLabel: "sto-agent-store-v1");
    private static readonly CredentialProtectionProfile ExchangeProtection = new(
        Encoding.UTF8.GetBytes("netratel-native-refresh-exchange-v1"), LinuxUser: null,
        KeyLabel: "netratel-native-refresh-exchange-v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string ExchangePath => _path + ".native-refresh";
    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly Func<string> _machineNameProvider;
    private readonly Func<bool> _containerRuntimeProvider;

    public AgentCredentialStore(
        string? pathOverride = null,
        string? legacyPathOverride = null,
        Func<string>? machineNameProvider = null,
        Func<bool>? containerRuntimeProvider = null)
    {
        _path = ResolvePath(pathOverride);
        _legacyPath = legacyPathOverride ?? ResolveLegacyPath(pathOverride);
        _machineNameProvider = machineNameProvider ?? (() => Environment.MachineName);
        _containerRuntimeProvider = containerRuntimeProvider ?? IsRunningInContainer;
    }

    public static string DefaultPath() => ResolvePath(null);

    public Task SaveAsync(string agentId, string refreshToken) => SaveAsync(agentId, refreshToken, CancellationToken.None);

    public async Task SaveAsync(string agentId, string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("agentId is required", nameof(agentId));
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new ArgumentException("refreshToken is required", nameof(refreshToken));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(ct).ConfigureAwait(false);
            var existing = await LoadReconciledPayloadAsync(ct).ConfigureAwait(false);
            var journal = await ReadExchangeJournalAsync(ct).ConfigureAwait(false);
            if (journal?.SuccessorRefreshToken is not null && agentId == journal.Exchange.AgentId &&
                refreshToken == journal.Exchange.ParentRefreshToken)
                return; // A stale save cannot temporarily restore an already committed parent.
            // A legitimate legacy refresh can advance beyond our recorded result.
            // Persist that current credential first, then retire obsolete pending state.
            await WritePayloadAsync(new CredentialPayload(agentId, refreshToken, existing?.PublicKey,
                existing?.PrivateKey, existing?.KeyAlgorithm ?? "ecdsa-p256"), ct).ConfigureAwait(false);
            if (journal is not null && (agentId != journal.Exchange.AgentId ||
                (refreshToken != journal.Exchange.ParentRefreshToken && refreshToken != journal.SuccessorRefreshToken)))
                File.Delete(ExchangePath);
        }
        finally { _gate.Release(); }
    }

    public Task<(string AgentId, string RefreshToken)?> LoadAsync() => LoadCredentialsAsync(CancellationToken.None);

    Task<(string AgentId, string RefreshToken)?> IAgentCredentialStore.LoadAsync(CancellationToken ct) => LoadCredentialsAsync(ct);

    public async Task<(string AgentId, string RefreshToken)?> LoadCredentialsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(ct).ConfigureAwait(false);
            var payload = await LoadReconciledPayloadAsync(ct).ConfigureAwait(false);
            return payload is null || string.IsNullOrWhiteSpace(payload.AgentId) || string.IsNullOrWhiteSpace(payload.RefreshToken)
                ? null : (payload.AgentId, payload.RefreshToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearRefreshCredentialsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(CancellationToken.None).ConfigureAwait(false);
            var existing = await LoadReconciledPayloadAsync(CancellationToken.None).ConfigureAwait(false);
            if (existing is null) return;
            if (string.IsNullOrWhiteSpace(existing.PublicKey) || string.IsNullOrWhiteSpace(existing.PrivateKey))
                throw new AgentCredentialStoreException("Refresh credentials cannot be cleared because this existing credential-only payload has no device key to preserve. Its bytes were left unchanged; provide a validated enrollment recovery before changing this installation identity.");
            await WritePayloadAsync(new CredentialPayload(null, null, existing.PublicKey, existing.PrivateKey,
                existing.KeyAlgorithm ?? "ecdsa-p256"), CancellationToken.None).ConfigureAwait(false);
            File.Delete(ExchangePath);
        }
        finally { _gate.Release(); }
    }

    public async Task ResetInstallationIdentityAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(CancellationToken.None).ConfigureAwait(false);
            File.Delete(_path);
            File.Delete(ExchangePath);
        }
        finally { _gate.Release(); }
    }

    [Obsolete("Use ClearRefreshCredentialsAsync so the stable device identity is preserved.")]
    public Task ClearAsync() => ClearRefreshCredentialsAsync();

    public async Task<AgentDeviceKeyMaterial> GetOrCreateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(ct).ConfigureAwait(false);
            var existing = await LoadReconciledPayloadAsync(ct).ConfigureAwait(false);
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.PublicKey) && !string.IsNullOrWhiteSpace(existing.PrivateKey))
                return new AgentDeviceKeyMaterial(existing.PublicKey, existing.PrivateKey, existing.KeyAlgorithm ?? "ecdsa-p256");
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var generated = new AgentDeviceKeyMaterial(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()), "ecdsa-p256");
            await WritePayloadAsync(new CredentialPayload(existing?.AgentId, existing?.RefreshToken,
                generated.PublicKey, generated.PrivateKey, generated.Algorithm), ct).ConfigureAwait(false);
            return generated;
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentDeviceKeyMaterial?> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(ct).ConfigureAwait(false);
            var payload = await LoadReconciledPayloadAsync(ct).ConfigureAwait(false);
            return payload is null || string.IsNullOrWhiteSpace(payload.PublicKey) || string.IsNullOrWhiteSpace(payload.PrivateKey)
                ? null : new AgentDeviceKeyMaterial(payload.PublicKey, payload.PrivateKey, payload.KeyAlgorithm ?? "ecdsa-p256");
        }
        finally { _gate.Release(); }
    }

    public async Task<PendingAgentRefreshExchange?> LoadPendingExchangeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(ct).ConfigureAwait(false);
            await LoadReconciledPayloadAsync(ct).ConfigureAwait(false);
            var journal = await ReadExchangeJournalAsync(ct).ConfigureAwait(false);
            return journal?.SuccessorRefreshToken is null ? journal?.Exchange : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<PendingAgentRefreshExchange> BeginRefreshExchangeAsync(PendingAgentRefreshExchange exchange, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(ct).ConfigureAwait(false);
            var payload = await LoadReconciledPayloadAsync(ct).ConfigureAwait(false);
            var existing = await ReadExchangeJournalAsync(ct).ConfigureAwait(false);
            if (existing is not null && existing.SuccessorRefreshToken is null) return existing.Exchange;
            if (payload?.AgentId != exchange.AgentId || payload.RefreshToken != exchange.ParentRefreshToken ||
                exchange.Version != 1 || exchange.ExchangeId == Guid.Empty || exchange.RequestedScopes is null ||
                string.IsNullOrWhiteSpace(exchange.DeviceKeyHash) || GetDeviceKeyHash(payload) != exchange.DeviceKeyHash)
                throw new AgentCredentialStoreException("Native refresh exchange does not match the current protected credentials.");
            await WriteExchangeJournalAsync(new ExchangeJournal(1, exchange with { RequestedScopes = [.. exchange.RequestedScopes] }, null), ct).ConfigureAwait(false);
            return exchange;
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteRefreshExchangeAsync(PendingAgentRefreshExchange exchange, string successorRefreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(successorRefreshToken)) throw new ArgumentException("Successor refresh token is required.", nameof(successorRefreshToken));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireInstallationLeaseAsync(ct).ConfigureAwait(false);
            var payload = await LoadReconciledPayloadAsync(ct).ConfigureAwait(false);
            var journal = await ReadExchangeJournalAsync(ct).ConfigureAwait(false);
            if (journal is null || journal.Exchange.ExchangeId != exchange.ExchangeId || payload?.AgentId != exchange.AgentId)
                throw new AgentClientAuthException("An obsolete native refresh response cannot replace current protected credentials.",
                    code: "refresh_exchange_obsolete", endpointRole: AgentAuthEndpointRole.Token,
                    failureKind: AgentAuthFailureKind.LocalContention);
            if (!SameExchange(journal.Exchange, exchange) ||
                (journal.SuccessorRefreshToken is not null && journal.SuccessorRefreshToken != successorRefreshToken))
                throw new AgentClientAuthException("Native refresh result does not match the recorded exchange binding.",
                    code: "refresh_exchange_result_mismatch", endpointRole: AgentAuthEndpointRole.Token,
                    failureKind: AgentAuthFailureKind.Protocol);
            if (payload.RefreshToken != exchange.ParentRefreshToken && payload.RefreshToken != successorRefreshToken)
                throw new AgentClientAuthException("An obsolete native refresh response cannot replace current protected credentials.",
                    code: "refresh_exchange_obsolete", endpointRole: AgentAuthEndpointRole.Token,
                    failureKind: AgentAuthFailureKind.LocalContention);
            if (GetDeviceKeyHash(payload) != exchange.DeviceKeyHash)
                throw new AgentCredentialStoreException("Protected native refresh exchange does not match the current registered device key; its bytes were preserved.");
            // The protected journal commits the successor before the released-format
            // credential file. A crash at either boundary is reconciled on the next load.
            await WriteExchangeJournalAsync(journal with { SuccessorRefreshToken = successorRefreshToken }, ct).ConfigureAwait(false);
            await WritePayloadAsync(payload with { RefreshToken = successorRefreshToken }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<FileStream> AcquireInstallationLeaseAsync(CancellationToken ct)
    {
        var lockPath = ExchangePath + ".lock";
        var directory = Path.GetDirectoryName(lockPath);
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                if (OperatingSystem.IsWindows()) WindowsAgentDataDirectory.EnsureForPath(directory);
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new AgentCredentialStoreException(ex); }
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // The empty file has no pending data. FileShare.None holds the OS
                // lease until disposal and is released even when a process crashes.
                var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                TryTightenPermissions(lockPath);
                return lease;
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                    throw new AgentClientAuthException("Protected credential storage is in use by another native operation.",
                        code: "credential_store_busy", endpointRole: AgentAuthEndpointRole.Token,
                        failureKind: AgentAuthFailureKind.LocalContention);
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new AgentCredentialStoreException(ex); }
        }
    }

    private async Task<CredentialPayload?> LoadReconciledPayloadAsync(CancellationToken ct)
    {
        var payload = await TryLoadPayloadAsync(ct).ConfigureAwait(false);
        var journal = await ReadExchangeJournalAsync(ct).ConfigureAwait(false);
        if (journal is null) return payload;
        if (payload?.AgentId != journal.Exchange.AgentId ||
            (payload.RefreshToken != journal.Exchange.ParentRefreshToken && payload.RefreshToken != journal.SuccessorRefreshToken))
        {
            // Missing credentials or a later legitimate legacy save wins. Never
            // restore the recorded parent over a newer credential or reset identity.
            File.Delete(ExchangePath);
            return payload;
        }
        if (GetDeviceKeyHash(payload) != journal.Exchange.DeviceKeyHash)
            throw new AgentCredentialStoreException("Protected native refresh exchange does not match the current registered device key; its bytes were preserved.");
        if (journal.SuccessorRefreshToken is not null && payload.RefreshToken == journal.Exchange.ParentRefreshToken)
        {
            payload = payload with { RefreshToken = journal.SuccessorRefreshToken };
            await WritePayloadAsync(payload, ct).ConfigureAwait(false);
        }
        return payload;
    }

    private async Task<ExchangeJournal?> ReadExchangeJournalAsync(CancellationToken ct)
    {
        if (!TryGetExistingCredentialFile(ExchangePath)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(ExchangePath, ct).ConfigureAwait(false);
            var journal = JsonSerializer.Deserialize<ExchangeJournal>(Unprotect(bytes, ExchangeProtection));
            if (journal?.Version != 1 || journal.Exchange is null || journal.Exchange.Version != 1 || journal.Exchange.ExchangeId == Guid.Empty ||
                string.IsNullOrWhiteSpace(journal.Exchange.AgentId) || string.IsNullOrWhiteSpace(journal.Exchange.ParentRefreshToken) ||
                string.IsNullOrWhiteSpace(journal.Exchange.DeviceKeyHash) || journal.Exchange.RequestedScopes is null)
                throw new AgentCredentialStoreException("Protected native refresh exchange is invalid; its bytes were preserved.");
            return journal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            throw new AgentCredentialStoreException("Protected native refresh exchange cannot be read; its bytes were preserved.", ex);
        }
    }

    private Task WriteExchangeJournalAsync(ExchangeJournal journal, CancellationToken ct) =>
        WriteProtectedFileAsync(ExchangePath, JsonSerializer.SerializeToUtf8Bytes(journal), ExchangeProtection, ct);

    private static bool SameExchange(PendingAgentRefreshExchange left, PendingAgentRefreshExchange right) =>
        left.Version == right.Version && left.ExchangeId == right.ExchangeId && left.AgentId == right.AgentId &&
        left.ParentRefreshToken == right.ParentRefreshToken && left.DeviceKeyHash == right.DeviceKeyHash &&
        left.RequestedScopes.SequenceEqual(right.RequestedScopes, StringComparer.Ordinal);

    private static string? GetDeviceKeyHash(CredentialPayload payload) => string.IsNullOrWhiteSpace(payload.PublicKey) ||
        string.IsNullOrWhiteSpace(payload.PrivateKey) ? null :
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{(payload.KeyAlgorithm ?? "ecdsa-p256").ToLowerInvariant()}:{payload.PublicKey}")));

    private sealed record ExchangeJournal(int Version, PendingAgentRefreshExchange Exchange, string? SuccessorRefreshToken);

    private byte[] Protect(byte[] plaintext) => ProtectForPurpose(plaintext, CurrentProtection);

    private byte[] ProtectForPurpose(byte[] plaintext, CredentialProtectionProfile protection)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Protect(plaintext, protection.Entropy, DataProtectionScope.CurrentUser);
        }

        using var aes = Aes.Create();
        aes.Key = DeriveAesKey(protection);
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var output = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, output, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, output, aes.IV.Length, cipher.Length);
        return output;
    }

    private byte[] Unprotect(byte[] ciphertext, CredentialProtectionProfile protection)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Unprotect(ciphertext, protection.Entropy, DataProtectionScope.CurrentUser);
        }

        using var aes = Aes.Create();
        aes.Key = DeriveAesKey(protection);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var ivLength = aes.BlockSize / 8;
        if (ciphertext.Length <= ivLength)
        {
            throw new CryptographicException("Ciphertext payload is invalid.");
        }

        var iv = new byte[ivLength];
        var body = new byte[ciphertext.Length - ivLength];
        Buffer.BlockCopy(ciphertext, 0, iv, 0, ivLength);
        Buffer.BlockCopy(ciphertext, ivLength, body, 0, body.Length);

        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(body, 0, body.Length);
    }

    private byte[] DeriveAesKey(CredentialProtectionProfile protection)
    {
        var machine = ResolveMachineIdentity();
        var user = protection.LinuxUser ?? Environment.UserName;
        var material = Encoding.UTF8.GetBytes($"{machine}|{user}|{protection.KeyLabel}");
        return SHA256.HashData(material);
    }

    private string ResolveMachineIdentity()
    {
        var configured = Environment.GetEnvironmentVariable(CredentialMachineIdentityEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        if (!UsesPersistentContainerMachineIdentity())
        {
            return _machineNameProvider();
        }

        var identityPath = GetCredentialMachineIdentityPath();
        if (File.Exists(identityPath))
        {
            var persisted = File.ReadAllText(identityPath).Trim();
            if (!string.IsNullOrWhiteSpace(persisted))
            {
                return persisted;
            }

            throw new InvalidOperationException($"NetRatel credential machine identity at '{identityPath}' is empty.");
        }

        // Existing credentials from older containers were derived from the current
        // Docker hostname. Retain that read behavior just long enough to migrate a
        // live installation after it has successfully decrypted its credentials.
        return _machineNameProvider();
    }

    private async Task EnsurePersistentContainerMachineIdentityAsync(CancellationToken ct)
    {
        if (!UsesPersistentContainerMachineIdentity() ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CredentialMachineIdentityEnvironmentVariable)))
        {
            return;
        }

        var identityPath = GetCredentialMachineIdentityPath();
        if (File.Exists(identityPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(identityPath)!;
        Directory.CreateDirectory(directory);

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var temporaryPath = $"{identityPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, generated, Encoding.UTF8, ct).ConfigureAwait(false);
            TryTightenPermissions(temporaryPath);

            try
            {
                File.Move(temporaryPath, identityPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(identityPath))
            {
                // Another process completed the same one-time initialization.
                // Validate its output instead of silently accepting a partial file.
                if (string.IsNullOrWhiteSpace(File.ReadAllText(identityPath).Trim()))
                {
                    throw new InvalidOperationException($"NetRatel credential machine identity at '{identityPath}' is empty.");
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        TryTightenPermissions(identityPath);
    }

    private bool UsesPersistentContainerMachineIdentity() =>
        OperatingSystem.IsLinux() && _containerRuntimeProvider();

    private string GetCredentialMachineIdentityPath() =>
        Path.Combine(Path.GetDirectoryName(_path)!, CredentialMachineIdentityFileName);

    private static bool IsRunningInContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    private static string ResolvePath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        if (OperatingSystem.IsWindows())
        {
            var basePath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(basePath, "NetRatel", "agent.dat");
        }

        var linuxPath = "/var/lib/netratel/agent.dat";
        try
        {
            var dir = Path.GetDirectoryName(linuxPath);
            if (!string.IsNullOrWhiteSpace(dir) && EnsureWritableDirectory(dir))
            {
                return linuxPath;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Unable to use the machine-wide NetRatel credential directory: {0}", exception.Message);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        return ResolveUnixFallbackPath(home, executableDirectory,
            Environment.GetEnvironmentVariable("NETRATEL_POWERSHELL_HOME"));
    }

    internal static string ResolveUnixFallbackPath(string home, string executableDirectory, string? legacyPowerShellHome)
    {
        // The removed embedded host changed HOME before credential initialization.
        // Keep an existing installation in its original directory, including its
        // refresh journal and container machine identity, without changing HOME.
        var legacyHome = string.IsNullOrWhiteSpace(legacyPowerShellHome)
            ? Path.Combine(executableDirectory, "_psprofile") : legacyPowerShellHome;
        var legacyPath = Path.Combine(legacyHome, ".local", "share", "netratel", "agent.dat");
        if (TryGetExistingCredentialFile(legacyPath)) return legacyPath;

        return Path.Combine(home, ".local", "share", "netratel", "agent.dat");
    }

    private static string? ResolveLegacyPath(string? overridePath)
    {
        // A caller-supplied credential path is an explicit installation choice. Only the
        // machine-wide default location participates in the one-time STO path migration.
        if (!string.IsNullOrWhiteSpace(overridePath) || !OperatingSystem.IsLinux())
        {
            return null;
        }

        return "/var/lib/sto/agent.dat";
    }

    private static bool EnsureWritableDirectory(string directory)
    {
        Directory.CreateDirectory(directory);

        var probe = Path.Combine(directory, $".netratel-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Unable to remove NetRatel credential write probe: {0}", cleanupException.Message);
            }

            return false;
        }
    }

    private static void TryTightenPermissions(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning("Unable to tighten NetRatel credential file permissions: {0}", exception.Message);
        }
    }

    private async Task<CredentialPayload?> TryLoadPayloadAsync(CancellationToken ct)
    {
        if (!TryGetExistingCredentialFile(_path))
        {
            return await TryMigrateLegacyPathAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            var current = TryDeserialize(encrypted, CurrentProtection);
            if (current is not null)
            {
                if (UsesPersistentContainerMachineIdentity() &&
                    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CredentialMachineIdentityEnvironmentVariable)) &&
                    !File.Exists(GetCredentialMachineIdentityPath()))
                {
                    // The payload was successfully read using the legacy container
                    // hostname contract. Rewrite it once under a stable identity
                    // before a future recreate gives the container a new hostname.
                    await WritePayloadAsync(current, ct).ConfigureAwait(false);
                }

                return current;
            }

            var legacy = TryDeserialize(encrypted, LegacyStoProtection);
            if (legacy is null)
            {
                throw new AgentCredentialStoreException();
            }

            await WritePayloadAsync(legacy, ct).ConfigureAwait(false);
            return legacy;
        }
        catch (IOException exception)
        {
            throw new AgentCredentialStoreException(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentCredentialStoreException(exception);
        }
    }

    private async Task<CredentialPayload?> TryMigrateLegacyPathAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_legacyPath) || !TryGetExistingCredentialFile(_legacyPath))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_legacyPath, ct).ConfigureAwait(false);
            var payload = TryDeserialize(encrypted, CurrentProtection) ?? TryDeserialize(encrypted, LegacyStoProtection);
            if (payload is null)
            {
                throw new AgentCredentialStoreException();
            }

            // WritePayloadAsync uses a temp file and atomic rename. The legacy file stays
            // intact until the durable NetRatel-path write has completed, so an interrupted
            // migration cannot lose an installation identity.
            await WritePayloadAsync(payload, ct).ConfigureAwait(false);
            TryDeleteLegacyPath(_legacyPath);
            return payload;
        }
        catch (IOException exception)
        {
            throw new AgentCredentialStoreException(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentCredentialStoreException(exception);
        }
    }

    private static bool TryGetExistingCredentialFile(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException exception)
        {
            throw new AgentCredentialStoreException(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentCredentialStoreException(exception);
        }
    }

    private static void TryDeleteLegacyPath(string legacyPath)
    {
        try
        {
            File.Delete(legacyPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Unable to remove migrated legacy STO credential file: {0}", exception.Message);
        }
    }

    private CredentialPayload? TryDeserialize(byte[] encrypted, CredentialProtectionProfile protection)
    {
        try
        {
            var json = Unprotect(encrypted, protection);
            var payload = JsonSerializer.Deserialize<CredentialPayload>(json);
            return HasRecognizedPayload(payload) ? payload : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasRecognizedPayload(CredentialPayload? payload)
    {
        if (payload is null)
        {
            return false;
        }

        var hasCredentials = !string.IsNullOrWhiteSpace(payload.AgentId) &&
            !string.IsNullOrWhiteSpace(payload.RefreshToken);
        var hasDeviceKey = !string.IsNullOrWhiteSpace(payload.PublicKey) &&
            !string.IsNullOrWhiteSpace(payload.PrivateKey);
        return hasCredentials || hasDeviceKey;
    }

    private Task WritePayloadAsync(CredentialPayload payload, CancellationToken ct) =>
        WriteProtectedFileAsync(_path, JsonSerializer.SerializeToUtf8Bytes(payload), CurrentProtection, ct);

    private async Task WriteProtectedFileAsync(string path, byte[] plaintext, CredentialProtectionProfile protection, CancellationToken ct)
    {
        await EnsurePersistentContainerMachineIdentityAsync(ct).ConfigureAwait(false);
        var encrypted = ProtectForPurpose(plaintext, protection);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (OperatingSystem.IsWindows()) WindowsAgentDataDirectory.EnsureForPath(directory);
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                TryTightenPermissions(temporaryPath);
                await stream.WriteAsync(encrypted, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        TryTightenPermissions(path);
    }

    private sealed record CredentialPayload(
        string? AgentId,
        string? RefreshToken,
        string? PublicKey,
        string? PrivateKey,
        string? KeyAlgorithm);

    private sealed record CredentialProtectionProfile(byte[] Entropy, string? LinuxUser, string KeyLabel);
}

public sealed class AgentCredentialStoreException : InvalidOperationException
{
    public AgentCredentialStoreException(Exception? innerException = null)
        : this("An existing NetRatel credential file cannot be read or decrypted for the current service identity. Its bytes were preserved; no replacement installation identity was created.", innerException)
    {
    }

    public AgentCredentialStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
