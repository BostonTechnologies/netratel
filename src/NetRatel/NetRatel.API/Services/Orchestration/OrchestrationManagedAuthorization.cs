using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.API.Services.Orchestration;

/// <summary>The bounded orchestration routes require a current saved connection and scoped business credential.</summary>
public static class OrchestrationManagedAuthorization
{
    public const string ReadScope = "netratel.orchestration.read";
    public const string InvokeScope = "netratel.orchestration.invoke";
    public const string AuthenticationScheme = "OrchestrationService";
    public const string ReadPolicy = "OrchestrationRead";
    public const string InvokePolicy = "OrchestrationInvoke";

    public static IServiceCollection AddOrchestrationManagedServices(this IServiceCollection services)
    {
        services.AddAuthentication().AddPolicyScheme(AuthenticationScheme, AuthenticationScheme,
            options => options.ForwardDefault = "ManagedService");
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReadPolicy, policy => policy.AddAuthenticationSchemes(AuthenticationScheme)
                .RequireAuthenticatedUser().AddRequirements(new OrchestrationServiceRequirement(ReadScope)));
            options.AddPolicy(InvokePolicy, policy => policy.AddAuthenticationSchemes(AuthenticationScheme)
                .RequireAuthenticatedUser().AddRequirements(new OrchestrationServiceRequirement(InvokeScope)));
        });
        services.AddScoped<IAuthorizationHandler, OrchestrationServiceAuthorizationHandler>();
        services.TryAddSingleton<IAuthorizationMiddlewareResultHandler, OrchestrationAuthorizationResultHandler>();
        services.AddScoped<ManagedOrchestrationInvocationGuard>();
        services.AddScoped<INetRatelExternalServiceCallbackReplayService, NetRatelExternalServiceCallbackReplayService>();
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

    public static PairingResourceConstraints Constraints(ServicePrincipalRegistration principal) =>
        ServicePrincipalRegistry.ReadConstraints(principal);
}

/// <summary>Keep ordinary authorization behavior, adding bounded diagnostics only for the paired contract.</summary>
public sealed class OrchestrationAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult result)
    {
        await fallback.HandleAsync(next, context, policy, result);
        if (result.Succeeded || context.Response.HasStarted ||
            !policy.Requirements.OfType<OrchestrationServiceRequirement>().Any()) return;
        var status = context.Response.StatusCode;
        await context.Response.WriteAsJsonAsync(new { operation = "paired-automation-authorization", status,
            code = status == 401 ? "authentication_rejected" : "access_denied",
            detail = "The current paired credential and tenant mapping must authorize this operation.",
            correlationId = context.TraceIdentifier }, context.RequestAborted);
    }
}

public sealed record OrchestrationServiceRequirement(string Scope) : IAuthorizationRequirement;

public sealed class OrchestrationServiceAuthorizationHandler(
    IServicePrincipalRegistry registry) : AuthorizationHandler<OrchestrationServiceRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OrchestrationServiceRequirement requirement)
    {
        if (OrchestrationManagedAuthorization.IsManaged(context.User) &&
            await registry.ResolvePrincipalAsync(context.User, requirement.Scope).ConfigureAwait(false) is not null)
            context.Succeed(requirement);
    }
}
