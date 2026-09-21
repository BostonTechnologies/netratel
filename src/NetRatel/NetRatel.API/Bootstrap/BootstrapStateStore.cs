using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetRatel.API.Bootstrap;

/// <summary>
/// Owns descriptor, operation journal, and local bootstrap proof files. Every mutation holds an
/// exclusive cross-process lease and replaces the descriptor atomically, so a crash can only leave
/// the last complete descriptor plus an auditable operation record.
/// </summary>
public sealed class BootstrapStateStore
{
    private const string DescriptorFileName = "descriptor.json";
    private const string JournalFileName = "journal.jsonl";
    private const string KeyMaterialProofFileName = "key-material-proof";
    private const string GeneratedSetupProofFileName = "setup-proof";
    private const string LockFileName = "bootstrap.lock";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly BootstrapOptions _options;
    private readonly TimeProvider _timeProvider;

    public BootstrapStateStore(BootstrapOptions options, TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BootstrapDescriptor> LoadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = await ReadDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (descriptor is not null)
        {
            descriptor = await ValidateKeyMaterialAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (descriptor.State == BootstrapState.Configuring &&
                (descriptor.OperationLeaseExpiresAtUtc is null || descriptor.OperationLeaseExpiresAtUtc <= _timeProvider.GetUtcNow()))
            {
                return await EnterRecoveryAsync(descriptor, "configuration-lease-expired", cancellationToken).ConfigureAwait(false);
            }

            return await RenewExpiredSetupProofAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }

        var hasRecoveryEvidence = File.Exists(PathFor(JournalFileName)) || File.Exists(PathFor(KeyMaterialProofFileName));
        if (hasRecoveryEvidence)
        {
            var recovery = CreateDescriptor(BootstrapState.RecoveryRequired, null, null, null, false);
            await WriteDescriptorAsync(recovery, cancellationToken).ConfigureAwait(false);
            await WriteJournalAsync(recovery, "descriptor-missing", cancellationToken).ConfigureAwait(false);
            return recovery;
        }

        var created = CreateDescriptor(BootstrapState.Unconfigured, null, null, null, false);
        await WritePrivateRandomFileAsync(PathFor(KeyMaterialProofFileName), cancellationToken).ConfigureAwait(false);
        created = created with { KeyMaterialProofHash = Hash(await ReadPrivateFileAsync(PathFor(KeyMaterialProofFileName), cancellationToken).ConfigureAwait(false)) };
        created = await EnsureSetupProofAsync(created, cancellationToken).ConfigureAwait(false);
        await WriteDescriptorAsync(created, cancellationToken).ConfigureAwait(false);
        await WriteJournalAsync(created, "descriptor-created", cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<BootstrapDescriptor> UpdateAsync(
        Func<BootstrapDescriptor, BootstrapDescriptor> update,
        string eventName,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = await ReadDescriptorAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The bootstrap descriptor is missing; recovery is required.");
        descriptor = await ValidateKeyMaterialAsync(descriptor, cancellationToken).ConfigureAwait(false);
        var updated = update(descriptor) with { UpdatedAtUtc = _timeProvider.GetUtcNow() };
        await WriteDescriptorAsync(updated, cancellationToken).ConfigureAwait(false);
        await WriteJournalAsync(updated, eventName, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<bool> VerifySetupProofAsync(string proof, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(proof))
        {
            return false;
        }

        await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = await ReadDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (descriptor is null || !IsSha256Hash(descriptor.SetupProofHash) || descriptor.State is BootstrapState.Ready or BootstrapState.RecoveryRequired || descriptor.SetupProofExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(descriptor.SetupProofHash),
            SHA256.HashData(Encoding.UTF8.GetBytes(proof)));
    }

    public async Task<BootstrapClaimResult> ClaimSetupAsync(
        string proof,
        string? selectedProvider,
        string? connectionReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(proof))
        {
            return BootstrapClaimResult.Rejected();
        }

        await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = await ReadDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return BootstrapClaimResult.Rejected();
        }

        descriptor = await ValidateKeyMaterialAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (descriptor.State != BootstrapState.Unconfigured || descriptor.SetupProofExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return BootstrapClaimResult.Rejected(descriptor);
        }

        if (!IsSha256Hash(descriptor.SetupProofHash))
        {
            descriptor = await EnterRecoveryAsync(descriptor, "setup-proof-hash-invalid", cancellationToken).ConfigureAwait(false);
            return BootstrapClaimResult.Rejected(descriptor);
        }

        var expectedHash = Convert.FromHexString(descriptor.SetupProofHash);
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(proof));
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            return BootstrapClaimResult.Rejected(descriptor);
        }

