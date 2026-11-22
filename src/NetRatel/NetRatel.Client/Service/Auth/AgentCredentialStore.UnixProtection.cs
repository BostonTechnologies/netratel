using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace NetRatel.Client.Service.Auth;

public sealed partial class AgentCredentialStore
{
    private static ReadOnlySpan<byte> FormatHeader => "NetRatel-AES-GCM-v2\0"u8;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const string KeyFileEnvironment = "NetRatel_CREDENTIAL_PROTECTION_KEY_FILE";
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static bool IsLegacyUnixPayload(byte[] payload) =>
        !OperatingSystem.IsWindows() && !payload.AsSpan().StartsWith(FormatHeader);

    internal static string GetUnixProtectionKeyPath(string credentialPath)
    {
        var configured = Environment.GetEnvironmentVariable(KeyFileEnvironment);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new CryptographicException("The credential protection key path must be absolute.");
            return Path.GetFullPath(configured);
        }

        if (Path.GetFullPath(credentialPath) == "/var/lib/netratel/agent.dat")
            return "/etc/netratel/agent-protection/credential.key";

        var configDirectory = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configDirectory) || !Path.IsPathFullyQualified(configDirectory))
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.GetFullPath(Path.Combine(configDirectory, "netratel-client", "credential.key"));
    }

    [UnsupportedOSPlatform("windows")]
    private byte[] ProtectUnix(byte[] plaintext, CredentialProtectionProfile profile)
    {
        var key = LoadUnixProtectionKey(createIfMissing: true);
        try
        {
            var output = new byte[FormatHeader.Length + NonceBytes + TagBytes + plaintext.Length];
            FormatHeader.CopyTo(output);
            var nonce = output.AsSpan(FormatHeader.Length, NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, output.AsSpan(FormatHeader.Length + NonceBytes + TagBytes),
                output.AsSpan(FormatHeader.Length + NonceBytes, TagBytes), profile.Entropy);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private byte[] UnprotectUnix(byte[] ciphertext, CredentialProtectionProfile profile)
    {
        var contents = ciphertext.AsSpan(FormatHeader.Length);
        if (contents.Length <= NonceBytes + TagBytes)
            throw new CryptographicException("Protected credential payload is invalid.");

        var key = LoadUnixProtectionKey(createIfMissing: false);
        try
        {
            var plaintext = new byte[contents.Length - NonceBytes - TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(contents[..NonceBytes], contents[(NonceBytes + TagBytes)..],
                contents.Slice(NonceBytes, TagBytes), plaintext, profile.Entropy);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private byte[] LoadUnixProtectionKey(bool createIfMissing)
    {
        var keyPath = GetUnixProtectionKeyPath(_path);
        var stateDirectory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        if (keyPath.StartsWith(stateDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CryptographicException("The credential protection key must be stored outside the credential state directory.");

        var keyDirectory = Path.GetDirectoryName(keyPath)!;
        if (createIfMissing && !File.Exists(keyPath) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyFileEnvironment)))
        {
            Directory.CreateDirectory(keyDirectory, PrivateFile | UnixFileMode.UserExecute);
            VerifyPrivateDirectory(keyDirectory);
            CreateUnixProtectionKey(keyPath);
        }

        if (new FileInfo(keyPath).LinkTarget is not null ||
            (File.GetUnixFileMode(keyPath) & ~PrivateFile) != 0)
            throw new CryptographicException("The credential protection key requires a private regular file.");

        var key = new byte[KeyBytes];
        try
        {
            using var stream = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != KeyBytes)
                throw new CryptographicException("The credential protection key must contain 32 bytes.");
            stream.ReadExactly(key);
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void VerifyPrivateDirectory(string directory)
    {
        const UnixFileMode privateDirectory = PrivateFile | UnixFileMode.UserExecute;
        if (new DirectoryInfo(directory).LinkTarget is not null ||
            (File.GetUnixFileMode(directory) & ~privateDirectory) != 0)
            throw new CryptographicException("The credential protection key directory must be private.");
    }

    [UnsupportedOSPlatform("windows")]
    private static void CreateUnixProtectionKey(string keyPath)
    {
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        var temporary = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = PrivateFile
            }))
            {
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, keyPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                // Concurrent installations on the same account share the winning key.
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            File.Delete(temporary);
        }
    }
}
