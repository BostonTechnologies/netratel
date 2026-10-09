using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Abstractions;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.Infrastructure.SystemPairing;
/// <summary>Read-only MCP connectivity projection; connection configuration is owned solely by pairing.</summary>
public sealed class PairingConnectivityService(OrchestratorDbContext db, PairingService pairing) : IM2MConnectivityService
{
    public async Task<M2MConnectivitySettingsDto> GetAsync(CancellationToken ct)
    {
        var pair = await db.Set<SystemPairRecord>().AsNoTracking().Where(x => x.DeletedAtUtc == null && x.ProtectedOutboundSecret != null).OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var peer = pair is null ? null : JsonSerializer.Deserialize<PairingMetadata>(pair.PeerMetadataJson, PairingTransport.Json);
        return new(pair is not null, peer?.ApiOrigin, peer is null ? null : "rateldesk.services", peer?.Name);
    }
    public async Task<M2MConnectivityTestResultDto> TestAsync(M2MConnectivityTestRequestDto request, CancellationToken ct)
    {
        if (request.SettingsOverride is not null) throw new ArgumentException("Pair and configure the connection on the integration account page.");
        var rows = await db.Set<PairingConnectionRecord>().AsNoTracking().Where(x => x.Active && x.DeletedAtUtc == null).Take(10).ToArrayAsync(ct); var results = new List<M2MConnectivityProbeResultDto>();
        foreach (var row in rows)
        {
            try { var test = await pairing.TestAsync(row.PairId, row.Id, PairingAuthority.Retained(row.AdministratorId), ct); results.Add(new("ConnectionAccess", row.Id.ToString("D"), test.Success ? TrafficLight.Green : TrafficLight.Red, null, null, test.Message, "RatelDesk", test.TestedAtUtc)); }
            catch (PairingException error) { results.Add(new("ConnectionAccess", row.Id.ToString("D"), TrafficLight.Red, error.StatusCode, null, error.Message, "RatelDesk", DateTimeOffset.UtcNow)); }
        }
        if (rows.Length == 0) results.Add(new("ConnectionAccess", "", TrafficLight.Amber, null, null, "No configured system connection.", null, DateTimeOffset.UtcNow));
        return new(results);
    }
}
