using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace NetRatel.Shared.Operations;

/// <summary>
/// Deployment-owned settings for the signed assertion that preserves the MCP
/// caller identity across the MCP-to-API service boundary. The shared key is a
/// secret reference supplied separately for each environment; it is never an
/// OAuth access token and must never be written to logs or audit records.
/// </summary>
public sealed class McpOperatorDelegationOptions
{
    public const string SectionName = "NetRatel:Mcp:Delegation";
    public const string HeaderName = "X-NetRatel-Mcp-Delegation";

    /// <summary>
    /// Keeps the existing deployed Dev path unchanged until the matching MCP
    /// host and API secret references have been provisioned together.
    /// </summary>
    public bool Enabled { get; set; }

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string ServicePrincipal { get; set; } = "netratel-mcp-http";

    public string KeyId { get; set; } = string.Empty;

    /// <summary>Base64-encoded, at least 256-bit HMAC key supplied by the secret store.</summary>
    public string SharedKeyBase64 { get; set; } = string.Empty;

    public int LifetimeSeconds { get; set; } = 90;

    public void EnsureValid()
    {
        if (!Enabled)
            return;

        RequireToken(Issuer, nameof(Issuer));
        RequireToken(Audience, nameof(Audience));
        RequireToken(ServicePrincipal, nameof(ServicePrincipal));
        RequireToken(KeyId, nameof(KeyId));
        if (LifetimeSeconds is < 30 or > 300)
            throw new InvalidOperationException($"{SectionName}:LifetimeSeconds must be between 30 and 300.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(SharedKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{SectionName}:SharedKeyBase64 must be valid base64.", exception);
        }

        if (key.Length < 32)
            throw new InvalidOperationException($"{SectionName}:SharedKeyBase64 must decode to at least 32 bytes.");
    }

    internal SymmetricSecurityKey CreateSigningKey()
    {
        EnsureValid();
        return new SymmetricSecurityKey(Convert.FromBase64String(SharedKeyBase64))
        {
            KeyId = KeyId
        };
    }

    private static void RequireToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':' and not '/'))
            throw new InvalidOperationException($"{SectionName}:{name} must be a non-empty bounded identifier.");
    }
}

/// <summary>
/// Explicitly enables a deployment-owned local agent identity for the direct
/// CLI and stdio transports. This is intentionally opt-in: an API M2M
/// credential is not an operator identity merely because it can reach the
/// API. When enabled, only the exact configured client IDs may be represented
/// as their own OAuth identity; they cannot name or impersonate a human.
/// </summary>
public sealed class McpOperatorLocalAgentOptions
{
    public const string SectionName = "NetRatel:Mcp:LocalAgent";

    public bool Enabled { get; set; }

    public string ServicePrincipal { get; set; } = "netratel-local-agent";

    public string[] AllowedClientIds { get; set; } = [];

    public void EnsureValid()
    {
        if (!Enabled)
            return;

        RequireToken(ServicePrincipal, nameof(ServicePrincipal));
        var allowedClientIds = AllowedClientIds ?? [];
        if (allowedClientIds.Length is < 1 or > 64)
            throw new InvalidOperationException($"{SectionName}:AllowedClientIds must contain between one and 64 client IDs when enabled.");
        foreach (var clientId in allowedClientIds)
            RequireToken(clientId, nameof(AllowedClientIds));
    }

    public bool Allows(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Enabled && (AllowedClientIds ?? []).Contains(principal.FindFirst("client_id")?.Value, StringComparer.Ordinal);
    }

    private static void RequireToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':' and not '/'))
            throw new InvalidOperationException($"{SectionName}:{name} must be a non-empty bounded identifier.");
    }
}

/// <summary>Content-safe caller facts copied from a validated inbound OAuth principal.</summary>
public sealed record McpOperatorDelegationIdentity(
    string Subject,
    string? ClientId,
    string? AuthorizedParty,
    IReadOnlyCollection<string> Groups,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Scopes,
    int? ActiveTenantId = null);

