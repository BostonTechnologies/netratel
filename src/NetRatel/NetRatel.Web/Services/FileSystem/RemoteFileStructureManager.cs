using Microsoft.AspNetCore.Components.Forms;
using MudBlazor.Extensions.Core.FileManager;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Web.Services.FileSystem;

/// <summary>One dialog's remote context. Nodes are identities, never authority.</summary>
public sealed class RemoteFileStructureManager(GatewayFileSystemApiService gateway, int tenantId, Guid agentId,
    string initialPath, CancellationToken lifetime) : IMudExFileStructureManager, IMudExFileStructureWriter
{
    private readonly Guid _owner = Guid.NewGuid();
    private long _generation;
    private readonly Dictionary<string, MudExFileStructureNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteDirectoryListing> _listings = new(StringComparer.Ordinal);
    public event Func<Task>? ListingChanged;
    public GatewayFileSystemListResponse? Listing => _listings.GetValueOrDefault(CurrentPath)?.Response;
    public RemoteDirectoryListing DirectoryListing(MudExFileStructureNode node) =>
        TryEntry(node, out var entry) ? _listings.GetValueOrDefault(entry.FullPath) ?? RemoteDirectoryListing.NotLoaded : RemoteDirectoryListing.NotLoaded;
    public string CurrentPath { get; set; } = initialPath;
    public GatewayFileSystemMutationResponse? LastOutcomes { get; private set; }
    public IReadOnlyList<string> RefreshFailures { get; private set; } = [];
    public MudExFileManagerCapabilities Capabilities => MudExFileManagerCapabilities.Read |
        (Listing?.CanCreateDirectory == true ? MudExFileManagerCapabilities.CreateDirectory : 0) |
        (Listing?.Capabilities.MoveEntry == true ? MudExFileManagerCapabilities.Move | MudExFileManagerCapabilities.Rename : 0) |
        (Listing?.Capabilities.DeleteRecursive == true ? MudExFileManagerCapabilities.Delete : 0);

    public async Task<HashSet<MudExFileStructureNode>> GetRootAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        Invalidate();
        var generation = _generation;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        try
        {
            var roots = await gateway.RootsAsync(tenantId, agentId, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (generation != _generation) throw new OperationCanceledException("The remote roots were replaced.");
            return roots.Entries.Where(entry => entry.IsDirectory).Select(entry => Node(entry)).ToHashSet();
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // An older agent can still browse its selected volume. Never
            // substitute roots from the Web host or pretend seeking is supported.
            var root = RemoteFilePath.IsWindows(CurrentPath) ? CurrentPath[..3] : "/";
            return [Node(new(root, root, true, 0))];
        }
    }

    public Task<HashSet<MudExFileStructureNode>> GetChildrenAsync(MudExFileStructureNode node, CancellationToken ct = default)
    {
        var handle = Resolve(node);
        if (!handle.Entry.IsDirectory) throw new InvalidOperationException("Select a directory.");
        return LoadChildrenAsync(node, handle, ct);
    }

    private async Task<HashSet<MudExFileStructureNode>> LoadChildrenAsync(MudExFileStructureNode node, Handle handle, CancellationToken ct)
    {
        var path = handle.Entry.FullPath;
        _listings[path] = new(RemoteDirectoryLoadStatus.Loading);
        if (ListingChanged is { } started) await started();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        HashSet<MudExFileStructureNode> children = [];
        try
        {
            var listing = await gateway.ListAsync(tenantId, agentId, path, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!TryEntry(node, out _)) return [];
            if (listing.TenantId != tenantId || listing.AgentId != agentId || listing.Path != path || listing.Entries.Count > 4096)
                throw new InvalidDataException("The remote listing did not match this directory or exceeded its 4,096-entry limit.");
            children = listing.Entries.Select(entry => Node(entry, node)).ToHashSet();
            _listings[path] = new(RemoteDirectoryLoadStatus.Loaded, listing);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (!TryEntry(node, out _)) return [];
            _listings[path] = new(RemoteDirectoryLoadStatus.Unavailable, Error: exception is OperationCanceledException
                ? "Directory loading was cancelled. Use Refresh to try again." : $"Listing unavailable: {exception.Message}");
        }
        // Hierarchical.LoadChildren is one-shot: a second caller returns before
        // the first load finishes, and its continuation mutates Children outside
        // the renderer. Commit the canonical node before notifying the dialog.
        // The package's continuation can then repeat this same assignment safely.
        node.LoadChildrenFunc = null;
        node.Children = children;
        if (ListingChanged is { } completed) await completed();
        return children;
    }

    public MudExFileStructureNode Directory(string path)
    {
        if (!RemoteFilePath.TryNormalizeDirectory(path, out path)) throw new InvalidOperationException("Enter an absolute remote directory path.");
        var parents = new Stack<string>();
        var current = path;
        parents.Push(current);
        while (RemoteFilePath.TryGetDirectoryParent(current, out var parent))
        { if (parents.Count > 256) throw new InvalidOperationException("The remote path has too many levels."); parents.Push(parent); current = parent; }
        MudExFileStructureNode? node = null;
        while (parents.TryPop(out var nextPath))
        {
            var separator = RemoteFilePath.IsWindows(nextPath) ? '\\' : '/';
            var name = RemoteFilePath.IsRoot(nextPath) ? nextPath : nextPath[(nextPath.LastIndexOf(separator) + 1)..];
            node = Node(new(name, nextPath, true, 0), node);
        }
        return node!;
    }
    public GatewayFileSystemEntryDto Entry(MudExFileStructureNode node) => Resolve(node).Entry;
    public bool TryEntry(MudExFileStructureNode node, out GatewayFileSystemEntryDto entry)
    {
        if (node.Handle is Handle handle && handle.Owner == _owner && handle.Generation == _generation && !lifetime.IsCancellationRequested)
        { entry = handle.Entry; return true; }
        entry = null!;
        return false;
    }

    public async Task<Stream> OpenReadAsync(MudExFileStructureNode node, CancellationToken ct = default)
    {
        var entry = Resolve(node).Entry;
        if (entry.IsDirectory || entry.SizeBytes > 8 * 1024 * 1024) throw new InvalidOperationException("Use the governed preview or streaming download action.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        using var response = await gateway.DownloadAsync(tenantId, agentId, entry.FullPath, linked.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
        var memory = new MemoryStream();
        try
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, linked.Token)) != 0)
            {
                if (memory.Length + read > 8 * 1024 * 1024) throw new InvalidOperationException("Preview limit exceeded.");
                await memory.WriteAsync(buffer.AsMemory(0, read), linked.Token);
            }
            Resolve(node);
            memory.Position = 0;
            return memory;
        }
        catch { await memory.DisposeAsync(); throw; }
    }

    public async Task<MudExFileStructureNode> CreateDirectoryAsync(MudExFileStructureNode parent, string name, CancellationToken ct = default)
    {
        var path = ResolveParent(parent);
        if (!RemoteFilePath.TryJoinDirectory(path, name, out var target)) throw new InvalidOperationException("Enter one valid child name.");
        LastOutcomes = null;
        RefreshFailures = [];
        try
        {
            await gateway.CreateDirectoryAsync(tenantId, agentId, path, name, ct);
            LastOutcomes = new([new(target, null, "completed", AffectedEntries: 1)]);
        }
        catch (Exception exception) when (exception is HttpRequestException { StatusCode: null } or OperationCanceledException)
        {
            LastOutcomes = new([new(target, null, "unknown", "response_lost_refresh_before_retry")]);
            throw new RemoteFileBatchException(LastOutcomes);
        }
        finally { Invalidate(); }
        return Node(new(name, target, true, 0));
    }

    public async Task<MudExFileStructureNode> RenameAsync(MudExFileStructureNode node, string newName, CancellationToken ct = default)
    {
        var entry = Resolve(node).Entry;
        if (!RemoteFilePath.TryGetDirectoryParent(entry.FullPath, out var parent) ||
            !RemoteFilePath.TryJoinDirectory(parent, newName, out var destination)) throw new InvalidOperationException("Enter one valid child name.");
        await ExecuteAsync(new("move", [new(entry.FullPath, destination)]), ct);
        return Node(entry with { Name = newName, FullPath = destination });
    }

    public Task MoveAsync(IReadOnlyCollection<MudExFileStructureNode> nodes, MudExFileStructureNode target, CancellationToken ct = default)
    {
        var parent = ResolveParent(target);
        var items = nodes.Select(node => new GatewayFileSystemMutationItem(Resolve(node).Entry.FullPath,
            RemoteFilePath.Join(parent, node.Name))).ToArray();
        return ExecuteAsync(new("move", items), ct);
    }

    public Task DeleteAsync(IReadOnlyCollection<MudExFileStructureNode> nodes, CancellationToken ct = default) =>
        ExecuteAsync(new("delete", nodes.Select(node => new GatewayFileSystemMutationItem(Resolve(node).Entry.FullPath)).ToArray(),
            nodes.Any(node => node.IsDirectory)), ct);

    public async Task<MudExFileStructureNode> UploadAsync(MudExFileStructureNode target, IBrowserFile file, CancellationToken ct = default)
    {
        if (Listing?.Capabilities.ConditionalUpload != true) throw new NotSupportedException("This client cannot perform create-only uploads.");
        var parent = ResolveParent(target);
        if (!RemoteFilePath.TryJoinDirectory(parent, file.Name, out var path)) throw new InvalidOperationException("Invalid remote filename.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        try
        {
            await using var source = file.OpenReadStream(16L * 1024 * 1024 * 1024, linked.Token);
            await gateway.UploadCreateOnlyAsync(tenantId, agentId, path, source, linked.Token);
        }
        finally { Invalidate(); }
        return Node(new(file.Name, path, false, file.Size));
    }

    public Task CreateFileAsync(string parent, string name, CancellationToken ct)
    {
        if (!RemoteFilePath.TryJoinDirectory(parent, name, out var path)) throw new InvalidOperationException("Enter one valid child name.");
        return ExecuteAsync(new("create_file", [new(path)]), ct);
    }

    private async Task ExecuteAsync(GatewayFileSystemMutationRequest request, CancellationToken ct)
    {
        if (request.Items.Count is 0 or > 128) throw new InvalidOperationException("Choose 1–128 top-level items per operation.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        LastOutcomes = null;
        RefreshFailures = [];
        try
        {
            LastOutcomes = await gateway.MutateAsync(tenantId, agentId, request, linked.Token);
            if (LastOutcomes.Items.Any(item => item.Outcome != "completed")) throw new RemoteFileBatchException(LastOutcomes);
        }
        catch (Exception exception) when (exception is HttpRequestException { StatusCode: null } or OperationCanceledException)
        {
            LastOutcomes = new(request.Items.Select(item => new GatewayFileSystemMutationResult(item.Path, item.DestinationPath,
                "unknown", "response_lost_refresh_before_retry")).ToArray());
            throw new RemoteFileBatchException(LastOutcomes);
        }
        finally
        {
            // Mutations are not atomic batches. Read both source and destination
            // parents even after a partial/unknown result, without replaying any
            // write or retaining another directory's entries in the UI cache.
            var failures = new List<string>();
            var paths = request.Items.SelectMany(item => new[] { item.Path, item.DestinationPath })
                .Where(path => path is not null)
                .Select(path => RemoteFilePath.TryGetDirectoryParent(path!, out var parent) ? parent : null)
                .Where(parent => parent is not null).Distinct(StringComparer.Ordinal);
            foreach (var parent in paths)
            {
                if (lifetime.IsCancellationRequested) break;
                try { await gateway.ListAsync(tenantId, agentId, parent!, lifetime); }
                catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or OperationCanceledException)
                { failures.Add($"{parent}: {exception.Message}"); }
            }
            RefreshFailures = failures;
            Invalidate();
        }
    }

    public void Invalidate() { _generation++; _nodes.Clear(); _listings.Clear(); }
    private string ResolveParent(MudExFileStructureNode? parent) => parent is null ? CurrentPath : Resolve(parent).Entry.FullPath;
    private Handle Resolve(MudExFileStructureNode node) => node?.Handle is Handle handle && handle.Owner == _owner &&
        handle.Generation == _generation && !lifetime.IsCancellationRequested ? handle : throw new InvalidOperationException("This remote node is stale. Refresh the directory.");
    private MudExFileStructureNode Node(GatewayFileSystemEntryDto entry, MudExFileStructureNode? parent = null)
    {
        if (_nodes.TryGetValue(entry.FullPath, out var existing)) return existing;
        var node = new MudExFileStructureNode
        {
            Name = entry.Name, FullPath = entry.FullPath, IsDirectory = entry.IsDirectory, Size = entry.SizeBytes,
            LastModified = entry.LastModifiedUtc, Parent = parent, Handle = new Handle(_owner, _generation, entry)
        };
        if (entry.IsDirectory) node.LoadChildrenFunc = GetChildrenAsync;
        _nodes[entry.FullPath] = node;
        return node;
    }
    private sealed record Handle(Guid Owner, long Generation, GatewayFileSystemEntryDto Entry);
}

public enum RemoteDirectoryLoadStatus { NotLoaded, Loading, Loaded, Unavailable }
public sealed record RemoteDirectoryListing(RemoteDirectoryLoadStatus Status, GatewayFileSystemListResponse? Response = null, string? Error = null)
{
    public static RemoteDirectoryListing NotLoaded { get; } = new(RemoteDirectoryLoadStatus.NotLoaded);
}

public sealed class RemoteFileBatchException(GatewayFileSystemMutationResponse outcomes) : Exception(
    string.Join("; ", outcomes.Items.Select(item => $"{item.Path}: {item.Outcome} ({item.Code ?? "ok"}; {item.AffectedEntries} affected)")))
{
    public GatewayFileSystemMutationResponse Outcomes { get; } = outcomes;
}
