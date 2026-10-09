using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.SystemPairing;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Application.RatelDesk;
namespace NetRatel.Infrastructure.SystemPairing;
public sealed partial class PairingService
{
    public static string[] ExpectedRatelDeskScopes(PairingMapping mapping) => new[]
    {
        mapping.CreateIncidents ? "rateldesk.incidents.create" : null,
        mapping.CreateIncidents ? "rateldesk.incident-receipts.read" : null,
        mapping.CreateIncidents ? "rateldesk.incident-targets.read" : null,
        mapping.RunAutomation ? "rateldesk.orchestration.callback" : null
    }.Where(x => x != null).Select(x => x!).Order(StringComparer.Ordinal).ToArray();
    private async Task ValidateRemoteCredentialAsync(SystemPairRecord pair, PairingMapping mapping, PairingBusinessCredential? credential, CancellationToken ct)
    {
        var expected = ExpectedRatelDeskScopes(mapping);
        if (expected.Length == 0) { if (credential is not null) throw new PairingException(422, "credential-unexpected", "No inbound RatelDesk capability was selected."); return; }
        var peer = Read<PairingMetadata>(pair.PeerMetadataJson); var installed = await identities.GetAsync(ct);
        if (credential is null || credential.ClientId is not { Length: >= 1 and <= 128 } || credential.ClientSecret is not { Length: >= 32 and <= 1024 } || credential.Scopes is null ||
            credential.TokenEndpoint != PairingTransport.Origin(peer.ApiOrigin) + "/connect/token" || string.IsNullOrWhiteSpace(credential.Issuer) || credential.Issuer.Length > 2048 ||
            string.IsNullOrWhiteSpace(credential.Audience) || credential.Audience.Length > 256 || !credential.Scopes.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal) ||
            (mapping.CreateIncidents ? credential.SourceInstanceId != installed.SourceInstanceId!.Value.ToString("D") || credential.SourceNamespaceId != mapping.Id.ToString("D") : credential.SourceInstanceId is not null || credential.SourceNamespaceId is not null))
            throw new PairingException(422, "business-credential-mismatch", "The peer credential does not match the selected connection, fixed token endpoint, producer and capabilities.");
    }
    private async Task<PairingConnectionRecord> PrepareAsync(SystemPairRecord pair, PairingMapping mapping, string owner,
        Guid operationId, long revision, PairingBusinessCredential? offered, bool receiving, CancellationToken ct)
    {
        if (mapping.PairId != pair.Id || operationId == Guid.Empty) throw new PairingException(422, "mapping-pair-mismatch", "The selected mapping belongs to a different system pairing.");
        await authority.RequireMappingAsync(mapping, pair.AdministratorId, ct); await authority.RequireMappingAsync(mapping, owner, ct);
        if (receiving) await ValidateRemoteCredentialAsync(pair, mapping, offered, ct);
        var json = Json(mapping); var fingerprint = Hash(Json(new PairingSaveRequest(operationId, revision, mapping, offered)));
        var row = await db.Set<PairingConnectionRecord>().SingleOrDefaultAsync(x => x.Id == mapping.Id, ct);
        if (row?.DeletedAtUtc is not null) throw new PairingException(410, "connection-deleted", "This connection was deleted. Create a new named mapping.");
        if (row is not null)
        {
            if (row.PairId != pair.Id) throw new PairingException(409, "connection-owner-conflict", "The connection ID is already owned by another pairing.");
            if (row.OperationId == operationId && row.Revision == revision)
            {
                if (receiving && row.SaveFingerprint != fingerprint || row.MappingJson != json) throw new PairingException(409, "save-payload-changed", "The same Save operation cannot change its submitted values.");
                return row;
            }
            if (revision <= row.Revision) throw new PairingException(409, "connection-revision-conflict", "The connection changed. Reload its current configuration before saving.");
            if (row.InboundPrincipalId is { } old) await principals.RevokeAsync(old, ct);
            row.InboundPrincipalId = null; row.ProtectedInboundCredential = null; row.ProtectedOutboundCredential = null; row.LastTestJson = null;
        }
        else { row = new() { Id = mapping.Id, PairId = pair.Id, CreatedAtUtc = clock.GetUtcNow() }; db.Add(row); }
        row.MappingJson = json; row.AdministratorId = owner; row.OperationId = operationId; row.Revision = revision; row.Active = false; row.SaveFingerprint = fingerprint;
        if (offered is not null) row.ProtectedOutboundCredential = Protect(row.Id + "/outbound-business", Json(offered));
        await db.SaveChangesAsync(ct);
        if (mapping.RunAutomation)
        {
            var tenant = int.Parse(mapping.NetRatelTenantId, CultureInfo.InvariantCulture); var resources = new PairingResourceConstraints { TenantId = mapping.NetRatelTenantId };
            var principal = await principals.CreateAsync(new(mapping.Name, tenant, pair.PeerInstanceId, mapping.RatelDeskOrganizationId,
                ServiceIdentityScopes.Business, Json(resources), mapping.Id.ToString("D"), Hash(json), revision), owner, true, ct);
            var runtime = (await settings.ResolveAsync(ct)).Identity; var source = await identities.GetAsync(ct);
            var credential = new PairingBusinessCredential(principal.Principal.ClientId, principal.ClientSecret, runtime.ApiBaseUrl.TrimEnd('/') + "/connect/token", runtime.Audience, runtime.Issuer,
                ServiceIdentityScopes.Business, source.SourceInstanceId!.Value.ToString("D"), mapping.Id.ToString("D"));
            row.InboundPrincipalId = principal.Principal.Id; row.ProtectedInboundCredential = Protect(row.Id + "/inbound-business", Json(credential)); await db.SaveChangesAsync(ct);
        }
        return row;
    }
    private PairingBusinessCredential? Inbound(PairingConnectionRecord row) => row.ProtectedInboundCredential is null ? null : Read<PairingBusinessCredential>(Unprotect(row.Id + "/inbound-business", row.ProtectedInboundCredential));
    public async Task<PairingSaveResponse> ReceiveSaveAsync(SystemPairRecord pair, Guid mappingId, PairingSaveRequest request, CancellationToken ct)
    {
        if (request?.Mapping is null || request.Revision <= 0 || mappingId != request.Mapping.Id) throw new PairingException(422, "mapping-id-mismatch", "The submitted connection ID differs from the selected connection.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(ct);
        var current = await db.Set<SystemPairRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == pair.Id && x.DeletedAtUtc == null && x.Revision == pair.Revision, ct);
        if (current is null) throw new PairingException(410, "pair-deleted", "This pairing was deleted while Save started.");
        var row = await PrepareAsync(current, request.Mapping, current.AdministratorId, request.OperationId, request.Revision, request.Credential, true, ct);
        if (!row.Active)
        {
            // The receiving Save authorizes this exact mapping. The initiating side
            // keeps its business credentials inactive until its authenticated Save response.
            await ProvisionConnectorAsync(current, row, ct);
            row.Active = true; if (row.InboundPrincipalId is { } principal) await principals.ActivateAsync(principal, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(request.Mapping, Inbound(row));
    }
    public async Task<PairingConnectionDto> SaveAsync(string pairId, Guid mappingId, PairingMapping mapping, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (mapping is null || mapping.Id != mappingId) throw new PairingException(422, "mapping-id-mismatch", "The submitted connection ID differs from the selected connection.");
        var pair = await HumanPairAsync(pairId, actor, ct);
        if (pair.ProtectedOutboundSecret is null) throw new PairingException(409, "pair-incomplete", "Complete Pair & connect before saving the mapping.");
        var actorId = PairingAuthority.ActorId(actor);
        await StageDraftAsync(pair, mapping, actorId, ct);
        await authority.RequireMappingAsync(mapping, actorId, ct); await authority.RequireMappingAsync(mapping, pair.AdministratorId, ct);
        PairingConnectionRecord row;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAsync(ct);
            var existing = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == mappingId, ct);
            var matching = existing is not null && existing.MappingJson == Json(mapping) && existing.DeletedAtUtc is null;
            var retry = matching && existing!.OperationId != Guid.Empty;
            if (retry && mapping.RunAutomation && (existing!.InboundPrincipalId is not { } currentPrincipal ||
                !await db.Set<ServicePrincipalSecret>().AsNoTracking().AnyAsync(x => x.ServicePrincipalId == currentPrincipal && x.Status != "revoked" && x.ExpiresAtUtc > clock.GetUtcNow(), ct))) retry = false;
            row = await PrepareAsync(pair, mapping, actorId, retry ? existing!.OperationId : Guid.NewGuid(), retry ? existing!.Revision : (existing?.Revision ?? 0) + 1, null, false, ct);
            row.Active = false; await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        var savedOperation = row.OperationId; var savedRevision = row.Revision;
        var installed = await identities.GetAsync(ct); var peer = Read<PairingMetadata>(pair.PeerMetadataJson);
        var response = await transport.SendAsync<PairingSaveResponse>(peer.ApiOrigin, HttpMethod.Put, "/mappings/" + mappingId.ToString("D"),
            new PairingSaveRequest(row.OperationId, row.Revision, mapping, Inbound(row)), Unprotect(pair.Id + "/outbound", pair.ProtectedOutboundSecret), installed.InstanceId.ToString("D"), ct, callerSecretHash: pair.InboundSecretHash);
        if (response?.Mapping is null || Json(response.Mapping) != Json(mapping)) throw new PairingException(502, "saved-mapping-mismatch", "The peer returned a different tenant mapping or capabilities.");
        await ValidateRemoteCredentialAsync(pair, mapping, response.Credential, ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAsync(ct); db.ChangeTracker.Clear();
            var currentPair = await db.Set<SystemPairRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == pair.Id && x.DeletedAtUtc == null && x.Revision == pair.Revision, ct);
            row = await db.Set<PairingConnectionRecord>().SingleAsync(x => x.Id == mappingId, ct);
            if (currentPair is null || row.DeletedAtUtc is not null || row.OperationId != savedOperation || row.Revision != savedRevision || row.MappingJson != Json(mapping)) throw new PairingException(410, "connection-deleted", "The selected connection was changed or deleted while Save completed.");
            if (response.Credential is not null) row.ProtectedOutboundCredential = Protect(row.Id + "/outbound-business", Json(response.Credential));
            await ProvisionConnectorAsync(currentPair, row, ct);
            row.Active = true; if (row.InboundPrincipalId is { } principal) await principals.ActivateAsync(principal, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        // Validate real authenticated access automatically after both sides activate this exact Save.
        try
        {
            var validation = await ReceiveTestAsync(pair, mappingId, ct);
            if (validation.Success) validation = await transport.SendAsync<PairingTestResult>(peer.ApiOrigin, HttpMethod.Post, "/mappings/" + mappingId.ToString("D") + "/test", null,
                Unprotect(pair.Id + "/outbound", pair.ProtectedOutboundSecret), installed.InstanceId.ToString("D"), ct, callerSecretHash: pair.InboundSecretHash);
            if (!validation.Success) throw new PairingException(422, "saved-access-unavailable", validation.Message, validation.Diagnostic);
        }
        catch (Exception error) when (error is PairingException or HttpRequestException or IOException or OperationCanceledException)
        {
            await db.Set<PairingConnectionRecord>().Where(x => x.Id == mappingId && x.DeletedAtUtc == null && x.OperationId == savedOperation && x.Revision == savedRevision).ExecuteUpdateAsync(x => x.SetProperty(y => y.Active, false), CancellationToken.None);
            throw;
        }
        await RequireUnchangedAsync(pair, row.Id, savedOperation, savedRevision, ct);
        return ToDto(pair, row);
    }
    private async Task StageDraftAsync(SystemPairRecord pair, PairingMapping mapping, string owner, CancellationToken ct)
    {
        PairingAuthority.RequireShape(mapping);
        if (mapping.PairId != pair.Id) throw new PairingException(422, "mapping-pair-mismatch", "The selected mapping belongs to a different system pairing.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(ct); db.ChangeTracker.Clear();
        if (!await db.Set<SystemPairRecord>().AsNoTracking().AnyAsync(x => x.Id == pair.Id && x.Revision == pair.Revision && x.DeletedAtUtc == null, ct)) throw new PairingException(410, "pair-deleted", "This system pairing was changed or deleted.");
        var row = await db.Set<PairingConnectionRecord>().SingleOrDefaultAsync(x => x.Id == mapping.Id, ct);
        if (row?.DeletedAtUtc is not null) throw new PairingException(410, "connection-deleted", "This connection was deleted. Create a new named mapping.");
        if (row is not null)
        {
            if (row.PairId != pair.Id) throw new PairingException(409, "connection-owner-conflict", "The connection belongs to another system pairing.");
            var old = Read<PairingMapping>(row.MappingJson);
            if (row.AdministratorId != owner && !await authority.CanManageAsync(owner, int.Parse(old.NetRatelTenantId, CultureInfo.InvariantCulture), false, false, ct)) throw new PairingException(403, "mapping-not-authorized", "Integration management permission is required for the current tenant mapping.");
            if (row.MappingJson == Json(mapping)) return;
            if (row.InboundPrincipalId is { } principal) await principals.RevokeAsync(principal, ct);
            row.InboundPrincipalId = null; row.ProtectedInboundCredential = null; row.ProtectedOutboundCredential = null; row.LastTestJson = null;
        }
        else { row = new() { Id = mapping.Id, PairId = pair.Id, CreatedAtUtc = clock.GetUtcNow() }; db.Add(row); }
        // Persist only bounded nonsecret form values before policy/peer validation, so refresh keeps a failed draft.
        row.MappingJson = Json(mapping); row.AdministratorId = owner; row.Active = false; row.OperationId = Guid.Empty; row.SaveFingerprint = ""; row.Revision++;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private async Task RequireUnchangedAsync(SystemPairRecord pair, Guid id, Guid operation, long revision, CancellationToken ct)
    {
        if (!await db.Set<SystemPairRecord>().AsNoTracking().AnyAsync(x => x.Id == pair.Id && x.Revision == pair.Revision && x.DeletedAtUtc == null, ct) ||
            !await db.Set<PairingConnectionRecord>().AsNoTracking().AnyAsync(x => x.Id == id && x.PairId == pair.Id && x.OperationId == operation && x.Revision == revision && x.DeletedAtUtc == null && x.Active, ct))
            throw new PairingException(410, "connection-changed", "This connection was changed or deleted while authenticated validation completed.");
    }
    private async Task ProvisionConnectorAsync(SystemPairRecord pair, PairingConnectionRecord row, CancellationToken ct)
    {
        var mapping = Read<PairingMapping>(row.MappingJson); var tenant = int.Parse(mapping.NetRatelTenantId, CultureInfo.InvariantCulture);
        var existing = await connectors.GetAsync(tenant, mapping.Id, ct);
        if (!mapping.CreateIncidents)
        {
            if (existing is not null && existing.Configuration.Enabled) _ = await connectors.SaveAsync(existing with { Revision = existing.Revision + 1, RowVersion = existing.RowVersion + 1, Configuration = existing.Configuration with { Enabled = false }, Readiness = null }, existing.RowVersion, ct);
            return;
        }
        var peer = Read<PairingMetadata>(pair.PeerMetadataJson);
        var config = new RatelDeskConnectorConfiguration(mapping.Name, peer.ApiOrigin, mapping.RatelDeskOrganizationId, mapping.RatelDeskCustomerId!, null, [], new(), true);
        var state = new RatelDeskConnectorState(mapping.Id, tenant, (existing?.Revision ?? 0) + 1, (existing?.RowVersion ?? 0) + 1, row.AdministratorId,
            config, null, 0, new(RatelDeskAuthenticationMode.PairedSystem, mapping.Id.ToString("D")), null);
        if (!await connectors.SaveAsync(state, existing?.RowVersion ?? 0, ct)) throw new PairingException(409, "connector-revision-conflict", "The Flow connector changed while this mapping was saved. Retry the same connection.");
    }
    public async Task<PairingTestResult> ReceiveTestAsync(SystemPairRecord pair, Guid id, CancellationToken ct)
    {
        var row = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PairId == pair.Id && x.Active && x.DeletedAtUtc == null, ct) ?? throw new PairingException(409, "connection-not-configured", "Save this connection's tenant mapping and capabilities first.");
        var mapping = Read<PairingMapping>(row.MappingJson); await authority.RequireMappingAsync(mapping, pair.AdministratorId, ct); await authority.RequireMappingAsync(mapping, row.AdministratorId, ct);
        if (mapping.RunAutomation)
        {
            var credential = Inbound(row);
            var current = credential is null ? null : await principals.AuthenticateClientAsync(credential.ClientId, credential.ClientSecret, ct);
            if (current is null || !await principals.CanIssueScopesAsync(current, ServiceIdentityScopes.Business, ct)) throw new PairingException(403, "automation-binding-unavailable", "The saved automation credential has expired, was revoked, or is no longer authorized.");
        }
        if (mapping.CreateIncidents)
        {
            var connector = await connectors.GetAsync(int.Parse(mapping.NetRatelTenantId, CultureInfo.InvariantCulture), id, ct);
            if (connector is null) throw new PairingException(409, "incident-connector-unavailable", "The saved incident connection is unavailable. Save this mapping again.");
            var validation = await receiver.TestAsync(connector, ct);
            await RequireUnchangedAsync(pair, id, row.OperationId, row.Revision, ct);
            if (!validation.AutomaticDeliveryAvailable)
            {
                await db.Set<PairingConnectionRecord>().Where(x => x.Id == id && x.DeletedAtUtc == null && x.OperationId == row.OperationId && x.Revision == row.Revision).ExecuteUpdateAsync(x => x.SetProperty(y => y.Active, false), ct);
                return new(false, validation.Diagnostic is { } diagnostic && PairingReadinessDiagnostics.IsValid(diagnostic)
                    ? PairingReadinessDiagnostics.Describe(diagnostic)
                    : "The saved incident receiver could not be validated. Check the server reference, then retry this connection.",
                    clock.GetUtcNow(), validation.Diagnostic);
            }
        }
        await RequireUnchangedAsync(pair, id, row.OperationId, row.Revision, ct);
        return new(true, "Authenticated access and the saved tenant mapping/capabilities are current.", clock.GetUtcNow());
    }
    public async Task<PairingTestResult> TestAsync(string pairId, Guid id, ClaimsPrincipal actor, CancellationToken ct)
    {
        var pair = await HumanPairAsync(pairId, actor, ct);
        var row = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PairId == pairId && x.Active && x.DeletedAtUtc == null, ct) ?? throw new PairingException(409, "connection-not-configured", "Save the tenant mapping and capabilities first.");
        var peer = Read<PairingMetadata>(pair.PeerMetadataJson); var installed = await identities.GetAsync(ct); PairingTestResult current;
        try
        {
            current = await ReceiveTestAsync(pair, id, ct);
            if (current.Success) current = await transport.SendAsync<PairingTestResult>(peer.ApiOrigin, HttpMethod.Post, "/mappings/" + id.ToString("D") + "/test", null,
                Unprotect(pair.Id + "/outbound", pair.ProtectedOutboundSecret!), installed.InstanceId.ToString("D"), ct, callerSecretHash: pair.InboundSecretHash);
        }
        catch (PairingException error) { current = new(false, error.Diagnostic is { } diagnostic ? PairingReadinessDiagnostics.Describe(diagnostic) : error.Message,
            clock.GetUtcNow(), error.Diagnostic); }
        var stillCurrent = await db.Set<SystemPairRecord>().AsNoTracking().AnyAsync(x => x.Id == pair.Id && x.Revision == pair.Revision && x.DeletedAtUtc == null, ct);
        var updated = stillCurrent ? await db.Set<PairingConnectionRecord>().Where(x => x.Id == id && x.DeletedAtUtc == null && x.OperationId == row.OperationId && x.Revision == row.Revision)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.LastTestJson, Json(current)), ct) : 0;
        if (updated != 1) throw new PairingException(410, "connection-changed", "This connection was changed or deleted while Test completed.");
        return current;
    }
}
