using System;

namespace NetRatel.API.Models.ClientTaskModels;

public sealed record TaskLogResponse(
    long Id,
    string RequestId,
    string ClientIdentity,
    DateTimeOffset Ts,
    string Stream,
    string Message,
    long Seq
);
