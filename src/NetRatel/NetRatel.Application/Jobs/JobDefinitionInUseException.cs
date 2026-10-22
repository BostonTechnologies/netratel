namespace NetRatel.Application.Jobs;

public sealed class JobDefinitionInUseException() : InvalidOperationException(
    "An active job run must be cancelled or settled before retiring its definition.");
