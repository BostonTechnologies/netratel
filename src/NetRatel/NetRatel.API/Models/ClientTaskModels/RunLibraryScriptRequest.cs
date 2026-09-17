using System.Collections.Generic;
using NetRatel.Shared;

namespace NetRatel.API.Models.ClientTaskModels;

// Deprecated request body for running a script from the library.
public record RunLibraryScriptRequest(
    string targetCliSearchString,
    int? TenantId,
    ClientEnvironment Environment,
    ulong ScriptId,
    string? RequestId,
    Dictionary<string, string>? Parameters
);
