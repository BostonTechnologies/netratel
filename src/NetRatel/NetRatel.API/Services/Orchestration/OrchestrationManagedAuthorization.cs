using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.API.Services.Orchestration;

/// <summary>These alternatives apply only to the existing bounded orchestration routes.</summary>
public static class OrchestrationManagedAuthorization
{
    public const string ReadScope = "netratel.orchestration.read";
    public const string InvokeScope = "netratel.orchestration.invoke";
    public const string AuthenticationScheme = "OrchestrationService";
    public const string ReadPolicy = "OrchestrationRead";
    public const string InvokePolicy = "OrchestrationInvoke";

    public static IServiceCollection AddOrchestrationManagedServices(this IServiceCollection services)
    {
        services.AddAuthentication().AddPolicyScheme(AuthenticationScheme, AuthenticationScheme, options =>
        {
            options.ForwardDefaultSelector = http =>
            {
                var authorization = http.Request.Headers.Authorization.ToString();
                if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var token = authorization[7..].Trim();
                    if (token.Length <= 32768)
                    {
                        try
                        {
                            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
                            if (jwt.Claims.Any(claim => claim.Type == "token_use" && claim.Value == "netratel_service")) return "ManagedService";
                        }
                        catch (Exception error) when (error is ArgumentException or SecurityTokenException) { }
                    }
                }
                return "M2M";
            };
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReadPolicy, policy => policy.AddAuthenticationSchemes(AuthenticationScheme)
                .RequireAuthenticatedUser().AddRequirements(new OrchestrationServiceRequirement(ReadScope)));
            options.AddPolicy(InvokePolicy, policy => policy.AddAuthenticationSchemes(AuthenticationScheme)
                .RequireAuthenticatedUser().AddRequirements(new OrchestrationServiceRequirement(InvokeScope)));
        });
        services.AddScoped<IAuthorizationHandler, OrchestrationServiceAuthorizationHandler>();
        services.AddScoped<ManagedOrchestrationInvocationGuard>();
        services.AddScoped<OrchestrationCallbackReconciler>();
        services.AddHostedService<OrchestrationCallbackWorker>();
        services.AddHostedService<ManagedJobRunRecoveryWorker>();
        return services;
    }

    public static bool IsManaged(ClaimsPrincipal principal) => principal.Claims.Any(claim =>
        claim.Type == "token_use" && claim.Value == "netratel_service");

    public static async Task<ServicePrincipalRegistration?> ResolveAsync(HttpContext http, string scope, CancellationToken ct)
    {
        if (!IsManaged(http.User)) return null;
        var registry = http.RequestServices.GetRequiredService<IServicePrincipalRegistry>();
        return await registry.ResolvePrincipalAsync(http.User, scope, ct).ConfigureAwait(false);
    }

    public static ServiceLinkResourceConstraints Constraints(ServicePrincipalRegistration principal) =>
        ServicePrincipalRegistry.ReadConstraints(principal);
}

public sealed record OrchestrationServiceRequirement(string Scope) : IAuthorizationRequirement;

public sealed class OrchestrationServiceAuthorizationHandler(
    IServicePrincipalRegistry registry,
    IServiceProvider services) : AuthorizationHandler<OrchestrationServiceRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OrchestrationServiceRequirement requirement)
    {
        if (OrchestrationManagedAuthorization.IsManaged(context.User))
        {
            if (await registry.ResolvePrincipalAsync(context.User, requirement.Scope).ConfigureAwait(false) is not null)
                context.Succeed(requirement);
            return;
        }
        if ((await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User, context.Resource, "M2MOnly").ConfigureAwait(false)).Succeeded)
            context.Succeed(requirement);
    }
}
