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
    [InlineData("api_bearer")]
    [InlineData("service_link")]
    [InlineData("")]
    public void Authentication_selection_never_infers_modes_from_a_secret_or_ambiguous_label(string mode)
    {
        Action parse = () => RatelDeskConnectorReceiver.Parse(new(mode));
        parse.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Omitted_mode_preserves_existing_pairing_reference_and_cannot_create_unbound_authority()
    {
        var managed = new RatelDeskConnectorAuthentication(RatelDeskAuthenticationMode.PairedSystem,
            "33333333-3333-4333-8333-333333333333");
        RatelDeskConnectorReceiver.Parse(null, managed).Should().Be(managed);
        Action unbound = () => RatelDeskConnectorReceiver.Parse(null);
        unbound.Should().ThrowExactly<ArgumentException>();
        Action mixed = () => RatelDeskConnectorReceiver.Parse(new("api_bearer", managed.ManagedLinkId));
        mixed.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Browser_DTO_omits_internal_credential_ciphertext_and_receiver_evidence()
    {
        var state = new Store().Current with { ProtectedCredential = "synthetic-protected-value", CredentialRevision = 19 };
        var json = System.Text.Json.JsonSerializer.Serialize(RatelDeskConnectorService.ToDto(state));
        json.Should().NotContain("synthetic-protected-value").And.NotContain("ProtectedCredential").And.NotContain("ExactCreateBodyJson");
        RatelDeskConnectorService.ToDto(state).Authentication.Should().Be(new RatelDeskConnectorAuthenticationDto("pairing", state.Authentication!.ManagedLinkId));
    }

    private static ClaimsPrincipal Human => new(new ClaimsIdentity([new("netratel_principal_id", "owner")], "Oidc"));
    private sealed class Store : IRatelDeskConnectorStore
    {
        public RatelDeskConnectorState Current = new(Guid.Parse("44444444-4444-4444-8444-444444444444"), 17, 7, 3, "owner",
            new("Managed", "https://desk.example", "org", "customer", null, [], new(), true), null, 0,
            new(RatelDeskAuthenticationMode.PairedSystem, "33333333-3333-4333-8333-333333333333"));
        public int Writes;
        public Task<RatelDeskConnectorState?> GetAsync(int tenant, Guid id, CancellationToken ct) => Task.FromResult<RatelDeskConnectorState?>(tenant == Current.TenantId && id == Current.Id ? Current : null);
        public Task<IReadOnlyList<RatelDeskConnectorState>> ListAsync(int tenant, CancellationToken ct) => Task.FromResult<IReadOnlyList<RatelDeskConnectorState>>([Current]);
        public Task<bool> SaveAsync(RatelDeskConnectorState value, long expected, CancellationToken ct) { Writes++; Current = value; return Task.FromResult(true); }
    }
}
