using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using MudBlazor.Services;
using Microsoft.AspNetCore.StaticFiles;
using MudBlazor.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Http.Resilience;
using NetRatel.Web.Components;

using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Script;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Services.Tenants;
using NetRatel.Web.Services.Requests;
using NetRatel.Web.Services.ClientTasks;
using NetRatel.Web.Services.Terminal;
using NetRatel.Web.Services.Jobs;
using NetRatel.Web.Services.FileSystem;
using NetRatel.Web.Services.RemoteSupport;

using System.Security.Claims;
using System.Threading;
using BlazorDownloadFile;
using Microsoft.Extensions.Options;
using NetRatel.Web.Services.ScriptLibrary;
using NetRatel.Web.Services.Agents;
using NetRatel.Web.Services.Notifications;
using NetRatel.Web.Services.Enrollment;
using NetRatel.Web.Services.Telemetry;
using NetRatel.Web.Configuration;
using NetRatel.Web.Services.Search;
using NetRatel.Web.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// Add services to the container. Version Bump
var maxEditorPayloadBytes = builder.Configuration.GetValue(
    "ScriptLibrary:MaxEditorPayloadBytes",
    10 * 1024 * 1024);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = maxEditorPayloadBytes;
    });

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorPages();
builder.Services.AddControllers();

// Register MudBlazor services and extensions
// Add MudBlazor services
//builder.Services.AddMudServices();
// Prefer scoped registration; DO NOT use AddMudServicesWithExtensions()
//var scan = new[] { typeof(Program).Assembly };
//builder.Services.AddMudExtensions(scan);
builder.Services.AddMudServicesWithExtensions();
MudExtensions.Services.ExtensionServiceCollectionExtensions.AddMudExtensions(builder.Services, _ => { });

builder.Services.AddSingleton(new ClientApiOptions());
builder.Services.AddScoped<ScriptService>();
builder.Services.AddScoped<ScriptApiService>();
builder.Services.AddScoped<ClientApiService>();
builder.Services.AddScoped<ClientPresenceApiService>();
builder.Services.AddScoped<ClientPresentationService>();
builder.Services.AddScoped<GatewayClientActionApiService>();
builder.Services.AddScoped<IGatewayLogApiService, GatewayLogApiService>();
builder.Services.AddScoped<IGatewayLogLiveStreamService, GatewayLogLiveStreamService>();
builder.Services.AddScoped<ClientStreamService>();
builder.Services.AddScoped<TelemetryApiService>();
builder.Services.AddScoped<GatewayTelemetryApiService>();
builder.Services.AddScoped<GatewayTelemetryLiveStreamService>();
builder.Services.AddTransient<TelemetryOverviewStreamService>();
builder.Services.AddTransient<ClientTelemetryStreamService>();
builder.Services.AddScoped<TenantApiService>();
builder.Services.AddScoped<RequestApiService>();
builder.Services.AddScoped<RequestStreamService>();
builder.Services.AddScoped<IRequestStreamService>(sp => sp.GetRequiredService<RequestStreamService>());
builder.Services.AddScoped<TaskApiService>();
builder.Services.AddScoped<FileSystemApiService>();
builder.Services.AddScoped<GatewayFileSystemApiService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IFileBrowserDownloadTransferRegistry, FileBrowserDownloadTransferRegistry>();
builder.Services.AddHostedService<FileBrowserDownloadTransferCleanupService>();
builder.Services.AddScoped<FileBrowserContentClassifier>();
builder.Services.AddOptions<FileBrowserOptions>()
    .Bind(builder.Configuration.GetSection(FileBrowserOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<ClientArtifactsService>();
builder.Services.AddScoped<IClientArtifactsService>(sp => sp.GetRequiredService<ClientArtifactsService>());
builder.Services.AddScoped<ClientSettingsApiService>();
builder.Services.AddScoped<IAgentApiService, AgentApiService>();
builder.Services.AddScoped<IEnrollmentCodeApiService, EnrollmentCodeApiService>();
builder.Services.AddScoped<ITenantApiService>(sp => sp.GetRequiredService<TenantApiService>());
builder.Services.AddScoped<IClientApiService>(sp => sp.GetRequiredService<ClientApiService>());
builder.Services.AddScoped<IScriptLibraryService>(sp => sp.GetRequiredService<ScriptService>());
builder.Services.AddScoped<IJobApiClient, JobApiClient>();
builder.Services.AddScoped<INetRatelNotificationApiClient, NetRatelNotificationApiClient>();
builder.Services.AddScoped<IGlobalSearchService, GlobalSearchService>();
builder.Services.AddScoped<IAppBarVersionApiClient, AppBarVersionApiClient>();
builder.Services.AddScoped<NetRatelNotificationEventBus>();
builder.Services.AddScoped<NetRatelNotificationStreamService>();
builder.Services.AddScoped<SseClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("OrchestratorApi"); // reuse configured client
    http.Timeout = Timeout.InfiniteTimeSpan; // allow long-lived event streams
    return new SseClient(http);
});

