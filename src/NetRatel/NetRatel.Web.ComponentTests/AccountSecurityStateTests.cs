using System.Security.Claims;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Web.Components.Pages;
using NetRatel.Web.Services.Authentication;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class AccountSecurityStateTests : AsyncBunitContext
{
    [Fact]
    public void Local_account_waits_for_real_status_before_showing_factor_actions()
    {
        var api = Configure(localAccount: true);
        var page = Render<AccountSecurity>();

        page.Find("[data-testid='security-status-loading']").Should().NotBeNull();
        page.FindAll("[data-testid='open-mfa-setup']").Should().BeEmpty();

        api.CompleteStatus(new LocalSecurityStatus(false, 15));
        page.WaitForAssertion(() =>
        {
            page.Find("[data-testid='open-mfa-setup']").Should().NotBeNull();
            page.FindAll("[data-testid='manage-mfa']").Should().BeEmpty();
        });
    }

    [Fact]
    public void Status_failure_does_not_present_mfa_as_disabled()
    {
        var api = Configure(localAccount: true);
        var page = Render<AccountSecurity>();

        api.FailStatus();
        page.WaitForAssertion(() =>
        {
            page.Find("[data-testid='security-status-error']").TextContent.Should().Contain("unavailable");
            page.FindAll("[data-testid='open-mfa-setup']").Should().BeEmpty();
            page.FindAll("[data-testid='manage-mfa']").Should().BeEmpty();
        });
    }

    [Fact]
    public void Idp_managed_account_has_no_local_secret_controls()
    {
        var api = Configure(localAccount: false);
        var page = Render<AccountSecurity>();

        page.Find("[data-testid='account-security-managed']").TextContent.Should().Contain("identity provider");
        page.FindAll("[data-testid='open-password-dialog']").Should().BeEmpty();
        page.FindAll("[data-testid='open-mfa-setup']").Should().BeEmpty();
        api.StatusRequested.Should().BeFalse();
    }

    private StubLocalAccountApi Configure(bool localAccount)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        AddAuthorization();
        Services.AddSingleton<AuthenticationStateProvider>(new StaticAuthenticationStateProvider(localAccount));
        var api = new StubLocalAccountApi();
        Services.AddSingleton<ILocalAccountApiService>(api);
        return api;
    }

    private sealed class StaticAuthenticationStateProvider(bool localAccount) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            Claim[] claims = localAccount ? [new("auth_mode", "local")] : [new("auth_mode", "oidc")];
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
        }
    }

    private sealed class StubLocalAccountApi : ILocalAccountApiService
    {
        private readonly TaskCompletionSource<LocalSecurityStatus> _status = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StatusRequested { get; private set; }
        public void CompleteStatus(LocalSecurityStatus value) => _status.SetResult(value);
        public void FailStatus() => _status.SetException(new HttpRequestException("Unavailable"));
        public Task<LocalSecurityStatus> GetSecurityStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusRequested = true;
            return _status.Task;
        }
        public Task ActivateAsync(LocalAccountActivationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ChangePasswordAsync(LocalPasswordChangeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthenticatorSetup> BeginTwoFactorSetupAsync(string currentPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisableTwoFactorAsync(LocalTwoFactorDisableRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
