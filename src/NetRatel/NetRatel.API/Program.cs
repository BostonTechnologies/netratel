using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.Resource;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Linq;
using System.IO;
using NetRatel.API.Endpoints;
using NetRatel.Application;
using NetRatel.Application.Abstractions;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure;
using NetRatel.API.Realtime;
using NetRatel.API.Services;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Operations;
using NetRatel.API.Models;
using NetRatel.API.Security;
using NetRatel.API.Security.M2M;
using Microsoft.AspNetCore.Authorization;
using NetRatel.API.Application.Requests.Services;
using NetRatel.API.Application;
using NetRatel.Shared.Connectivity;
using NetRatel.Shared.Operations;
using NetRatel.Application.Common;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Services;
using NetRatel.Infrastructure.Events;
using NetRatel.Infrastructure.Startup;
using Microsoft.OpenApi;
using NetRatel.API.Services.Orchestration;
using NetRatel.API.Services.Events;
using NetRatel.API.Services.RemoteSupport;
using NetRatel.API.Services.Requests;
using NetRatel.API.Services.Search;
using NetRatel.API.Services.Terminal;
using NetRatel.API.Ops;
using NetRatel.API.Gateway;
using NetRatel.Akka.Configuration;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using HttpProtocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols;
var builder = WebApplication.CreateBuilder(args);
var akkaMigrationOptions = builder.Configuration.GetSection(NetRatelAkkaMigrationOptions.SectionName).Get<NetRatelAkkaMigrationOptions>() ?? new NetRatelAkkaMigrationOptions();
var aiAgentOpsLogBuffer = new AiAgentOpsLogBuffer();

builder.AddServiceDefaults();
builder.Services.AddNetRatelAkkaMigration(builder.Configuration, builder.Environment);
builder.Services.AddRemoteSupportIceConfiguration(builder.Configuration);
builder.Services.AddSingleton(aiAgentOpsLogBuffer);
builder.Logging.AddProvider(new AiAgentOpsLoggerProvider(aiAgentOpsLogBuffer));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICorrelationContext, HttpCorrelationContext>();
var mcpDelegationOptions = builder.Configuration.GetSection(McpOperatorDelegationOptions.SectionName).Get<McpOperatorDelegationOptions>()
    ?? new McpOperatorDelegationOptions();
mcpDelegationOptions.EnsureValid();
builder.Services.AddSingleton(mcpDelegationOptions);
builder.Services.AddSingleton<McpOperatorDelegationTokenService>();
var mcpLocalAgentOptions = builder.Configuration.GetSection(McpOperatorLocalAgentOptions.SectionName).Get<McpOperatorLocalAgentOptions>()
    ?? new McpOperatorLocalAgentOptions();
mcpLocalAgentOptions.EnsureValid();
builder.Services.AddSingleton(mcpLocalAgentOptions);
builder.Services.AddScoped<McpOperatorClientObservabilityService>();
builder.Services.AddScoped<NetRatel.API.Services.Operations.McpDevelopmentScriptAdapter>();
if (akkaMigrationOptions.IsCommandAuthorityActive)
{
    builder.Services.AddScoped<McpOperatorTaskReconciliationService>();
    builder.Services.AddHostedService<McpOperatorTaskReconciliationHostedService>();
}
builder.Services.AddResponseCompression(options =>
{
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes
        .Where(m => !string.Equals(m, "text/event-stream", StringComparison.OrdinalIgnoreCase));
    options.ExcludedMimeTypes = new[] { "text/event-stream" };
});


// Add services to the container.
builder.Services
  .AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

#region Authentication & Authorization
// Normalize inbound claims (avoid legacy remapping)
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
var machineTokenConfiguration = builder.Configuration.GetSection("Authentication:MachineToken");
if (!machineTokenConfiguration.Exists())
{
    // Kept as a read-only compatibility alias for deployments that have not
    // migrated their configuration yet. New deployments use MachineToken.
    machineTokenConfiguration = builder.Configuration.GetSection("Authentication:OidcAiAgent");
}

var machineTokenOptions = machineTokenConfiguration.Get<MachineTokenAuthenticationOptions>()
    ?? new MachineTokenAuthenticationOptions();
machineTokenOptions.Validate();

