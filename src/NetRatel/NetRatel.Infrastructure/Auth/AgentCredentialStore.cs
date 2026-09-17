using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.ClientAuth;

namespace NetRatel.Infrastructure.Auth;

public sealed class AgentCredentialStore : IAgentCredentialStore, IAgentDeviceKeyStore
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

    public async Task SaveAsync(string agentId, string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("agentId is required", nameof(agentId));
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new ArgumentException("refreshToken is required", nameof(refreshToken));

        var existing = await TryLoadPayloadAsync().ConfigureAwait(false);
        var payload = new CredentialPayload(
            agentId,
            refreshToken,
            existing?.PublicKey,
            existing?.PrivateKey,
            existing?.KeyAlgorithm ?? "ecdsa-p256");
        await WritePayloadAsync(payload, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<(string AgentId, string RefreshToken)?> LoadAsync()
    {
        var payload = await TryLoadPayloadAsync().ConfigureAwait(false);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AgentId) || string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            return null;
        }

        return (payload.AgentId, payload.RefreshToken);
    }

    public async Task ClearRefreshCredentialsAsync()
    {
        var existing = await TryLoadPayloadAsync().ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var payload = new CredentialPayload(
            AgentId: null,
            RefreshToken: null,
            existing.PublicKey,
            existing.PrivateKey,
            existing.KeyAlgorithm ?? "ecdsa-p256");
        await WritePayloadAsync(payload, CancellationToken.None).ConfigureAwait(false);
    }

    public Task ResetInstallationIdentityAsync()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    [Obsolete("Use ClearRefreshCredentialsAsync so the stable device identity is preserved.")]
    public Task ClearAsync() => ClearRefreshCredentialsAsync();

    public async Task<AgentDeviceKeyMaterial> GetOrCreateAsync(CancellationToken ct)
    {
        var existing = await TryLoadPayloadAsync().ConfigureAwait(false);
        if (existing is not null &&
            !string.IsNullOrWhiteSpace(existing.PublicKey) &&
            !string.IsNullOrWhiteSpace(existing.PrivateKey))
        {
            return new AgentDeviceKeyMaterial(existing.PublicKey, existing.PrivateKey, existing.KeyAlgorithm ?? "ecdsa-p256");
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = ecdsa.ExportPkcs8PrivateKey();
        var publicKey = ecdsa.ExportSubjectPublicKeyInfo();

        var generated = new AgentDeviceKeyMaterial(
            Convert.ToBase64String(publicKey),
            Convert.ToBase64String(privateKey),
            "ecdsa-p256");

        var payload = new CredentialPayload(
            existing?.AgentId,
            existing?.RefreshToken,
            generated.PublicKey,
            generated.PrivateKey,
            generated.Algorithm);
        await WritePayloadAsync(payload, ct).ConfigureAwait(false);

        return generated;
    }

    public async Task<AgentDeviceKeyMaterial?> LoadAsync(CancellationToken ct)
    {
        var payload = await TryLoadPayloadAsync().ConfigureAwait(false);
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.PublicKey) ||
            string.IsNullOrWhiteSpace(payload.PrivateKey))
        {
            return null;
        }

        return new AgentDeviceKeyMaterial(payload.PublicKey, payload.PrivateKey, payload.KeyAlgorithm ?? "ecdsa-p256");
    }

    private byte[] Protect(byte[] plaintext)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Protect(plaintext, CurrentProtection.Entropy, DataProtectionScope.CurrentUser);
        }

        using var aes = Aes.Create();
        aes.Key = DeriveAesKey(CurrentProtection);
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

    private async Task<CredentialPayload?> TryLoadPayloadAsync()
    {
        if (!File.Exists(_path))
        {
            return await TryMigrateLegacyPathAsync().ConfigureAwait(false);
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_path).ConfigureAwait(false);
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
                    await WritePayloadAsync(current, CancellationToken.None).ConfigureAwait(false);
                }

                return current;
            }

            var legacy = TryDeserialize(encrypted, LegacyStoProtection);
            if (legacy is null)
            {
                return null;
            }

            await WritePayloadAsync(legacy, CancellationToken.None).ConfigureAwait(false);
            return legacy;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<CredentialPayload?> TryMigrateLegacyPathAsync()
    {
        if (string.IsNullOrWhiteSpace(_legacyPath) || !File.Exists(_legacyPath))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_legacyPath).ConfigureAwait(false);
            var payload = TryDeserialize(encrypted, CurrentProtection) ?? TryDeserialize(encrypted, LegacyStoProtection);
            if (payload is null)
            {
                return null;
            }

            // WritePayloadAsync uses a temp file and atomic rename. The legacy file stays
            // intact until the durable NetRatel-path write has completed, so an interrupted
            // migration cannot lose an installation identity.
            await WritePayloadAsync(payload, CancellationToken.None).ConfigureAwait(false);
            TryDeleteLegacyPath(_legacyPath);
            return payload;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
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
            return JsonSerializer.Deserialize<CredentialPayload>(json);
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

    private async Task WritePayloadAsync(CredentialPayload payload, CancellationToken ct)
    {
        await EnsurePersistentContainerMachineIdentityAsync(ct).ConfigureAwait(false);
        var encrypted = Protect(JsonSerializer.SerializeToUtf8Bytes(payload));
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, encrypted, ct).ConfigureAwait(false);
            TryTightenPermissions(temporaryPath);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        TryTightenPermissions(_path);
    }

    private sealed record CredentialPayload(
        string? AgentId,
        string? RefreshToken,
        string? PublicKey,
        string? PrivateKey,
        string? KeyAlgorithm);

    private sealed record CredentialProtectionProfile(byte[] Entropy, string? LinuxUser, string KeyLabel);
}
