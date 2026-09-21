using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Core.Prompts;
using NetRatel.Shared.Operations;
using System.Threading.RateLimiting;

namespace NetRatel.Mcp.Http;

/// <summary>Configures the stateless, bearer-protected HTTP MCP boundary.</summary>
public static class NetRatelMcpHttpApplication
{
    public const string McpScheme = "NetRatelMcp";
    public const string JwtScheme = "NetRatelMcpJwt";
    public const string PolicyName = "NetRatelMcpRemote";
    public const string RateLimitPolicy = "NetRatelMcp";

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();

        var httpOptions = NetRatelMcpHttpOptions.FromConfiguration(builder.Configuration);
        NetRatelMcpHttpOptionsValidator.ThrowIfInvalid(httpOptions);
        if (!httpOptions.RequireHttpsMetadata && !builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "NetRatel:Mcp:Http:RequireHttpsMetadata=false is allowed only in the Development environment for isolated disposable test authorities.");
        }
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = httpOptions.MaxRequestBodyBytes);

        builder.Services.AddSingleton<IValidateOptions<NetRatelMcpHttpOptions>, NetRatelMcpHttpOptionsValidator>();
        builder.Services.AddOptions<NetRatelMcpHttpOptions>()
            .Configure(options => Copy(httpOptions, options))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton(httpOptions);

        var delegationOptions = builder.Configuration.GetSection(McpOperatorDelegationOptions.SectionName).Get<McpOperatorDelegationOptions>()
            ?? new McpOperatorDelegationOptions();
        delegationOptions.EnsureValid();
        if (httpOptions.IsOperatorSurfaceEnabled && !delegationOptions.Enabled)
        {
            throw new InvalidOperationException(
                "The Production MCP host requires NetRatel:Mcp:Delegation:Enabled=true; any host that publishes V2 operator routes requires the same setting.");
        }
        if (httpOptions.LocalCredentialMode && !delegationOptions.Enabled)
        {
            throw new InvalidOperationException(
                "Local HTTP MCP credential mode requires NetRatel:Mcp:Delegation:Enabled=true so the paired API exchange remains authenticated.");
        }

        var targetBinding = NetRatelMcpHttpTargetBinding.Resolve(httpOptions);
        var hostContext = new NetRatelMcpHostContext(
            targetBinding.Target,
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http",
            httpOptions.IsOperatorSurfaceEnabled);
        builder.Services.AddNetRatelMcpCore(hostContext);
        builder.Services.AddSingleton(targetBinding);
        builder.Services.AddSingleton(delegationOptions);
        builder.Services.AddSingleton<McpOperatorDelegationTokenService>();
        builder.Services.AddSingleton<IMcpOperatorDelegationContext, McpOperatorDelegationContext>();
        builder.Services.AddSingleton<McpOperatorDelegationPropagation>();
        builder.Services.AddNetRatelMcpOutboundClient(targetBinding.OutboundOptions);
