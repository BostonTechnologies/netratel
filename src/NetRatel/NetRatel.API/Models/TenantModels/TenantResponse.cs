namespace NetRatel.API.Models.TenantModels;

public record TenantResponse(
    int TenantId,
    string Name,
    string? Description,
    string? Location,
    List<string> Domains,
    string? ContactPerson,
    string? ContactEmail,
    bool AutoUpdate,
    string AutoUpdateChannel,
    string? AutoUpdateTargetVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
