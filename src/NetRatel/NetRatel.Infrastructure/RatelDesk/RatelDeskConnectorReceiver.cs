using System.Text.Json;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Events;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.SystemPairing;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class RatelDeskConnectorReceiver(IRatelDeskConnectorStore connectors,
    IRatelDeskConnectorBindingStore references, IRatelDeskConnectorReadinessStore observations,
    IRatelDeskConnectorReadiness readiness, IRatelDeskOutboundBindingResolver bindings,
    IRatelDeskReceiverTransport transport, IFlowSourceIdentityResolver source,
    RatelDeskReceiverNetworkPolicy network, TimeProvider clock,
    ICorrelationContext? correlation = null, ILogger<RatelDeskConnectorReceiver>? logger = null)
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
        var stage = "readiness-invalidation";
        var reference = PairingReadinessDiagnostics.NewReference(correlation?.GetOrCreate());
        RatelDeskConnectionTestResult Failure(string code, RatelDeskConnectionTestStatus status = RatelDeskConnectionTestStatus.Unavailable,
            int? httpStatus = null, int? retry = null)
        {
            if (!PairingReadinessDiagnostics.IsAllowedReason(code)) code = "receiver-readiness-unverified";
            var diagnostic = new PairingReadinessDiagnostic(stage, code, reference, httpStatus);
            logger?.LogWarning("Receiver readiness failed Stage={Stage} Code={Code} Reference={Reference} HttpStatus={HttpStatus}",
                stage, code, reference, httpStatus);
            return new(status, code, false, retry, diagnostic);
        }
        async Task<RatelDeskConnectionTestResult> FailureAsync(RatelDeskConnectionTestResult failure)
        {
            // A failed fresh observation cannot leave an older success presented as current.
            // Exact row CAS cannot erase a newer successful observation or concurrent credential/configuration edit.
            try { _ = await observations.ClearReadinessAsync(state, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception error) when (error is DbException or DbUpdateException or IOException or InvalidDataException or InvalidOperationException)
            {
                logger?.LogWarning("Receiver readiness cleanup failed Stage={Stage} Code={Code} Reference={Reference}",
                    stage, "receiver-persistence-unavailable", reference);
            }
            return failure;
        }
        try
        {
            if (state.Readiness is not null)
            {
                // Invalidate the previous observation before probing, so inner ten-second
                // cancellation or a caller stop cannot expose a retained success afterward.
                if (!await observations.ClearReadinessAsync(state, token).ConfigureAwait(false))
                    return Failure("connector-changed-during-readiness");
                state = state with { RowVersion = checked(state.RowVersion + 1), Readiness = null };
            }
            stage = "paired-binding";
            var authentication = await references.GetAuthenticationAsync(state.TenantId, state.Id, token);
            var flowSource = await source.EnsureAsync(token);
            var peer = await bindings.CaptureAsync(state, authentication, flowSource, token);
            stage = "receipt-token";
            var bearer = await bindings.GetBearerAsync(state, peer, "rateldesk.incident-receipts.read", token);
            stage = "receiver-capabilities";
            var capability = await transport.CapabilitiesAsync(peer, bearer, token);
            stage = "target-token";
            bearer = await bindings.GetBearerAsync(state, peer, "rateldesk.incident-targets.read", token);
            stage = "receiver-targets";
            await transport.ValidateTargetsAsync(peer, capability, bearer, token);
            // The current resolver rechecks local owner/source/profile after every network phase.
            stage = "create-token";
            _ = await bindings.GetBearerAsync(state, peer, "rateldesk.incidents.create", token);
            stage = "readiness-persistence";
            var after = await connectors.GetAsync(state.TenantId, state.Id, token);
            if (after is null || after.Revision != state.Revision || after.RowVersion != state.RowVersion ||
                after.CredentialRevision != state.CredentialRevision || after.OwnerPrincipalId != state.OwnerPrincipalId)
                return await FailureAsync(Failure("connector-changed-during-readiness"));
            var observed = new RatelDeskReadinessObservation(state.Revision, peer, capability, clock.GetUtcNow());
            if (!await observations.SaveReadinessAsync(after, observed, token))
                return await FailureAsync(Failure("connector-changed-during-readiness"));
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
            return await FailureAsync(Failure(error.Code, status, error.HttpStatus, error.RetryAfter is null ? null :
                (int)Math.Clamp(Math.Ceiling(error.RetryAfter.Value.TotalSeconds), 1, 300)));
        }
        catch (UnauthorizedAccessException error)
        {
            return await FailureAsync(Failure(PairingReadinessDiagnostics.IsAllowedReason(error.Message) ? error.Message : "receiver-current-authority-denied",
                RatelDeskConnectionTestStatus.AuthenticationRejected));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or ArgumentException or InvalidOperationException or
            HttpRequestException or NetRatel.Shared.SystemPairing.PairingException or DbException or DbUpdateException)
        {
            var (code, status) = Classify(error);
            return await FailureAsync(Failure(code, status is 401 or 403 ? RatelDeskConnectionTestStatus.AuthenticationRejected :
                RatelDeskConnectionTestStatus.Unavailable, status));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Failure("receiver-readiness-timeout"); }
    }

    private static (string Code, int? Status) Classify(Exception error)
    {
        // Only known scalar reasons cross the boundary. Exception text, URLs and token responses never do.
        var depth = 0;
        for (Exception? current = error; current is not null && depth++ < 8; current = current.InnerException)
        {
            if (current.Data["Pairing.NetworkPolicyRejected"] is true) return ("receiver-address-blocked", null);
            if (current is PairingException pairing && PairingReadinessDiagnostics.IsAllowedReason(pairing.Code))
                return (pairing.Code, pairing.StatusCode);
            if (current is InvalidDataException or InvalidOperationException or UnauthorizedAccessException && PairingReadinessDiagnostics.IsAllowedReason(current.Message))
                return (current.Message, null);
            if (current is AuthenticationException) return ("receiver-tls-unverified", null);
            if (current is SocketException socket && socket.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
                return ("receiver-dns-unavailable", null);
        }
        return error switch
        {
            JsonException => ("receiver-json-unverified", null),
            DbException or DbUpdateException => ("receiver-persistence-unavailable", null),
            HttpRequestException => ("receiver-transport-unavailable", null),
            IOException => ("receiver-response-interrupted", null),
            _ => ("receiver-readiness-unverified", null)
        };
    }

    public static RatelDeskConnectorAuthentication Parse(RatelDeskConnectorAuthenticationDto? value,
        RatelDeskConnectorAuthentication? existing = null)
    {
        if (value is null && existing?.Mode == RatelDeskAuthenticationMode.PairedSystem) return existing;
        if (value is { Mode: "pairing", ManagedLinkId: { } id } && Guid.TryParseExact(id, "D", out _)) return new(RatelDeskAuthenticationMode.PairedSystem, id);
        throw new ArgumentException("invalid-connector-authentication-reference");
    }

    public static RatelDeskConnectorAuthenticationDto ToDto(RatelDeskConnectorAuthentication? value) => new("pairing", value?.ManagedLinkId);
}