/// <summary>Creates bounded audit facts from an already-validated OAuth principal.</summary>
public static class McpOperatorDelegationIdentityFactory
{
    public static bool TryCreate(ClaimsPrincipal principal, out McpOperatorDelegationIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(principal);
        identity = null;
        var subject = First(principal, JwtRegisteredClaimNames.Sub, ClaimTypes.NameIdentifier, "client_id", "azp");
        if (!IsSafe(subject))
            return false;

        var clientId = First(principal, "client_id");
        var authorizedParty = First(principal, "azp");
        var groups = Values(principal, "groups");
        var roles = Values(principal, "roles", ClaimTypes.Role, "role");
        var scopes = ScopeValues(principal);
        if (!TryOptionalPositiveInt(First(principal, "tenant_id", "tenantId"), out var activeTenantId))
            return false;
        if (!IsSafeOptional(clientId) || !IsSafeOptional(authorizedParty) ||
            !groups.All(IsSafe) || !roles.All(IsSafe) || !scopes.All(IsSafe))
        {
            return false;
        }

        identity = new McpOperatorDelegationIdentity(subject!, clientId, authorizedParty, groups, roles, scopes, activeTenantId);
        return true;
    }

    private static string? First(ClaimsPrincipal principal, params string[] types)
        => types.Select(type => principal.FindFirst(type)?.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string[] Values(ClaimsPrincipal principal, params string[] types)
        => principal.Claims
            .Where(claim => types.Contains(claim.Type, StringComparer.Ordinal))
            .Select(claim => claim.Value)
            .Where(IsSafe)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(64)
            .ToArray();

    private static string[] ScopeValues(ClaimsPrincipal principal)
        => principal.FindAll("scope").Concat(principal.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(IsSafe)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(64)
            .ToArray();

    private static bool TryOptionalPositiveInt(string? value, out int? parsed)
    {
        parsed = null;
        if (value is null)
            return true;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var integer) || integer <= 0)
            return false;
        parsed = integer;
        return true;
    }

    private static bool IsSafeOptional(string? value) => value is null || IsSafe(value);

    private static bool IsSafe(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);
}

/// <summary>
/// Server-derived facts bound into an MCP delegation assertion. The host takes
/// these only from its validated MCP request and closed schema, never from an
/// arbitrary client-supplied header.
/// </summary>
public sealed record McpOperatorDelegationRequest(
    string Tool,
    string Operation,
    string RequestId,
    string? Resource = null,
    string? Instance = null,
    int? TenantId = null,
    Guid? AgentId = null,
    string? CorrelationId = null)
{
    /// <summary>
    /// The opaque HTTP-MCP credential identifier after the API has exchanged
    /// it. This is never the bearer secret and is rechecked by the API before
    /// every delegated execution.
    /// </summary>
    public string? IngressCredentialId { get; init; }

    /// <summary>Server-selected application permission required by local execution.</summary>
    public string? IngressPermission { get; init; }
}

/// <summary>Verified, bounded delegation assertion available to an API operation.</summary>
public sealed record McpOperatorDelegation(
    McpOperatorDelegationIdentity Identity,
    string ServicePrincipal,
    string Tool,
    string Operation,
    string RequestId,
    DateTimeOffset ExpiresAtUtc,
    string? Resource = null,
    string? Instance = null,
    int? TenantId = null,
    Guid? AgentId = null,
    string? CorrelationId = null)
{
    /// <summary>Non-secret source credential identity for local HTTP MCP execution.</summary>
    public string? IngressCredentialId { get; init; }

    /// <summary>Server-selected application permission required by local execution.</summary>
    public string? IngressPermission { get; init; }
}

/// <summary>
/// Creates and verifies a short-lived signed assertion. The host keeps using
/// its isolated API service credential for authentication; this token carries
/// only actor attribution and operation intent.
/// </summary>
public sealed class McpOperatorDelegationTokenService
{
    private const int MaximumAssertionLength = 8_192;
    private readonly McpOperatorDelegationOptions _options;
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public McpOperatorDelegationTokenService(McpOperatorDelegationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        _options = options;
    }

    public string Create(
        McpOperatorDelegationIdentity identity,
        string tool,
        string operation,
        string requestId,
        DateTimeOffset? issuedAtUtc = null)
        => Create(identity, new McpOperatorDelegationRequest(tool, operation, requestId), issuedAtUtc);

    public string Create(
        McpOperatorDelegationIdentity identity,
        McpOperatorDelegationRequest request,
        DateTimeOffset? issuedAtUtc = null)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("MCP operator delegation is disabled.");

        ValidateIdentity(identity);
        ValidateRequest(request);