// Multiple JWT bearer schemes:
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "Bearer";
        options.DefaultChallengeScheme = "Bearer";
    })
    .AddPolicyScheme("Bearer", "Bearer", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            var configuredAgentIssuer = builder.Configuration["AgentAuth:Issuer"]?.TrimEnd('/');
            var configuredSystemIssuer = builder.Configuration["SystemToken:Issuer"]?.TrimEnd('/');
            if (authHeader?.StartsWith("System ", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "System";
            }

            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring("Bearer ".Length);
                var handler = new JwtSecurityTokenHandler();
                try
                {
                    var jwt = handler.ReadJwtToken(token);
                    var issuer = jwt.Issuer ?? string.Empty;
                    var tokenUse = jwt.Claims.FirstOrDefault(c => c.Type == "token_use")?.Value;
                    var authMode = jwt.Claims.FirstOrDefault(c => c.Type == "auth_mode")?.Value;
                    var isSystemToken =
                        string.Equals(tokenUse, "system", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(authMode, "development", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(configuredSystemIssuer) &&
                         string.Equals(issuer.TrimEnd('/'), configuredSystemIssuer, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(tokenUse, "system", StringComparison.OrdinalIgnoreCase));
                    if (isSystemToken)
                    {
                        return "System";
                    }

                    var isAgentToken =
                        !string.IsNullOrWhiteSpace(configuredAgentIssuer) &&
                        string.Equals(issuer.TrimEnd('/'), configuredAgentIssuer, StringComparison.OrdinalIgnoreCase) &&
                        (jwt.Claims.Any(c => c.Type == "agent_id")
                         || jwt.Claims.Any(c => c.Type == "role" && string.Equals(c.Value, "agent", StringComparison.OrdinalIgnoreCase)));
                    if (isAgentToken)
                    {
                        return "Agent";
                    }

                    if (MachineTokenAuthentication.IsCandidate(jwt, machineTokenOptions))
                    {
                        return "MachineToken";
                    }

                }
                catch (ArgumentException)
                {
                    return "Oidc";
                }
                catch (SecurityTokenException)
                {
                    return "Oidc";
                }
            }

            // All remaining bearer tokens are handled by the deployment's
            // provider-neutral OIDC configuration. Agent, system, and machine
            // credentials are selected above from their validated issuers.
            return "Oidc";
        };
    })
    .AddJwtBearer("MachineToken", options =>
    {
        // Registering the scheme keeps the explicit policy's public contract
        // stable. A disabled scheme intentionally yields no identity, so an
        // endpoint that explicitly selects it cannot bypass the feature flag.
        if (!machineTokenOptions.Enabled)
        {
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    context.NoResult();
                    return Task.CompletedTask;
                }
            };
            return;
        }

        var authority = machineTokenOptions.Authority;
        var audience = machineTokenOptions.Audience;
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.IncludeErrorDetails = builder.Environment.IsDevelopment();
        options.TokenValidationParameters = MachineTokenAuthentication.CreateValidationParameters(machineTokenOptions);

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var requiredGroups = machineTokenOptions.RequiredGroups;
                var actualGroups = context.Principal?.FindAll("groups").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                if (requiredGroups.Any(group => !actualGroups.Contains(group)))
                {
                    context.Fail("Machine token is missing one or more required groups.");
                    return Task.CompletedTask;
                }

                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    var sessionRoles = machineTokenOptions.SessionRoles;
                    foreach (var role in sessionRoles.Where(role => !string.IsNullOrWhiteSpace(role)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, role));
                        identity.AddClaim(new Claim("roles", role));
                    }

                    identity.AddClaim(new Claim("auth_mode", "machine_token"));
                    identity.AddClaim(new Claim("identity_provider", "oidc_machine_token"));
                }

                return Task.CompletedTask;
            }
        };
    })
    .AddJwtBearer("Oidc", options =>
    {
        var oidc = builder.Configuration.GetSection("Authentication:Oidc");
        if (!oidc.Exists())
        {
            // Compatibility aliases for deployments not yet migrated to
            // Authentication:Oidc. New deployments must use the canonical
            // provider-neutral section.
            oidc = builder.Configuration.GetSection("Authentication:Azure");
        }

        var authority = oidc["Authority"];
        var audience = oidc["Audience"] ?? oidc["ClientId"];
        var configuredAudiences = oidc.GetSection("Audiences").Get<string[]>() ?? [];
        var configuredIssuers = oidc.GetSection("ValidIssuers").Get<string[]>() ?? [];
        if (!oidc.Exists())
        {
            var azureAd = builder.Configuration.GetSection("AzureAd");
            var tenantId = azureAd["TenantId"];
            var clientId = azureAd["ClientId"] ?? azureAd["Audience"];
            authority = string.IsNullOrWhiteSpace(tenantId)
                ? authority
                : $"https://login.microsoftonline.com/{tenantId}/v2.0";
            audience = clientId;
            configuredAudiences = string.IsNullOrWhiteSpace(clientId)
                ? []
                : [clientId, azureAd["AppIdUri"] ?? $"api://{clientId}"];
            configuredIssuers = string.IsNullOrWhiteSpace(tenantId)
                ? []
                : [$"https://login.microsoftonline.com/{tenantId}/v2.0", $"https://sts.windows.net/{tenantId}/"];
        }

        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.Authority = authority;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = configuredIssuers.Length == 0 ? null : configuredIssuers,
            ValidateAudience = true,
            ValidAudiences = configuredAudiences.Length == 0 ? null : configuredAudiences,
            ValidAudience = configuredAudiences.Length == 0 ? audience : null,
            RoleClaimType = oidc["RoleClaimType"] ?? "roles",
            NameClaimType = oidc["NameClaimType"] ?? "preferred_username",
            ClockSkew = TimeSpan.FromMinutes(10)
        };
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = builder.Environment.IsDevelopment();
    })
    .AddJwtBearer("System", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["SystemToken:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["SystemToken:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["SystemTokenSecret"] ?? string.Empty)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "preferred_username",
            RoleClaimType = "groups"
        };
        options.IncludeErrorDetails = builder.Environment.IsDevelopment();
        options.RequireHttpsMetadata = false;
    })
    .AddJwtBearer("Agent", options =>
    {
        var agentAuth = builder.Configuration.GetSection("AgentAuth");
        var agentIssuer = agentAuth["Issuer"];
        var agentAudiences = (agentAuth.Get<AgentAuthOptions>() ?? new AgentAuthOptions()).GetAcceptedAudiences();

        options.RequireHttpsMetadata = false;
        options.IncludeErrorDetails = builder.Environment.IsDevelopment();
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = agentIssuer,
            ValidateAudience = true,
            ValidAudiences = agentAudiences,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, _, _) => ResolveAgentSigningValidationKeys(builder.Configuration),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
{
    string? ResolveAdminId() => builder.Configuration["Authorization:Oidc:AdminGroupId"]
                                 ?? builder.Configuration["Authorization:Azure:AdminGroupId"]
                                 ?? builder.Configuration["AzureAd:AdminGroupId"]
                                 ?? builder.Configuration["Jwt:AdminGroupId"];

    var m2m = builder.Configuration.GetSection("M2M").Get<M2MOptions>() ?? new M2MOptions
    {
        Authority = string.Empty,
        Audience = string.Empty,
        AllowedCallerClientIds = Array.Empty<string>(),
        AccessTokenLifetimeMinutes = 10
    };

    options.AddPolicy("Operator", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => HasAdminClaim(ctx.User, ResolveAdminId()));
    });

    options.AddPolicy("McpOperatorPolicyAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            HasAdminClaim(ctx.User, ResolveAdminId()) &&
            HasScope(ctx.User, "netratel.mcp.admin"));
    });

    options.AddPolicy("AkkaShadowAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => HasAdminClaim(ctx.User, ResolveAdminId()));
    });

    options.AddPolicy("ClientArtifactsWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => HasAdminClaim(ctx.User, ResolveAdminId()));
    });

    options.AddPolicy("ClientArtifactsUpload", policy =>
    {
        policy.AddAuthenticationSchemes("Bearer", "M2M");
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            HasAdminClaim(ctx.User, ResolveAdminId()) ||
            HasAllowedM2MClient(ctx.User, m2m.AllowedCallerClientIds, m2m.Audience));
    });

    options.AddPolicy("HealthRead", policy =>
    {
        policy.AddAuthenticationSchemes("Bearer", "M2M");
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            HasAdminClaim(ctx.User, ResolveAdminId()) ||
            HasAllowedM2MClient(ctx.User, m2m.AllowedCallerClientIds, m2m.Audience));
    });

    options.AddPolicy("ClientArtifactsDownload", policy =>
    {
        policy.AddAuthenticationSchemes("Bearer", "M2M", "Agent");
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            HasAdminClaim(ctx.User, ResolveAdminId()) ||
            HasAllowedM2MClient(ctx.User, m2m.AllowedCallerClientIds, m2m.Audience) ||
            (IsAgentPrincipal(ctx.User) && HasScope(ctx.User, "netratel:connect")));
    });

    // Require Operator by default (unless [AllowAnonymous])
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => HasAdminClaim(ctx.User, ResolveAdminId()))
        .Build();

    options.AddPolicy("M2MOnly", policy =>
    {
        policy.AddAuthenticationSchemes("M2M");
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new AllowedClientRequirement(m2m.AllowedCallerClientIds));
    });

    options.AddPolicy("AgentAccess", policy =>
    {
        policy.AddAuthenticationSchemes("Agent");
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => IsAgentPrincipal(ctx.User));
    });

    options.AddPolicy("AgentGatewayAccess", policy =>
    {
        policy.AddAuthenticationSchemes("Agent");
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            AgentGatewayIdentityResolver.TryResolve(ctx.User, out _, out _));
    });

    options.AddPolicy("MachineTokenApi", policy =>
    {
        policy.AddAuthenticationSchemes("MachineToken");
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(_ => machineTokenOptions.Enabled);
        policy.RequireClaim("auth_mode", "machine_token");
    });
});

