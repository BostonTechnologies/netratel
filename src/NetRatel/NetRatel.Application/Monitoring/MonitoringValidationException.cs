using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Application.Monitoring;

public sealed class MonitoringValidationException(MonitoringValidationIssue issue) : ArgumentException(issue.Code)
{
    public MonitoringValidationIssue Issue { get; } = issue;
}
