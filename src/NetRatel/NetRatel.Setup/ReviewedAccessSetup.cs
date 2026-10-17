using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.AgentClient;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Setup;

public sealed partial class SetupSession
{
    private bool _retainReviewedPending;
    private ReviewedMcpAccessProposal? _reviewedProposal;
    private string? _reviewedStateFile, _reviewedStateHash;
    private ReviewedSetupRecord? _reviewedState;
    public string? ReviewedSavedCredentialId => _reviewedState?.SavedCredentialId;
    public DateTimeOffset? ReviewedCredentialExpiresAtUtc => _reviewedProposal?.ExpiresAtUtc;
    private sealed record ReviewedSetupRecord(string Origin, string Resource, string Destination, string Profile,
        ReviewedMcpAccessProposal Proposal, IntegrationPairingStartResponse? Pairing, string? SavedCredentialId = null, string? SavedProfileHash = null)
    { public override string ToString() => $"Reviewed setup {Proposal.RequestId} (protected pairing material redacted)"; }

    public async Task PrepareReviewedAccessAsync(ReviewedMcpAccessProposal proposal, AgentClientConfiguration requestingClient,
        string stateFile, CancellationToken ct)
    {
        EnsureDistinctReviewedState(stateFile);
        if (Surface != ConfigurationSurface.HttpMcpClient || _origin is null || proposal.Action != "create" ||
            requestingClient.CredentialPurpose != "HttpMcp" || requestingClient.AuthenticationMode != "IntegrationCredential" ||
            !McpResourceUri.Equivalent(requestingClient.ApiBaseUrl, CanonicalTarget))
            throw new AgentClientValidationException("Reviewed creation requires an existing protected local HTTP MCP profile bound to the same resource.");
        ConfigurationFileStorage.EnsureSafePath(stateFile);
        var record = new ReviewedSetupRecord(_origin.AbsoluteUri, CanonicalTarget, Path, ProfileName, proposal, null);
        if (File.Exists(stateFile))
        {
            var previous = JsonSerializer.Deserialize<ReviewedSetupRecord>(ConfigurationFileStorage.ReadText(stateFile, secret: true), AgentClientConfiguration.JsonOptions)
                ?? throw new AgentClientValidationException("The saved access request is invalid.");
            if (previous.Origin != record.Origin || previous.Resource != record.Resource || previous.Destination != record.Destination ||
                JsonSerializer.Serialize(previous.Proposal) != JsonSerializer.Serialize(proposal))
                throw new AgentClientValidationException("The saved access proposal differs. Resume it or use a separate request and state file.");
            record = previous;
        }
        else ConfigurationFileStorage.WriteAtomic(stateFile, JsonSerializer.Serialize(record, AgentClientConfiguration.JsonOptions));
        if (record.Pairing is null)
        {
            var pendingHash = ConfigurationFileStorage.Hash(stateFile);
            var token = await GetVerificationTokenAsync(requestingClient, ct);
            using var message = new HttpRequestMessage(HttpMethod.Post, CanonicalTarget.TrimEnd('/') + "/setup/access/start")
            { Content = JsonContent.Create(proposal) };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode) throw new AgentClientValidationException("The reviewed setup request was not admitted. Inspect the same requestId before retrying; no capability or secret is printed.");
            var paired = await response.Content.ReadFromJsonAsync<IntegrationPairingStartResponse>(cancellationToken: ct)
                ?? throw new AgentClientValidationException("The protected setup response was empty.");
            ValidateTrustedLink(paired.VerificationUri, _origin);
            if (paired.RequestId != Guid.Parse(proposal.RequestId).ToString("N") || ValidateDiscoveredOrigin(paired.PublicOrigin) != _origin)
                throw new AgentClientValidationException("The protected setup response did not match the requested instance and requestId.");
            record = record with { Pairing = paired };
            ConfigurationFileStorage.WriteAtomic(stateFile, JsonSerializer.Serialize(record, AgentClientConfiguration.JsonOptions), replace: true, expectedHash: pendingHash);
        }
        Pairing = record.Pairing; PairingStatus = "pending"; _retainReviewedPending = true; _reviewedProposal = record.Proposal;
        _reviewedState = record; _reviewedStateFile = stateFile; _reviewedStateHash = ConfigurationFileStorage.Hash(stateFile);
    }

    public async Task RestoreReviewedAccessAsync(string stateFile, CancellationToken ct)
    {
        EnsureDistinctReviewedState(stateFile);
        var record = JsonSerializer.Deserialize<ReviewedSetupRecord>(ConfigurationFileStorage.ReadText(stateFile, secret: true), AgentClientConfiguration.JsonOptions)
            ?? throw new AgentClientValidationException("The saved access request is invalid.");
        if (record.Pairing is null) throw new AgentClientValidationException("Preparation was interrupted before capability receipt. Rerun prepare with the same request, client and state file.");
        if (record.Destination != Path || record.Profile != ProfileName || Surface != ConfigurationSurface.HttpMcpClient)
            throw new AgentClientValidationException("Resume must use the destination profile saved by prepare.");
        await DiscoverAsync(record.Origin, ct);
        if (!McpResourceUri.Equivalent(record.Resource, CanonicalTarget)) throw new AgentClientValidationException("The reviewed resource changed. Request a fresh independent approval.");
        ValidateTrustedLink(record.Pairing.VerificationUri, _origin!);
        if (record.SavedCredentialId is not null && (record.SavedProfileHash is null || record.SavedProfileHash != ReviewedProfileHash()))
            throw new AgentClientValidationException("The saved profile changed after its reviewed save receipt. Inspect the existing credential and profile before resuming; no credential was reissued.");
        Pairing = record.Pairing; PairingStatus = "pending"; _retainReviewedPending = true; _reviewedProposal = record.Proposal;
        _reviewedState = record; _reviewedStateFile = stateFile; _reviewedStateHash = ConfigurationFileStorage.Hash(stateFile);
    }
    private void EnsureDistinctReviewedState(string stateFile)
    {
        ConfigurationFileStorage.EnsureSafePath(stateFile);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(System.IO.Path.GetFullPath(stateFile), System.IO.Path.GetFullPath(Path), comparison))
            throw new AgentClientValidationException("The protected state file must be different from the destination configuration file.");
    }
    private string ReviewedProfileHash()
    {
        if (!File.Exists(Path) || string.IsNullOrWhiteSpace(Configuration.IntegrationCredential))
            throw new AgentClientValidationException("The saved reviewed profile is unavailable. Inspect the existing credential before resuming.");
        var protectedCredentialHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Configuration.IntegrationCredential)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ConfigurationFileStorage.Hash(Path) + ":" + protectedCredentialHash)));
    }
    private void CheckpointReviewedSave()
    {
        if (_reviewedState is null || _reviewedStateFile is null || Delivery is null) return;
        EnsureDistinctReviewedState(_reviewedStateFile);
        var hash = ReviewedProfileHash();
        if (_reviewedState.SavedCredentialId is not null)
        {
            if (_reviewedState.SavedCredentialId != Delivery.CredentialId || _reviewedState.SavedProfileHash != hash)
                throw new AgentClientValidationException("The saved reviewed profile changed. Inspect it before acknowledging delivery.");
            return;
        }
        var updated = _reviewedState with { SavedCredentialId = Delivery.CredentialId, SavedProfileHash = hash };
        ConfigurationFileStorage.WriteAtomic(_reviewedStateFile, JsonSerializer.Serialize(updated, AgentClientConfiguration.JsonOptions), replace: true, expectedHash: _reviewedStateHash);
        _reviewedState = updated; _reviewedStateHash = ConfigurationFileStorage.Hash(_reviewedStateFile);
    }
    public async Task<bool> ReconcileReviewedSavedAsync(CancellationToken ct)
    {
        if (_reviewedState?.SavedCredentialId is not { } credentialId || _reviewedStateFile is null || Pairing is null || _origin is null) return false;
        EnsureDistinctReviewedState(_reviewedStateFile);
        if (_reviewedState.SavedProfileHash != ReviewedProfileHash())
            throw new AgentClientValidationException("The saved profile changed after its reviewed save receipt. Inspect it before resuming.");
        using var response = await _http.PostAsJsonAsync(Endpoint(_origin, "/api/integration-pairing/v1/acknowledge"), new { deviceCapability = Pairing.DeviceCapability, credentialId }, ct);
        response.EnsureSuccessStatusCode();
        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!status.RootElement.TryGetProperty("status", out var value) || value.GetString() != "consumed")
            throw new AgentClientValidationException("The saved reviewed credential has not been acknowledged. Inspect this same request before retrying.");
        _saved = _acknowledged = true; PairingStatus = "consumed";
        return true;
    }

}
