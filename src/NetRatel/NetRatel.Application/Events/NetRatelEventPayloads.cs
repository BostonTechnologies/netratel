namespace NetRatel.Application.Events;

public sealed record StateChangedPayload(string FromState, string ToState, string? Reason = null, string? Actor = null);

public sealed record CompletionPayload(string Result, double? DurationMs = null, string? ErrorSummary = null, string? Actor = null);

public sealed record TenantChangedPayload(int TenantId, string? Name, string? Actor, IReadOnlyList<string>? ChangedFields = null);

public sealed record ClientChangedPayload(string ClientId, int? TenantId, string? Actor, IReadOnlyList<string>? ChangedFields = null, string? Reason = null);

public sealed record ScriptChangedPayload(ulong ScriptId, string? Name, string? ScriptType, string? Actor);

public sealed record JobLifecyclePayload(ulong JobId, ulong? JobRunId, string? Actor, string? Result = null, string? Error = null);

public sealed record TaskLifecyclePayload(string RequestId, string TaskType, string? Result = null, string? Error = null);

public sealed record AgentLifecyclePayload(Guid AgentId, int? TenantId, string? Actor, string? Reason = null);

public sealed record ExceptionPayload(string Path, string Method, string CorrelationId, string Error, string? StackTrace);
