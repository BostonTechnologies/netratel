using System.Security.Claims;
using AwesomeAssertions;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class RatelDeskConnectorModeTests
{
    [Theory]
    [InlineData("oauth_client_secret")]
    [InlineData("rdk")]
    [InlineData("Api_Bearer")]
    [InlineData("")]
    public void Authentication_selection_never_infers_modes_from_a_secret_or_ambiguous_label(string mode)
    {
        Action parse = () => RatelDeskConnectorReceiver.Parse(new(mode));
        parse.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Omitted_mode_preserves_existing_managed_reference_while_legacy_rows_remain_API_bearer()
    {
        var managed = new RatelDeskConnectorAuthentication(RatelDeskAuthenticationMode.ManagedServiceLink,
            "33333333-3333-4333-8333-333333333333");
        RatelDeskConnectorReceiver.Parse(null, managed).Should().Be(managed);
        RatelDeskConnectorReceiver.Parse(null).Should().Be(new RatelDeskConnectorAuthentication(RatelDeskAuthenticationMode.ManualApiBearer, null));
        Action mixed = () => RatelDeskConnectorReceiver.Parse(new("api_bearer", managed.ManagedLinkId));
        mixed.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public async Task Managed_connector_rejects_manual_rotation_before_protecting_or_saving_any_secret()
    {
        var store = new Store(); var protection = new Protection();
        var service = new RatelDeskConnectorService(store, new Authority(), protection, new Origin(), new Tester());
        Func<Task> rotate = () => service.RotateAsync(17, store.Current.Id,
            new(0, "rdk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Human, default);
        await rotate.Should().ThrowExactlyAsync<ArgumentException>();
        protection.Writes.Should().Be(0); store.Writes.Should().Be(0);
        store.Current.Revision.Should().Be(7); store.Current.Authentication!.ManagedLinkId.Should().Be("33333333-3333-4333-8333-333333333333");
    }

    [Fact]
    public async Task Disabled_current_connector_owner_cannot_rotate_manual_credentials()
    {
        var store = new Store(); store.Current = store.Current with { Authentication = new(RatelDeskAuthenticationMode.ManualApiBearer, null) };
        var protection = new Protection();
        var service = new RatelDeskConnectorService(store, new Authority { OwnerAllowed = false }, protection, new Origin(), new Tester());
        Func<Task> rotate = () => service.RotateAsync(17, store.Current.Id,
            new(0, "rdk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Human, default);
        await rotate.Should().ThrowExactlyAsync<UnauthorizedAccessException>();
        protection.Writes.Should().Be(0); store.Writes.Should().Be(0);
    }

    [Fact]
    public void Browser_DTO_omits_internal_credential_ciphertext_and_receiver_evidence()
    {
        var state = new Store().Current with { ProtectedCredential = "synthetic-protected-value", CredentialRevision = 19 };
        var json = System.Text.Json.JsonSerializer.Serialize(RatelDeskConnectorService.ToDto(state));
        json.Should().NotContain("synthetic-protected-value").And.NotContain("ProtectedCredential").And.NotContain("ExactCreateBodyJson");
        RatelDeskConnectorService.ToDto(state).Authentication.Should().Be(new RatelDeskConnectorAuthenticationDto("service_link", state.Authentication!.ManagedLinkId));
    }

    private static ClaimsPrincipal Human => new(new ClaimsIdentity([new("netratel_principal_id", "owner")], "Oidc"));
    private sealed class Store : IRatelDeskConnectorStore
    {
        public RatelDeskConnectorState Current = new(Guid.Parse("44444444-4444-4444-8444-444444444444"), 17, 7, 3, "owner",
            new("Managed", "https://desk.example", "org", "customer", null, [], new(), true), null, 0,
            new(RatelDeskAuthenticationMode.ManagedServiceLink, "33333333-3333-4333-8333-333333333333"));
        public int Writes;
        public Task<RatelDeskConnectorState?> GetAsync(int tenant, Guid id, CancellationToken ct) => Task.FromResult<RatelDeskConnectorState?>(tenant == Current.TenantId && id == Current.Id ? Current : null);
        public Task<IReadOnlyList<RatelDeskConnectorState>> ListAsync(int tenant, CancellationToken ct) => Task.FromResult<IReadOnlyList<RatelDeskConnectorState>>([Current]);
        public Task<bool> SaveAsync(RatelDeskConnectorState value, long expected, CancellationToken ct) { Writes++; Current = value; return Task.FromResult(true); }
    }
    private sealed class Protection : IRatelDeskCredentialProtector
    {
        public int Writes;
        public string Protect(int tenant, Guid id, string credential) { Writes++; return "synthetic-protected"; }
        public string Unprotect(int tenant, Guid id, string ciphertext) => throw new InvalidOperationException("not-needed");
    }
    private sealed class Authority : IRatelDeskConnectorAuthorization
    {
        public bool OwnerAllowed = true;
        public Task<bool> CanManageAsync(ClaimsPrincipal actor, int tenant, CancellationToken ct) => Task.FromResult(tenant == 17);
        public Task<bool> CanExecuteAsync(string principal, string? credential, int tenant, CancellationToken ct) => Task.FromResult(OwnerAllowed && tenant == 17 && principal == "owner");
    }
    private sealed class Origin : IRatelDeskOriginPolicy
    { public bool TryValidate(string value, out Uri? normalized) { normalized = null; return false; } }
    private sealed class Tester : IRatelDeskConnectionTester
    { public Task<RatelDeskConnectionTestResult> TestAsync(int tenant, Guid id, RatelDeskConnectorConfiguration c, string secret, CancellationToken ct) => throw new InvalidOperationException("not-needed"); }
}