builder.Services.AddScoped<ITerminalService, TerminalService>();
builder.Services.AddScoped<AkkaAuthorityFanoutClient>();
builder.Services.AddScoped<GatewayRemoteSupportApiService>();
builder.Services.AddScoped<PrimaryClientGatewayCardReadApiService>();

// Register authentication services and token handlers
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITokenProvider, ServerTokenProvider>();
builder.Services.AddTransient<TokenAuthorizationHandler>();
builder.Services.AddTransient<SystemTokenAuthorizationHandler>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<CookieOidcSessionEvents>();
builder.Services.AddSingleton<ISystemTokenService, SystemTokenService>();
builder.Services.AddBlazorDownloadFile();
var oidcConfiguration = builder.Configuration.GetSection("Authentication:Oidc");
if (!oidcConfiguration.Exists())
{
    // Transitional aliases for existing deployments. New deployments
    // must use the provider-neutral Authentication:Oidc section.
    oidcConfiguration = builder.Configuration.GetSection("Authentication:Azure");
}
if (!oidcConfiguration.Exists())
{
    oidcConfiguration = builder.Configuration.GetSection("AzureAd");
}

builder.Services.AddSingleton<IValidateOptions<OidcOptions>, OidcOptionsValidator>();
builder.Services.AddOptions<OidcOptions>()
    .Bind(oidcConfiguration)
    .ValidateOnStart();
var machineTokenConfiguration = builder.Configuration.GetSection(MachineTokenOptions.SectionName);
if (!machineTokenConfiguration.Exists())
{
    // Compatibility with existing deployments during the configuration migration.
    machineTokenConfiguration = builder.Configuration.GetSection("Authentication:OidcAiAgent");
}

builder.Services.AddSingleton<IValidateOptions<MachineTokenOptions>, MachineTokenOptionsValidator>();
builder.Services.AddOptions<MachineTokenOptions>()
    .Bind(machineTokenConfiguration)
    .Validate(options => !options.Enabled ||
        !new[] { oidcConfiguration["ClientId"], oidcConfiguration["Audience"] }.Contains(options.Audience, StringComparer.Ordinal),
        "Machine-token authentication requires a dedicated audience distinct from interactive OIDC.")
    .ValidateOnStart();
builder.Services.AddSingleton<IMachineTokenValidator, OidcMachineTokenValidator>();

