using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

internal sealed record PhysicalProduct(string Version, string Source, string ApiImage, string WebImage);
internal sealed record PhysicalProducer(string ImageId, string ExecutableSha256, string Version, string Source,
    string Producer, int SlowCollectionSeconds, int MaximumLifetimeSeconds, string CaSha256,
    bool TrustedTlsAlpnVerified, bool UnrelatedCaRejected, bool OwnedExt4Visible);
internal sealed record PhysicalVolume(long BackingBytes, long SelectedAvailableBefore, long HostAvailableBefore,
    string Filesystem, bool SparseBacking, PhysicalAllocation Allocation, PhysicalAllocation Recovery, PhysicalAllocation SecondAllocation);
internal sealed record PhysicalCleanup(bool ClientStoppedAndRemoved, bool MountRemoved, bool LoopDetached,
    bool BackingRemoved, bool PrivateCredentialsRemoved, bool AllApiProcessesExited,
    bool BothProductFixturesDisposed, bool ApiPrivateStateRemoved, bool OwnedMetadataRemoved, int ImagesRemoved, int FleetChanges);
internal sealed record PhysicalDiskProof(string Schema, string Status, string CandidateSource, string CandidateTree,
    string ReviewedSource, string ActualTestCheckout, PhysicalProduct Companion, PhysicalProducer Producer,
    PhysicalAdmission Admission, PhysicalVolume Volume, PhysicalRule Rule, PhysicalRecordedTask RecordedTask,
    PhysicalDiskSample[] BaselineCollections, PhysicalDiskSample[] BreachCollections,
    PhysicalDiskSample[] ContinuedBreachCollections, PhysicalDiskSample[] RecoveryCollections,
    PhysicalDiskSample[] SecondBreachCollections, PhysicalCommittedLoss CommittedLoss, PhysicalRotation Rotation,
    PhysicalDurableProof DurableIdentities, PhysicalUnlinkProof Unlink, PhysicalApiProcess[] ApiProcesses,
    PhysicalCleanup Cleanup);

internal sealed partial class PhysicalIncidentFixture
{
    public async ValueTask DisposeAsync()
    {
        if (closed) return;
        var ownedPair = pair;
        var ownedNetRatel = ownedPair?.NetRatel;
        var failures = new List<Exception>();
        if (cachedAuthorization is not null) try { await cachedAuthorization.DisposeAsync(); } catch (Exception e) { failures.Add(e); }
        // Continue owned cleanup after an individual failure, but never export PASS
        // unless every owned producer/process/filesystem/private-state cleanup proves.
        if (responseLoss is not null) try { await responseLoss.DisposeAsync(); } catch (Exception e) { failures.Add(e); }
        if (disk is not null) try { await disk.DisposeAsync(); } catch (Exception e) { failures.Add(e); }
        if (ownedPair is not null)
        {
            try { await ownedPair.DisposeAsync(); } catch (Exception e) { failures.Add(e); }
            terminalProcesses = ownedNetRatel is null ? [] : ownedNetRatel.PhysicalProcesses.ToArray();
        }
        try { listener.Dispose(); } catch (Exception e) { failures.Add(e); }
        if (failures.Count > 0) throw new InvalidOperationException("Physical acceptance failed owned cleanup; no PASS artifact is permitted.", new AggregateException(failures));
        Require((ownedPair is null || ownedNetRatel is not null && ownedPair.OwnedCleanupComplete && ownedNetRatel.PhysicalOwnedCleanupComplete) &&
            (disk is null || disk.OwnedMetadataRemoved),
            "Every actual product, API private state and acknowledged helper metadata owner must prove cleanup.");
        closed = true;
    }

