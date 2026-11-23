using System.Globalization;
using System.Text.Json;
using NetRatel.Application.Secrets;
using NetRatel.Shared.Contracts.Secrets;
using static NetRatel.Infrastructure.Secrets.SecretProviderHttp;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Secrets;

internal sealed class OpenBaoSecretProvider(SecretProviderHttp http, SecretProviderTokenCache tokens, TimeProvider clock) : ISecretProvider
{
    private static readonly HttpMethod List = new("LIST");
    private const int VersionLimit = 50;
    private const int FieldLimit = 128;

    public SecretProviderKind Kind => SecretProviderKind.OpenBao;
    public SecretProviderCapabilities Capabilities { get; } = new(SecretWriteConcurrency.CompareAndSwap, SecretDeletionMode.SoftDeleteCurrentVersion, true);
    public string ValidateLocator(string locator) => Path(locator, allowRoot: false);
    public void ValidateVersion(string version) => PositiveInt64(version);

    public async Task<bool> TestMetadataAsync(SecretProviderContext context, CancellationToken ct)
    {
        using var response = await ListFolderAsync(context, "", ct);
        return response is not null && Property(Property(response.RootElement, "data"), "keys").ValueKind == JsonValueKind.Array;
    }

    public async Task<SecretBrowsePage> BrowseAsync(SecretProviderContext context, SecretBrowseQuery query, CancellationToken ct)
    {
        var folder = Path(query.Folder, allowRoot: true);
        using var response = await ListFolderAsync(context, folder, ct);
        var entries = response is null ? [] : ReadChildren(Property(Property(response.RootElement, "data"), "keys"), folder);
        return SecretFolderPage.Create(entries, query, "Current folder");
    }