#region Authentication UI
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, cookieOptions =>
{
    cookieOptions.LoginPath = "/login";
    cookieOptions.AccessDeniedPath = "/login";
    cookieOptions.Cookie.HttpOnly = true;
    cookieOptions.Cookie.SameSite = SameSiteMode.Lax;
    cookieOptions.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    cookieOptions.SlidingExpiration = true;
    cookieOptions.ExpireTimeSpan = builder.Configuration.GetValue<TimeSpan?>("Authentication:Cookie:ExpireTimeSpan")
        ?? TimeSpan.FromHours(8);
    cookieOptions.EventsType = typeof(CookieOidcSessionEvents);
})
.AddOpenIdConnect("Oidc", options =>
{
    options.Authority = oidcConfiguration["Authority"];
    options.ClientId = oidcConfiguration["ClientId"];
    options.ClientSecret = builder.Configuration["OIDC_CLIENT_SECRET"]
        ?? builder.Configuration["AZURE_CLIENT_SECRET"]
        ?? oidcConfiguration["ClientSecret"];
    // HTTPS metadata is required by default. A disposable test provider may opt out
    // explicitly, but self-hosted production configuration must retain this default.
    options.RequireHttpsMetadata = oidcConfiguration.GetValue("RequireHttpsMetadata", true);
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = true;
    options.UseTokenLifetime = false;
    options.GetClaimsFromUserInfoEndpoint = false;
    options.CallbackPath = oidcConfiguration["CallbackPath"] ?? "/signin-oidc";
    options.SignedOutCallbackPath = oidcConfiguration["SignedOutCallbackPath"] ?? "/signout-callback-oidc";
    options.TokenValidationParameters = new TokenValidationParameters
    {
        NameClaimType = "preferred_username",
        RoleClaimType = ClaimTypes.Role,
        ValidateIssuer = true
    };

    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
    options.Scope.Add("offline_access");

    var apiScope = oidcConfiguration["ApiScope"];
    if (!string.IsNullOrWhiteSpace(apiScope))
    {
        options.Scope.Add(apiScope);
    }

    options.Events = new OpenIdConnectEvents
    {
        OnRedirectToIdentityProvider = ctx => Task.CompletedTask
    };
})
;

builder.Services.AddAuthorization();

#endregion

#region DataProtection keys
// Appsettings-driven key directory with local fallback.
var configuredKeysDir = builder.Configuration["NetRatel_KEYS_DIR"]
                        ?? builder.Configuration["DataProtection:KeysDirectory"];
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

builder.Services.AddTransient<RedirectReissueHandler>();

