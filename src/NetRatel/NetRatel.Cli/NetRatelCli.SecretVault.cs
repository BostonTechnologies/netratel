using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text;
using System.Text.Json;

internal static partial class NetRatelCli
{
    private const int MaximumVaultRequestBytes = 256 * 1024;

    private static Command BuildTenantVaultCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var vault = new Command("vault", "Browse and manage tenant provider connections, secret entries and credential references.");
        vault.AddCommand(VaultRead(runtime, globals, "permissions", "List vault permissions for one tenant.", "/permissions"));
        vault.AddCommand(VaultRead(runtime, globals, "connections", "List saved provider connection metadata.", "/connections"));
        vault.AddCommand(VaultRead(runtime, globals, "profiles", "List saved credential profiles.", "/profiles"));
        vault.AddCommand(VaultBrowse(runtime, globals, metadata: false));
        vault.AddCommand(VaultBrowse(runtime, globals, metadata: true));
        vault.AddCommand(VaultWrite(runtime, globals, "add-connection", "Save a new provider connection.", HttpMethod.Post, "/connections"));
        vault.AddCommand(VaultWrite(runtime, globals, "edit-connection", "Save changes to a provider connection.", HttpMethod.Put, "/connections/{id}", "--connection-id"));
        vault.AddCommand(VaultWrite(runtime, globals, "save-default", "Set the default provider for new selections.", HttpMethod.Put, "/connections/{id}/default", "--connection-id"));
        vault.AddCommand(VaultWrite(runtime, globals, "test-connection", "Test a saved provider's metadata listing.", HttpMethod.Post, "/connections/{id}/test", "--connection-id"));
        vault.AddCommand(VaultWrite(runtime, globals, "enable-connection", "Enable a saved provider connection.", HttpMethod.Post, "/connections/{id}/enable", "--connection-id"));
        vault.AddCommand(VaultWrite(runtime, globals, "write-entry", "Create or edit one secret entry.", HttpMethod.Put, "/connections/{id}/entry", "--connection-id"));
        vault.AddCommand(VaultWrite(runtime, globals, "add-profile", "Save a credential profile.", HttpMethod.Post, "/profiles"));
        vault.AddCommand(VaultWrite(runtime, globals, "edit-profile", "Update a saved credential profile.", HttpMethod.Put, "/profiles/{id}", "--profile-id"));
        vault.AddCommand(VaultWrite(runtime, globals, "preview-removal", "Preview a reviewed vault deletion or disable operation.", HttpMethod.Post, "/removals/preview"));
        vault.AddCommand(VaultWrite(runtime, globals, "confirm-removal", "Confirm an exact reviewed removal using its saved preview.", HttpMethod.Post, "/removals/confirm"));
        vault.AddCommand(VaultWrite(runtime, globals, "reveal", "Deliberately reveal one value with a human account and secret.reveal.", HttpMethod.Post, "/reveal", requireConfirm: true));
        return vault;
    }

    private static Command VaultRead(CliRuntime runtime, GlobalOptions globals, string name, string description, string path)
    {
        var command = new Command(name, description);
        var tenant = RequiredTenantIdOption();
        command.AddOption(tenant);
        command.SetHandler(ctx => SendVaultAsync(runtime, globals, ctx, HttpMethod.Get, VaultRoot(ctx, tenant) + path));
        return command;
    }

    private static Command VaultBrowse(CliRuntime runtime, GlobalOptions globals, bool metadata)
    {
        var command = new Command(metadata ? "entry" : "browse", metadata ? "Read one entry's metadata and observed versions." : "Browse a provider's secret metadata.");
        var tenant = RequiredTenantIdOption();
        var connection = new Option<Guid>("--connection-id") { Required = true, Description = "Saved provider connection UUID." };
        var locator = new Option<string?>("--locator") { Required = metadata, Description = "Canonical secret locator." };
        var version = new Option<string?>("--version") { Description = "Exact provider version." };
        var folder = new Option<string?>("--folder") { Description = "Folder relative to the connection's base path." };
        var search = new Option<string?>("--search") { Description = "Search within the provider's advertised result scope." };
        var cursor = new Option<string?>("--cursor") { Description = "Continuation from the previous page." };
        command.AddOption(tenant);
        command.AddOption(connection);
        foreach (var option in metadata ? new Option[] { locator, version } : [folder, search, cursor])
            command.AddOption(option);
        command.SetHandler(ctx =>
        {
            var path = $"{VaultRoot(ctx, tenant)}/connections/{VaultId(ctx, connection):D}";
            path += metadata
                ? "/entry" + Query(ctx, (locator, "locator"), (version, "version"))
                : "/entries" + Query(ctx, (folder, "folder"), (search, "search"), (cursor, "cursor"));
            return SendVaultAsync(runtime, globals, ctx, HttpMethod.Get, path);
        });
        return command;
    }

    private static Command VaultWrite(CliRuntime runtime, GlobalOptions globals, string name, string description, HttpMethod method,
        string path, string? idOption = null, bool requireConfirm = false)
    {
        var command = new Command(name, description);
        var tenant = RequiredTenantIdOption();
        var file = new Option<string?>("--request-file") { Description = "Read the JSON request from a private file." };
        var stdin = new Option<bool>("--stdin") { Description = "Read the JSON request from standard input." };
        var confirm = new Option<bool>("--confirm") { Description = "Display the authorized plaintext value." };
        var id = idOption is null ? null : new Option<Guid>(idOption) { Required = true, Description = "Exact saved resource UUID." };
        command.AddOption(tenant);
        command.AddOption(file);
        command.AddOption(stdin);
        if (id is not null) command.AddOption(id);
        if (requireConfirm) command.AddOption(confirm);
        command.SetHandler(async ctx =>
        {
            if (requireConfirm && !ctx.ParseResult.GetValueForOption(confirm))
                throw new CliValidationException("Pass --confirm to explicitly display the value.");
            var request = await ReadVaultRequestAsync(runtime, ctx.ParseResult.GetValueForOption(file), ctx.ParseResult.GetValueForOption(stdin), ctx.GetCancellationToken());
            var route = id is null ? path : path.Replace("{id}", VaultId(ctx, id).ToString("D"), StringComparison.Ordinal);
            await SendVaultAsync(runtime, globals, ctx, method, VaultRoot(ctx, tenant) + route, request);
        });
        return command;
    }

    private static async Task<string> ReadVaultRequestAsync(CliRuntime runtime, string? file, bool stdin, CancellationToken ct)
    {
        if ((file is not null) == stdin)
            throw new CliValidationException("Choose --request-file or --stdin.");
        try
        {
            if (file is null)
                return await ReadBoundedVaultJsonAsync(runtime.In, ct);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(stream.SafeFileHandle) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead |
                UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new CliValidationException("Use an owner-only request file or protected standard input.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return await ReadBoundedVaultJsonAsync(reader, ct);
        }
        catch (IOException) { throw new CliValidationException("The vault request could not be read."); }
        catch (UnauthorizedAccessException) { throw new CliValidationException("The vault request file is inaccessible."); }
        catch (DecoderFallbackException) { throw new CliValidationException("The vault request requires valid UTF-8."); }
    }

    private static async Task<string> ReadBoundedVaultJsonAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[MaximumVaultRequestBytes + 1];
        try
        {
            var length = await reader.ReadBlockAsync(buffer, ct);
            if (length > MaximumVaultRequestBytes || Encoding.UTF8.GetByteCount(buffer.AsSpan(0, length)) > MaximumVaultRequestBytes)
                throw new CliValidationException("The vault request exceeds 256 KiB.");
            var json = new string(buffer, 0, length);
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.ValueKind == JsonValueKind.Object ? json : throw new CliValidationException("The vault request requires a JSON object.");
        }
        catch (JsonException) { throw new CliValidationException("The vault request requires a valid JSON object."); }
        finally { Array.Clear(buffer); }
    }

    private static async Task SendVaultAsync(CliRuntime runtime, GlobalOptions globals, CliInvocationContext ctx,
        HttpMethod method, string path, string? body = null)
    {
        var endpoint = LoadConfig(ctx, globals).Resolved!.ApiBaseUrl;
        if (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
            throw new CliValidationException("Use an HTTPS API endpoint for vault requests.");
        try { await SendAsync(runtime, globals, ctx, method, path, body); }
        catch (CliRemoteException error)
        {
            throw new CliRemoteException(error.StatusCode is 401 or 403 ? "vault_denied" : "vault_request_failed",
                "The vault operation failed. Check the account's grant and the saved provider or entry revision.", error.StatusCode, responseBody: null);
        }
    }

    private static string VaultRoot(CliInvocationContext ctx, Option<int> tenant)
    {
        var id = ctx.ParseResult.GetValueForOption(tenant);
        if (id <= 0) throw new CliValidationException("Select a positive tenant ID.");
        return $"/api/v1/tenants/{id.ToString(CultureInfo.InvariantCulture)}/secret-vault";
    }

    private static Guid VaultId(CliInvocationContext ctx, Option<Guid> option)
    {
        var id = ctx.ParseResult.GetValueForOption(option);
        return id == Guid.Empty ? throw new CliValidationException("Select a nonempty resource UUID.") : id;
    }
}
