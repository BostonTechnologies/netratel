using AwesomeAssertions;
using NetRatel.Client.Services;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class FileSystemServiceGatewayPathTests
{
    [Fact]
    public async Task CreateOnlyStagingPreservesConflictsAndCreatesARealEmptyFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-create-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "empty.txt");
        try
        {
            using (var create = new AgentFileGatewayClient.ClientFileOperation(Guid.NewGuid(), Guid.NewGuid(), "write", path) { CreateOnly = true })
            {
                create.BeginWrite();
                await create.CommitWriteAsync(CancellationToken.None);
            }
            new FileInfo(path).Length.Should().Be(0);
            File.WriteAllText(path, "keep");
            using var conflict = new AgentFileGatewayClient.ClientFileOperation(Guid.NewGuid(), Guid.NewGuid(), "write", path) { CreateOnly = true };
            conflict.Invoking(operation => operation.BeginWrite()).Should().Throw<IOException>();
            File.ReadAllText(path).Should().Be("keep");
            Directory.EnumerateFiles(root, "*.tmp").Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NativeMovePreservesPosixNamesRefusesCollisionsAndMovesDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, OperatingSystem.IsWindows() ? "Reports" : "Reports ");
        var destination = Path.Combine(root, OperatingSystem.IsWindows() ? "reports" : "reports ");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
        try
        {
            NativeFileOperations.Move(source, destination);
            if (OperatingSystem.IsWindows()) new DirectoryInfo(Directory.EnumerateDirectories(root).Single()).Name.Should().Be("reports");
            else Directory.Exists(source).Should().BeFalse();
            File.ReadAllText(Path.Combine(destination, "keep.txt")).Should().Be("keep");
            var collision = Path.Combine(root, "collision");
            File.WriteAllText(collision, "collision");
            var move = () => NativeFileOperations.Move(destination, collision);
            move.Should().Throw<NativeFileOperationException>().Which.Code.Should().Be("destination_already_exists");
            File.ReadAllText(collision).Should().Be("collision");
            Directory.Exists(destination).Should().BeTrue();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RecursiveDeletionCancelsAfterProgressAndNeverTraversesLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-delete-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"netratel-keep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        try
        {
            var removeLink = () => NativeFileOperations.DeleteTree(root, CancellationToken.None, _ => { });
            removeLink.Should().Throw<NativeFileOperationException>().Which.Code.Should().Be("protected_path");
            File.ReadAllText(Path.Combine(outside, "keep.txt")).Should().Be("keep");
            Directory.Delete(Path.Combine(root, "link"));
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "b.txt"), "b");
            using var cancellation = new CancellationTokenSource();
            long affected = 0;
            var remove = () => NativeFileOperations.DeleteTree(root, cancellation.Token, count => { affected = count; cancellation.Cancel(); });
            remove.Should().Throw<OperationCanceledException>();
            affected.Should().Be(1);
            Directory.EnumerateFiles(root).Should().ContainSingle();
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    [Fact]
    public void Enumeration_ReturnsOnlyImmediateEntriesAndTheirFilesystemTimestamps()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-file-metadata-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "folder");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "nested.txt"), "not enumerated");
        var modified = new DateTime(2026, 1, 15, 10, 20, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(directory, modified);
        try
        {
            var entries = new FileSystemService().EnumerateEntries(root).ToArray();
            var folder = entries.Should().ContainSingle().Which;
            folder.IsDirectory.Should().BeTrue();
            folder.SizeBytes.Should().Be(0, "folder size is not calculated recursively");
            folder.LastModifiedUtc.Should().Be(new DateTimeOffset(modified));
            folder.CreatedUtc.Should().NotBeNull();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MutationGuardRejectsRootsAndFinalLinksWithoutChangingTheirTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-file-mutation-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "alias");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        var files = new FileSystemService();
        try
        {
            files.IsGatewayMutationTargetAllowed(root, root, [root]).Should().BeFalse();
            var filesystemRoot = Path.GetPathRoot(root)!;
            files.IsGatewayMutationTargetAllowed(filesystemRoot, filesystemRoot, []).Should().BeFalse();
            files.IsGatewayMutationTargetAllowed(link, target, [root]).Should().BeFalse();
            files.IsGatewayMutationTargetAllowed(target, target, [root]).Should().BeTrue();
            Directory.Exists(target).Should().BeTrue();
            new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryResolveGatewayPath_AdmitsAnExistingFileBeneathTheResolvedRoot()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"netratel-file-policy-{Guid.NewGuid():N}");
        var root = Path.Combine(temporaryRoot, "approved");
        var file = Path.Combine(root, "logs", "agent.log");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "safe");

        try
        {
            var resolved = new FileSystemService().TryResolveGatewayPath(
                file,
                [root],
                requireExisting: true,
                out var resolvedPath);

            resolved.Should().BeTrue();
            resolvedPath.Should().Be(Path.GetFullPath(file));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveGatewayPath_RejectsASymbolicLinkThatEscapesTheApprovedRoot()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"netratel-file-policy-{Guid.NewGuid():N}");
        var root = Path.Combine(temporaryRoot, "approved");
        var outside = Path.Combine(temporaryRoot, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not-readable-through-the-root");
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);

        try
        {
            var resolved = new FileSystemService().TryResolveGatewayPath(
                Path.Combine(root, "escape", "secret.txt"),
                [root],
                requireExisting: true,
                out _);

            resolved.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveGatewayPath_RejectsANewDirectoryBeneathASymbolicLinkThatEscapesTheApprovedRoot()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"netratel-file-policy-{Guid.NewGuid():N}");
        var root = Path.Combine(temporaryRoot, "approved");
        var outside = Path.Combine(temporaryRoot, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);

        try
        {
            var resolved = new FileSystemService().TryResolveGatewayPath(
                Path.Combine(root, "escape", "new-directory"),
                [root],
                requireExisting: false,
                out _);

            resolved.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}