#endregion

#region DataProtection keys
// Appsettings-driven key directory with local fallback.
var configuredKeysDir = builder.Configuration["DataProtection:KeysDirectory"];
string keyRingPath = string.IsNullOrWhiteSpace(configuredKeysDir)
    ? Path.Combine(AppContext.BaseDirectory, ".keys")
    : (Path.IsPathRooted(configuredKeysDir) ? configuredKeysDir : Path.Combine(AppContext.BaseDirectory, configuredKeysDir));

Directory.CreateDirectory(keyRingPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName(
        builder.Configuration.GetSection("DataProtection")["ApplicationName"]
        ?? "NetRatel-Keyring");
#endregion

builder.Services.AddSingleton<IAuthorizationHandler, AllowedClientHandler>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT Authorization header using the Bearer scheme"
        };

        document.Security ??= new List<OpenApiSecurityRequirement>();
        if (!document.Security.Any(requirement =>
                requirement.Keys.Any(scheme => string.Equals(scheme.Reference?.Id, "Bearer", StringComparison.OrdinalIgnoreCase))))
        {
            document.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
        }

        return Task.CompletedTask;
    });

});

builder.Services.AddSingleton<JobRunExecutionRegistry>();
builder.Services.AddHostedService<AgentUpdateScriptSeedService>();
builder.Services.AddHostedService<McpOperatorFileArtifactRetentionService>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<DevelopmentMcpFileArtifactRetentionService>();
}
if (akkaMigrationOptions.IsTerminalAuthorityActive)
{
    // Browser circuits can vanish without running component disposal. Keep a
    // bounded server-side attachment lease so an invisible V2 terminal is
    // closed (and retried across a transport reconnect) instead of surviving
    // indefinitely on the client.
    builder.Services.AddSingleton<GatewayTerminalBrowserAttachmentLeaseRegistry>();
    builder.Services.AddHostedService<GatewayTerminalBrowserAttachmentExpiryService>();
    builder.Services.AddHostedService<ProductionMcpTerminalExpiryService>();
}
builder.Services.AddSingleton<ClientUpdateBroadcaster>();
builder.Services.AddSingleton<ClientLogBroadcaster>();
builder.Services.AddSingleton<IAgentTelemetryCompatibilityRegistry, GatewayTelemetryCompatibilityRegistry>();
builder.Services.AddScoped<ITenantLookupService, PostgresTenantLookupService>();
builder.Services.AddScoped<IClientScriptService, ClientScriptService>();
builder.Services.Configure<ClientArtifactsOptions>(builder.Configuration.GetSection("ClientArtifacts"));
builder.Services.Configure<TerminalTransportOptions>(builder.Configuration.GetSection("Terminal"));
builder.Services.Configure<AgentAuthOptions>(builder.Configuration.GetSection("AgentAuth"));
builder.Services.Configure<SecurityHardeningOptions>(builder.Configuration.GetSection("Security"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("StorageOptions"));
builder.Services.AddSingleton<StorageInitializer>();
builder.Services.AddScoped<IClientArtifactsService, ClientArtifactsService>();
builder.Services.AddScoped<ClientUpdateAuthorityService>();
builder.Services.AddScoped<IClientUpdatePublisher>(services => services.GetRequiredService<ClientUpdateAuthorityService>());
builder.Services.AddScoped<IClientUpdateOperatorAuthority>(services => services.GetRequiredService<ClientUpdateAuthorityService>());
builder.Services.AddScoped<IMcpOperatorEventAuthority, McpOperatorEventAuthority>();
builder.Services.AddScoped<IMcpOperatorConnectivityAuthority, McpOperatorConnectivityAuthority>();
builder.Services.AddSingleton<ClientUpdateCatalog>();
builder.Services.AddSingleton<IClientUpdateCatalog>(services => services.GetRequiredService<ClientUpdateCatalog>());
builder.Services.AddHostedService<ClientUpdateCatalogRefreshService>();
builder.Services.AddHealthChecks().AddCheck<ClientUpdateCatalogHealthCheck>(
    "client-update-catalog", tags: ["ready", "client-updates", "akka-authority"]);
builder.Services.AddScoped<JobTaskBridge>();
builder.Services.AddSingleton<JobAuthorityIdGenerator>();
builder.Services.Configure<NetRatelExternalServiceCallbackOptions>(builder.Configuration.GetSection("Orchestration:ExternalService"));
builder.Services.AddScoped<INetRatelSystemTokenService, NetRatelSystemTokenService>();
builder.Services.AddScoped<INetRatelExternalServiceCallbackReplayService, NetRatelExternalServiceCallbackReplayService>();
builder.Services.AddHttpClient<INetRatelExternalServiceCallbackClient, NetRatelExternalServiceCallbackClient>((sp, http) =>
{
    var callbackOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NetRatelExternalServiceCallbackOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(callbackOptions.BaseUrl))
    {
        http.BaseAddress = new Uri(callbackOptions.BaseUrl);
    }
});
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient("ConnectivityProbe", http =>
{
    http.Timeout = TimeSpan.FromSeconds(6);
})
.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

