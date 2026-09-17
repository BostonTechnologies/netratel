using System;
using System.Collections.Generic;

namespace NetRatel.Shared.Contracts;

public sealed record TenantDto(
    int TenantId,
    string Name,
    string? Description,
    string? Location,
    IReadOnlyList<string> Domains,
    string? ContactPerson,
    string? ContactEmail,
    bool AutoUpdate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public int Id => TenantId;
}