    public async Task WritePrivacySafePhysicalProofAsync(PhysicalAdmission admitted, PhysicalRule rule,
        PhysicalDiskSample[] baseline, PhysicalDiskSample[] firstBreach, PhysicalDiskSample[] continuedBad,
        PhysicalDiskSample[] recovery, PhysicalDiskSample[] secondBreach, PhysicalAllocation allocated,
        PhysicalAllocation recovered, PhysicalRotation rotation, PhysicalCommittedLoss loss,
        PhysicalDurableProof terminal, PhysicalUnlinkProof unlink, CancellationToken ct)
    {
        var cleanup = disk.Cleanup ?? throw new InvalidOperationException("The owned physical disk helper has not produced cleanup evidence.");
        Require(closed && disk.OwnedMetadataRemoved && pair.OwnedCleanupComplete &&
            pair.NetRatel.PhysicalOwnedCleanupComplete && terminalProcesses is { Length: 4 } &&
            terminalProcesses.All(x => x.ExitedAtUtc is not null) && terminalProcesses.Select(x => x.ProcessId).Distinct().Count() == 4 &&
            terminalProcesses.Count(x => x.Role == "primary") == 2 && terminalProcesses.Count(x => x.Role == "replica") == 2 &&
            recordedTask is not null && allocationCount == 2 && secondAllocation is not null && terminal.ActionSucceeded && !terminal.MayHaveCommitted && terminal.Actions.Length == 2 &&
            unlink.CachedAuthorizationCommonExpiresAtUtc > unlink.LatestDenialObservedAtUtc &&
            unlink.PositivelyControlledInboundReplicas == 2 && unlink.ImmediateDeniedInboundReplicas == 2 &&
            unlink.SettledDeniedInboundReplicas == 2 && unlink.SamePrivateAuthorizationHandleUsed,
            "Physical proof requires actual completed acceptance and positive owned cleanup facts.");
        var current = await ServiceLinkNativeArtifacts.ResolveAsync(ct);
        Require(current == artifacts, "The actual source/artifacts changed before cleanup-first proof export.");
        var tree = await ReadGitTreeAsync(ct);
        var reviewed = Environment.GetEnvironmentVariable("NETRATEL_REVIEW_SOURCE_SHA") ?? artifacts.CandidateSourceSha;
        Require(reviewed.Length == 40 && reviewed.All(Uri.IsHexDigit), "The reviewed source identity is invalid.");
        var volume = provision.GetProperty("volume");
        var proof = new PhysicalDiskProof("netratel.physical-disk-incident-proof.v2", "PASS_ACTUAL_PHYSICAL_INCIDENT",
            artifacts.CandidateSourceSha, tree, reviewed, artifacts.CandidateSourceSha,
            new(ServiceLinkPublishedRatelDeskPeer.PublishedVersion, ServiceLinkPublishedRatelDeskPeer.PublishedSource,
                ServiceLinkPublishedRatelDeskPeer.ApiImage, ServiceLinkPublishedRatelDeskPeer.WebImage),
            new(client.GetProperty("clientImageId").GetString()!, client.GetProperty("clientExecutableSha256").GetString()!,
                client.GetProperty("clientVersion").GetString()!, client.GetProperty("sourceSha").GetString()!,
                client.GetProperty("producer").GetString()!, client.GetProperty("slowCollectionSeconds").GetInt32(),
                client.GetProperty("maximumClientLifetimeSeconds").GetInt32(), client.GetProperty("caSha256").GetString()!,
                client.GetProperty("trustedTlsAlpnVerified").GetBoolean(), client.GetProperty("unrelatedCaRejected").GetBoolean(),
                client.GetProperty("daemonVisibleOwnedExt4Verified").GetBoolean()), admitted,
            new(volume.GetProperty("backingBytes").GetInt64(), volume.GetProperty("selectedAvailable").GetInt64(),
                volume.GetProperty("hostAvailable").GetInt64(), volume.GetProperty("realFilesystem").GetString()!,
                volume.GetProperty("sparseBacking").GetBoolean(), allocated, recovered, secondAllocation!), rule, recordedTask!, baseline,
            firstBreach, continuedBad, recovery, secondBreach, loss, rotation, terminal, unlink, terminalProcesses!,
            new(cleanup.GetProperty("ownedClientStoppedAndRemoved").GetBoolean(), cleanup.GetProperty("ownedMountRemoved").GetBoolean(),
                cleanup.GetProperty("ownedLoopDetached").GetBoolean(), cleanup.GetProperty("ownedBackingRemoved").GetBoolean(),
                cleanup.GetProperty("privateCredentialsRemoved").GetBoolean(), true, pair.OwnedCleanupComplete,
                pair.NetRatel.PhysicalOwnedCleanupComplete, disk.OwnedMetadataRemoved, cleanup.GetProperty("imagesRemoved").GetInt32(),
                cleanup.GetProperty("fleetChanges").GetInt32()));
        var directory = Environment.GetEnvironmentVariable("NETRATEL_PHYSICAL_DISK_EVIDENCE_DIRECTORY")
            ?? Path.Combine("TestResults", "physical-disk-incident");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "disk-incident-" + Guid.NewGuid().ToString("N") + ".json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, proof, new JsonSerializerOptions(json) { WriteIndented = true }, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> ReadGitTreeAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = FindRepository(), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "rev-parse", "HEAD^{tree}" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Physical proof source inspection could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token); var tree = (await output).Trim(); _ = await errors;
            Require(process.ExitCode == 0 && tree.Length == 40 && tree.All(Uri.IsHexDigit), "Physical proof has no actual source tree identity.");
            return tree;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            try { _ = await output; _ = await errors; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }
}