        var operationId = Guid.NewGuid();
        var claimed = descriptor with
        {
            State = BootstrapState.Configuring,
            SetupProofHash = string.Empty,
            RecoveryReason = null,
            SelectedProvider = selectedProvider,
            ConnectionReference = connectionReference,
            OperationId = operationId,
            OperationLeaseExpiresAtUtc = _timeProvider.GetUtcNow() + _options.OperationLeaseDuration,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };
        await WriteDescriptorAsync(claimed, cancellationToken).ConfigureAwait(false);
        await WriteJournalAsync(claimed, "setup-claimed", cancellationToken).ConfigureAwait(false);

        var generatedPath = PathFor(GeneratedSetupProofFileName);
        if (File.Exists(generatedPath))
        {
            File.Delete(generatedPath);
        }

        return BootstrapClaimResult.Accepted(claimed);
    }

    public async Task DeleteSetupProofAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var generatedPath = PathFor(GeneratedSetupProofFileName);
        if (File.Exists(generatedPath))
        {
            File.Delete(generatedPath);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<bool> CompleteSetupAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = await ReadDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (descriptor is null || descriptor.State != BootstrapState.Configuring || descriptor.OperationId != operationId)
        {
            return false;
        }

        descriptor = await ValidateKeyMaterialAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (descriptor.State != BootstrapState.Configuring || descriptor.OperationId != operationId)
        {
            return false;
        }

        var completed = descriptor with
        {
            State = BootstrapState.Ready,
            OperationId = null,
            OperationLeaseExpiresAtUtc = null,
            RecoveryReason = null,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };
        await WriteDescriptorAsync(completed, cancellationToken).ConfigureAwait(false);
        await WriteJournalAsync(completed, "setup-completed", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private BootstrapDescriptor CreateDescriptor(
        BootstrapState state,
        string? provider,
        string? connectionReference,
        Guid? operationId,
        bool adopted)
    {
        var now = _timeProvider.GetUtcNow();
        return new BootstrapDescriptor(
            Version: 1,
            InstanceId: Guid.NewGuid(),
            State: state,
            SetupProofHash: string.Empty,
            SetupProofExpiresAtUtc: now + _options.SetupProofLifetime,
            KeyMaterialProofHash: string.Empty,
            SelectedProvider: provider,
            ConnectionReference: connectionReference,
            OperationId: operationId,
            OperationLeaseExpiresAtUtc: operationId is null ? null : now + _options.OperationLeaseDuration,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            AdoptedExistingInstallation: adopted);
    }

    private async Task<BootstrapDescriptor> ValidateKeyMaterialAsync(BootstrapDescriptor descriptor, CancellationToken cancellationToken)
    {
        var keyMaterialPath = PathFor(KeyMaterialProofFileName);
        if (!File.Exists(keyMaterialPath))
        {
            return await EnterRecoveryAsync(descriptor, "key-material-missing", cancellationToken).ConfigureAwait(false);
        }

        var actualHash = Hash(await ReadPrivateFileAsync(keyMaterialPath, cancellationToken).ConfigureAwait(false));
        var expectedHash = IsSha256Hash(descriptor.KeyMaterialProofHash)
            ? Convert.FromHexString(descriptor.KeyMaterialProofHash)
            : null;

        if (expectedHash is null || !CryptographicOperations.FixedTimeEquals(expectedHash, Convert.FromHexString(actualHash)))
        {
            return await EnterRecoveryAsync(descriptor, "key-material-mismatch", cancellationToken).ConfigureAwait(false);
        }

        return descriptor;
    }

    private async Task<BootstrapDescriptor> RenewExpiredSetupProofAsync(BootstrapDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.State != BootstrapState.Unconfigured || descriptor.SetupProofExpiresAtUtc > _timeProvider.GetUtcNow())
        {
            return descriptor;
        }

        var proofPath = _options.SetupProofPath ?? PathFor(GeneratedSetupProofFileName);
        if (_options.SetupProofPath is null)
        {
            await WritePrivateRandomFileAsync(proofPath, cancellationToken).ConfigureAwait(false);
        }
        else if (!File.Exists(proofPath))
        {
            // A deployment-managed secret must be replaced by the deployment owner, not
            // generated into an arbitrary mounted secret path by the application.
            return descriptor;
        }

        var replacementHash = Hash(await ReadPrivateFileAsync(proofPath, cancellationToken).ConfigureAwait(false));
        if (string.Equals(replacementHash, descriptor.SetupProofHash, StringComparison.Ordinal))
        {
            return descriptor;
        }

        var replacement = descriptor with
        {
            SetupProofHash = replacementHash,
            SetupProofExpiresAtUtc = _timeProvider.GetUtcNow() + _options.SetupProofLifetime,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };
        await WriteDescriptorAsync(replacement, cancellationToken).ConfigureAwait(false);
        await WriteJournalAsync(replacement, "setup-proof-renewed", cancellationToken).ConfigureAwait(false);
        return replacement;
    }

    private async Task<BootstrapDescriptor> EnterRecoveryAsync(BootstrapDescriptor descriptor, string eventName, CancellationToken cancellationToken)
    {
        var recovery = descriptor with
        {
            State = BootstrapState.RecoveryRequired,
            OperationId = null,
            OperationLeaseExpiresAtUtc = null,
            RecoveryReason = eventName,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };
        await WriteDescriptorAsync(recovery, cancellationToken).ConfigureAwait(false);
        await WriteJournalAsync(recovery, eventName, cancellationToken).ConfigureAwait(false);
        return recovery;
    }

    private async Task<BootstrapDescriptor> EnsureSetupProofAsync(BootstrapDescriptor descriptor, CancellationToken cancellationToken)
    {
        var proofPath = _options.SetupProofPath ?? PathFor(GeneratedSetupProofFileName);
        if (!File.Exists(proofPath))
        {
            await WritePrivateRandomFileAsync(proofPath, cancellationToken).ConfigureAwait(false);
        }

        var proof = await ReadPrivateFileAsync(proofPath, cancellationToken).ConfigureAwait(false);
        return descriptor with { SetupProofHash = Hash(proof) };
    }

    private async Task<BootstrapDescriptor?> ReadDescriptorAsync(CancellationToken cancellationToken)
    {
        var path = PathFor(DescriptorFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<BootstrapDescriptor>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // The retained key-material proof causes LoadOrCreateAsync to reconstruct only a
            // RecoveryRequired descriptor; malformed lifecycle state is never a fresh install.
            return null;
        }
    }

    private async Task WriteDescriptorAsync(BootstrapDescriptor descriptor, CancellationToken cancellationToken)
    {
        var target = PathFor(DescriptorFileName);
        var temporary = target + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, descriptor, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        SetPrivatePermissions(temporary);
        File.Move(temporary, target, true);
        SetPrivatePermissions(target);
    }

    private async Task WriteJournalAsync(BootstrapDescriptor descriptor, string eventName, CancellationToken cancellationToken)
    {
        var operationId = descriptor.OperationId ?? Guid.NewGuid();
        var entry = new BootstrapJournalEntry(operationId, eventName, descriptor.State, _timeProvider.GetUtcNow());
        await File.AppendAllTextAsync(PathFor(JournalFileName), JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        SetPrivatePermissions(PathFor(JournalFileName));
    }

    private async Task<BootstrapLease> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.StateDirectory);
        SetPrivateDirectoryPermissions(_options.StateDirectory);
        var leaseDeadline = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (true)
        {
            FileStream? stream = null;
            try
            {
                stream = new FileStream(PathFor(LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
                var usesRangeLock = !OperatingSystem.IsMacOS();
                if (usesRangeLock)
                {
                    stream.Lock(0, 1);
                }

                return new BootstrapLease(stream, usesRangeLock);
            }
            catch (IOException) when (_timeProvider.GetUtcNow() < leaseDeadline)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            catch
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            if (_timeProvider.GetUtcNow() >= leaseDeadline)
            {
                throw new TimeoutException("Timed out waiting for the bootstrap state lease.");
            }
        }
    }

    private async Task WritePrivateRandomFileAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(path, random, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        SetPrivatePermissions(path);
    }

    private static async Task<string> ReadPrivateFileAsync(string path, CancellationToken cancellationToken)
        => (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();

    private string PathFor(string fileName) => Path.Combine(_options.StateDirectory, fileName);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha256Hash(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static void SetPrivatePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void SetPrivateDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed class BootstrapLease(FileStream stream, bool usesRangeLock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (usesRangeLock && !OperatingSystem.IsMacOS())
            {
                stream.Unlock(0, 1);
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed record BootstrapClaimResult(bool Succeeded, BootstrapDescriptor? Descriptor)
{
    public static BootstrapClaimResult Accepted(BootstrapDescriptor descriptor) => new(true, descriptor);
    public static BootstrapClaimResult Rejected(BootstrapDescriptor? descriptor = null) => new(false, descriptor);
}
