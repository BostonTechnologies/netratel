using NetRatel.Shared;

namespace NetRatel.API.Models;

public record ClientCreateRequest(
    string Name,
    Guid TenantId,
    ClientEnvironment Environment
);

public record ClientUpdateRequest(
    Guid Id,
    string Name,
    Guid TenantId,
    ClientEnvironment Environment
);