// M2M (machine-to-machine) configuration and services
builder.Services.Configure<M2MOptions>(builder.Configuration.GetSection("M2M"));
builder.Services.AddSingleton<IClientCredentialsTokenService, ClientCredentialsTokenService>();
builder.Services.AddHttpClient("oidc"); // discovery & token requests

// Dedicated M2M JWT bearer scheme (audience-scoped)
builder.Services.AddAuthentication()
    .AddJwtBearer("M2M", _ => { });
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, M2MJwtBearerOptionsConfigurator>();

var legacyQueueWorkerEnabled = builder.Configuration.GetValue<bool>("LegacyQueueWorker:Enabled");

// Downstream: ExternalService API (legacy queue worker path only)
var downstreamExternalService = builder.Configuration.GetSection("Downstream:ExternalServiceApi").Get<NetRatel.API.Security.M2M.DownstreamApiOptions>();
if (legacyQueueWorkerEnabled
    && downstreamExternalService is not null
    && !string.IsNullOrWhiteSpace(downstreamExternalService.BaseUrl)
    && !IsPlaceholder(downstreamExternalService.Authority)
    && !IsPlaceholder(downstreamExternalService.TokenEndpoint)
    && !IsPlaceholder(downstreamExternalService.ClientId)
    && !IsPlaceholder(downstreamExternalService.ClientSecret))
{
    builder.Services.AddSingleton(downstreamExternalService);
    builder.Services.AddHttpClient("ExternalServiceApi", (sp, http) =>
    {
        http.BaseAddress = new Uri(downstreamExternalService.BaseUrl);
    })
    .AddHttpMessageHandler(sp => new M2MTokenHandler(
        sp.GetRequiredService<IClientCredentialsTokenService>(),
        sp.GetRequiredService<NetRatel.API.Security.M2M.DownstreamApiOptions>()))
    .AddStandardResilienceHandler();
}

