using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
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
using System.Threading.RateLimiting;
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
using NetRatel.Infrastructure.Identity;
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
using NetRatel.API.Bootstrap;
using NetRatel.API.Security.Local;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Integration;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Persistence;
using NetRatel.API.OpenApi;
var builder = WebApplication.CreateBuilder(args);
NetRatelDatabaseConfigurationResolver.ValidateProvider(builder.Configuration);

// Bootstrap reconciliation intentionally happens before any operational registration. A fresh or
// recovering installation must expose only the setup/liveness surface; it must not initialize a
// database, OIDC handler, agent gateway, scheduler, outbox, or Akka authority in the background.
var bootstrapOptions = BootstrapOptions.FromConfiguration(builder.Configuration);
if (BootstrapOperatorCommand.IsSupported(args))
{
    Environment.ExitCode = await BootstrapOperatorCommand.RunAsync(
        args[0], bootstrapOptions, Console.Out, Console.Error);
    return;
}

var bootstrapLifecycle = new BootstrapLifecycleService(new BootstrapStateStore(bootstrapOptions), builder.Configuration);
var bootstrapDescriptor = await bootstrapLifecycle.InitializeAsync();
if (args is [UnattendedBootstrapCommand.CommandName])
{
    if (bootstrapDescriptor.State != BootstrapState.Unconfigured)
    {
        await Console.Error.WriteLineAsync("Unattended initialization is available only for an unconfigured NetRatel instance.");
        return;
    }

    var command = new UnattendedBootstrapCommand(
        bootstrapOptions,
        bootstrapLifecycle,
        new BootstrapInitializationService(
            new BootstrapStateStore(bootstrapOptions),
            builder.Configuration,
            new PasswordHasher<LocalUser>(),
            Options.Create(BootstrapApplicationExtensions.CreateBootstrapIdentityOptions())));
    var result = await command.InitializeAsync();
    if (!result.Succeeded)
    {
        await Console.Error.WriteLineAsync(result.Error ?? "Unattended initialization could not be completed.");
        return;
    }

    await Console.Out.WriteLineAsync("NetRatel initialization completed. Start the API normally to serve the application.");
    return;
}
if (args is [DeploymentLocalAdministratorRecoveryCommand.CommandName])
{
    if (bootstrapDescriptor.State != BootstrapState.Ready)
    {
        await Console.Error.WriteLineAsync("Deployment-local administrator recovery is available only for a ready NetRatel instance.");
        return;
    }

    var command = new DeploymentLocalAdministratorRecoveryCommand(
        bootstrapOptions,
        new BootstrapInitializationService(
            new BootstrapStateStore(bootstrapOptions),
            builder.Configuration,
            new PasswordHasher<LocalUser>(),
            Options.Create(BootstrapApplicationExtensions.CreateBootstrapIdentityOptions())));
    var result = await command.RecoverAsync();
    if (!result.Succeeded)
    {
        await Console.Error.WriteLineAsync(result.Error ?? "Deployment-local administrator recovery could not be completed.");
        return;
    }

    await Console.Out.WriteLineAsync("Local administrator recovery completed. Existing local sessions were invalidated.");
    return;
}
if (bootstrapDescriptor.State != BootstrapState.Ready)
{
    builder.Services.AddBootstrapRuntime(bootstrapOptions, builder.Configuration);
    builder.ConfigureBootstrapListener();
    var bootstrapApp = builder.Build();
    bootstrapApp.UseBootstrapRuntime();
    bootstrapApp.Run();
    return;
}

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
builder.Services.AddRateLimiter(rateLimits =>
{
    rateLimits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimits.AddPolicy("local-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    rateLimits.AddPolicy("local-security", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

#region Authentication & Authorization
// Normalize inbound claims (avoid legacy remapping)
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
var localAuthenticationOptions = LocalAuthenticationOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(localAuthenticationOptions);
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
if (machineTokenOptions.Enabled)
{
    var human = builder.Configuration.GetSection("Authentication:Oidc");
    if (!human.Exists())
        human = builder.Configuration.GetSection("Authentication:Azure");
    var humanAudiences = (human.GetSection("Audiences").Get<string[]>() ?? [])
        .Concat([human["Audience"], human["ClientId"], builder.Configuration["AzureAd:ClientId"],
            builder.Configuration["AzureAd:Audience"], builder.Configuration["AzureAd:AppIdUri"]]);
    if (humanAudiences.Contains(machineTokenOptions.Audience, StringComparer.Ordinal))
        throw new InvalidOperationException("Machine-token authentication requires a dedicated audience distinct from human OIDC audiences.");
}

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
            if (string.IsNullOrWhiteSpace(authHeader) && localAuthenticationOptions.SupportsLocalAccounts &&
                context.Request.Cookies.ContainsKey(localAuthenticationOptions.CookieName))
            {
                return LocalAuthenticationOptions.Scheme;
            }
            var configuredAgentIssuer = builder.Configuration["AgentAuth:Issuer"]?.TrimEnd('/');
            var configuredSystemIssuer = builder.Configuration["SystemToken:Issuer"]?.TrimEnd('/');
            if (authHeader?.StartsWith("System ", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "System";
            }

            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring("Bearer ".Length);
                if (token.StartsWith(IntegrationCredentialService.ApiTokenPrefix, StringComparison.Ordinal))
                {
                    return IntegrationCredentialAuthenticationHandler.SchemeName;
                }
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
    .AddScheme<AuthenticationSchemeOptions, IntegrationCredentialAuthenticationHandler>(
        IntegrationCredentialAuthenticationHandler.SchemeName, _ => { })
    .AddJwtBearer("MachineToken", options =>
        MachineTokenAuthentication.Configure(options, machineTokenOptions, builder.Environment.IsDevelopment()))
    .AddJwtBearer("Oidc", options =>
    {
        var oidc = OidcApiConfiguration.Resolve(builder.Configuration);

        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.Authority = oidc.Authority;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = oidc.ValidIssuers.Length == 0 ? null : oidc.ValidIssuers,
            ValidateAudience = true,
            ValidAudiences = oidc.Audiences.Length == 0 ? null : oidc.Audiences,
            ValidAudience = oidc.Audiences.Length == 0 ? oidc.Audience : null,
            // LocalPrincipalClaimsTransformation accepts only the validated
            // OIDC identity. Keep this explicit rather than depending on the
            // IdentityModel default authentication type.
            AuthenticationType = "Oidc",
            RoleClaimType = oidc.RoleClaimType,
            NameClaimType = oidc.NameClaimType,
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
    })
    .AddCookie(LocalAuthenticationOptions.Scheme, options =>
    {
        options.Cookie.Name = localAuthenticationOptions.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        // The only HTTP profile is an operator-opted-in, loopback-bound Compose evaluation.
        // Public deployments leave this false and always receive a Secure cookie.
        options.Cookie.SecurePolicy = localAuthenticationOptions.AllowInsecureLocalhost
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!localAuthenticationOptions.SupportsLocalAccounts || string.IsNullOrWhiteSpace(userId))
            {
                context.RejectPrincipal();
                return;
            }

            var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<LocalUser>>();
            var user = await users.FindByIdAsync(userId).ConfigureAwait(false);
            if (user is null || !LocalSessionValidator.IsValid(context.Principal!, user))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(LocalAuthenticationOptions.Scheme).ConfigureAwait(false);
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
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

    // A bare RequireAuthorization() only proves authentication. Integration
    // credentials are deliberately attenuated and must therefore use a route
    // with an explicit EffectiveAccessRequirement; otherwise a newly added
    // authenticated endpoint could accidentally bypass their stored grant
    // tuples. Existing OIDC, local, M2M, machine, and native-agent paths keep
    // the established default policy unchanged.
    options.DefaultPolicy = new AuthorizationPolicyBuilder("Bearer")
        .RequireAuthenticatedUser()
        .RequireAssertion(context => IntegrationCredentialAuthorizationBoundary.AllowsDefaultAuthenticatedRoute(context.User))
        .Build();

    options.AddPolicy("McpLocalDelegationExchange", policy =>
    {
        policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context => string.Equals(
            context.User.FindFirst("integration_credential_purpose")?.Value,
            "http_mcp",
            StringComparison.Ordinal));
    });

    options.AddPolicy("Operator", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => HasAdminClaim(ctx.User, ResolveAdminId()));
    });

    options.AddPolicy("InstanceAdministrator", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.UserRoleAdministration, instanceScope: true));
    });

    options.AddPolicy("AccessAdministration", policy =>
    {
        policy.RequireAuthenticatedUser();
    });

    options.AddPolicy("TenantAdministrator", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.TenantAdministration));
    });

    options.AddPolicy("ClientManager", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.ClientManagement));
    });

    options.AddPolicy("TelemetryReader", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.TelemetryRead));
    });

    options.AddPolicy("TerminalOperator", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.TerminalAccess));
    });

    options.AddPolicy("RemoteSupportOperator", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.RemoteSupport));
    });

    options.AddPolicy("ScriptEditor", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.ScriptEdit));
    });

    options.AddPolicy("SecretRevealer", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.SecretReveal));
    });

    options.AddPolicy("AuditReader", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.AuditRead));
    });

    options.AddPolicy("CommandOperator", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.ScriptExecute));
    });

    options.AddPolicy("FileReader", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.FileRead));
    });

    options.AddPolicy("FileWriter", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.FileWrite));
    });

    options.AddPolicy("ArtifactPublisher", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.ArtifactPublication));
    });

    options.AddPolicy("McpOperatorPolicyAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.McpPolicyAdministration, legacyRequiredScope: "netratel.mcp.admin"));
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

    options.AddPolicy(LocalAuthenticationOptions.LocalUserPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(LocalAuthenticationOptions.Scheme);
        policy.RequireAuthenticatedUser();
    });

    options.AddPolicy("InteractiveAccount", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            context.User.Identities.Any(identity => identity.IsAuthenticated &&
                (string.Equals(identity.AuthenticationType, "Oidc", StringComparison.Ordinal) ||
                 string.Equals(identity.AuthenticationType, LocalAuthenticationOptions.Scheme, StringComparison.Ordinal))));
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
builder.Services.AddScoped<IAuthorizationHandler, EffectiveAccessHandler>();
builder.Services.AddScoped<InstanceAdministratorInvariant>();

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
            Description = "OIDC bearer token. Local browser sessions and opaque API credentials are documented as separate alternatives where supported."
        };

        document.Components.SecuritySchemes["M2M"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "Bearer", BearerFormat = "JWT", Description = "Machine-to-machine access token." };
        document.Components.SecuritySchemes["Agent"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "Bearer", BearerFormat = "JWT", Description = "Native NetRatel Client token." };
        document.Components.SecuritySchemes["MachineToken"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "Bearer", BearerFormat = "JWT", Description = "Machine-token API credential." };
        document.Components.SecuritySchemes["IntegrationCredential"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "Bearer", Description = "Opaque API credential constrained by its durable grants. Purpose-bound local HTTP-MCP ingress credentials require pairing and are excluded from interactive API documentation." };
        document.Components.SecuritySchemes["LocalSession"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = localAuthenticationOptions.CookieName, Description = "Local-account browser session cookie." };

        return Task.CompletedTask;
    });

    options.AddOperationTransformer(NetRatelOpenApiCatalog.TransformOperationAsync);

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
builder.Services.AddOptions<DeploymentBrandingOptions>()
    .BindConfiguration(DeploymentBrandingOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DeploymentBrandingOptions>, DeploymentBrandingOptionsValidator>();
builder.Services.AddSingleton<StorageInitializer>();
builder.Services.AddScoped<IClientArtifactsService, ClientArtifactsService>();
builder.Services.AddHttpClient("GitHubClientReleases", http =>
    {
        http.BaseAddress = new Uri("https://api.github.com/");
        http.Timeout = TimeSpan.FromSeconds(15);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IGitHubClientReleaseCatalog>(services =>
    new GitHubClientReleaseCatalog(
        services.GetRequiredService<IHttpClientFactory>().CreateClient("GitHubClientReleases"),
        services.GetRequiredService<IConfiguration>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILogger<GitHubClientReleaseCatalog>>()));
builder.Services.AddHttpClient("GitHubClientAssets", http => http.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped(services => new GitHubClientAssetDownloader(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("GitHubClientAssets"),
    services.GetRequiredService<IConfiguration>()));
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
builder.Services.AddIdentityCore<LocalUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Password.RequiredLength = 15;
        options.Password.RequiredUniqueChars = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<NetRatelIdentityDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, LocalPrincipalClaimsTransformation>();
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
app.MapReadyBootstrapStatus(bootstrapDescriptor);

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
app.UseRateLimiter();
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