    public async Task<SecretEntryDetails> GetMetadataAsync(SecretProviderContext context, string locator, string? version, CancellationToken ct)
    {
        using var response = await GetAsync(context, "metadata", locator, ct);
        var metadata = Property(response!.RootElement, "data");
        var current = CurrentMetadata(locator, metadata);
        var versions = Versions(metadata).OrderByDescending(x => PositiveInt64(x)).Take(VersionLimit + 1).ToArray();
        if (version is not null && !AvailableVersion(metadata, version))
            throw MissingVersion();
        SecretFieldMetadata[] fields = [];
        if (version is not null || current.Enabled is true)
        {
            // The subkeys endpoint returns structure with leaf values stripped.
            using var subkeys = await GetAsync(context, "subkeys", locator, ct, version ?? current.Version, versionRequired: true);
            var shape = Property(Property(subkeys!.RootElement, "data"), "subkeys");
            if (shape.ValueKind != JsonValueKind.Object)
                throw Unavailable();
            fields = shape.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Null && IsFieldName(x.Name))
                .Take(FieldLimit).Select(x => new SecretFieldMetadata(x.Name, "scalar")).ToArray();
        }
        return new(current, fields, versions.Take(VersionLimit).ToArray(), versions.Length > VersionLimit, []);
    }

    public async Task<SecretEntryMetadata> WriteAsync(SecretProviderContext context, WriteSecretEntryRequest request, CancellationToken ct)
    {
        if (request.Locator is null || request.Name is not null || request.Description is not null || request.Enabled is not null || request.Value is not null)
            throw Invalid("Specify an OpenBao path and its structured fields.");
        var locator = ValidateLocator(request.Locator);
        var cas = request.ExpectedVersion is null ? 0L : PositiveInt64(request.ExpectedVersion);
        if (request.ExpectedRevision != request.ExpectedVersion)
            throw Invalid("Use the observed OpenBao version and revision together.");
        var (edits, removals) = ValidateFields(request);
        if (cas == 0 && (edits.Count == 0 || removals.Length != 0))
            throw Invalid("A new OpenBao entry requires at least one field.");

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (cas != 0)
        {
            using var stored = await GetAsync(context, "data", locator, ct, request.ExpectedVersion, versionRequired: true);
            var data = Property(Property(stored!.RootElement, "data"), "data");
            if (data.ValueKind != JsonValueKind.Object)
                throw Unavailable();
            fields = data.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
        }
        foreach (var name in removals)
            fields.Remove(name);
        foreach (var edit in edits)
            fields[edit.Key] = edit.Value;
        if (fields.Count == 0)
            throw Invalid("Keep at least one field on the entry.");

        using var result = await SendAsync(context, HttpMethod.Post, Route(context, "data", locator), ct,
            body: new { options = new { cas }, data = fields }, openBaoCompareAndSwap: true);
        var saved = Property(result!.RootElement, "data");
        var version = Format(Number(saved, "version"));
        return Entry(locator, version, OptionalTimestamp(saved, "created_time"), true);
    }

    public async Task<SecretDeletionResult> DeleteAsync(SecretProviderContext context, DeleteSecretEntryRequest request, CancellationToken ct)
    {
        using var response = await GetAsync(context, "metadata", request.Locator, ct);
        var current = CurrentMetadata(request.Locator, Property(response!.RootElement, "data"));
        RequireExpected(current, request.ExpectedRevision, request.ExpectedVersion);
        // The version-specific soft-delete endpoint cannot delete a concurrent rotation.
        using var ignored = await SendAsync(context, HttpMethod.Post, Route(context, "delete", request.Locator), ct,
            body: new { versions = new[] { PositiveInt64(request.ExpectedVersion) } }, readBody: false);
        return new(request.Locator, request.ExpectedVersion, Capabilities.Deletion, false);
    }

    public async Task<ProviderSecretValues> ResolveAsync(SecretProviderContext context, string locator, string? version,
        string?[] fields, CancellationToken ct)
    {
        using var response = await GetAsync(context, "data", locator, ct, version, versionRequired: true);
        var result = Property(response!.RootElement, "data");
        var metadata = Property(result, "metadata");
        var resolved = Format(Number(metadata, "version"));
        if (version is not null && resolved != version || !IsAvailable(metadata))
            throw MissingVersion();
        var values = Property(result, "data");
        return new(resolved, fields.Select(field => ReadField(values, field)).ToArray());
    }

    private async Task<JsonDocument?> ListFolderAsync(SecretProviderContext context, string folder, CancellationToken ct) =>
        await SendAsync(context, List, Route(context, "metadata", folder) + $"?limit={SecretFolderPage.EntryLimit + 1}", ct, allowNotFound: true);

    private async Task<JsonDocument?> GetAsync(SecretProviderContext context, string operation, string locator, CancellationToken ct,
        string? version = null, bool versionRequired = false)
    {
        var path = Route(context, operation, locator);
        if (version is not null)
            path += "?version=" + PositiveInt64(version).ToString(CultureInfo.InvariantCulture);
        var response = await SendAsync(context, HttpMethod.Get, path, ct, allowNotFound: versionRequired);
        return response ?? throw MissingVersion();
    }

    private async Task<JsonDocument?> SendAsync(SecretProviderContext context, HttpMethod method, string route, CancellationToken ct,
        object? body = null, bool allowNotFound = false, bool readBody = true, bool openBaoCompareAndSwap = false)
    {
        var token = await tokens.GetAsync(context, loginCt => LoginAsync(context, loginCt), ct);
        try { return await http.SendAsync(context, method, route, ct, body, token, allowNotFound, readBody, openBaoCompareAndSwap); }
        catch (SecretProviderException error) when (error.Reason is SecretProviderFailure.Authentication or SecretProviderFailure.Forbidden)
        {
            tokens.Invalidate(context);
            throw;
        }
    }

    private async Task<ProviderAccessToken> LoginAsync(SecretProviderContext context, CancellationToken ct)
    {
        var bootstrap = context.ReadAuthentication() ?? throw Unavailable();
        var authMount = EncodePath(context.Configuration.AuthenticationMount!);
        using var response = await http.SendAsync(context, HttpMethod.Post, $"/v1/auth/{authMount}/login", ct,
            new { role_id = bootstrap.RoleId, secret_id = bootstrap.SecretId });
        var authentication = Property(response!.RootElement, "auth");
        var seconds = Math.Clamp(Number(authentication, "lease_duration"), 1, 3600);
        return new("X-Vault-Token", TokenText(authentication, "client_token"), TimeSpan.FromSeconds(seconds));
    }

    private static string Route(SecretProviderContext context, string operation, string relative)
    {
        var path = EncodePath(JoinPath(context.Configuration.BasePath!, relative));
        return $"/v1/{EncodePath(context.Configuration.Mount!)}/{operation}/{path}";
    }

    private static SecretEntryMetadata[] ReadChildren(JsonElement keys, string folder)
    {
        if (keys.ValueKind != JsonValueKind.Array)
            throw Unavailable();
        return keys.EnumerateArray().Take(SecretFolderPage.EntryLimit + 1).Select(key =>
        {
            var text = key.ValueKind == JsonValueKind.String ? key.GetString()! : throw Unavailable();
            var directory = text.EndsWith('/');
            var name = Segment(directory ? text[..^1] : text);
            return new SecretEntryMetadata(JoinPath(folder, name), name, directory);
        }).ToArray();
    }

    private SecretEntryMetadata CurrentMetadata(string locator, JsonElement metadata)
    {
        var current = Format(Number(metadata, "current_version"));
        return Entry(locator, current, OptionalTimestamp(metadata, "updated_time"), AvailableVersion(metadata, current));
    }

    private static SecretEntryMetadata Entry(string locator, string version, DateTimeOffset? updatedAt, bool active) =>
        new(locator, locator.Split('/')[^1], false, version, version, updatedAt, Enabled: active);

    private IEnumerable<string> Versions(JsonElement metadata) => Property(metadata, "versions").EnumerateObject()
        .Where(x => IsAvailable(x.Value)).Select(x => x.Name);

    private bool AvailableVersion(JsonElement metadata, string version) => Property(metadata, "versions").TryGetProperty(version, out var entry) && IsAvailable(entry);

    private bool IsAvailable(JsonElement metadata) => Property(metadata, "destroyed").ValueKind == JsonValueKind.False &&
        (OptionalTimestamp(metadata, "deletion_time") is not { } deleted || deleted > clock.GetUtcNow());

    private static string ReadField(JsonElement values, string? name)
    {
        if (name is null || values.ValueKind != JsonValueKind.Object || !values.TryGetProperty(name, out var field) || !IsScalar(field))
            throw MissingVersion();
        return field.ValueKind == JsonValueKind.String ? field.GetString()! : field.GetRawText();
    }

    private static (Dictionary<string, JsonElement> Edits, string[] Removals) ValidateFields(WriteSecretEntryRequest request)
    {
        var edits = request.Fields ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var removals = request.RemoveFields ?? [];
        if (edits.Count + removals.Length is < 1 or > FieldLimit || removals.Any(name => !IsFieldName(name) || edits.ContainsKey(name)) ||
            edits.Any(field => !IsFieldName(field.Key) || !IsScalar(field.Value)) || edits.Sum(field => (long)field.Value.GetRawText().Length) > MaximumSecretLength)
            throw Invalid("Supply supported scalar fields and explicit removals within the size limits.");
        return (edits, removals);
    }

    private static bool IsFieldName(string? name) => name is { Length: > 0 and <= 256 } && name.Trim() == name && !name.Any(char.IsControl);
    private static bool IsScalar(JsonElement field) => field.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    private static string Format(long version) => version.ToString(CultureInfo.InvariantCulture);
}
