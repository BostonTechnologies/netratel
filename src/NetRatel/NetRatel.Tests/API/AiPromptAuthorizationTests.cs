using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NetRatel.API.Endpoints;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Netclaw;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AiPromptAuthorizationTests
{
    [Fact]
    public async Task General_chat_checks_current_tenant_read_and_manage_permissions_before_any_data_access()
    {
        var http = new DefaultHttpContext { User = new(new ClaimsIdentity([new Claim("netratel_principal_id", "operator")], "Oidc")) };
        var denied = new Access();
        Assert.IsType<ForbidHttpResult>(await NetclawEndpoints.ListChatsAsync(17, 0, http, denied, null!, default));
        Assert.IsType<ForbidHttpResult>(await NetclawEndpoints.CreateChatAsync(17, new(Guid.NewGuid(), "Chat"), http, denied, null!, null!, default));
        var readOnly = new Access { AllowRead = true };
        Assert.IsType<ForbidHttpResult>(await NetclawEndpoints.CreateChatAsync(17, new(Guid.NewGuid(), "Chat"), http, readOnly, null!, null!, default));
        Assert.IsType<ForbidHttpResult>(await NetclawEndpoints.ListChatsAsync(18, 0, http, readOnly, null!, default));
        Assert.Contains((NetRatelPermissions.ClientManagement, (int?)17), readOnly.Checks);
        Assert.Contains((NetRatelPermissions.TelemetryRead, (int?)18), readOnly.Checks);
    }

    [Fact]
    public async Task Current_tenant_permissions_are_checked_before_prompt_load_or_save()
    {
        var http = new DefaultHttpContext { User = new(new ClaimsIdentity([new Claim("netratel_principal_id", "operator")], "Oidc")) };
        var denied = new Access(); var id = Guid.NewGuid();
        Assert.IsType<ForbidHttpResult>(await AiPromptEndpoints.ListAsync(17, null, false, http, denied, null!, default));
        Assert.IsType<ForbidHttpResult>(await AiPromptEndpoints.GetAsync(17, id, 1, http, denied, null!, default));
        Assert.IsType<ForbidHttpResult>(await AiPromptEndpoints.CreateAsync(17, new("Prompt", null, "Guidance"), http, denied, null!, default));
        var readOnly = new Access { AllowRead = true };
        Assert.IsType<ForbidHttpResult>(await AiPromptEndpoints.UpdateAsync(17, id, new("Prompt", null, "Guidance", 1), http, readOnly, null!, default));
        Assert.IsType<ForbidHttpResult>(await AiPromptEndpoints.ArchiveAsync(17, id, new(1, 0), http, readOnly, null!, default));
        Assert.IsType<ForbidHttpResult>(await AiPromptEndpoints.DefaultAsync(17, new(id, 0, 1), http, readOnly, null!, default));
        Assert.Contains((NetRatelPermissions.ClientManagement, (int?)17), readOnly.Checks);
        Assert.All(denied.Checks, check => Assert.Equal(17, check.Tenant));
    }
    private sealed class Access : IEffectiveAccessService
    {
        public bool AllowRead { get; init; }
        public List<(string Permission, int? Tenant)> Checks { get; } = [];
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default)
        { Checks.Add((permission, tenantId)); return Task.FromResult(AllowRead && permission == NetRatelPermissions.TelemetryRead && tenantId == 17); }
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
