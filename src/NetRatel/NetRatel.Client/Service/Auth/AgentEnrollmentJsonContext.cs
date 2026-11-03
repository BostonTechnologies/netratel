using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Auth;

// Property order, default escaping and explicit null device metadata contribute
// to the enrollment proof. Keep the Web response reader's existing defaults.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(EnrollmentDeviceInfo))]
[JsonSerializable(typeof(EnrollmentRequest))]
[JsonSerializable(typeof(EnrollmentResponse))]
internal partial class AgentEnrollmentJsonContext : JsonSerializerContext;

internal sealed record EnrollmentDeviceInfo(
    string Os,
    string OsVersion,
    string HostName,
    string? ReportedAddress,
    string Architecture,
    string Framework);

internal sealed record EnrollmentRequest(
    string EnrollmentCode,
    string PublicKey,
    string KeyAlgorithm,
    string[] RequestedScopes,
    string DeviceInfoJson);

internal sealed record EnrollmentResponse(Guid AgentId, string RefreshToken, int ExpiresInDays);