// Request execution queue + worker
builder.Services.AddSingleton<IExecutionQueue, ExecutionQueue>();
if (legacyQueueWorkerEnabled)
{
    builder.Services.AddHostedService<ExecutionWorker>();
}
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddHostedService<GlobalSearchQueryWarmupService>();
builder.Services.AddNetRatelApplication();
builder.Services.AddNetRatelInfrastructure(builder.Configuration);
builder.Services.AddScoped<IM2MConnectivityService, RuntimeM2MConnectivityService>();

// Increase upload limits (for large client artifacts)
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1_000_000_000; // ~1 GB; set as needed
    o.MemoryBufferThreshold = 32 * 1024 * 1024;
});

// If hosting behind Kestrel and you need more:
builder.WebHost.ConfigureKestrel(k =>
{
    var httpPort = builder.Configuration.GetValue<int>("NetRatel_HTTP_PORT", 9222);
    if (httpPort is < 1 or > 65535)
    {
        throw new InvalidOperationException("NetRatel_HTTP_PORT must be between 1 and 65535.");
    }

    var akkaMigration = builder.Configuration
        .GetSection(NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions.SectionName)
        .Get<NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions>()
        ?? new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions();

    // h2c and HTTP/1.1 use separate listeners so REST and legacy clients remain unchanged.
    k.ListenAnyIP(httpPort, listen => listen.Protocols = HttpProtocols.Http1);
    if (akkaMigration.Enabled && akkaMigration.GatewayEnabled)
    {
        k.ListenAnyIP(
            akkaMigration.GatewayGrpcPort,
            listen => listen.Protocols = HttpProtocols.Http2);
    }

    var hardening = builder.Configuration.GetSection("Security").Get<SecurityHardeningOptions>() ?? new SecurityHardeningOptions();
    k.Limits.MaxRequestBodySize = 1_000_000_000; // same limit
    if (hardening.EnableMTls)
    {
        k.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = hardening.RequireMTls
                ? ClientCertificateMode.RequireCertificate
                : ClientCertificateMode.AllowCertificate;
        });
    }
});

