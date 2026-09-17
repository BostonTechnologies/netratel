using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Infrastructure.Auth;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class AgentCredentialStoreTests
{
    [Fact]
    public async Task ClearRefreshCredentialsAsync_PreservesStableDeviceKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-agent-store-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "agent.dat");
        Directory.CreateDirectory(directory);

        try
        {
            var store = new AgentCredentialStore(path);
            var originalKey = await store.GetOrCreateAsync(CancellationToken.None);
            await store.SaveAsync(Guid.NewGuid().ToString(), "refresh-token");

            await store.ClearRefreshCredentialsAsync();

            (await store.LoadAsync()).Should().BeNull();
            (await store.LoadAsync(CancellationToken.None)).Should().Be(originalKey);
            (await store.GetOrCreateAsync(CancellationToken.None)).Should().Be(originalKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResetInstallationIdentityAsync_RemovesDeviceKeyAndCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-agent-store-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "agent.dat");
        Directory.CreateDirectory(directory);

        try
        {
            var store = new AgentCredentialStore(path);
            await store.GetOrCreateAsync(CancellationToken.None);
            await store.SaveAsync(Guid.NewGuid().ToString(), "refresh-token");

            await store.ResetInstallationIdentityAsync();

            (await store.LoadAsync()).Should().BeNull();
            (await store.LoadAsync(CancellationToken.None)).Should().BeNull();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyStoLinuxCredentialStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-agent-store-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "agent.dat");
        Directory.CreateDirectory(directory);

        try
        {
            var legacyBytes = EncryptLegacyStoPayload(new CredentialPayload(
                "agent-legacy",
                "refresh-legacy",
                "public-key",
                "private-key",
                "ecdsa-p256"));
            await File.WriteAllBytesAsync(path, legacyBytes);

            var store = new AgentCredentialStore(path);
            var credentials = await store.LoadAsync();

            credentials.Should().Be(("agent-legacy", "refresh-legacy"));
            var migratedBytes = await File.ReadAllBytesAsync(path);
            migratedBytes.Should().NotEqual(legacyBytes);
            (await new AgentCredentialStore(path).LoadAsync())
                .Should().Be(("agent-legacy", "refresh-legacy"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyStoPathToNetRatelPathWithoutOverwritingExistingCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-agent-store-{Guid.NewGuid():N}");
        var currentPath = Path.Combine(directory, "netratel", "agent.dat");
        var legacyPath = Path.Combine(directory, "sto", "agent.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);

        try
        {
            var legacyBytes = EncryptLegacyStoPayload(new CredentialPayload(
                "agent-legacy",
                "refresh-legacy",
                "public-key",
                "private-key",
                "ecdsa-p256"));
            await File.WriteAllBytesAsync(legacyPath, legacyBytes);

            var store = new AgentCredentialStore(currentPath, legacyPath);

            (await store.LoadAsync()).Should().Be(("agent-legacy", "refresh-legacy"));
            File.Exists(currentPath).Should().BeTrue();
            File.Exists(legacyPath).Should().BeFalse();
            (await new AgentCredentialStore(currentPath).LoadAsync()).Should().Be(("agent-legacy", "refresh-legacy"));

            await File.WriteAllBytesAsync(legacyPath, legacyBytes);
            await store.SaveAsync("agent-current", "refresh-current");

            (await store.LoadAsync()).Should().Be(("agent-current", "refresh-current"));
            File.Exists(legacyPath).Should().BeTrue("an existing NetRatel credential must never be replaced by a legacy STO file");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_UsesPersistedMachineIdentityAcrossContainerRecreate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-agent-store-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "agent.dat");
        Directory.CreateDirectory(directory);

        try
        {
            var beforeRecreate = new AgentCredentialStore(
                path,
                machineNameProvider: () => "container-before-recreate",
                containerRuntimeProvider: () => true);
            await beforeRecreate.SaveAsync("agent-container", "refresh-container");

            var identityPath = Path.Combine(directory, ".netratel-credential-machine-id");
            File.Exists(identityPath).Should().BeTrue();

            var afterRecreate = new AgentCredentialStore(
                path,
                machineNameProvider: () => "container-after-recreate",
                containerRuntimeProvider: () => true);

            (await afterRecreate.LoadAsync()).Should().Be(("agent-container", "refresh-container"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] EncryptLegacyStoPayload(CredentialPayload payload)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        var keyMaterial = Encoding.UTF8.GetBytes($"{Environment.MachineName}|sto|sto-agent-store-v1");

        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(keyMaterial);
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        var encrypted = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, encrypted, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, encrypted, aes.IV.Length, cipher.Length);
        return encrypted;
    }

    private sealed record CredentialPayload(
        string AgentId,
        string RefreshToken,
        string PublicKey,
        string PrivateKey,
        string KeyAlgorithm);
}
