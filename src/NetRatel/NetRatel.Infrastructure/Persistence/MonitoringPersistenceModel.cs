using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

internal static class MonitoringPersistenceModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<MonitoringTenantConfigurationRecord>(entity =>
        {
            entity.ToTable("MonitoringTenantConfigurations");
            entity.HasKey(row => row.TenantId);
            entity.Property(row => row.TenantId).ValueGeneratedNever();
            entity.Property(row => row.Revision).IsConcurrencyToken();
        });
        model.Entity<MonitoringRuleRecord>(entity =>
        {
            entity.ToTable("MonitoringRules"); entity.HasKey(row => new { row.TenantId, row.RuleId });
            entity.Property(row => row.DefinitionJson).HasColumnType("jsonb");
        });
        model.Entity<MonitoringGroupRecord>(entity =>
        {
            entity.ToTable("MonitoringGroups"); entity.HasKey(row => new { row.TenantId, row.GroupId });
            entity.Property(row => row.DefinitionJson).HasColumnType("jsonb");
        });
        model.Entity<MonitoringBypassRecord>(entity =>
        {
            entity.ToTable("MonitoringBypasses"); entity.HasKey(row => new { row.TenantId, row.BypassId });
            entity.Property(row => row.DefinitionJson).HasColumnType("jsonb");
            entity.HasIndex(row => new { row.TenantId, row.ExpiresAtUtc });
        });
        model.Entity<MonitoringEvidenceStreamRecord>(entity =>
        {
            entity.ToTable("MonitoringEvidenceStreams", table =>
            {
                table.HasCheckConstraint("CK_MonitoringEvidenceStreams_Ordinal",
                    """
                    "CommittedRegistrationOrdinal">=0
                    """);
            });
            entity.HasKey(row => new { row.TenantId, row.AgentId });
            entity.Property(row => row.CommittedRegistrationOrdinal).HasDefaultValue(0L);
            entity.Property(row => row.Revision).IsConcurrencyToken();
        });
        model.Entity<MonitoringEvidenceRegistrationCounterRecord>(entity =>
        {
            entity.ToTable("MonitoringEvidenceRegistrationCounters", table =>
            {
                table.HasCheckConstraint("CK_MonitoringEvidenceRegistrationCounters_Identity",
                    """
                    "TenantId">0 AND "AgentId"<>'00000000-0000-0000-0000-000000000000'::uuid AND "LastIssuedOrdinal">=0
                    """);
            });
            entity.HasKey(row => new { row.TenantId, row.AgentId });
            entity.Property(row => row.LastIssuedOrdinal).HasDefaultValue(0L);
        });
        model.Entity<MonitoringEvidenceRegistrationAttemptRecord>(entity =>
        {
            entity.ToTable("MonitoringEvidenceRegistrationAttempts", table =>
            {
                table.HasCheckConstraint("CK_MonitoringEvidenceRegistrationAttempts_Shape",
                    """
                    "TenantId">0 AND "AgentId"<>'00000000-0000-0000-0000-000000000000'::uuid AND "RegistrationId"<>'00000000-0000-0000-0000-000000000000'::uuid AND "ConnectionId"<>'00000000-0000-0000-0000-000000000000'::uuid AND "ConnectionEpoch">0 AND "RegistrationOrdinal">0 AND "Status" IN (1,2,3) AND "ExpiresAtUtc">"CreatedAtUtc" AND "RetainUntilUtc">="ExpiresAtUtc"
                    """);
            });
            entity.HasKey(row => new { row.TenantId, row.AgentId, row.RegistrationId });
            entity.HasIndex(row => new { row.TenantId, row.AgentId, row.RegistrationOrdinal }).IsUnique();
            entity.HasIndex(row => row.RetainUntilUtc);
        });
        model.Entity<MonitoringSeriesRecord>(entity =>
        {
            entity.ToTable("MonitoringSeries"); entity.HasKey(row => new { row.TenantId, row.RuleId, row.AgentId, row.ResourceKey });
            entity.Property(row => row.ResourceKey).HasMaxLength(MonitoringLimits.MaximumResourceKeyLength);
            entity.Property(row => row.StateRevision).IsConcurrencyToken();
            entity.Property(row => row.Phase).HasConversion<short>();
            entity.Property(row => row.EvidenceQuality).HasConversion<short>();
            entity.Property(row => row.StateJson).HasColumnType("jsonb");
            entity.HasIndex(row => new { row.TenantId, row.AgentId });
        });
        model.Entity<MonitoringOccurrenceRecord>(entity =>
        {
            entity.ToTable("MonitoringOccurrences"); entity.HasKey(row => row.OccurrenceId);
            entity.Property(row => row.OccurrenceId).ValueGeneratedNever();
            entity.Property(row => row.ResourceKey).HasMaxLength(MonitoringLimits.MaximumResourceKeyLength);
            entity.Property(row => row.OccurrenceJson).HasColumnType("jsonb");
            entity.HasIndex(row => new { row.TenantId, row.RuleId, row.AgentId, row.ResourceKey })
                .IsUnique().HasFilter("\"EndedAtUtc\" IS NULL").HasDatabaseName("UX_MonitoringOccurrence_OpenSeries");
            entity.HasIndex(row => row.RaisedEventId).IsUnique();
            entity.HasIndex(row => new { row.TenantId, row.EndedAtUtc });
        });
        model.Entity<MonitoringEventRecord>(entity =>
        {
            entity.ToTable("MonitoringEvents"); entity.HasKey(row => row.EventId);
            entity.Property(row => row.EventId).ValueGeneratedNever();
            entity.Property(row => row.EventJson).HasColumnType("jsonb");
            entity.HasIndex(row => new { row.TenantId, row.AtUtc, row.EventId });
            entity.HasOne<MonitoringOccurrenceRecord>().WithMany().HasForeignKey(row => row.OccurrenceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MonitoringFlowOutboxRecord>(entity =>
        {
            entity.ToTable("MonitoringFlowOutbox"); entity.HasKey(row => row.OutboxId);
            entity.Property(row => row.OutboxId).ValueGeneratedNever();
            entity.Property(row => row.StableFlowDispatchKey).HasMaxLength(256);
            entity.Property(row => row.IntentJson).HasColumnType("jsonb");
            entity.Property(row => row.OutcomeJson).HasColumnType("jsonb");
            entity.Property(row => row.Status).HasConversion<short>();
            entity.Property(row => row.Code).HasMaxLength(64);
            entity.Property(row => row.LeaseFence).IsConcurrencyToken();
            entity.HasIndex(row => row.StableFlowDispatchKey).IsUnique();
            entity.HasIndex(row => row.FlowRunId).IsUnique().HasFilter("\"FlowRunId\" IS NOT NULL");
            entity.HasIndex(row => new { row.TenantId, row.Status, row.NextAttemptAtUtc, row.LeaseExpiresAtUtc });
            entity.HasOne<MonitoringEventRecord>().WithMany().HasForeignKey(row => row.EventId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MonitoringAuditRecord>(entity =>
        {
            entity.ToTable("MonitoringAudits"); entity.HasKey(row => row.AuditId);
            entity.Property(row => row.AuditId).ValueGeneratedNever();
            entity.Property(row => row.EntityKind).HasMaxLength(32);
            entity.Property(row => row.Operation).HasMaxLength(64);
            entity.Property(row => row.Reason).HasMaxLength(MonitoringLimits.MaximumReasonLength);
            entity.Property(row => row.DetailsJson).HasColumnType("jsonb");
            entity.HasIndex(row => new { row.TenantId, row.AtUtc });
        });
    }
}
