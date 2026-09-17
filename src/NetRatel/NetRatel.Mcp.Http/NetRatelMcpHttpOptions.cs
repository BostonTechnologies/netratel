using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetRatel.Shared.Operations;

namespace NetRatel.Mcp.Http;

public sealed class NetRatelMcpHttpOptions
{
    public const string DefaultReadScope = "netratel.mcp.read";
    public const string DefaultObserveScope = "netratel.mcp.observe";
    public const string DefaultFilesScope = "netratel.mcp.files";
    public const string DefaultDevelopmentWriteScope = "netratel.mcp.write";
    public const string DefaultExecuteScope = "netratel.mcp.execute";
    public const string DefaultDevelopmentOnboardingScope = "netratel.mcp.onboarding";
    public const string DefaultAdminScope = "netratel.mcp.admin";
    public const string OpenIdScope = "openid";
    public const string ProfileScope = "profile";
    public const string OfflineAccessScope = "offline_access";
    public const string SectionName = "NetRatel:Mcp:Http";
    public const string InstanceEnvironmentVariable = "NETRATEL_MCP_INSTANCE";
    public const string ConfigurationPathEnvironmentVariable = "NETRATEL_MCP_CONFIG";
    public const string DevApiBaseUrlEnvironmentVariable = "NETRATEL_MCP_DEV_API_BASE_URL";
    public const string ProdApiBaseUrlEnvironmentVariable = "NETRATEL_MCP_PROD_API_BASE_URL";

    [Required] public string Instance { get; set; } = string.Empty;
    /// <summary>
    /// Enables the policy-admitted V2 operator surface for Development. A
    /// missing value preserves existing Dev compatibility; Production is
    /// always operator-surface enabled.
    /// </summary>
    public bool? OperatorSurfaceEnabled { get; set; }
    public string ConfigurationPath { get; set; } = string.Empty;
    public string DevApiBaseUrl { get; set; } = string.Empty;
    public string ProdApiBaseUrl { get; set; } = string.Empty;
    [Required, Url] public string PublicResourceUri { get; set; } = string.Empty;
    [Required, Url] public string Authority { get; set; } = string.Empty;
    /// <summary>
    /// Requires HTTPS discovery metadata for the OAuth authority. This remains
    /// enabled by default and may only be disabled by a Development host for an
    /// isolated disposable test authority.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;
    [Required] public string Audience { get; set; } = string.Empty;
    [MinLength(1)] public string[] RequiredGroups { get; set; } = [];
    [MinLength(1)] public string[] RequiredScopes { get; set; } = [];
    [Required] public string DevelopmentWriteScope { get; set; } = DefaultDevelopmentWriteScope;
    [Required] public string DevelopmentOnboardingScope { get; set; } = DefaultDevelopmentOnboardingScope;
    [Required] public string ReadScope { get; set; } = DefaultReadScope;
    [Required] public string ObserveScope { get; set; } = DefaultObserveScope;
    [Required] public string FilesScope { get; set; } = DefaultFilesScope;
    [Required] public string ExecuteScope { get; set; } = DefaultExecuteScope;
    [Required] public string AdminScope { get; set; } = DefaultAdminScope;
    public string[] AllowedOrigins { get; set; } = [];
    [Range(1_024, 1_048_576)] public long MaxRequestBodyBytes { get; set; } = 1_048_576;

