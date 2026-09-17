using NetRatel.Application.Operations;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Explicit ownership and current reviewed metadata for one tenant-scoped
/// Production operator script. The linked ScriptDefinition contains content;
/// this record prevents that otherwise global table from being used as an
/// ownerless operator library.
/// </summary>
public sealed class McpOperatorScriptRecord
{
    public Guid Id { get; set; }
    public long ScriptId { get; set; }
    public int TenantId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string McpResource { get; set; } = string.Empty;
    public string McpInstance { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ShellType { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public long PolicyVersion { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "[]";
    public int TimeoutSeconds { get; set; }
    public string WorkingDirectory { get; set; } = string.Empty;
    public string DeclaredSideEffectsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public long Version { get; set; }
}

/// <summary>
/// Append-only revision evidence. It stores immutable hashes and reviewed
/// metadata, not a duplicate of raw script content or secret values.
/// </summary>
public sealed class McpOperatorScriptVersionRecord
{
    public Guid Id { get; set; }
    public Guid ScriptRecordId { get; set; }
    public long ScriptId { get; set; }
    public long ScriptVersion { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid AcceptedAuditId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ShellType { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "[]";
    public int TimeoutSeconds { get; set; }
    public string WorkingDirectory { get; set; } = string.Empty;
    public string DeclaredSideEffectsJson { get; set; } = "[]";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
