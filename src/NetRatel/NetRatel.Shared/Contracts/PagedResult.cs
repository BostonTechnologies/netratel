using System.Collections.Generic;

namespace NetRatel.Shared.Contracts;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);