    public bool IsOperatorSurfaceEnabled => OperatorSurfaceEnabled ??
        string.Equals(Instance.Trim(), "prod", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Binds the HTTP-host section and overlays the compact deployment variables.
    /// The overlay is deliberately limited to instance selection, isolated config
    /// path, and target allowlist endpoints; authentication remains explicit in
    /// the typed HTTP section rather than being inferred from ambient credentials.
    /// </summary>
    public static NetRatelMcpHttpOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration.GetSection(SectionName).Get<NetRatelMcpHttpOptions>() ?? new NetRatelMcpHttpOptions();

        return new NetRatelMcpHttpOptions
        {
            Instance = ValueOrConfigured(configuration, InstanceEnvironmentVariable, configured.Instance),
            OperatorSurfaceEnabled = configured.OperatorSurfaceEnabled,
            ConfigurationPath = ValueOrConfigured(configuration, ConfigurationPathEnvironmentVariable, configured.ConfigurationPath),
            DevApiBaseUrl = ValueOrConfigured(configuration, DevApiBaseUrlEnvironmentVariable, configured.DevApiBaseUrl),
            ProdApiBaseUrl = ValueOrConfigured(configuration, ProdApiBaseUrlEnvironmentVariable, configured.ProdApiBaseUrl),
            PublicResourceUri = configured.PublicResourceUri,
            Authority = configured.Authority,
            RequireHttpsMetadata = configured.RequireHttpsMetadata,
            Audience = configured.Audience,
            RequiredGroups = configured.RequiredGroups,
            RequiredScopes = configured.RequiredScopes,
            DevelopmentWriteScope = configured.DevelopmentWriteScope,
            DevelopmentOnboardingScope = configured.DevelopmentOnboardingScope,
            ReadScope = configured.ReadScope,
            ObserveScope = configured.ObserveScope,
            FilesScope = configured.FilesScope,
            ExecuteScope = configured.ExecuteScope,
            AdminScope = configured.AdminScope,
            AllowedOrigins = configured.AllowedOrigins,
            MaxRequestBodyBytes = configured.MaxRequestBodyBytes
        };
    }

    private static string ValueOrConfigured(IConfiguration configuration, string key, string configured)
        => string.IsNullOrWhiteSpace(configuration[key]) ? configured : configuration[key]!;

    public string RequiredScopeFor(McpOperationAccessScope scope) => scope switch
    {
        McpOperationAccessScope.Read => ReadScope,
        McpOperationAccessScope.Observe => ObserveScope,
        McpOperationAccessScope.Files => FilesScope,
        McpOperationAccessScope.Write => DevelopmentWriteScope,
        McpOperationAccessScope.Execute => ExecuteScope,
        McpOperationAccessScope.Onboarding => DevelopmentOnboardingScope,
        McpOperationAccessScope.Admin => AdminScope,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "The scope class is not an MCP operation permission.")
    };

    public McpDevelopmentScopeRequirement DevelopmentCompatibilityRequirementFor(McpDevelopmentCompatibilityScope scope) => scope switch
    {
        McpDevelopmentCompatibilityScope.HostRequirement => new(null, null),
        McpDevelopmentCompatibilityScope.DevelopmentWrite => new(DevelopmentWriteScope, "missing_development_write_scope"),
        McpDevelopmentCompatibilityScope.DevelopmentOnboarding => new(DevelopmentOnboardingScope, "missing_development_onboarding_scope"),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "The development compatibility requirement is not catalogued.")
    };

    public IReadOnlyList<string> AdvertisedScopes => RequiredScopes
        // Oidc emits mapped operator roles through profile. Discovery-based
        // clients must be able to request it along with the application scopes.
        .Append(OpenIdScope).Append(ProfileScope)
        .Append(ReadScope).Append(ObserveScope).Append(FilesScope)
        .Append(DevelopmentWriteScope).Append(ExecuteScope)
        .Append(DevelopmentOnboardingScope).Append(AdminScope).Append(OfflineAccessScope)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

public sealed record McpDevelopmentScopeRequirement(string? Scope, string? MissingScopeCode);

