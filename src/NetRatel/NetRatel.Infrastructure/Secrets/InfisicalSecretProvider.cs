using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.Secrets;
using NetRatel.Shared.Contracts.Secrets;
using static NetRatel.Infrastructure.Secrets.SecretProviderHttp;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Secrets;

internal sealed class InfisicalSecretProvider(SecretProviderHttp http, SecretProviderTokenCache tokens) : ISecretProvider
{
    private const string SecretsRoute = "/api/v4/secrets";

    public SecretProviderKind Kind => SecretProviderKind.Infisical;
    public SecretProviderCapabilities Capabilities { get; } = new(SecretWriteConcurrency.BestEffort, SecretDeletionMode.RemoveEntry, false);
    public string ValidateLocator(string locator) => Path(locator, allowRoot: false);
    public void ValidateVersion(string version) => PositiveInt64(version);

    public async Task<bool> TestMetadataAsync(SecretProviderContext context, CancellationToken ct)
    {
        using var result = await ListSecretsAsync(context, "", ct);
        return Property(result!.RootElement, "secrets").ValueKind == JsonValueKind.Array;
    }

    public async Task<SecretBrowsePage> BrowseAsync(SecretProviderContext context, SecretBrowseQuery query, CancellationToken ct)
    {
        var folder = Path(query.Folder, allowRoot: true);
        var scope = "Current folder";
        using var result = await ListSecretsAsync(context, folder, ct);
        var secrets = Property(result!.RootElement, "secrets");
        if (secrets.ValueKind != JsonValueKind.Array)
            throw Unavailable();
        var entries = secrets.EnumerateArray().Take(SecretFolderPage.EntryLimit + 1).Select(secret =>
        {
            var name = Segment(Text(secret, "secretKey"));
            var locator = JoinPath(folder, name);
            return Entry(context, secret, locator);
        }).ToList();
        try
        {
            using var response = await SendAsync(context, HttpMethod.Get, "/api/v2/folders?" + SharedQuery(context) +
                "&path=" + Uri.EscapeDataString(FullFolder(context, folder)) + "&recursive=false", ct);
            entries.InsertRange(0, ReadFolders(Property(response!.RootElement, "folders"), folder));
        }
        catch (SecretProviderException error) when (error.Reason == SecretProviderFailure.Forbidden)
        {
            scope = "Current folder; the connection cannot list subfolders";
        }
        return SecretFolderPage.Create(entries, query, scope);
    }

    public async Task<SecretEntryDetails> GetMetadataAsync(SecretProviderContext context, string locator, string? version, CancellationToken ct)
    {
        using var document = await ReadAsync(context, locator, version, reveal: false, ct);
        if (document is null)
            throw version is null ? Missing() : MissingVersion();
        var entry = Entry(context, Property(document.RootElement, "secret"), locator);
        if (version is not null && entry.Version != version)
            throw MissingVersion();
        return new(entry, [], [entry.Version!], PositiveInt64(entry.Version) > 1, []);
    }

    public async Task<SecretEntryMetadata> WriteAsync(SecretProviderContext context, WriteSecretEntryRequest request, CancellationToken ct)
    {
        CheckScalarWrite(request);
        if (request.Locator is null || request.Name is not null || request.Enabled is not null)
            throw Invalid("Specify an Infisical secret path and name.");
        var locator = ValidateLocator(request.Locator);
        var (folder, name) = Split(locator);
        var description = OptionalText(request.Description, 1024, "Comment");
        var creating = request.ExpectedVersion is null && request.ExpectedRevision is null;
        if (creating && request.Value is null || !creating && request.Value is null && request.Description is null)
            throw Invalid("Supply a value for a new secret or a value or comment for an edit.");
        if (!creating)
        {
            var current = (await GetMetadataAsync(context, locator, null, ct)).Entry;
            RequireExpected(current, request.ExpectedRevision, request.ExpectedVersion);
        }

        var body = MutationScope(context, folder);
        if (request.Value is not null)
        {
            body["secretValue"] = request.Value;
            body["skipMultilineEncoding"] = true;
        }
        if (request.Description is not null)
            body["secretComment"] = description ?? "";
        using var response = await SendAsync(context, creating ? HttpMethod.Post : HttpMethod.Patch, SecretRoute(name), ct, body);
        return Entry(context, Property(response!.RootElement, "secret"), locator);
    }

    public async Task<SecretDeletionResult> DeleteAsync(SecretProviderContext context, DeleteSecretEntryRequest request, CancellationToken ct)
    {
        var current = (await GetMetadataAsync(context, request.Locator, null, ct)).Entry;
        RequireExpected(current, request.ExpectedRevision, request.ExpectedVersion);
        var (folder, name) = Split(request.Locator);
        using var ignored = await SendAsync(context, HttpMethod.Delete, SecretRoute(name), ct, MutationScope(context, folder), readBody: false);
        return new(request.Locator, request.ExpectedVersion, Capabilities.Deletion, false);
    }

