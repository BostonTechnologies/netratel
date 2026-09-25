using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;

namespace NetRatel.Infrastructure.Persistence;

public class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<BootstrapInitializationRecord> BootstrapInitializations => Set<BootstrapInitializationRecord>();
    public DbSet<M2MConnectivitySettings> M2MConnectivitySettings => Set<M2MConnectivitySettings>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<DevelopmentOperatorTargetGrant> DevelopmentOperatorTargetGrants => Set<DevelopmentOperatorTargetGrant>();
    public DbSet<DevelopmentOperatorAcceptedAuditRecord> DevelopmentOperatorAcceptedAudits => Set<DevelopmentOperatorAcceptedAuditRecord>();
    public DbSet<McpOperatorPolicyRecord> McpOperatorPolicies => Set<McpOperatorPolicyRecord>();
    public DbSet<McpOperatorTargetProfileRecord> McpOperatorTargetProfiles => Set<McpOperatorTargetProfileRecord>();
    public DbSet<McpOperatorPolicyChangeAuditRecord> McpOperatorPolicyChangeAudits => Set<McpOperatorPolicyChangeAuditRecord>();
    public DbSet<McpOperatorConfirmationPlanRecord> McpOperatorConfirmationPlans => Set<McpOperatorConfirmationPlanRecord>();
    public DbSet<McpOperatorIdempotencyRecord> McpOperatorIdempotencyRecords => Set<McpOperatorIdempotencyRecord>();
    public DbSet<McpOperatorAcceptedAuditRecord> McpOperatorAcceptedAudits => Set<McpOperatorAcceptedAuditRecord>();
    public DbSet<McpOperatorTerminalSessionRecord> McpOperatorTerminalSessions => Set<McpOperatorTerminalSessionRecord>();
    public DbSet<McpOperatorTerminalSessionAuditRecord> McpOperatorTerminalSessionAudits => Set<McpOperatorTerminalSessionAuditRecord>();
    public DbSet<McpOperatorTerminalActionRecord> McpOperatorTerminalActions => Set<McpOperatorTerminalActionRecord>();
    public DbSet<McpOperatorCommandRecord> McpOperatorCommands => Set<McpOperatorCommandRecord>();
    public DbSet<McpOperatorScriptRecord> McpOperatorScripts => Set<McpOperatorScriptRecord>();
    public DbSet<McpOperatorScriptVersionRecord> McpOperatorScriptVersions => Set<McpOperatorScriptVersionRecord>();
    public DbSet<McpOperatorJobRecord> McpOperatorJobs => Set<McpOperatorJobRecord>();
    public DbSet<McpOperatorJobAuditRecord> McpOperatorJobAudits => Set<McpOperatorJobAuditRecord>();
    public DbSet<McpOperatorJobRunRecord> McpOperatorJobRuns => Set<McpOperatorJobRunRecord>();
    public DbSet<McpOperatorJobRunAuditRecord> McpOperatorJobRunAudits => Set<McpOperatorJobRunAuditRecord>();
    public DbSet<McpOperatorTaskRecord> McpOperatorTasks => Set<McpOperatorTaskRecord>();
    public DbSet<McpOperatorTaskAuditRecord> McpOperatorTaskAudits => Set<McpOperatorTaskAuditRecord>();
    public DbSet<McpOperatorRequestRecord> McpOperatorRequests => Set<McpOperatorRequestRecord>();
    public DbSet<McpOperatorRequestAuditRecord> McpOperatorRequestAudits => Set<McpOperatorRequestAuditRecord>();
    public DbSet<McpOperatorFileArtifactRecord> McpOperatorFileArtifacts => Set<McpOperatorFileArtifactRecord>();
    public DbSet<DevelopmentMcpFileArtifactRecord> DevelopmentMcpFileArtifacts => Set<DevelopmentMcpFileArtifactRecord>();
    public DbSet<DevelopmentMcpScriptRecord> DevelopmentMcpScripts => Set<DevelopmentMcpScriptRecord>();
    public DbSet<DevelopmentMcpMarkerJobRecord> DevelopmentMcpMarkerJobs => Set<DevelopmentMcpMarkerJobRecord>();
    public DbSet<PrimaryClientAgentBinding> PrimaryClientAgentBindings => Set<PrimaryClientAgentBinding>();
    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();
    public DbSet<AgentRefreshToken> AgentRefreshTokens => Set<AgentRefreshToken>();
    public DbSet<AgentNonceLog> AgentNonceLogs => Set<AgentNonceLog>();
    public DbSet<AgentTokenEvent> AgentTokenEvents => Set<AgentTokenEvent>();
    public DbSet<OidcSigningKey> OidcSigningKeys => Set<OidcSigningKey>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<SecretRecord> Secrets => Set<SecretRecord>();
    public DbSet<ScriptDefinition> Scripts => Set<ScriptDefinition>();
    public DbSet<ScriptParameterDefinition> ScriptParameters => Set<ScriptParameterDefinition>();
    public DbSet<JobDefinition> Jobs => Set<JobDefinition>();
    public DbSet<JobParameterDefinition> JobParameters => Set<JobParameterDefinition>();
    public DbSet<JobStepDefinition> JobSteps => Set<JobStepDefinition>();
    public DbSet<JobRunRecord> JobRuns => Set<JobRunRecord>();
    public DbSet<JobStepRunRecord> JobStepRuns => Set<JobStepRunRecord>();
    public DbSet<JobTaskActivityRecord> JobTaskActivities => Set<JobTaskActivityRecord>();
    public DbSet<JobTaskLogRecord> JobTaskLogs => Set<JobTaskLogRecord>();
    public DbSet<RequestRecord> Requests => Set<RequestRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OutboxProcessedEvent> OutboxProcessedEvents => Set<OutboxProcessedEvent>();
    public DbSet<OutboxReadReceipt> OutboxReadReceipts => Set<OutboxReadReceipt>();
    public DbSet<CommandInboxReceipt> CommandInboxReceipts => Set<CommandInboxReceipt>();
    public DbSet<CommandIntentEventRecord> CommandIntentEvents => Set<CommandIntentEventRecord>();
    public DbSet<CommandOutboxRecord> CommandOutbox => Set<CommandOutboxRecord>();
    public DbSet<JobShadowObservationRecord> JobShadowObservations => Set<JobShadowObservationRecord>();
    public DbSet<ClientWindowsSessionSnapshot> ClientWindowsSessionSnapshots => Set<ClientWindowsSessionSnapshot>();
    public DbSet<ClientWindowsSessionInventoryRefresh> ClientWindowsSessionInventoryRefreshes => Set<ClientWindowsSessionInventoryRefresh>();
    public DbSet<RemoteSupportTargetSelectionEvent> RemoteSupportTargetSelectionEvents => Set<RemoteSupportTargetSelectionEvent>();
    public DbSet<RemoteSupportSessionRecord> RemoteSupportSessions => Set<RemoteSupportSessionRecord>();
    public DbSet<RemoteSupportAuditEventRecord> RemoteSupportAuditEvents => Set<RemoteSupportAuditEventRecord>();

    public DbSet<ClientUpdateReleaseRecord> ClientUpdateReleases => Set<ClientUpdateReleaseRecord>();
    public DbSet<ClientUpdateAttemptRecord> ClientUpdateAttempts => Set<ClientUpdateAttemptRecord>();
    public DbSet<AgentClientUpdateStateRecord> AgentClientUpdateStates => Set<AgentClientUpdateStateRecord>();
    public DbSet<ClientUpdateCatalogRevision> ClientUpdateCatalogRevisions => Set<ClientUpdateCatalogRevision>();
    public DbSet<ClientReleaseImportOperation> ClientReleaseImportOperations => Set<ClientReleaseImportOperation>();
    public DbSet<ClientReleaseImportAsset> ClientReleaseImportAssets => Set<ClientReleaseImportAsset>();
    public DbSet<ClientReleaseAutomationSettings> ClientReleaseAutomationSettings => Set<ClientReleaseAutomationSettings>();
    public DbSet<ClientInstallGrant> ClientInstallGrants => Set<ClientInstallGrant>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AdvanceScriptSourceRevisions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AdvanceScriptSourceRevisions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AdvanceScriptSourceRevisions()
    {
        // Every tracked writer, including existing MCP and UI services, uses
        // the canonical source fence. Re-entering SaveChanges after a failure
        // retains the same proposed revision rather than advancing it twice.
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<ScriptDefinition>().Where(entry => entry.State == EntityState.Modified))
            entry.Entity.SourceRevision = checked(entry.Property(script => script.SourceRevision).OriginalValue + 1);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Database.IsNpgsql())
        {
            modelBuilder.HasPostgresExtension("pg_trgm");
        }

        modelBuilder.Entity<BootstrapInitializationRecord>(entity =>
        {
            entity.ToTable("BootstrapInitializations");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id).ValueGeneratedNever();
            entity.Property(record => record.AdministratorUserId).HasMaxLength(450).IsRequired();
            entity.HasIndex(record => record.BootstrapInstanceId).IsUnique();
        });

        modelBuilder.Entity<M2MConnectivitySettings>().ToTable("M2MConnectivitySettings");

        modelBuilder.Entity<EnrollmentCode>(entity =>
        {
            entity.ToTable("EnrollmentCodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).IsRequired();
            if (Database.IsNpgsql())
            {
                entity.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
            }
            entity.Property(x => x.Uses).HasDefaultValue(0);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.CodeHash).HasMaxLength(64);
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ValidToUtc });
            entity.HasIndex(x => new { x.TenantId, x.DevelopmentMcpTargetAgentId, x.DevelopmentMcpMarker });
        });

        modelBuilder.Entity<Agent>(entity =>
        {
            entity.ToTable("Agents");
            entity.HasKey(x => x.Id);
            entity.HasQueryFilter(x => x.SupersededAtUtc == null && x.DeletedAtUtc == null);
            entity.Property(x => x.Status).HasConversion<short>();
            entity.Property(x => x.IsEnabled).HasDefaultValue(true);
            entity.Property(x => x.KeyAlgorithm).HasDefaultValue("ecdsa-p256");
            entity.HasIndex(x => x.TenantId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.TenantId, x.IsEnabled });
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.PublicKeyFingerprint })
                .IsUnique()
                .HasFilter("\"PublicKeyFingerprint\" IS NOT NULL AND \"SupersededAtUtc\" IS NULL");
            entity.HasIndex(x => x.SupersededByAgentId);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.SupersededByAgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.Name)
                .HasDatabaseName("IX_Agents_GlobalSearch_Name_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
            entity.HasIndex(x => x.DeviceInfoJson)
                .HasDatabaseName("IX_Agents_GlobalSearch_DeviceInfoJson_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<DevelopmentOperatorTargetGrant>(entity =>
        {
            entity.ToTable("DevelopmentOperatorTargetGrants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Classification).HasConversion<short>();
            entity.Property(x => x.AllowedOperations).HasConversion<int>();
            entity.Property(x => x.FileFixtureRoot).HasMaxLength(4096);
            entity.Property(x => x.EvidenceReference).HasMaxLength(512).IsRequired();
            entity.Property(x => x.GrantedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RevokedBy).HasMaxLength(256);
            entity.Property(x => x.RevocationReason).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.RevokedAtUtc, x.ExpiresAtUtc });
            entity.HasIndex(x => new { x.AgentId, x.RevokedAtUtc })
                .IsUnique()
                .HasFilter("\"RevokedAtUtc\" IS NULL");
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DevelopmentOperatorAcceptedAuditRecord>(entity =>
        {
            entity.ToTable("DevelopmentOperatorAcceptedAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Operation).HasConversion<short>();
            entity.Property(x => x.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TargetGrantId, x.OccurredAtUtc });
            entity.HasOne<DevelopmentOperatorTargetGrant>()
                .WithMany()
                .HasForeignKey(x => x.TargetGrantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorPolicyRecord>(entity =>
        {
            entity.ToTable("McpOperatorPolicies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Environment).HasConversion<short>();
            entity.Property(x => x.Effect).HasConversion<short>();
            entity.Property(x => x.PrincipalSelectorKind).HasConversion<short>();
            entity.Property(x => x.PrincipalSelectorValue).HasMaxLength(256).IsRequired();
            entity.Property(x => x.TargetSelectorKind).HasConversion<short>();
            entity.Property(x => x.ClientTag).HasMaxLength(128);
            entity.Property(x => x.TargetClassification).HasConversion<short>();
            entity.Property(x => x.OperationFamily).HasConversion<int>();
            entity.Property(x => x.Operation).HasMaxLength(256);
            entity.Property(x => x.ConstraintsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.DisabledBy).HasMaxLength(256);
            entity.Property(x => x.LifecycleState).HasConversion<short>().HasDefaultValue(McpOperatorPolicyLifecycleState.Active);
            entity.Property(x => x.AuditReference).HasMaxLength(512);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Environment, x.TenantId, x.DisabledAtUtc, x.ExpiresAtUtc, x.Effect, x.Priority });
            entity.HasIndex(x => new { x.Environment, x.TargetSelectorKind, x.TenantId, x.AgentId });
            entity.HasIndex(x => new { x.Environment, x.PrincipalSelectorKind, x.PrincipalSelectorValue });
        });

        modelBuilder.Entity<McpOperatorAcceptedAuditRecord>(entity =>
        {
            entity.ToTable("McpOperatorAcceptedAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Environment).HasConversion<short>();
            entity.Property(x => x.ServicePrincipal).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256);
            entity.Property(x => x.AuthorizedParty).HasMaxLength(256);
            entity.Property(x => x.GroupsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.RolesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ScopesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512);
            entity.Property(x => x.McpInstance).HasMaxLength(32);
            entity.Property(x => x.Tool).HasMaxLength(128);
            entity.Property(x => x.OperationFamily).HasConversion<int>();
            entity.Property(x => x.Operation).HasMaxLength(256).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RequestId).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => new { x.PolicyId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.RequestId, x.OccurredAtUtc });
            entity.HasOne<McpOperatorPolicyRecord>()
                .WithMany()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorTargetProfileRecord>(entity =>
        {
            entity.ToTable("McpOperatorTargetProfiles");
            entity.HasKey(x => x.AgentId);
            entity.Property(x => x.Classification).HasConversion<short>();
            entity.Property(x => x.TagsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.Classification });
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorPolicyChangeAuditRecord>(entity =>
        {
            entity.ToTable("McpOperatorPolicyChangeAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.PolicyId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.AgentId, x.OccurredAtUtc });
        });

        modelBuilder.Entity<McpOperatorConfirmationPlanRecord>(entity =>
        {
            entity.ToTable("McpOperatorConfirmationPlans");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Environment).HasConversion<short>();
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512);
            entity.Property(x => x.McpInstance).HasMaxLength(32);
            entity.Property(x => x.TargetSetDigest).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OperationFamily).HasConversion<int>();
            entity.Property(x => x.Operation).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ConfirmationClass).HasConversion<short>();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.ExpiresAtUtc, x.ConsumedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<McpOperatorIdempotencyRecord>(entity =>
        {
            entity.ToTable("McpOperatorIdempotencyRecords");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Environment).HasConversion<short>();
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.TargetSetDigest).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OperationFamily).HasConversion<int>();
            entity.Property(x => x.Operation).HasMaxLength(256).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Outcome).HasConversion<short>();
            entity.Property(x => x.ResultReference).HasMaxLength(512);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Environment, x.Subject, x.ClientId, x.TenantId, x.OperationFamily, x.Operation, x.IdempotencyKey })
                .IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<McpOperatorTerminalSessionRecord>(entity =>
        {
            entity.ToTable("McpOperatorTerminalSessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SessionId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512).IsRequired();
            entity.Property(x => x.McpInstance).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ShellType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.WorkingDirectory).HasMaxLength(4096);
            entity.Property(x => x.EffectiveConstraintsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.State).HasConversion<short>();
            entity.Property(x => x.CloseReason).HasMaxLength(128);
            entity.Property(x => x.FailureCode).HasMaxLength(64);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.SessionId).IsUnique();
            entity.HasIndex(x => x.IdempotencyId).IsUnique().HasFilter("\"IdempotencyId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.State, x.ExpiresAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.Subject, x.ClientId, x.State });
            entity.HasOne<McpOperatorPolicyRecord>()
                .WithMany()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorIdempotencyRecord>()
                .WithMany()
                .HasForeignKey(x => x.IdempotencyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorTerminalSessionAuditRecord>(entity =>
        {
            entity.ToTable("McpOperatorTerminalSessionAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.State).HasConversion<short>();
            entity.Property(x => x.Action).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(128);
            entity.HasIndex(x => new { x.SessionRecordId, x.OccurredAtUtc });
            entity.HasOne<McpOperatorTerminalSessionRecord>()
                .WithMany()
                .HasForeignKey(x => x.SessionRecordId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorTerminalActionRecord>(entity =>
        {
            entity.ToTable("McpOperatorTerminalActions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Operation).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DelegationRequestId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Outcome).HasConversion<short>();
            entity.Property(x => x.ResultReference).HasMaxLength(128);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.SessionRecordId, x.Operation, x.DelegationRequestId }).IsUnique();
            entity.HasIndex(x => new { x.SessionRecordId, x.CreatedAtUtc });
            entity.HasOne<McpOperatorTerminalSessionRecord>()
                .WithMany()
                .HasForeignKey(x => x.SessionRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorCommandRecord>(entity =>
        {
            entity.ToTable("McpOperatorCommands");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CommandId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512).IsRequired();
            entity.Property(x => x.McpInstance).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ShellType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.WorkingDirectory).HasMaxLength(4096);
            entity.Property(x => x.CommandHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.EnvironmentReferencesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.EffectiveConstraintsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.State).HasConversion<short>();
            entity.Property(x => x.FailureCode).HasMaxLength(64);
            entity.Property(x => x.OutputJson).HasColumnType("jsonb");
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.CommandId).IsUnique();
            entity.HasIndex(x => x.IdempotencyId).IsUnique().HasFilter("\"IdempotencyId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.Subject, x.ClientId, x.State });
            entity.HasOne<McpOperatorPolicyRecord>()
                .WithMany()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorIdempotencyRecord>()
                .WithMany()
                .HasForeignKey(x => x.IdempotencyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorScriptRecord>(entity =>
        {
            entity.ToTable("McpOperatorScripts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512).IsRequired();
            entity.Property(x => x.McpInstance).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(512).IsRequired();
            entity.Property(x => x.ShellType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ManifestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ParametersJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.WorkingDirectory).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.DeclaredSideEffectsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.ScriptId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Subject, x.ClientId, x.McpResource, x.McpInstance, x.DeletedAtUtc, x.UpdatedAtUtc });
            entity.HasOne<ScriptDefinition>()
                .WithMany()
                .HasForeignKey(x => x.ScriptId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorPolicyRecord>()
                .WithMany()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorScriptVersionRecord>(entity =>
        {
            entity.ToTable("McpOperatorScriptVersions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(512).IsRequired();
            entity.Property(x => x.ShellType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ManifestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ParametersJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.WorkingDirectory).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.DeclaredSideEffectsJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.ScriptRecordId, x.ScriptVersion }).IsUnique();
            entity.HasIndex(x => new { x.ScriptId, x.OccurredAtUtc });
            entity.HasOne<McpOperatorScriptRecord>()
                .WithMany()
                .HasForeignKey(x => x.ScriptRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorJobRecord>(entity =>
        {
            entity.ToTable("McpOperatorJobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512).IsRequired();
            entity.Property(x => x.McpInstance).HasMaxLength(32).IsRequired();
            entity.Property(x => x.TargetSetDigest).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.JobId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.Subject, x.ClientId, x.McpResource, x.McpInstance, x.DeletedAtUtc, x.UpdatedAtUtc });
            entity.HasOne<JobDefinition>()
                .WithMany()
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorPolicyRecord>()
                .WithMany()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorJobAuditRecord>(entity =>
        {
            entity.ToTable("McpOperatorJobAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.JobRecordId, x.JobVersion }).IsUnique();
            entity.HasIndex(x => new { x.JobId, x.OccurredAtUtc });
            entity.HasOne<McpOperatorJobRecord>()
                .WithMany()
                .HasForeignKey(x => x.JobRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorJobRunRecord>(entity =>
        {
            entity.ToTable("McpOperatorJobRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.JobRunId).HasPrecision(20, 0);
            entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.TargetSetDigest).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.JobRunId).IsUnique();
            entity.HasIndex(x => x.IdempotencyId).IsUnique().HasFilter("\"IdempotencyId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.DeletedAtUtc, x.CreatedAtUtc });
            entity.HasOne<McpOperatorJobRecord>()
                .WithMany()
                .HasForeignKey(x => x.JobRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorIdempotencyRecord>()
                .WithMany()
                .HasForeignKey(x => x.IdempotencyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorJobRunAuditRecord>(entity =>
        {
            entity.ToTable("McpOperatorJobRunAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.JobRunRecordId, x.OccurredAtUtc });
            entity.HasOne<McpOperatorJobRunRecord>()
                .WithMany()
                .HasForeignKey(x => x.JobRunRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorTaskRecord>(entity =>
        {
            entity.ToTable("McpOperatorTasks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CommandId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512).IsRequired();
            entity.Property(x => x.McpInstance).HasMaxLength(32).IsRequired();
            entity.Property(x => x.TargetSetDigest).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.TaskType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ShellType).HasMaxLength(32);
            entity.Property(x => x.CommandHash).HasMaxLength(64);
            entity.Property(x => x.ScriptContentHash).HasMaxLength(64);
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ResultSummary).HasMaxLength(48 * 1024);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.TaskActivityId).IsUnique();
            entity.HasIndex(x => x.CommandId).IsUnique();
            entity.HasIndex(x => x.IdempotencyId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.Subject, x.ClientId, x.McpResource, x.McpInstance, x.State, x.UpdatedAtUtc });
            entity.HasOne<JobTaskActivityRecord>()
                .WithMany()
                .HasForeignKey(x => x.TaskActivityId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorPolicyRecord>()
                .WithMany()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorIdempotencyRecord>()
                .WithMany()
                .HasForeignKey(x => x.IdempotencyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorTaskAuditRecord>(entity =>
        {
            entity.ToTable("McpOperatorTaskAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TaskRecordId, x.OccurredAtUtc });
            entity.HasOne<McpOperatorTaskRecord>()
                .WithMany()
                .HasForeignKey(x => x.TaskRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorRequestRecord>(entity =>
        {
            entity.ToTable("McpOperatorRequests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512).IsRequired();
            entity.Property(x => x.McpInstance).HasMaxLength(32).IsRequired();
            entity.Property(x => x.TargetSetDigest).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.ResultSummary).HasMaxLength(48 * 1024);
            entity.Property(x => x.ClaimReferenceHash).HasMaxLength(64);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.HasIndex(x => x.IdempotencyId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.Subject, x.ClientId, x.McpResource, x.McpInstance, x.State, x.UpdatedAtUtc });
            entity.HasIndex(x => new { x.JobId, x.UpdatedAtUtc });
            entity.HasOne<RequestRecord>()
                .WithMany()
                .HasForeignKey(x => x.RequestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<JobDefinition>()
                .WithMany()
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorPolicyRecord>()
                .WithMany()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorIdempotencyRecord>()
                .WithMany()
                .HasForeignKey(x => x.IdempotencyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorRequestAuditRecord>(entity =>
        {
            entity.ToTable("McpOperatorRequestAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.RequestRecordId, x.OccurredAtUtc });
            entity.HasOne<McpOperatorRequestRecord>()
                .WithMany()
                .HasForeignKey(x => x.RequestRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpOperatorFileArtifactRecord>(entity =>
        {
            entity.ToTable("McpOperatorFileArtifacts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.McpResource).HasMaxLength(512).IsRequired();
            entity.Property(x => x.McpInstance).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ReadRootFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.MimeType).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Content).IsRequired();
            entity.HasIndex(x => x.IdempotencyId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.Subject, x.ClientId, x.McpResource, x.McpInstance, x.ExpiresAtUtc });
            entity.HasOne<McpOperatorAcceptedAuditRecord>()
                .WithMany()
                .HasForeignKey(x => x.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorIdempotencyRecord>()
                .WithMany()
                .HasForeignKey(x => x.IdempotencyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DevelopmentMcpFileArtifactRecord>(entity =>
        {
            entity.ToTable("DevelopmentMcpFileArtifacts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileName).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Content).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.ExpiresAtUtc });
            entity.HasIndex(x => new { x.TargetGrantId, x.CreatedAtUtc });
            entity.HasOne<DevelopmentOperatorTargetGrant>()
                .WithMany()
                .HasForeignKey(x => x.TargetGrantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DevelopmentMcpScriptRecord>(entity =>
        {
            entity.ToTable("DevelopmentMcpScripts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Marker).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Shell).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ExecutionMode).HasMaxLength(32).IsRequired().HasDefaultValue("standard");
            entity.HasIndex(x => x.ScriptId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.DeletedAtUtc, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TargetGrantId, x.CreatedAtUtc });
            entity.HasOne<DevelopmentOperatorTargetGrant>()
                .WithMany()
                .HasForeignKey(x => x.TargetGrantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DevelopmentMcpMarkerJobRecord>(entity =>
        {
            entity.ToTable("DevelopmentMcpMarkerJobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Marker).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.JobId).IsUnique();
            entity.HasIndex(x => x.ScriptId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.DeletedAtUtc, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TargetGrantId, x.CreatedAtUtc });
            entity.HasOne<DevelopmentOperatorTargetGrant>()
                .WithMany()
                .HasForeignKey(x => x.TargetGrantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JobDefinition>(entity =>
        {
            entity.HasIndex(x => new { x.TenantId, x.AgentId });
        });

        modelBuilder.Entity<JobRunRecord>(entity =>
        {
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<JobTaskActivityRecord>(entity =>
        {
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<RequestRecord>(entity =>
        {
            entity.HasIndex(x => new { x.TargetTenantId, x.TargetAgentId });
        });

        modelBuilder.Entity<PrimaryClientAgentBinding>(entity =>
        {
            entity.ToTable("PrimaryClientAgentBindings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PrimaryClientIdentity).IsRequired();
            entity.Property(x => x.CreatedBy).IsRequired();
            entity.Property(x => x.BindingSource).IsRequired();
            entity.Property(x => x.Status).HasConversion<short>();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.AgentId)
                .IsUnique()
                .HasFilter("\"AgentId\" IS NOT NULL AND \"Status\" <> 3");
            entity.HasIndex(x => new { x.TenantId, x.PrimaryClientIdentity })
                .IsUnique()
                .HasFilter("\"Status\" <> 3");
            entity.HasIndex(x => x.EnrollmentCodeId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
            entity.HasOne(x => x.Agent)
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.EnrollmentCode)
                .WithMany()
                .HasForeignKey(x => x.EnrollmentCodeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentCredential>(entity =>
        {
            entity.ToTable("AgentCredentials");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RefreshTokenHash).IsRequired();
            entity.HasIndex(x => x.AgentId);
            entity.HasIndex(x => x.RefreshTokenHash).IsUnique();
            entity.HasOne(x => x.Agent)
                .WithMany(x => x.Credentials)
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentRefreshToken>(entity =>
        {
            entity.ToTable("AgentRefreshTokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).IsRequired();
            entity.HasIndex(x => x.AgentId);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.AgentId, x.CreatedAtUtc });
            entity.HasOne(x => x.Agent)
                .WithMany(x => x.RefreshTokens)
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentNonceLog>(entity =>
        {
            entity.ToTable("AgentNonceLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Nonce).IsRequired();
            entity.HasIndex(x => new { x.AgentId, x.Nonce }).IsUnique();
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<OidcSigningKey>(entity =>
        {
            entity.ToTable("OidcSigningKeys");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.KeyId).IsRequired();
            entity.Property(x => x.PrivateKeyPem).IsRequired();
            entity.HasIndex(x => x.KeyId).IsUnique();
            entity.HasIndex(x => x.IsActive);
        });

        modelBuilder.Entity<AgentTokenEvent>(entity =>
        {
            entity.ToTable("AgentTokenEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("Tenants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Domains).HasColumnType(Database.IsNpgsql() ? "text[]" : "TEXT");
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<ClientUpdateReleaseRecord>(entity =>
        {
            entity.ToTable("ClientUpdateReleases");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PublicId).ValueGeneratedNever();
            entity.Property(x => x.RuntimeId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Channel).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ArtifactKey).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ManifestJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => x.PublicId).IsUnique();
            entity.HasIndex(x => new { x.RuntimeId, x.Version }).IsUnique();
            entity.HasIndex(x => x.Revision).IsUnique();
            entity.HasIndex(x => new { x.Enabled, x.RuntimeId, x.Channel, x.PublishedAtUtc });
        });

        modelBuilder.Entity<ClientUpdateAttemptRecord>(entity =>
        {
            entity.ToTable("ClientUpdateAttempts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PublicId).ValueGeneratedNever();
            entity.Property(x => x.State).HasConversion<short>();
            entity.Property(x => x.FromVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TargetVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RuntimeId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.AdmissionNonceHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FailureCode).HasMaxLength(64);
            entity.HasIndex(x => x.PublicId).IsUnique();
            entity.HasIndex(x => new { x.AgentId, x.ReleaseId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.UpdatedAtUtc });
            entity.HasOne(x => x.Release)
                .WithMany(x => x.Attempts)
                .HasForeignKey(x => x.ReleaseId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentClientUpdateStateRecord>(entity =>
        {
            entity.ToTable("AgentClientUpdateStates");
            entity.HasKey(x => x.AgentId);
            entity.Property(x => x.SuspensionReason).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.SuspendedAtUtc });
            entity.HasOne<Agent>()
                .WithOne()
                .HasForeignKey<AgentClientUpdateStateRecord>(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClientUpdateCatalogRevision>(entity =>
        {
            entity.ToTable("ClientUpdateCatalogRevision");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<ClientReleaseImportOperation>(entity =>
        {
            entity.ToTable("ClientReleaseImportOperations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.SourceRepository).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Tag).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Version).HasMaxLength(64).IsRequired();
            entity.Property(x => x.BuildCommit).HasMaxLength(40);
            entity.Property(x => x.PublicationSha256).HasMaxLength(64);
            entity.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.PublishedBy).HasMaxLength(256);
            entity.Property(x => x.State).HasConversion<short>();
            entity.Property(x => x.Error).HasMaxLength(2048);
            entity.Property(x => x.AutomaticPublishError).HasMaxLength(2048);
            entity.Property(x => x.LeaseOwner).IsConcurrencyToken();
            entity.Property(x => x.LeaseGeneration).IsConcurrencyToken();
            entity.HasIndex(x => x.GitHubReleaseId).IsUnique();
            entity.HasIndex(x => new { x.State, x.LeaseUntilUtc, x.CreatedAtUtc });
            entity.HasIndex(x => x.Version);
        });

        modelBuilder.Entity<ClientReleaseImportAsset>(entity =>
        {
            entity.ToTable("ClientReleaseImportAssets");
            entity.HasKey(x => new { x.OperationId, x.RuntimeId });
            entity.Property(x => x.RuntimeId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SourceName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.SourceSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LocalSha256).HasMaxLength(64);
            entity.Property(x => x.ConversionContract).HasMaxLength(128);
            entity.Property(x => x.State).HasConversion<short>();
            entity.Property(x => x.Error).HasMaxLength(2048);
            entity.HasOne(x => x.Operation)
                .WithMany(x => x.Assets)
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClientReleaseAutomationSettings>(entity =>
        {
            entity.ToTable("ClientReleaseAutomationSettings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.LastError).HasMaxLength(2048);
            entity.Property(x => x.UpdatedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
        });

        modelBuilder.Entity<ClientInstallGrant>(entity =>
        {
            entity.ToTable("ClientInstallGrants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RequestKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RuntimeId).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ArtifactVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ArtifactSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PublicWebBaseUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.PublicApiBaseUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RevokedBy).HasMaxLength(256);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.RequestKey).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            entity.HasOne(x => x.EnrollmentCode).WithMany()
                .HasForeignKey(x => x.EnrollmentCodeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SecretRecord>(entity =>
        {
            entity.ToTable("Secrets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Value).IsRequired();
            entity.HasIndex(x => x.TenantId);
            entity.HasIndex(x => x.ClientIdentity);
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<ScriptDefinition>(entity =>
        {
            entity.ToTable("Scripts");
            entity.HasQueryFilter(script => script.DeletedAtUtc == null);
            entity.Property(script => script.SourceRevision).HasDefaultValue(1L).IsConcurrencyToken();
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.FolderPath).IsRequired();
            entity.Property(x => x.Description).IsRequired();
            entity.Property(x => x.Content).IsRequired();
            entity.Property(x => x.ScriptType).IsRequired();
            entity.HasIndex(x => new { x.FolderPath, x.Name });
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<ScriptParameterDefinition>(entity =>
        {
            entity.ToTable("ScriptParameters");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Type).IsRequired();
            entity.HasIndex(x => x.ScriptId);
            entity.HasOne(x => x.Script)
                .WithMany(x => x.Parameters)
                .HasForeignKey(x => x.ScriptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobDefinition>(entity =>
        {
            entity.ToTable("Jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.FolderPath).IsRequired();
            entity.Property(x => x.ClientIdentity).IsRequired();
            entity.Property(x => x.OptionsJson).HasColumnType("text");
            entity.HasIndex(x => x.TenantId);
            entity.HasIndex(x => x.ClientIdentity);
            entity.HasIndex(x => new { x.FolderPath, x.Name });
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<JobParameterDefinition>(entity =>
        {
            entity.ToTable("JobParameters");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Type).IsRequired();
            entity.HasIndex(x => x.JobId);
            entity.HasOne(x => x.Job)
                .WithMany(x => x.Parameters)
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobStepDefinition>(entity =>
        {
            entity.ToTable("JobSteps");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.JobId);
            entity.HasIndex(x => new { x.JobId, x.Ordinal });
            entity.HasOne(x => x.Job)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobRunRecord>(entity =>
        {
            entity.ToTable("JobRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.ClientIdentity).IsRequired();
            entity.Property(x => x.StartedBy).IsRequired();
            entity.HasIndex(x => x.JobId);
            entity.HasIndex(x => x.TenantId);
            entity.HasIndex(x => x.ClientIdentity);
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasOne(x => x.Job)
                .WithMany()
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JobStepRunRecord>(entity =>
        {
            entity.ToTable("JobStepRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasIndex(x => x.JobRunId);
            entity.HasIndex(x => new { x.JobRunId, x.Ordinal });
            entity.HasOne(x => x.JobRun)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.JobRunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.JobStep)
                .WithMany()
                .HasForeignKey(x => x.JobStepId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        modelBuilder.Entity<JobTaskActivityRecord>(entity =>
        {
            entity.ToTable("JobTaskActivities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.RequestId).IsRequired();
            entity.Property(x => x.ClientIdentity).IsRequired();
            entity.Property(x => x.TaskType).IsRequired();
            entity.Property(x => x.Status).IsRequired();
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.HasIndex(x => x.JobRunId);
            entity.HasIndex(x => x.JobStepId);
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasOne(x => x.JobRun)
                .WithMany(x => x.Activities)
                .HasForeignKey(x => x.JobRunId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);
            entity.HasOne(x => x.JobStep)
                .WithMany()
                .HasForeignKey(x => x.JobStepId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
        });

        modelBuilder.Entity<JobTaskLogRecord>(entity =>
        {
            entity.ToTable("JobTaskLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RequestId).IsRequired();
            entity.Property(x => x.ClientIdentity).IsRequired();
            entity.Property(x => x.Stream).IsRequired();
            entity.Property(x => x.Message).IsRequired();
            entity.HasIndex(x => x.RequestId);
            entity.HasIndex(x => new { x.RequestId, x.Sequence, x.Stream }).IsUnique();
            entity.HasIndex(x => x.TimestampUtc);
            entity.HasOne(x => x.Activity)
                .WithMany(x => x.Logs)
                .HasForeignKey(x => x.JobTaskActivityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RequestRecord>(entity =>
        {
            entity.ToTable("Requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceSystem).IsRequired();
            entity.Property(x => x.TargetClientIdentity).IsRequired();
            // Keep the deployed column names during the compatibility window; public and
            // application contracts use neutral execution terminology.
            entity.Property(x => x.JobDefinitionId)
                .HasColumnName("RundeckJobDefinitionId")
                .IsRequired();
            entity.Property(x => x.ExecutionId).HasColumnName("RundeckExecutionId");
            entity.Property(x => x.Status).IsRequired();
            entity.Property(x => x.Logs).HasColumnType(Database.IsNpgsql() ? "text[]" : "TEXT");
            entity.HasIndex(x => x.TargetClientIdentity);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => x.UpdatedAtUtc);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).IsRequired();
            entity.Property(x => x.PayloadJson).IsRequired();
            entity.Property(x => x.Source).IsRequired();
            entity.Property(x => x.CorrelationId).IsRequired();
            entity.Property(x => x.Status).IsRequired();
            entity.HasIndex(x => new { x.Status, x.NextAttemptUtc, x.LockedUntilUtc });
            entity.HasIndex(x => x.OccurredUtc);
            entity.HasIndex(x => x.CorrelationId);
            entity.HasIndex(x => x.EntityId);
            entity.HasIndex(x => x.Type);
        });

        modelBuilder.Entity<OutboxProcessedEvent>(entity =>
        {
            entity.ToTable("OutboxProcessedEvents");
            entity.HasKey(x => new { x.EventId, x.ConsumerName });
            entity.Property(x => x.ConsumerName).IsRequired();
            entity.HasIndex(x => x.ProcessedUtc);
        });

        modelBuilder.Entity<OutboxReadReceipt>(entity =>
        {
            entity.ToTable("OutboxReadReceipts");
            entity.HasKey(x => new { x.EventId, x.UserId });
            entity.Property(x => x.UserId).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.ReadUtc });
        });

        modelBuilder.Entity<CommandInboxReceipt>(entity =>
        {
            entity.ToTable("CommandInboxReceipts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.CommandId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Version).HasPrecision(20, 0);
            entity.Property(x => x.Sequence).HasPrecision(20, 0);
            entity.HasIndex(x => new { x.TenantId, x.CommandId, x.Version, x.Sequence })
                .IsUnique()
                .HasDatabaseName("UX_CommandInbox_Idempotency");
            entity.HasIndex(x => new { x.TenantId, x.CorrelationId });
            entity.HasIndex(x => x.FirstReceivedAtUtc);
        });

        modelBuilder.Entity<CommandIntentEventRecord>(entity =>
        {
            entity.ToTable("CommandIntentEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.CommandId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Version).HasPrecision(20, 0);
            entity.Property(x => x.Sequence).HasPrecision(20, 0);
            entity.Property(x => x.Status).HasConversion<short>();
            entity.Property(x => x.Source).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.CommandId, x.Version, x.Sequence })
                .IsUnique()
                .HasDatabaseName("UX_CommandIntentHistory_Order");
            entity.HasIndex(x => new { x.TenantId, x.CorrelationId });
            entity.HasIndex(x => x.RecordedAtUtc);
        });

        modelBuilder.Entity<CommandOutboxRecord>(entity =>
        {
            entity.ToTable("CommandOutbox");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.CommandId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.LastAcceptedVersion).HasPrecision(20, 0);
            entity.Property(x => x.LastAcceptedSequence).HasPrecision(20, 0);
            entity.Property(x => x.CurrentStatus).HasConversion<short>();
            entity.Property(x => x.Mode).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.CommandId })
                .IsUnique()
                .HasDatabaseName("UX_CommandOutbox_Command");
            entity.HasIndex(x => new { x.TerminalAtUtc, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.CorrelationId });
        });

        modelBuilder.Entity<JobShadowObservationRecord>(entity =>
        {
            entity.ToTable("JobShadowObservations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.SourceSystem).HasMaxLength(64).IsRequired();
            entity.Property(x => x.JobRunId).HasPrecision(20, 0);
            entity.Property(x => x.JobId).HasPrecision(20, 0);
            entity.Property(x => x.ClientIdentity).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Kind).HasConversion<short>();
            entity.Property(x => x.StartedBy).HasMaxLength(256);
            entity.Property(x => x.RunStatus).HasConversion<short>();
            entity.Property(x => x.JobStepRunId).HasPrecision(20, 0);
            entity.Property(x => x.JobStepId).HasPrecision(20, 0);
            entity.Property(x => x.StepStatus).HasConversion<short>();
            entity.Property(x => x.TaskRequestId).HasMaxLength(256);
            entity.Property(x => x.CommandCorrelationStatus).HasConversion<short>();
            entity.Property(x => x.CorrelatedCommandStatus).HasConversion<short>();
            entity.HasIndex(x => new { x.SourceSystem, x.SourceEventId })
                .IsUnique()
                .HasDatabaseName("UX_JobShadowObservation_Source");
            entity.HasIndex(x => new { x.JobRunId, x.SourceEventId });
            entity.HasIndex(x => new { x.TenantId, x.TaskRequestId });
            entity.HasIndex(x => x.RecordedAtUtc);
        });

        modelBuilder.Entity<ClientWindowsSessionSnapshot>(entity =>
        {
            entity.ToTable("ClientWindowsSessionSnapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ClientIdentity).IsRequired();
            entity.Property(x => x.State).IsRequired();
            entity.Property(x => x.Source).IsRequired();
            entity.HasIndex(x => new { x.ClientIdentity, x.WindowsSessionId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ClientIdentity });
            entity.HasIndex(x => new { x.ClientIdentity, x.InventorySequence });
            entity.HasIndex(x => x.ExpiresAtUtc);
        });

        modelBuilder.Entity<ClientWindowsSessionInventoryRefresh>(entity =>
        {
            entity.ToTable("ClientWindowsSessionInventoryRefreshes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RefreshRequestId).IsRequired();
            entity.Property(x => x.ClientIdentity).IsRequired();
            entity.Property(x => x.RequesterIdentity).IsRequired();
            entity.Property(x => x.Source).IsRequired();
            entity.Property(x => x.Status).IsRequired();
            entity.HasIndex(x => x.RefreshRequestId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ClientIdentity, x.RequestedAtUtc });
            entity.HasIndex(x => new { x.ClientIdentity, x.Completed });
        });

        modelBuilder.Entity<RemoteSupportTargetSelectionEvent>(entity =>
        {
            entity.ToTable("RemoteSupportTargetSelectionEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RequesterIdentity).IsRequired();
            entity.Property(x => x.ClientIdentity).IsRequired();
            entity.Property(x => x.TargetMode).IsRequired();
            entity.Property(x => x.Result).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.ClientIdentity, x.RequestedAtUtc });
            entity.HasIndex(x => x.SessionId);
        });

        modelBuilder.Entity<RemoteSupportSessionRecord>(entity =>
        {
            entity.ToTable("RemoteSupportSessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ContractVersion).HasDefaultValue(1);
            entity.Property(x => x.InitiatingOperatorId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.TargetKind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.TargetUserSidHash).HasMaxLength(256);
            entity.Property(x => x.RequestedCapabilitiesJson).IsRequired();
            entity.Property(x => x.GrantedCapabilitiesJson).IsRequired();
            entity.Property(x => x.State).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LifecycleRevision).HasPrecision(20, 0);
            entity.Property(x => x.TargetInventorySequence).HasPrecision(20, 0);
            entity.Property(x => x.TerminalReasonCode).HasMaxLength(128);
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.OpenRequestId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.InitiatingOperatorId, x.UpdatedAtUtc });
            entity.HasIndex(x => x.ExpiresAtUtc);
        });

        modelBuilder.Entity<RemoteSupportAuditEventRecord>(entity =>
        {
            entity.ToTable("RemoteSupportAuditEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ActorKind).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Outcome).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FailureCode).HasMaxLength(128);
            entity.Property(x => x.AuditSequence).HasPrecision(20, 0);
            entity.Property(x => x.LifecycleRevision).HasPrecision(20, 0);
            entity.HasIndex(x => new { x.RemoteSupportSessionId, x.AuditSequence }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.RemoteSupportSessionId, x.RequestId });
            entity.HasOne<RemoteSupportSessionRecord>()
                .WithMany()
                .HasForeignKey(x => x.RemoteSupportSessionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

    }
}
