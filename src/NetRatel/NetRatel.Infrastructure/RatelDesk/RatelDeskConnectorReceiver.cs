using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class RatelDeskConnectorReceiver(IRatelDeskConnectorStore connectors,
    IRatelDeskConnectorBindingStore references, IRatelDeskConnectorReadinessStore observations,
    IRatelDeskConnectorReadiness readiness, IRatelDeskOutboundBindingResolver bindings,
    IRatelDeskReceiverTransport transport, IFlowSourceIdentityResolver source,
    RatelDeskReceiverNetworkPolicy network, TimeProvider clock)
{
    public string ValidateApiBase(RatelDeskConnectorAuthentication authentication, string api) =>
        network.ValidateApprovedApiBase(authentication.Mode, api);

    public Task<(bool Available, string Code)> CurrentAsync(RatelDeskConnectorState state, CancellationToken ct) =>
        readiness.CurrentAsync(state, ct);

    public async Task<RatelDeskConnectionTestResult> TestAsync(RatelDeskConnectorState state, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        var token = linked.Token;
        async Task<RatelDeskConnectionTestResult> FailureAsync(RatelDeskConnectionTestResult failure)
        {
            // A failed fresh observation cannot leave an older success presented as current.
            // Exact row CAS cannot erase a newer successful observation or concurrent credential/configuration edit.
            _ = await observations.ClearReadinessAsync(state, token).ConfigureAwait(false);
            return failure;
        }
        try
        {
            if (state.Readiness is not null)
            {
                // Invalidate the previous observation before probing, so inner ten-second
                // cancellation or a caller stop cannot expose a retained success afterward.
                if (!await observations.ClearReadinessAsync(state, token).ConfigureAwait(false))
                    return new(RatelDeskConnectionTestStatus.Unavailable, "connector-changed-during-readiness");
                state = state with { RowVersion = checked(state.RowVersion + 1), Readiness = null };
            }
            var authentication = await references.GetAuthenticationAsync(state.TenantId, state.Id, token);
            var flowSource = await source.EnsureAsync(token);
            var peer = await bindings.CaptureAsync(state, authentication, flowSource, token);
            var bearer = await bindings.GetBearerAsync(state, peer, "rateldesk.incident-receipts.read", token);
            var capability = await transport.CapabilitiesAsync(peer, bearer, token);
            bearer = await bindings.GetBearerAsync(state, peer, "rateldesk.incident-targets.read", token);
            await transport.ValidateTargetsAsync(peer, capability, bearer, token);
            // The current resolver rechecks local owner/source/profile after every network phase.
            _ = await bindings.GetBearerAsync(state, peer, "rateldesk.incidents.create", token);
            var after = await connectors.GetAsync(state.TenantId, state.Id, token);
            if (after is null || after.Revision != state.Revision || after.RowVersion != state.RowVersion ||
                after.CredentialRevision != state.CredentialRevision || after.OwnerPrincipalId != state.OwnerPrincipalId)
                return await FailureAsync(new(RatelDeskConnectionTestStatus.Unavailable, "connector-changed-during-readiness"));
            var observed = new RatelDeskReadinessObservation(state.Revision, peer, capability, clock.GetUtcNow());
            if (!await observations.SaveReadinessAsync(after, observed, token))
                return await FailureAsync(new(RatelDeskConnectionTestStatus.Unavailable, "connector-changed-during-readiness"));
            return new(RatelDeskConnectionTestStatus.MappingValidated, "receiver-ready", true);
        }
        catch (RatelDeskReceiverReadException error)
        {
            var status = error.HttpStatus switch
            {
                401 or 403 => RatelDeskConnectionTestStatus.AuthenticationRejected,
                400 or 422 => RatelDeskConnectionTestStatus.MappingRejected,
                _ => RatelDeskConnectionTestStatus.Unavailable
            };
            return await FailureAsync(new(status, error.Code, false, error.RetryAfter is null ? null :
                (int)Math.Clamp(Math.Ceiling(error.RetryAfter.Value.TotalSeconds), 1, 300)));
        }
        catch (UnauthorizedAccessException) { return await FailureAsync(new(RatelDeskConnectionTestStatus.AuthenticationRejected, "receiver-current-authority-denied")); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or
            HttpRequestException or NetRatel.Infrastructure.ServiceLinks.ServiceLinkProtocolException)
        { return await FailureAsync(new(RatelDeskConnectionTestStatus.Unavailable, "receiver-readiness-unverified")); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(RatelDeskConnectionTestStatus.Unavailable, "receiver-readiness-timeout"); }
    }

    public static RatelDeskConnectorAuthentication Parse(RatelDeskConnectorAuthenticationDto? value,
        RatelDeskConnectorAuthentication? existing = null)
    {
        if (value is null) return existing ?? new(RatelDeskAuthenticationMode.ManualApiBearer, null);
        if (value is { Mode: "api_bearer", ManagedLinkId: null }) return new(RatelDeskAuthenticationMode.ManualApiBearer, null);
        if (value.Mode == "service_link" && Guid.TryParseExact(value.ManagedLinkId, "D", out var link) &&
            link != Guid.Empty && link.ToString("D") == value.ManagedLinkId)
            return new(RatelDeskAuthenticationMode.ManagedServiceLink, value.ManagedLinkId);
        throw new ArgumentException("invalid-connector-authentication-reference");
    }

    public static RatelDeskConnectorAuthenticationDto ToDto(RatelDeskConnectorAuthentication? value) =>
        value?.Mode == RatelDeskAuthenticationMode.ManagedServiceLink ? new("service_link", value.ManagedLinkId) : new("api_bearer");
}
