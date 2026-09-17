using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.API.Services.Terminal;

public sealed class ClientTerminalSettingsService
{
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<ClientTerminalSettingsService> _logger;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    public ClientTerminalSettingsService(OrchestratorDbContext db, ILogger<ClientTerminalSettingsService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, TerminalTransportKind?>> GetOverridesAsync(
        IEnumerable<string> clientIdentities,
        CancellationToken ct)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        var normalized = clientIdentities
            .Select(NormalizeIdentity)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return new Dictionary<string, TerminalTransportKind?>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, TerminalTransportKind?>(StringComparer.OrdinalIgnoreCase);
        var conn = _db.Database.GetDbConnection();
        await using var command = conn.CreateCommand();
        command.CommandText = """
            select "ClientIdentity", "TerminalTransportOverride"
            from "ClientTerminalSettings"
            where "ClientIdentity" = any(@ids)
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "ids";
        parameter.Value = normalized;
        command.Parameters.Add(parameter);

        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var identity = reader.GetString(0);
            var value = reader.IsDBNull(1) ? null : reader.GetString(1);
            result[identity] = ParseTransport(value);
        }

        return result;
    }

    public async Task<TerminalTransportKind?> GetOverrideAsync(string clientIdentity, CancellationToken ct)
    {
        var map = await GetOverridesAsync(new[] { clientIdentity }, ct).ConfigureAwait(false);
        return map.TryGetValue(NormalizeIdentity(clientIdentity), out var value) ? value : null;
    }

    public async Task SetOverrideAsync(string clientIdentity, TerminalTransportKind? transport, CancellationToken ct)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        var normalized = NormalizeIdentity(clientIdentity);
        var value = transport?.ToString();
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into "ClientTerminalSettings" ("ClientIdentity", "TerminalTransportOverride", "UpdatedAtUtc")
            values ({normalized}, {value}, now())
            on conflict ("ClientIdentity") do update set
                "TerminalTransportOverride" = excluded."TerminalTransportOverride",
                "UpdatedAtUtc" = now()
            """, ct).ConfigureAwait(false);
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_ensured)
        {
            return;
        }

        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured)
            {
                return;
            }

            await _db.Database.ExecuteSqlRawAsync("""
                create table if not exists "ClientTerminalSettings" (
                    "ClientIdentity" text primary key,
                    "TerminalTransportOverride" text null,
                    "UpdatedAtUtc" timestamp with time zone not null default now()
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Terminal] Failed to ensure ClientTerminalSettings table.");
            throw;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private static TerminalTransportKind? ParseTransport(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<TerminalTransportKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    public static string NormalizeIdentity(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
}
