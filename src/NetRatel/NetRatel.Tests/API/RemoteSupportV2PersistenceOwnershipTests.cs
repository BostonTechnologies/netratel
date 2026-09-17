using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RemoteSupportV2PersistenceOwnershipTests
{
    [Fact]
    public void Current_gateway_endpoints_do_not_write_the_reserved_v2_lifecycle_schema()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "NetRatel", "NetRatel.API", "Endpoints", "RemoteAccess", "AgentRemoteSupportGatewayEndpoints.cs"));

        source.Should().NotContain("RemoteSupportSessions");
        source.Should().NotContain("RemoteSupportAuditEvents");
    }

    [Fact]
    public void Reserved_lifecycle_and_audit_schema_have_stable_table_and_key_shapes()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql("Host=localhost;Database=netratel_contract_tests;Username=contract;Password=contract")
            .Options;
        using var db = new OrchestratorDbContext(options);

        var session = db.Model.FindEntityType(typeof(RemoteSupportSessionRecord))!;
        var audit = db.Model.FindEntityType(typeof(RemoteSupportAuditEventRecord))!;
        var sessionIdempotencyKey = new[] { "TenantId", "AgentId", "OpenRequestId" };
        var auditSequenceKey = new[] { "RemoteSupportSessionId", "AuditSequence" };

        session.GetTableName().Should().Be("RemoteSupportSessions");
        session.GetIndexes().Should().Contain(index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(sessionIdempotencyKey));
        audit.GetTableName().Should().Be("RemoteSupportAuditEvents");
        audit.GetIndexes().Should().Contain(index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(auditSequenceKey));
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
}
