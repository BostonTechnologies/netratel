using System.Security.Claims;
using System.Reflection;
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

    [Fact]
    public async Task Enrollment_cannot_close_while_setup_is_pending()
    {
        var api = Configure(localAccount: true);
        var page = Render<AccountSecurity>();
        api.CompleteStatus(new LocalSecurityStatus(false, 15));
        Call(page.Instance, "OpenEnrollment");
        Set(page.Instance, "_setupPassword", "a local-first passphrase");
        var pending = CallAsync(page.Instance, "BeginMfaSetupAsync");

        Call(page.Instance, "CloseEnrollment");
        CallWithArg(page.Instance, "EnrollmentVisibleChanged", false);

        Field<bool>(page.Instance, "_enrollmentDialog").Should().BeTrue();
        api.CompleteSetup(new AuthenticatorSetup("SYNTHETICKEY", "otpauth://totp/NetRatel:synthetic?secret=SYNTHETICKEY"));
        await pending;
        Field<AuthenticatorSetup>(page.Instance, "_authenticatorSetup").Should().NotBeNull();
    }

    [Fact]
    public async Task Enablement_keeps_recovery_codes_in_the_open_acknowledgment()
    {
        var api = Configure(localAccount: true);
        var page = Render<AccountSecurity>();
        api.CompleteStatus(new LocalSecurityStatus(false, 15));
        Call(page.Instance, "OpenEnrollment");
        Set(page.Instance, "_setupPassword", "a local-first passphrase");
        var setup = CallAsync(page.Instance, "BeginMfaSetupAsync");
        api.CompleteSetup(new AuthenticatorSetup("SYNTHETICKEY", "otpauth://totp/NetRatel:synthetic?secret=SYNTHETICKEY"));
        await setup;
        Set(page.Instance, "_enrollmentCode", "123456");
        var enable = CallAsync(page.Instance, "EnableMfaAsync");

        Call(page.Instance, "CloseEnrollment");
        Field<bool>(page.Instance, "_enrollmentDialog").Should().BeTrue();
        api.CompleteEnable(["synthetic-recovery-code"]);
        await enable;
        Field<IReadOnlyList<string>>(page.Instance, "_recoveryCodes").Should().ContainSingle()
            .Which.Should().Be("synthetic-recovery-code");
        Call(page.Instance, "CloseEnrollment");
        Field<bool>(page.Instance, "_enrollmentDialog").Should().BeTrue();
    }

    [Fact]
    public async Task Disposed_enrollment_ignores_late_setup_response()
    {
        var api = Configure(localAccount: true);
        var page = Render<AccountSecurity>();
        api.CompleteStatus(new LocalSecurityStatus(false, 15));
        Call(page.Instance, "OpenEnrollment");
        Set(page.Instance, "_setupPassword", "a local-first passphrase");
        var pending = CallAsync(page.Instance, "BeginMfaSetupAsync");

        page.Instance.Dispose();
        api.CompleteSetup(new AuthenticatorSetup("SYNTHETICKEY", "otpauth://totp/NetRatel:synthetic?secret=SYNTHETICKEY"));
        await pending;

        Field<AuthenticatorSetup?>(page.Instance, "_authenticatorSetup").Should().BeNull();
        Field<string?>(page.Instance, "_qrDataUrl").Should().BeNull();
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

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static void Call(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
    private static void CallWithArg(object instance, string name, object value) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, [value]);
    private static Task CallAsync(object instance, string name) =>
        (Task)instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null)!;

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
        private readonly TaskCompletionSource<AuthenticatorSetup> _setup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<string>> _enable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StatusRequested { get; private set; }
        public void CompleteStatus(LocalSecurityStatus value) => _status.SetResult(value);
        public void FailStatus() => _status.SetException(new HttpRequestException("Unavailable"));
        public void CompleteSetup(AuthenticatorSetup value) => _setup.SetResult(value);
        public void CompleteEnable(IReadOnlyList<string> codes) => _enable.SetResult(codes);
        public Task<LocalSecurityStatus> GetSecurityStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusRequested = true;
            return _status.Task;
        }
        public Task ActivateAsync(LocalAccountActivationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ChangePasswordAsync(LocalPasswordChangeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthenticatorSetup> BeginTwoFactorSetupAsync(string currentPassword, CancellationToken cancellationToken = default) => _setup.Task;
        public Task<IReadOnlyList<string>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default) => _enable.Task;
        public Task DisableTwoFactorAsync(LocalTwoFactorDisableRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
