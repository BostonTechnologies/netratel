using System;

namespace NetRatel.Shared.Contracts;

public record ClientSettingsDto(
    int HeartbeatSeconds,
    bool AutoUpdate,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy
);

public record UpdateClientSettingsRequest(
    int? HeartbeatSeconds,
    bool? AutoUpdate
);