var app = builder.Build();

var storageInit = app.Services.GetRequiredService<StorageInitializer>();
storageInit.EnsureCreated();

app.UseForwardedHeaders();
app.UseResponseCompression();

app.UseMiddleware<NetRatel.API.Middleware.CorrelationIdMiddleware>();
app.UseMiddleware<NetRatel.API.Middleware.CorrelationLoggingMiddleware>();

app.MapApiEndpoints();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;

        var (status, title) = ex switch
        {
            RequestValidationException => (StatusCodes.Status400BadRequest, "Validation error"),
            FileNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };

        var corr = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? context.TraceIdentifier;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers["X-Correlation-Id"] = corr;

        var problem = new
        {
            type = $"https://httpstatuses.com/{status}",
            title,
            status,
            detail = ex?.Message,
            traceId = context.TraceIdentifier,
            extensions = new { correlationId = corr }
        };

        await context.Response.WriteAsJsonAsync(problem);
    });
});
app.UseMiddleware<NetRatel.API.Middleware.ExceptionNotificationMiddleware>();
app.UseHttpsRedirection();
app.UseCors();
app.UseWebSockets();
app.UseAuthentication();
app.UseMiddleware<NetRatel.API.Middleware.McpOperatorDelegationMiddleware>();
app.UseAuthorization();
var applyMigrationsOnStartup = app.Environment.IsDevelopment() ||
    app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup") ||
    string.Equals(app.Configuration["OTEL_SERVICE_NAME"], "netratel-api-dev", StringComparison.OrdinalIgnoreCase);
if (applyMigrationsOnStartup)
{
    try
    {
        await app.Services.MigrateNetRatelInfrastructureAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "NetRatel infrastructure migration failed during startup.");
    }
}

