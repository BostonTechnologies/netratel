using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.SoftwareInventory;

// Identity hashes use General defaults independently of the Web wire contract.
[JsonSourceGenerationOptions(JsonSerializerDefaults.General, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(IEnumerable<string>), TypeInfoPropertyName = "IdentityFields")]
internal partial class SoftwareIdentityJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SoftwarePackage))]
[JsonSerializable(typeof(SoftwarePackage[]))]
internal partial class SoftwareInventoryWireJsonContext : JsonSerializerContext;
