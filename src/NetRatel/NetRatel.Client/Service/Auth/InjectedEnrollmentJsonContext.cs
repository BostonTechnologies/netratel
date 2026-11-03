using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Auth;

// This producer contract accepts property casing but keeps numeric types strict.
// Web defaults would incorrectly accept quoted tenant IDs.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(InjectedEnrollmentBootstrap.InjectedEnrollmentPayload))]
internal partial class InjectedEnrollmentJsonContext : JsonSerializerContext;
