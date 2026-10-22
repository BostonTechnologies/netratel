namespace NetRatel.Application.Jobs;

public sealed class JobDefinitionUnavailableException() : InvalidOperationException(
    "The job definition is unavailable for a new run.");