#region Http clients for API access
builder.Services.AddHttpClient("TokenClient", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("OrchestratorApi", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001/");
    c.Timeout = Timeout.InfiniteTimeSpan;
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
.AddHttpMessageHandler<RedirectReissueHandler>()
.AddHttpMessageHandler<TokenAuthorizationHandler>();

builder.Services.AddHttpClient("OrchestratorApiStreaming", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001/");
    c.Timeout = Timeout.InfiniteTimeSpan;
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
.AddHttpMessageHandler<RedirectReissueHandler>()
.AddHttpMessageHandler<TokenAuthorizationHandler>()
.RemoveAllResilienceHandlers();

builder.Services.AddHttpClient("Bff", (sp, c) =>
{
    var accessor = sp.GetRequiredService<IHttpContextAccessor>();
    var ctx = accessor.HttpContext;
    if (ctx is not null)
    {
        var req = ctx.Request;
        c.BaseAddress = new Uri($"{req.Scheme}://{req.Host}{req.PathBase}");
    }
    else
    {
        var fallback = sp.GetService<IConfiguration>()?["PublicBaseUrl"] ?? "https://localhost:7247/";
        c.BaseAddress = new Uri(fallback, UriKind.Absolute);
    }
});

builder.Services.AddHttpClient<IWebClientDownloadService, WebClientDownloadService>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001/");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
.AddHttpMessageHandler<RedirectReissueHandler>()
.AddHttpMessageHandler<TokenAuthorizationHandler>();

builder.Services.AddHttpClient("SystemApi", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001/");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
.AddHttpMessageHandler<RedirectReissueHandler>()
.AddHttpMessageHandler<SystemTokenAuthorizationHandler>();

builder.Services.AddHttpClient("SystemApiNoAuth", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001/");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
.AddHttpMessageHandler<RedirectReissueHandler>();

// Register API services
builder.Services.AddScoped<TenantApiService>();
builder.Services.AddScoped<UploadsApiClient>();
builder.Services.AddScoped<IUploadsApiClient>(sp => sp.GetRequiredService<UploadsApiClient>());
builder.Services.AddScoped<ScriptLibraryClient>();

#endregion

// Add YARP reverse proxy configuration
builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapDefaultEndpoints();

// Forwarded values are accepted only from loopback (the framework default) or
// explicitly configured proxy addresses/networks. This keeps direct requests
// from being able to forge an HTTPS origin or client address.
var forwardedHeadersOptions = ProxyTrustOptions.Create(builder.Configuration);
app.UseForwardedHeaders(forwardedHeadersOptions);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.PhysicalPath ?? string.Empty;
        if (path.EndsWith("remote-support-dialog.js", StringComparison.OrdinalIgnoreCase))
        {
            // The unhashed compatibility route must always revalidate. Production pages use
            // the immutable fingerprinted route exposed by MapStaticAssets below.
            ctx.Context.Response.Headers.CacheControl = "no-cache, max-age=0, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
        else if (app.Environment.IsDevelopment() &&
                 (path.Contains("MudBlazor", StringComparison.OrdinalIgnoreCase) ||
                  path.Contains("MudBlazor.Extensions", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Context.Response.Headers.CacheControl = "no-store, max-age=0";
        }
    }
});

app.UseAntiforgery();

app.UseAuthentication();

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (NetRatel.Web.Services.Authentication.ReauthRequiredException)
    {
        try
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
        catch (Exception exception)
        {
            app.Logger.LogWarning(exception, "Cookie sign-out failed while handling an expired API session.");
        }
        try
        {
            await ctx.SignOutAsync("Oidc");
        }
        catch (Exception exception)
        {
            app.Logger.LogWarning(exception, "OIDC sign-out failed while handling an expired API session.");
        }

        if (ctx.Response.HasStarted)
        {
            return;
        }

        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ctx.Response.Redirect("/login");
    }
});

app.UseAuthorization();

// Browser requests reach the API through YARP rather than the typed HttpClients
// that already attach the OIDC access token. Preserve an explicit Authorization
// header, otherwise project the authenticated Web session's token to the API.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase) &&
        ctx.User.Identity?.IsAuthenticated is true &&
        !ctx.Request.Headers.ContainsKey("Authorization"))
    {
        var tokenService = ctx.RequestServices.GetRequiredService<ITokenService>();
        var accessToken = await tokenService.GetValidAccessTokenAsync();
        ctx.Request.Headers.Authorization = $"Bearer {accessToken}";
    }

    await next();
});

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value;
    if (string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, "/api/", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Redirect("/api/docs/");
        return;
    }

    if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/docs", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/openapi", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

// Host the Scalar UI in the web app and load the OpenAPI JSON through the API proxy.
app.MapScalarApiReference(
    "/api/docs",
    (options, context) =>
    {
        var request = context.Request;
        var openApiUrl = $"{request.Scheme}://{request.Host}{request.PathBase}/api/openapi/v1.json";
        options.AddDocument("v1", "NetRatel API", openApiUrl, isDefault: true);
        options.Servers = new[] { new ScalarServer(ScalarApiReferenceOptions.BuildPublicServerUrl(request)) };
    }).AllowAnonymous();
app.MapReverseProxy().AllowAnonymous();
app.MapRazorPages();

app.MapControllers();
app.MapStaticAssets();

app.MapGet("/login-machine-token", (IOptions<MachineTokenOptions> options) =>
{
    if (!options.Value.Enabled)
    {
        return Results.NotFound();
    }

    return Results.Text(
        "POST a valid OIDC machine token to /auth/machine-token/exchange to establish a session for this development instance.",
        "text/plain");
}).AllowAnonymous();

app.MapRazorComponents<NetRatel.Web.Components.App>()
   .AddInteractiveServerRenderMode();

app.MapFallbackToPage("/_Host");

app.Run();