#pragma warning disable EXTEXP0001
        // Long-polling MCP reads are deliberately bounded to 15 seconds. The
        // shared resilience defaults impose a shorter per-attempt timeout, so
        // keep this isolated API client on its explicit 60-second budget.
        builder.Services.AddHttpClient(NetRatelMcpOutboundClient.ApiHttpClientName)
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddSingleton<INetRatelMcpApiClient, NetRatelMcpHttpApiClient>();

        var authorizationServer = $"{httpOptions.Authority.TrimEnd('/')}/";
        var resourceMetadataUri = new Uri(new Uri(httpOptions.PublicResourceUri, UriKind.Absolute), "/.well-known/oauth-protected-resource/mcp");
        var protectedResourceMetadata = new ProtectedResourceMetadata
        {
            Resource = httpOptions.PublicResourceUri.TrimEnd('/'),
            AuthorizationServers = [authorizationServer],
            ScopesSupported = httpOptions.AdvertisedScopes.ToArray(),
            BearerMethodsSupported = ["header"]
        };
        builder.Services.AddSingleton(new NetRatelMcpHttpRuntimeConfiguration(httpOptions, httpOptions.LocalCredentialMode ? null : protectedResourceMetadata));

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultChallengeScheme = httpOptions.LocalCredentialMode ? McpLocalCredentialAuthenticationHandler.SchemeName : McpScheme;
                options.DefaultAuthenticateScheme = httpOptions.LocalCredentialMode ? McpLocalCredentialAuthenticationHandler.SchemeName : McpScheme;
            })
            .AddJwtBearer(JwtScheme, jwtOptions =>
            {
                jwtOptions.Authority = httpOptions.Authority.TrimEnd('/');
                jwtOptions.Audience = httpOptions.Audience.TrimEnd('/');
                jwtOptions.RequireHttpsMetadata = httpOptions.RequireHttpsMetadata;
                jwtOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = httpOptions.Authority.TrimEnd('/'),
                    ValidateAudience = true,
                    ValidAudience = httpOptions.Audience.TrimEnd('/'),
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles"
                };
            })
            .AddMcp(McpScheme, "NetRatel MCP", options =>
            {
                options.ForwardAuthenticate = JwtScheme;
                options.ResourceMetadataUri = resourceMetadataUri;
                options.ResourceMetadata = protectedResourceMetadata;
            });

        if (httpOptions.LocalCredentialMode)
        {
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddHttpClient(McpLocalCredentialAuthenticationHandler.ApiHttpClientName, client =>
            {
                client.BaseAddress = targetBinding.Target.ApiBaseUri;
                client.Timeout = TimeSpan.FromSeconds(15);
            });
            builder.Services.AddSingleton<McpLocalCredentialPairingService>();
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpLocalCredentialAuthenticationHandler>(
                McpLocalCredentialAuthenticationHandler.SchemeName, _ => { });
        }

        builder.Services.AddSingleton<IAuthorizationHandler, RequiredMcpClaimsHandler>();
        builder.Services.AddAuthorization(options => options.AddPolicy(PolicyName, policy =>
        {
            policy.AddAuthenticationSchemes(httpOptions.LocalCredentialMode ? McpLocalCredentialAuthenticationHandler.SchemeName : McpScheme);
            policy.RequireAuthenticatedUser();
            if (!httpOptions.LocalCredentialMode)
                policy.Requirements.Add(new RequiredMcpClaimsRequirement(
                    httpOptions.RequiredScopes.ToHashSet(StringComparer.Ordinal),
                    httpOptions.RequiredGroups.ToHashSet(StringComparer.Ordinal)));
        }));
        builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter(RateLimitPolicy, limiter =>
        {
            limiter.PermitLimit = 32;
            limiter.QueueLimit = 64;
            limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        }));
        builder.Services.AddSingleton<McpToolInvocationAudit>();
        builder.Services.AddSingleton<McpOperationScopeAuthorization>();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(NetRatelMcpToolDefinitions.CreateForHttp(hostContext))
            .WithResources<NetRatelMcpCatalogResources>()
            .WithPrompts<NetRatelPrompts>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
            {
                var services = context.Services ?? throw new InvalidOperationException("MCP request context does not have a service provider.");
                var operationAuthorization = services.GetRequiredService<McpOperationScopeAuthorization>();
                return services.GetRequiredService<McpToolInvocationAudit>().InvokeAsync(
                    async (nextContext, nextCancellationToken) =>
                    {
                        operationAuthorization.EnsureAuthorized(nextContext.User, nextContext.Params.Name, nextContext.Params.Arguments);
                        IDisposable delegation;
                        try
                        {
                            delegation = await services.GetRequiredService<McpOperatorDelegationPropagation>()
                                .BeginAsync(nextContext.User, nextContext.Params.Name, nextContext.Params.Arguments, nextCancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (McpLocalCredentialExchangeException exception)
                        {
                            throw new McpException(McpErrorMessage(exception.Failure));
                        }
                        using (delegation)
                            return await next(nextContext, nextCancellationToken).ConfigureAwait(false);
                    },
                    context,
                    cancellationToken);
            }))
            .AddAuthorizationFilters();
    }

    private static string McpErrorMessage(McpLocalCredentialExchangeFailure failure) => failure.Code switch
    {
        "local_credential_invalid" => "The local HTTP MCP credential is invalid or has expired.",
        "local_credential_forbidden" => "The local HTTP MCP credential is not authorized for this operation.",
        "local_credential_throttled" => "The local credential service is temporarily throttling requests. Retry after the indicated delay.",
        "local_credential_exchange_timed_out" => "The local credential service did not respond before the deadline. Retry the operation.",
        "local_credential_exchange_unavailable" => "The local credential service is temporarily unavailable. Retry the operation.",
        _ => "The local credential service returned an invalid response. Retry the operation."
    };

    public static void ConfigurePipeline(WebApplication app)
    {
        var runtime = app.Services.GetRequiredService<NetRatelMcpHttpRuntimeConfiguration>();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.Headers.Origin is { Count: > 0 } origin &&
                !runtime.Options.AllowedOrigins.Contains(origin.ToString(), StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });
        app.UseRateLimiter();
        if (runtime.Options.LocalCredentialMode)
            app.Use((context, next) => McpLocalCredentialExchangeFailureResponses.InvokeAsync(context, next));
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
        app.MapHealthChecks("/health/ready");
        if (runtime.ProtectedResourceMetadata is not null)
            app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(runtime.ProtectedResourceMetadata)).AllowAnonymous();
        app.MapMcp("/mcp").RequireAuthorization(PolicyName).RequireRateLimiting(RateLimitPolicy);
    }

    private static void Copy(NetRatelMcpHttpOptions source, NetRatelMcpHttpOptions destination)
    {
        destination.Instance = source.Instance;
        destination.OperatorSurfaceEnabled = source.OperatorSurfaceEnabled;
        destination.ConfigurationPath = source.ConfigurationPath;
        destination.DevApiBaseUrl = source.DevApiBaseUrl;
        destination.ProdApiBaseUrl = source.ProdApiBaseUrl;
        destination.PublicResourceUri = source.PublicResourceUri;
        destination.LocalCredentialMode = source.LocalCredentialMode;
        destination.Authority = source.Authority;
        destination.RequireHttpsMetadata = source.RequireHttpsMetadata;
        destination.Audience = source.Audience;
        destination.RequiredGroups = source.RequiredGroups;
        destination.RequiredScopes = source.RequiredScopes;
        destination.DevelopmentWriteScope = source.DevelopmentWriteScope;
        destination.DevelopmentOnboardingScope = source.DevelopmentOnboardingScope;
        destination.ReadScope = source.ReadScope;
        destination.ObserveScope = source.ObserveScope;
        destination.FilesScope = source.FilesScope;
        destination.ExecuteScope = source.ExecuteScope;
        destination.AdminScope = source.AdminScope;
        destination.AllowedOrigins = source.AllowedOrigins;
        destination.MaxRequestBodyBytes = source.MaxRequestBodyBytes;
    }
}

public sealed record NetRatelMcpHttpRuntimeConfiguration(
    NetRatelMcpHttpOptions Options,
    ProtectedResourceMetadata? ProtectedResourceMetadata);