try
{
    using var keyScope = app.Services.CreateScope();
    var signingService = keyScope.ServiceProvider.GetRequiredService<OidcSigningService>();
    await signingService.GetActiveSigningKeyAsync(app.Lifetime.ApplicationStopping);
    app.Logger.LogInformation("Agent-auth signing key loaded.");
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Agent-auth signing key probe failed; application will continue serving while token issuance reports the configuration failure.");
}

app.Run();

static bool IsPlaceholder(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    return value.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase)
        || value.Contains("auth.example.com", StringComparison.OrdinalIgnoreCase)
        || value.Contains("external-service.local", StringComparison.OrdinalIgnoreCase);
}

static bool HasAdminClaim(ClaimsPrincipal user, string? adminGroupId)
{
    var hasRole = user.Claims.Any(c =>
        (c.Type == "roles" || c.Type == ClaimTypes.Role) &&
        string.Equals(c.Value, "Operator", StringComparison.OrdinalIgnoreCase));

    var hasGroupByName = user.Claims.Any(c =>
        c.Type == "groups" && string.Equals(c.Value, "Operator", StringComparison.OrdinalIgnoreCase));

    var hasGroupById = !string.IsNullOrWhiteSpace(adminGroupId) && user.Claims.Any(c =>
        c.Type == "groups" && string.Equals(c.Value, adminGroupId, StringComparison.OrdinalIgnoreCase));

    return hasRole || hasGroupByName || hasGroupById;
}

static bool HasAllowedM2MClient(ClaimsPrincipal user, IEnumerable<string> allowedClientIds, string? requiredScope)
{
    var candidates = new[]
    {
        user.FindFirst("client_id")?.Value,
        user.FindFirst("azp")?.Value,
        user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value
    }.Where(v => !string.IsNullOrWhiteSpace(v));

    var allowed = allowedClientIds.ToHashSet(StringComparer.Ordinal);
    if (!candidates.Any(v => allowed.Contains(v!)))
    {
        return false;
    }

    if (string.IsNullOrWhiteSpace(requiredScope))
    {
        return true;
    }

    return user.Claims
        .Where(c => string.Equals(c.Type, "scope", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, "scp", StringComparison.OrdinalIgnoreCase))
        .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Any(scope => string.Equals(scope, requiredScope, StringComparison.Ordinal));
}

static bool IsAgentPrincipal(ClaimsPrincipal user)
{
    var hasAgentRole = user.Claims.Any(c =>
        (c.Type == "role" || c.Type == "roles" || c.Type == ClaimTypes.Role) &&
        string.Equals(c.Value, "agent", StringComparison.OrdinalIgnoreCase));

    var hasAgentId = user.Claims.Any(c =>
        string.Equals(c.Type, "agent_id", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(c.Value));

    return hasAgentRole || hasAgentId;
}

static bool HasScope(ClaimsPrincipal user, string requiredScope)
    => user.Claims
        .Where(c => string.Equals(c.Type, "scope", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, "scp", StringComparison.OrdinalIgnoreCase))
        .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Any(scope => string.Equals(scope, requiredScope, StringComparison.Ordinal));

static IEnumerable<SecurityKey> ResolveAgentSigningValidationKeys(IConfiguration configuration)
{
    var options = configuration.GetSection("AgentAuth").Get<AgentAuthOptions>() ?? new AgentAuthOptions();
    var configuredPath = options.PrivateKeyPath;
    var signingKeyIds = options.GetAcceptedSigningKeyIds();

    var candidatePaths = new List<string>();
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        candidatePaths.Add(configuredPath);
    }
    candidatePaths.Add("/app/storage/keys/netratel-agent-es256-private.pem");
    candidatePaths.Add("/app/storage/keys/spacetime-es256-private.pem");

    var keyPath = candidatePaths.FirstOrDefault(File.Exists);
    if (string.IsNullOrWhiteSpace(keyPath))
    {
        return Array.Empty<SecurityKey>();
    }

    try
    {
        var pem = File.ReadAllText(keyPath);
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return signingKeyIds
            .Select(keyId => new ECDsaSecurityKey(ecdsa) { KeyId = keyId })
            .Cast<SecurityKey>()
            .ToArray();
    }
    catch
    {
        return Array.Empty<SecurityKey>();
    }
}
