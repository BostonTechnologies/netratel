namespace NetRatel.Application.Jobs;

public interface IJobShellCompatibility
{
    Task RequireDefinitionAsync(JobDefinitionDetails definition, CancellationToken cancellationToken);
}