        var issuedAt = issuedAtUtc ?? DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, identity.Subject),
            new("mcp_service", _options.ServicePrincipal),
            new("mcp_tool", request.Tool),
            new("mcp_operation", request.Operation),
            new(JwtRegisteredClaimNames.Jti, request.RequestId)
        };
        AddOptional(claims, "client_id", identity.ClientId);
        AddOptional(claims, "azp", identity.AuthorizedParty);
        if (identity.ActiveTenantId is { } activeTenantId)
            claims.Add(new Claim("mcp_active_tenant_id", activeTenantId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        AddOptional(claims, "mcp_resource", request.Resource);
        AddOptional(claims, "mcp_instance", request.Instance);
        AddOptional(claims, "mcp_correlation_id", request.CorrelationId);
        AddOptional(claims, "mcp_ingress_credential_id", request.IngressCredentialId);
        AddOptional(claims, "mcp_ingress_permission", request.IngressPermission);
        if (request.TenantId is { } tenantId)
            claims.Add(new Claim("mcp_tenant_id", tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (request.AgentId is { } agentId)
            claims.Add(new Claim("mcp_agent_id", agentId.ToString("D")));
        AddMany(claims, "groups", identity.Groups);
        AddMany(claims, "roles", identity.Roles);
        AddMany(claims, "scope", identity.Scopes);

        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            issuedAt.UtcDateTime.AddSeconds(-5),
            issuedAt.UtcDateTime.AddSeconds(_options.LifetimeSeconds),
            new SigningCredentials(_options.CreateSigningKey(), SecurityAlgorithms.HmacSha256));
        return _handler.WriteToken(token);
    }

    public bool TryValidate(string? assertion, out McpOperatorDelegation? delegation)
    {
        delegation = null;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(assertion) || assertion.Length > MaximumAssertionLength)
            return false;

        try
        {
            var principal = _handler.ValidateToken(assertion, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = true,
                ValidAudience = _options.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _options.CreateSigningKey(),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(15),
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = "roles"
            }, out var validatedToken);

            if (validatedToken is not JwtSecurityToken token ||
                !string.Equals(token.Header.Kid, _options.KeyId, StringComparison.Ordinal) ||
                !string.Equals(principal.FindFirst("mcp_service")?.Value, _options.ServicePrincipal, StringComparison.Ordinal))
                return false;

            var identity = new McpOperatorDelegationIdentity(
                RequireClaim(principal, JwtRegisteredClaimNames.Sub),
                OptionalClaim(principal, "client_id"),
                OptionalClaim(principal, "azp"),
                Values(principal, "groups"),
                Values(principal, "roles"),
                Values(principal, "scope"),
                OptionalPositiveIntClaim(principal, "mcp_active_tenant_id"));
            ValidateIdentity(identity);
            var tool = RequireClaim(principal, "mcp_tool");
            var operation = RequireClaim(principal, "mcp_operation");
            var requestId = RequireClaim(principal, JwtRegisteredClaimNames.Jti);
            RequireValue(tool, "mcp_tool");
            RequireValue(operation, "mcp_operation");
            RequireValue(requestId, JwtRegisteredClaimNames.Jti);
            var delegationRequest = new McpOperatorDelegationRequest(
                tool,
                operation,
                requestId,
                OptionalClaim(principal, "mcp_resource"),
                OptionalClaim(principal, "mcp_instance"),
                OptionalPositiveIntClaim(principal, "mcp_tenant_id"),
                OptionalGuidClaim(principal, "mcp_agent_id"),
                OptionalClaim(principal, "mcp_correlation_id"))
            {
                IngressCredentialId = OptionalClaim(principal, "mcp_ingress_credential_id"),
                IngressPermission = OptionalClaim(principal, "mcp_ingress_permission")
            };
            ValidateRequest(delegationRequest);
            delegation = new McpOperatorDelegation(
                identity,
                _options.ServicePrincipal,
                delegationRequest.Tool,
                delegationRequest.Operation,
                delegationRequest.RequestId,
                new DateTimeOffset(token.ValidTo, TimeSpan.Zero),
                delegationRequest.Resource,
                delegationRequest.Instance,
                delegationRequest.TenantId,
                delegationRequest.AgentId,
                delegationRequest.CorrelationId)
            {
                IngressCredentialId = delegationRequest.IngressCredentialId,
                IngressPermission = delegationRequest.IngressPermission
            };
            return true;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void AddOptional(ICollection<Claim> claims, string type, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            claims.Add(new Claim(type, value));
    }

    private static void AddMany(ICollection<Claim> claims, string type, IEnumerable<string> values)
    {
        foreach (var value in values.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            claims.Add(new Claim(type, value));
    }

    private static string RequireClaim(ClaimsPrincipal principal, string type)
        => principal.FindFirst(type)?.Value ?? throw new InvalidOperationException($"Delegation assertion is missing {type}.");

    private static string? OptionalClaim(ClaimsPrincipal principal, string type)
        => principal.FindFirst(type)?.Value;

    private static int? OptionalPositiveIntClaim(ClaimsPrincipal principal, string type)
    {
        var value = OptionalClaim(principal, type);
        if (value is null)
            return null;
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException($"Delegation assertion has an invalid {type} claim.");
    }

    private static Guid? OptionalGuidClaim(ClaimsPrincipal principal, string type)
    {
        var value = OptionalClaim(principal, type);
        if (value is null)
            return null;
        return Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidOperationException($"Delegation assertion has an invalid {type} claim.");
    }

    private static IReadOnlyCollection<string> Values(ClaimsPrincipal principal, string type)
        => principal.FindAll(type).Select(claim => claim.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static void ValidateIdentity(McpOperatorDelegationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RequireValue(identity.Subject, nameof(identity.Subject));
        ValidateOptional(identity.ClientId, nameof(identity.ClientId));
        ValidateOptional(identity.AuthorizedParty, nameof(identity.AuthorizedParty));
        if (identity.ActiveTenantId is <= 0)
            throw new InvalidOperationException("Delegation ActiveTenantId must be positive when supplied.");
        ValidateValues(identity.Groups, nameof(identity.Groups));
        ValidateValues(identity.Roles, nameof(identity.Roles));
        ValidateValues(identity.Scopes, nameof(identity.Scopes));
    }

    private static void ValidateRequest(McpOperatorDelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireValue(request.Tool, nameof(request.Tool));
        RequireValue(request.Operation, nameof(request.Operation));
        RequireValue(request.RequestId, nameof(request.RequestId));
        ValidateOptional(request.Resource, nameof(request.Resource));
        ValidateOptional(request.Instance, nameof(request.Instance));
        ValidateOptional(request.CorrelationId, nameof(request.CorrelationId));
        ValidateOptional(request.IngressCredentialId, nameof(request.IngressCredentialId));
        ValidateOptional(request.IngressPermission, nameof(request.IngressPermission));
        if (request.TenantId is <= 0)
            throw new InvalidOperationException("Delegation TenantId must be positive when supplied.");
        if (request.AgentId == Guid.Empty)
            throw new InvalidOperationException("Delegation AgentId must not be empty when supplied.");
    }

    private static void ValidateOptional(string? value, string name)
    {
        if (!string.IsNullOrWhiteSpace(value))
            RequireValue(value, name);
    }

    private static void ValidateValues(IReadOnlyCollection<string> values, string name)
    {
        if (values.Count > 64)
            throw new InvalidOperationException($"Delegation {name} exceeds 64 values.");
        foreach (var value in values)
            RequireValue(value, name);
    }

    private static void RequireValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new InvalidOperationException($"Delegation {name} must be a non-empty bounded value.");
    }
}

/// <summary>
/// Per-async-flow assertion storage. It never stores OAuth bearer tokens and
/// is cleared when the MCP tool invocation exits.
/// </summary>
public interface IMcpOperatorDelegationContext
{
    string? CurrentAssertion { get; }

    IDisposable Begin(string assertion);
}

public sealed class McpOperatorDelegationContext : IMcpOperatorDelegationContext
{
    private readonly AsyncLocal<Scope?> _current = new();

    public string? CurrentAssertion => _current.Value?.Assertion;

    public IDisposable Begin(string assertion)
    {
        if (string.IsNullOrWhiteSpace(assertion) || assertion.Length > MaximumAssertionLength)
            throw new ArgumentException("A bounded delegation assertion is required.", nameof(assertion));

        var previous = _current.Value;
        _current.Value = new Scope(assertion, previous);
        return new Revert(_current, previous);
    }

    private const int MaximumAssertionLength = 8_192;

    private sealed record Scope(string Assertion, Scope? Previous);

    private sealed class Revert(AsyncLocal<Scope?> current, Scope? previous) : IDisposable
    {
        private AsyncLocal<Scope?>? _current = current;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _current, null);
            if (current is not null)
                current.Value = previous;
        }
    }
}
