namespace NetRatel.Application.Operations;

/// <summary>Evaluates a bounded discovery batch without per-record policy queries or dispatch.</summary>
public interface IMcpOperatorSearchAuthorization
{
    Task<IReadOnlyList<McpOperatorDecision>> EvaluateAsync(
        IReadOnlyList<McpOperatorAccessRequest> requests, CancellationToken cancellationToken);
}
