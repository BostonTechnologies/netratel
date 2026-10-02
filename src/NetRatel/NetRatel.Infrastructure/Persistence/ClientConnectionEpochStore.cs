using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Presence;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>One atomic PostgreSQL statement allocates epochs above both previously issued and cached service fences.</summary>
public sealed class ClientConnectionEpochStore(IServiceScopeFactory scopeFactory) : IClientConnectionEpochStore
{
    public async Task<long> AllocateAsync(ClientKey client, long minimumEpoch, CancellationToken cancellationToken)
    {
        if (!client.IsValid) throw new ArgumentException("A valid authenticated client is required.", nameof(client));
        if (minimumEpoch < 0) throw new ArgumentOutOfRangeException(nameof(minimumEpoch));
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO "ClientConnectionEpochs" ("TenantId", "AgentId", "LastIssuedEpoch")
            VALUES (@tenant, @agent, GREATEST(@minimum,
                COALESCE((SELECT "ConnectionEpoch" FROM "ClientServicesSnapshots"
                    WHERE "TenantId" = @tenant AND "AgentId" = @agent), 0)) + 1)
            ON CONFLICT ("TenantId", "AgentId") DO UPDATE SET "LastIssuedEpoch" =
                GREATEST("ClientConnectionEpochs"."LastIssuedEpoch", @minimum,
                    COALESCE((SELECT "ConnectionEpoch" FROM "ClientServicesSnapshots"
                        WHERE "TenantId" = @tenant AND "AgentId" = @agent), 0)) + 1
            RETURNING "LastIssuedEpoch"
            """;
        var tenant = command.CreateParameter();
        tenant.ParameterName = "tenant"; tenant.DbType = DbType.Int32; tenant.Value = client.TenantId;
        command.Parameters.Add(tenant);
        var agent = command.CreateParameter();
        agent.ParameterName = "agent"; agent.DbType = DbType.Guid; agent.Value = client.AgentId;
        command.Parameters.Add(agent);
        var minimum = command.CreateParameter();
        minimum.ParameterName = "minimum"; minimum.DbType = DbType.Int64; minimum.Value = minimumEpoch;
        command.Parameters.Add(minimum);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
}
