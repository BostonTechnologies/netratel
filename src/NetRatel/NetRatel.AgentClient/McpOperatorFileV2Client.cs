using System.Text;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>Typed inputs accepted by the policy-admitted V2 file write workflow.</summary>
public sealed record McpOperatorFileWriteTextV2(string Path, string Text);

/// <summary>Typed inputs accepted by the bounded V2 file upload workflow.</summary>
public sealed record McpOperatorFileUploadV2(string Path, string ContentBase64);

/// <summary>Typed inputs accepted by the V2 file copy and move workflows.</summary>
public sealed record McpOperatorFileRelocationV2(string SourcePath, string DestinationPath);

/// <summary>
/// Policy-admitted V2 file operations for one exact tenant and agent. Mutations
/// are deliberately split into preview and confirmation calls.
/// </summary>
public interface IMcpOperatorFileV2Client
{
    Task<JsonNode?> BrowseAsync(McpOperatorV2Target target, string path, int? pageSize = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> StatAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default);
    Task<JsonNode?> ReadAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCollectArtifactAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCollectArtifactAsync(McpOperatorV2Target target, string path, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetArtifactStatusAsync(McpOperatorV2Target target, Guid artifactId, CancellationToken cancellationToken = default);
    Task<JsonNode?> DownloadArtifactAsync(McpOperatorV2Target target, Guid artifactId, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCleanupArtifactAsync(McpOperatorV2Target target, Guid artifactId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCleanupArtifactAsync(McpOperatorV2Target target, Guid artifactId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewWriteTextAsync(McpOperatorV2Target target, McpOperatorFileWriteTextV2 write, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmWriteTextAsync(McpOperatorV2Target target, McpOperatorFileWriteTextV2 write, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewUploadAsync(McpOperatorV2Target target, McpOperatorFileUploadV2 upload, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmUploadAsync(McpOperatorV2Target target, McpOperatorFileUploadV2 upload, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCreateDirectoryAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCreateDirectoryAsync(McpOperatorV2Target target, string path, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewDeleteAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmDeleteAsync(McpOperatorV2Target target, string path, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCopyAsync(McpOperatorV2Target target, McpOperatorFileRelocationV2 relocation, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCopyAsync(McpOperatorV2Target target, McpOperatorFileRelocationV2 relocation, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewMoveAsync(McpOperatorV2Target target, McpOperatorFileRelocationV2 relocation, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmMoveAsync(McpOperatorV2Target target, McpOperatorFileRelocationV2 relocation, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound V2 file facade. It only addresses a persisted tenant-agent pair,
/// has no legacy file route fallback, and never accepts a caller-supplied API path.
/// </summary>
public sealed class McpOperatorFileV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorFileV2Client
{
    private const int MaximumPageSize = 100;
    private const int MaximumPathLength = 4096;
    private const int MaximumWriteTextBytes = 16 * 1024;
    private const int MaximumUploadBytes = 64 * 1024;
    private const int MaximumUploadBase64Characters = ((MaximumUploadBytes + 2) / 3) * 4;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> BrowseAsync(McpOperatorV2Target target, string path, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        if (pageSize is <= 0 or > MaximumPageSize) throw McpOperatorTaskV2Client.Invalid("file pageSize");
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(Path(target, "/browse"), ("path", path), ("pageSize", pageSize?.ToString())), cancellationToken);
    }

    public Task<JsonNode?> StatAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default) =>
        GetPathAsync(target, "/stat", path, cancellationToken);

    public Task<JsonNode?> ReadAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default) =>
        GetPathAsync(target, "/read", path, cancellationToken);

    public Task<JsonNode?> PreviewCollectArtifactAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default) =>
        SendPathAsync(target, "/artifacts/collect/preview", path, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmCollectArtifactAsync(McpOperatorV2Target target, string path, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendPathAsync(target, "/artifacts/collect/confirm", path, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> GetArtifactStatusAsync(McpOperatorV2Target target, Guid artifactId, CancellationToken cancellationToken = default) =>
        GetArtifactAsync(target, artifactId, string.Empty, cancellationToken);

    public Task<JsonNode?> DownloadArtifactAsync(McpOperatorV2Target target, Guid artifactId, CancellationToken cancellationToken = default) =>
        GetArtifactAsync(target, artifactId, "/download", cancellationToken);

    public Task<JsonNode?> PreviewCleanupArtifactAsync(McpOperatorV2Target target, Guid artifactId, CancellationToken cancellationToken = default) =>
        SendArtifactAsync(target, artifactId, "/cleanup/preview", false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmCleanupArtifactAsync(McpOperatorV2Target target, Guid artifactId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendArtifactAsync(target, artifactId, "/cleanup/confirm", true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewWriteTextAsync(McpOperatorV2Target target, McpOperatorFileWriteTextV2 write, CancellationToken cancellationToken = default) =>
        SendWriteTextAsync(target, write, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmWriteTextAsync(McpOperatorV2Target target, McpOperatorFileWriteTextV2 write, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendWriteTextAsync(target, write, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewUploadAsync(McpOperatorV2Target target, McpOperatorFileUploadV2 upload, CancellationToken cancellationToken = default) =>
        SendUploadAsync(target, upload, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmUploadAsync(McpOperatorV2Target target, McpOperatorFileUploadV2 upload, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendUploadAsync(target, upload, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewCreateDirectoryAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default) =>
        SendPathAsync(target, "/create-directory/preview", path, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmCreateDirectoryAsync(McpOperatorV2Target target, string path, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendPathAsync(target, "/create-directory/confirm", path, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewDeleteAsync(McpOperatorV2Target target, string path, CancellationToken cancellationToken = default) =>
        SendPathAsync(target, "/delete/preview", path, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmDeleteAsync(McpOperatorV2Target target, string path, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendPathAsync(target, "/delete/confirm", path, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewCopyAsync(McpOperatorV2Target target, McpOperatorFileRelocationV2 relocation, CancellationToken cancellationToken = default) =>
        SendRelocationAsync(target, "/copy/preview", relocation, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmCopyAsync(McpOperatorV2Target target, McpOperatorFileRelocationV2 relocation, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendRelocationAsync(target, "/copy/confirm", relocation, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewMoveAsync(McpOperatorV2Target target, McpOperatorFileRelocationV2 relocation, CancellationToken cancellationToken = default) =>
        SendRelocationAsync(target, "/move/preview", relocation, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmMoveAsync(McpOperatorV2Target target, McpOperatorFileRelocationV2 relocation, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendRelocationAsync(target, "/move/confirm", relocation, true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> GetPathAsync(McpOperatorV2Target target, string endpoint, string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(Path(target, endpoint), ("path", path)), cancellationToken);
    }

    private Task<JsonNode?> SendPathAsync(McpOperatorV2Target target, string endpoint, string path, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var body = new JsonObject { ["path"] = path };
        AddPlan(body, confirmed, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, endpoint), body, cancellationToken);
    }

    private Task<JsonNode?> GetArtifactAsync(McpOperatorV2Target target, Guid artifactId, string suffix, CancellationToken cancellationToken)
    {
        ValidateArtifactId(artifactId);
        return _client.GetAsync($"{Path(target, "/artifacts")}/{artifactId:D}{suffix}", cancellationToken);
    }

    private Task<JsonNode?> SendArtifactAsync(McpOperatorV2Target target, Guid artifactId, string suffix, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ValidateArtifactId(artifactId);
        var body = new JsonObject();
        AddPlan(body, confirmed, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{Path(target, "/artifacts")}/{artifactId:D}{suffix}", body, cancellationToken);
    }

    private Task<JsonNode?> SendWriteTextAsync(McpOperatorV2Target target, McpOperatorFileWriteTextV2 write, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        ValidatePath(write.Path);
        if (!IsWriteText(write.Text)) throw McpOperatorTaskV2Client.Invalid("file text");
        var body = new JsonObject { ["path"] = write.Path, ["text"] = write.Text };
        AddPlan(body, confirmed, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, $"/write-text/{(confirmed ? "confirm" : "preview")}"), body, cancellationToken);
    }

    private Task<JsonNode?> SendUploadAsync(McpOperatorV2Target target, McpOperatorFileUploadV2 upload, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ValidatePath(upload.Path);
        if (!IsBase64(upload.ContentBase64)) throw McpOperatorTaskV2Client.Invalid("file upload");
        var body = new JsonObject { ["path"] = upload.Path, ["contentBase64"] = upload.ContentBase64 };
        AddPlan(body, confirmed, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, $"/upload/{(confirmed ? "confirm" : "preview")}"), body, cancellationToken);
    }

    private Task<JsonNode?> SendRelocationAsync(McpOperatorV2Target target, string endpoint, McpOperatorFileRelocationV2 relocation, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relocation);
        ValidatePath(relocation.SourcePath);
        ValidatePath(relocation.DestinationPath);
        var body = new JsonObject { ["sourcePath"] = relocation.SourcePath, ["destinationPath"] = relocation.DestinationPath };
        AddPlan(body, confirmed, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, endpoint), body, cancellationToken);
    }

    private static void AddPlan(JsonObject body, bool confirmed, string? planToken, string? idempotencyKey)
    {
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
    }

    private static void ValidatePath(string? path)
    {
        if (!McpOperatorTaskV2Client.IsText(path, MaximumPathLength)) throw McpOperatorTaskV2Client.Invalid("file path");
    }

    private static void ValidateArtifactId(Guid artifactId)
    {
        if (artifactId == Guid.Empty) throw McpOperatorTaskV2Client.Invalid("artifactId");
    }

    private static bool IsBase64(string? value)
    {
        if (value is null) return false;
        if (!McpOperatorTaskV2Client.IsText(value, MaximumUploadBase64Characters) || value.Length % 4 != 0) return false;
        var maximumDecodedLength = (value.Length / 4) * 3;
        var buffer = new byte[maximumDecodedLength];
        return Convert.TryFromBase64String(value, buffer, out var bytesWritten) &&
            bytesWritten <= MaximumUploadBytes &&
            string.Equals(value, Convert.ToBase64String(buffer, 0, bytesWritten), StringComparison.Ordinal);
    }

    private static bool IsWriteText(string? value)
    {
        if (value is null) return false;
        if (value.Length > MaximumWriteTextBytes || value.Contains('\0')) return false;
        try
        {
            return Utf8.GetByteCount(value) <= MaximumWriteTextBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private string Path(McpOperatorV2Target target, string suffix)
    {
        target.Validate();
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "file");
        return $"/api/v2/mcp/operator/agents/{target.TenantId}/{target.AgentId:D}/files{suffix}";
    }
}
