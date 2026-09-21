using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Events;
using NetRatel.Application.Abstractions;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;
using NetRatel.Application.Notifications;
using NetRatel.Application.Operations;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Application.Secrets;
using NetRatel.Application.Scripts;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Events;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Artifacts;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Requests;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;

namespace NetRatel.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNetRatelInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var database = NetRatelDatabaseConfigurationResolver.Resolve(configuration);

        services.AddScoped<DomainEventsToOutboxInterceptor>();
        services.AddScoped<DatabaseCommandMetricsInterceptor>();
        services.AddDbContext<OrchestratorDbContext>((sp, options) =>
        {
            ConfigureProvider(options, database);
            options.AddInterceptors(
                sp.GetRequiredService<DomainEventsToOutboxInterceptor>(),
                sp.GetRequiredService<DatabaseCommandMetricsInterceptor>());
        });
        services.AddDbContext<NetRatelIdentityDbContext>(options => ConfigureProvider(options, database));
        services.AddScoped<IApplicationPrincipalResolver, ApplicationPrincipalResolver>();
        services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        services.AddScoped<IIntegrationCredentialService, IntegrationCredentialService>();
        services.AddScoped<IIntegrationCredentialCurrentVerifier, IntegrationCredentialService>();
        services.AddScoped<IMcpOperationObjectTargetResolver, McpOperationObjectTargetResolver>();
        services.AddScoped<IDeploymentBrandingService, DeploymentBrandingService>();
        services.AddNetRatelCommandPersistence();
        services.AddNetRatelJobShadowPersistence();
        services.AddNetRatelRemoteSupportLifecycle();

        services.AddSingleton<INetRatelNotificationEventBus, NetRatelNotificationEventBus>();
        services.AddSingleton<IRequestEventBus, RequestEventBus>();
        services.AddScoped<IClientDisplayNameResolver, UnavailableClientDisplayNameResolver>();
        services.AddScoped<NetRatelNotificationDisplaySanitizer>();
        services.AddScoped<INetRatelNotificationService, NetRatelNotificationService>();
        services.AddScoped<IEventRecorder, OutboxEventRecorder>();
        services.AddScoped<IEventPublisher, InternalEventPublisher>();
        services.AddScoped<IJobDefinitionService, JobDefinitionService>();
        services.AddScoped<IJobRunService, JobRunService>();
        services.AddScoped<IRequestService, RequestService>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ISecretService, SecretService>();
        services.AddScoped<IScriptService, ScriptService>();

        services.AddScoped<IM2MConnectivityService, M2MConnectivityService>();
        services.AddScoped<IEnrollmentService, EnrollmentService>();
        services.AddScoped<IEnrollmentCodeIssueService, EnrollmentCodeIssueService>();
        services.AddScoped<IAgentTokenService, AgentTokenService>();
        services.AddScoped<IAgentManagementService, AgentManagementService>();
        services.AddScoped<IPrimaryClientAgentBindingService, PrimaryClientAgentBindingService>();
        services.AddScoped<IDevelopmentOperatorTargetAuthority, DevelopmentOperatorTargetAuthority>();
        services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
        services.AddScoped<IMcpOperatorSearchAuthorization, McpOperatorSearchAuthorization>();
        services.AddScoped<IMcpOperatorRouteAdmission, McpOperatorRouteAdmissionService>();
        services.AddScoped<IMcpOperatorAccessEvaluation, McpOperatorAccessEvaluationService>();
        services.AddScoped<McpOperatorPolicyAdministration>();
        services.AddScoped<IMcpOperatorPolicyAdministration>(serviceProvider => serviceProvider.GetRequiredService<McpOperatorPolicyAdministration>());
        services.AddScoped<IMcpOperatorPolicyRevocation>(serviceProvider => serviceProvider.GetRequiredService<McpOperatorPolicyAdministration>());
        services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        services.AddScoped<IMcpOperatorFileArtifactStore, McpOperatorFileArtifactStore>();
        services.AddScoped<IMcpOperatorTerminalSessionRecovery, McpOperatorTerminalSessionRecoveryService>();
        services.AddScoped<IMcpOperatorTerminalSessionStore, McpOperatorTerminalSessionStore>();
        services.AddScoped<IMcpOperatorTerminalActionStore, McpOperatorTerminalActionStore>();
        services.AddScoped<IMcpOperatorCommandStore, McpOperatorCommandStore>();
        services.AddScoped<IMcpOperatorScriptStore, McpOperatorScriptStore>();
        services.AddScoped<IMcpOperatorJobStore, McpOperatorJobStore>();
        services.AddScoped<IMcpOperatorTaskStore, McpOperatorTaskStore>();
        services.AddScoped<IMcpOperatorRequestStore, McpOperatorRequestStore>();
        services.AddScoped<AgentNonceReplayService>();
        services.AddScoped<IArtifactZipInjectionService, ZipInjectionService>();
        services.AddScoped<IScriptTemplateService, ScriptTemplateService>();
        services.AddScoped<OidcSigningService>();

        return services;
    }

    private static void ConfigureProvider(DbContextOptionsBuilder options, NetRatelDatabaseConfiguration database)
    {
        if (database.Provider is NetRatelDatabaseProvider.Sqlite)
        {
            options.UseSqlite(database.ConnectionString, sqlite =>
                sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"));
            return;
        }

        options.UseNpgsql(database.ConnectionString);
    }

    public static async Task MigrateNetRatelInfrastructureAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.MigrateAsync(ct);

        var identityDb = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
        await identityDb.Database.MigrateAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IEffectiveAccessService>()
            .ReconcileBuiltInRolesAsync(ct);

        if (!await db.M2MConnectivitySettings.AnyAsync(ct))
        {
            db.M2MConnectivitySettings.Add(new M2MConnectivitySettings { Enabled = false });
            await db.SaveChangesAsync(ct);
        }
    }
}
