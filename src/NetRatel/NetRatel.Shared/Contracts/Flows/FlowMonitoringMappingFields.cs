namespace NetRatel.Shared.Contracts.Flows;

public static class FlowMonitoringMappingFields
{
    public const string HistoricalTitle = "{severity}: {ruleName} on {clientName}";
    public const string HistoricalDescription = "{ruleName}\nClient: {clientName}\nResource: {resource}\nMetric: {metric}\nValue: {value}\nService: {serviceState}\nObserved: {occurredAt}";
    public const string Title = "{alertTitle}";
    public const string Description = "{alertDetails}";
    public static IReadOnlyList<string> Names { get; } =
        ["alertTitle", "alertDetails", "hostname", "tenantName", "clientUrl", "alertUrl", "formattedValue", "condition",
         "recoveryCondition", "raisedAt", "diskFree", "diskTotal"];

    // Only the exact historical stock mapping adopts new presentation. Published graphs remain byte-identical.
    public static bool IsHistoricalStock(FlowIncidentMappingDto mapping) =>
        mapping.TitleTemplate == HistoricalTitle && mapping.DescriptionTemplate == HistoricalDescription;
}
