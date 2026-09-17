using System;

namespace NetRatel.Shared.Contracts.Tasks;

public sealed record TaskLogDto(
    long Id,
    string RequestId,
    string ClientIdentity,
    DateTimeOffset Ts,
    string Stream,
    string Message,
    long Seq
);
