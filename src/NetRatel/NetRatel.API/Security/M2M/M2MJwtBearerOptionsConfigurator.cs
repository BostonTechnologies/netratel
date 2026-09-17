using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.Security.M2M;

/// <summary>
/// Configures the API-owned M2M bearer scheme with the same active signing key
/// used by the local token endpoint. This avoids an availability dependency on
/// the API reaching its own public OIDC discovery endpoint during validation.
/// </summary>
public sealed class M2MJwtBearerOptionsConfigurator(
    IOptions<M2MOptions> m2mOptions,
    OidcSigningService signingService) : IConfigureNamedOptions<JwtBearerOptions>
{
    private const string Scheme = "M2M";

    private readonly IOptions<M2MOptions> _m2mOptions = m2mOptions ?? throw new ArgumentNullException(nameof(m2mOptions));
    private readonly OidcSigningService _signingService = signingService ?? throw new ArgumentNullException(nameof(signingService));

    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(name, Scheme, StringComparison.Ordinal))
        {
            return;
        }

        var configured = _m2mOptions.Value;
        var authority = configured.Authority.TrimEnd('/');
        var signingKey = _signingService.GetActiveSigningKeyAsync(CancellationToken.None).GetAwaiter().GetResult();
        var configuration = new OpenIdConnectConfiguration { Issuer = authority };
        configuration.SigningKeys.Add(signingKey);

        options.Authority = authority;
        options.Audience = configured.Audience;
        options.RequireHttpsMetadata = true;
        options.Configuration = configuration;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [configured.Authority, authority],
            ValidateAudience = true,
            ValidAudience = configured.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "client_id"
        };
    }
}