    public async Task<ProviderSecretValues> ResolveAsync(SecretProviderContext context, string locator, string? version,
        string?[] fields, CancellationToken ct)
    {
        using var document = await ReadAsync(context, locator, version, reveal: true, ct) ?? throw MissingVersion();
        var secret = Property(document.RootElement, "secret");
        var entry = Entry(context, secret, locator);
        if (version is not null && entry.Version != version ||
            secret.TryGetProperty("secretValueHidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
            throw MissingVersion();
        return new(entry.Version!, Enumerable.Repeat(Text(secret, "secretValue"), fields.Length).ToArray());
    }

    private Task<JsonDocument?> ListSecretsAsync(SecretProviderContext context, string folder, CancellationToken ct) =>
        SendAsync(context, HttpMethod.Get, $"{SecretsRoute}?{SecretQuery(context, folder, false)}&recursive=false&includePersonalOverrides=false", ct);

    private Task<JsonDocument?> ReadAsync(SecretProviderContext context, string locator, string? version, bool reveal, CancellationToken ct)
    {
        var (folder, name) = Split(locator);
        var query = SecretQuery(context, folder, reveal) + "&type=shared";
        if (version is not null)
            query += "&version=" + PositiveInt64(version).ToString(CultureInfo.InvariantCulture);
        return SendAsync(context, HttpMethod.Get, $"{SecretRoute(name)}?{query}", ct, allowNotFound: true);
    }

    private async Task<JsonDocument?> SendAsync(SecretProviderContext context, HttpMethod method, string route, CancellationToken ct,
        object? body = null, bool readBody = true, bool allowNotFound = false)
    {
        var token = await tokens.GetAsync(context, loginCt => LoginAsync(context, loginCt), ct);
        try { return await http.SendAsync(context, method, route, ct, body, token, allowNotFound, readBody); }
        catch (SecretProviderException error) when (error.Reason == SecretProviderFailure.Authentication)
        {
            tokens.Invalidate(context);
            throw;
        }
    }

    private async Task<ProviderAccessToken> LoginAsync(SecretProviderContext context, CancellationToken ct)
    {
        var authentication = context.ReadAuthentication() ?? throw Unavailable();
        using var document = await http.SendAsync(context, HttpMethod.Post, "/api/v1/auth/universal-auth/login", ct,
            new { clientId = context.Configuration.ClientId, clientSecret = authentication.ClientSecret });
        var result = document!.RootElement;
        var seconds = Math.Clamp(Number(result, "expiresIn"), 1, 3600);
        return new("Authorization", "Bearer " + TokenText(result, "accessToken"), TimeSpan.FromSeconds(seconds));
    }

    private static string SharedQuery(SecretProviderContext context) =>
        "projectId=" + Uri.EscapeDataString(context.Configuration.ProjectId!) + "&environment=" + Uri.EscapeDataString(context.Configuration.Environment!);

    private static string SecretQuery(SecretProviderContext context, string folder, bool reveal) => SharedQuery(context) +
        "&secretPath=" + Uri.EscapeDataString(FullFolder(context, folder)) +
        "&viewSecretValue=" + (reveal ? "true" : "false") + "&expandSecretReferences=false&includeImports=false";

    private static Dictionary<string, object?> MutationScope(SecretProviderContext context, string folder) => new(StringComparer.Ordinal)
    {
        ["projectId"] = context.Configuration.ProjectId,
        ["environment"] = context.Configuration.Environment,
        ["secretPath"] = FullFolder(context, folder),
        ["type"] = "shared"
    };

    private static string FullFolder(SecretProviderContext context, string folder) => "/" + JoinPath(context.Configuration.BasePath!, folder);
    private static string SecretRoute(string name) => SecretsRoute + "/" + Uri.EscapeDataString(name);

    private static (string Folder, string Name) Split(string locator)
    {
        var index = locator.LastIndexOf('/');
        return index < 0 ? ("", locator) : (locator[..index], locator[(index + 1)..]);
    }

    private static SecretEntryMetadata Entry(SecretProviderContext context, JsonElement secret, string locator)
    {
        var (folder, name) = Split(locator);
        if (Text(secret, "secretKey") != name || Text(secret, "workspace") != context.Configuration.ProjectId ||
            Text(secret, "environment") != context.Configuration.Environment || Text(secret, "type") != "shared" ||
            OptionalString(secret, "secretPath") is { } upstreamPath && upstreamPath != FullFolder(context, folder))
            throw Unavailable();
        var version = Number(secret, "version").ToString(CultureInfo.InvariantCulture);
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Text(secret, "_id")}\n{version}\n{OptionalString(secret, "updatedAt")}")));
        return new(locator, name, false, version, revision, OptionalTimestamp(secret, "updatedAt"), OptionalString(secret, "secretComment"), true);
    }

    private static IEnumerable<SecretEntryMetadata> ReadFolders(JsonElement folders, string parent)
    {
        if (folders.ValueKind != JsonValueKind.Array)
            throw Unavailable();
        return folders.EnumerateArray().Take(SecretFolderPage.EntryLimit + 1).Select(folder =>
        {
            var name = Segment(Text(folder, "name"));
            return new SecretEntryMetadata(JoinPath(parent, name), name, true);
        }).ToArray();
    }
}