public sealed class NetRatelMcpHttpOptionsValidator : IValidateOptions<NetRatelMcpHttpOptions>
{
    public ValidateOptionsResult Validate(string? name, NetRatelMcpHttpOptions options)
    {
        var failures = new List<string>();
        var instance = options.Instance.Trim().ToLowerInvariant();
        if (instance is not ("dev" or "prod"))
            failures.Add("NetRatel:Mcp:Http:Instance must be dev or prod.");
        ValidateApiBaseUrl(options.DevApiBaseUrl, "DevApiBaseUrl", instance == "dev", failures);
        ValidateApiBaseUrl(options.ProdApiBaseUrl, "ProdApiBaseUrl", instance == "prod", failures);
        if (!string.IsNullOrWhiteSpace(options.DevApiBaseUrl) &&
            !string.IsNullOrWhiteSpace(options.ProdApiBaseUrl) &&
            Equivalent(options.DevApiBaseUrl, options.ProdApiBaseUrl))
            failures.Add("NetRatel:Mcp:Http:DevApiBaseUrl and ProdApiBaseUrl must be distinct.");
        if (!IsCanonicalMcpResource(options.PublicResourceUri))
            failures.Add("NetRatel:Mcp:Http:PublicResourceUri must be an absolute HTTPS /mcp URI.");
        if (!IsAuthorityUri(options.Authority, options.RequireHttpsMetadata))
            failures.Add(options.RequireHttpsMetadata
                ? "NetRatel:Mcp:Http:Authority must be an absolute HTTPS URI."
                : "NetRatel:Mcp:Http:Authority must be an absolute HTTP or HTTPS URI when RequireHttpsMetadata is false.");
        if (options.MaxRequestBodyBytes is < 1_024 or > 1_048_576)
            failures.Add("NetRatel:Mcp:Http:MaxRequestBodyBytes must be between 1024 and 1048576 bytes.");
        if (!string.Equals(Normalize(options.Audience), Normalize(options.PublicResourceUri), StringComparison.Ordinal))
            failures.Add("NetRatel:Mcp:Http:Audience must equal PublicResourceUri.");
        ValidateValues(options.RequiredScopes, "RequiredScopes", failures);
        ValidateValues(options.RequiredGroups, "RequiredGroups", failures);
        foreach (var (scopeName, scope) in new[]
                 {
                     (nameof(options.ReadScope), options.ReadScope),
                     (nameof(options.ObserveScope), options.ObserveScope),
                     (nameof(options.FilesScope), options.FilesScope),
                     (nameof(options.DevelopmentWriteScope), options.DevelopmentWriteScope),
                     (nameof(options.ExecuteScope), options.ExecuteScope),
                     (nameof(options.DevelopmentOnboardingScope), options.DevelopmentOnboardingScope),
                     (nameof(options.AdminScope), options.AdminScope)
                 })
        {
            if (!IsScopeToken(scope))
                failures.Add($"NetRatel:Mcp:Http:{scopeName} must be a non-empty OAuth scope token.");
        }
        if (options.AllowedOrigins.Any(origin => !IsOrigin(origin)))
            failures.Add("NetRatel:Mcp:Http:AllowedOrigins must contain absolute HTTPS origins only.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static void ThrowIfInvalid(NetRatelMcpHttpOptions options)
    {
        var result = new NetRatelMcpHttpOptionsValidator().Validate(Options.DefaultName, options);
        if (result.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(NetRatelMcpHttpOptions), result.Failures);
    }

    private static void ValidateValues(IEnumerable<string> values, string name, ICollection<string> failures)
    {
        var valuesArray = values.ToArray();
        if (valuesArray.Length == 0 || valuesArray.Any(string.IsNullOrWhiteSpace))
            failures.Add($"NetRatel:Mcp:Http:{name} must contain non-empty values.");
        if (valuesArray.Distinct(StringComparer.Ordinal).Count() != valuesArray.Length)
            failures.Add($"NetRatel:Mcp:Http:{name} must not contain duplicates.");
    }

    private static void ValidateApiBaseUrl(string value, string name, bool required, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
                failures.Add($"NetRatel:Mcp:Http:{name} is required for the selected instance.");

            return;
        }

        if (!IsAbsoluteHttps(value))
            failures.Add($"NetRatel:Mcp:Http:{name} must be an absolute HTTPS URI.");
    }

    private static bool IsAbsoluteHttps(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsAuthorityUri(string value, bool requireHttpsMetadata)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || !requireHttpsMetadata && uri.Scheme == Uri.UriSchemeHttp)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsCanonicalMcpResource(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.Equals(uri.AbsolutePath.TrimEnd('/'), "/mcp", StringComparison.Ordinal)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsOrigin(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.AbsolutePath == "/"
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsScopeToken(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static string Normalize(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsoluteUri.TrimEnd('/') : value;

    private static bool Equivalent(string left, string right)
        => Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
           && Uri.TryCreate(right, UriKind.Absolute, out var rightUri)
           && string.Equals(Normalize(leftUri.AbsoluteUri), Normalize(rightUri.AbsoluteUri), StringComparison.Ordinal);
}
