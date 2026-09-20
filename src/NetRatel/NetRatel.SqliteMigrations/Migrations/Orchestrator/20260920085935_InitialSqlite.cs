using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.SqliteMigrations.Migrations.Orchestrator
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentNonceLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nonce = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentNonceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    DisabledReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastTokenIssuedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: true),
                    PublicKeyFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    KeyAlgorithm = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "ecdsa-p256"),
                    KeyRegisteredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AllowedScopesJson = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceInfoJson = table.Column<string>(type: "TEXT", nullable: true),
                    MtlsThumbprint = table.Column<string>(type: "TEXT", nullable: true),
                    SupersededByAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_Agents_SupersededByAgentId",
                        column: x => x.SupersededByAgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentTokenEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Ip = table.Column<string>(type: "TEXT", nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTokenEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientUpdateCatalogRevision",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUpdateCatalogRevision", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientUpdateReleases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ArtifactKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ManifestJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PublishedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DisabledAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DisabledBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUpdateReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientWindowsSessionInventoryRefreshes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RefreshRequestId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    RequesterIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    InventorySequence = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientWindowsSessionInventoryRefreshes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientWindowsSessionSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    WindowsSessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    Domain = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayLabel = table.Column<string>(type: "TEXT", nullable: true),
                    UserSidHash = table.Column<string>(type: "TEXT", nullable: true),
                    IsConsoleSession = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsConnected = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsWinlogon = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAssistable = table.Column<bool>(type: "INTEGER", nullable: false),
                    SessionType = table.Column<string>(type: "TEXT", nullable: true),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    HelperConnected = table.Column<bool>(type: "INTEGER", nullable: false),
                    HelperVersionMatches = table.Column<bool>(type: "INTEGER", nullable: false),
                    HelperLaunchable = table.Column<bool>(type: "INTEGER", nullable: false),
                    HelperRepairable = table.Column<bool>(type: "INTEGER", nullable: false),
                    HelperPid = table.Column<int>(type: "INTEGER", nullable: true),
                    HelperVersion = table.Column<string>(type: "TEXT", nullable: true),
                    InventorySequence = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Stale = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientWindowsSessionSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommandInboxReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    Sequence = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    FirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastReceivedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DuplicateCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandInboxReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommandIntentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StatusTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    Sequence = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsAuthoritative = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandIntentEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommandOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TerminalAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastAcceptedVersion = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    LastAcceptedSequence = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    CurrentStatus = table.Column<short>(type: "INTEGER", nullable: false),
                    ObservedDispatchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsAuthoritative = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ValidFromUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ValidToUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    MaxUses = table.Column<int>(type: "INTEGER", nullable: true),
                    Uses = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedBy = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    DevelopmentMcpTargetAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DevelopmentMcpMarker = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobShadowObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceSystem = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceEventId = table.Column<long>(type: "INTEGER", nullable: false),
                    JobRunId = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    JobId = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<short>(type: "INTEGER", nullable: false),
                    StartedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RunStatus = table.Column<short>(type: "INTEGER", nullable: true),
                    CurrentStepOrdinal = table.Column<int>(type: "INTEGER", nullable: true),
                    RunCreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    JobStepRunId = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: true),
                    JobStepId = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: true),
                    StepStatus = table.Column<short>(type: "INTEGER", nullable: true),
                    StepOrdinal = table.Column<int>(type: "INTEGER", nullable: true),
                    TaskRequestId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CommandCorrelationStatus = table.Column<short>(type: "INTEGER", nullable: false),
                    CorrelatedCommandStatus = table.Column<short>(type: "INTEGER", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsAuthoritative = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobShadowObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "M2MConnectivitySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemoteBaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteAudience = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteSystemName = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_M2MConnectivitySettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorConfirmationPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Environment = table.Column<short>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetSetDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    OperationFamily = table.Column<int>(type: "INTEGER", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConfirmationClass = table.Column<short>(type: "INTEGER", nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConsumedIdempotencyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorConfirmationPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorIdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Environment = table.Column<short>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetSetDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    OperationFamily = table.Column<int>(type: "INTEGER", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Outcome = table.Column<short>(type: "INTEGER", nullable: false),
                    ResultReference = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorIdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Environment = table.Column<short>(type: "INTEGER", nullable: false),
                    Effect = table.Column<short>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    PrincipalSelectorKind = table.Column<short>(type: "INTEGER", nullable: false),
                    PrincipalSelectorValue = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TargetSelectorKind = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClientTag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TargetClassification = table.Column<short>(type: "INTEGER", nullable: true),
                    OperationFamily = table.Column<int>(type: "INTEGER", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReviewByUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DisabledAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DisabledBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LifecycleState = table.Column<short>(type: "INTEGER", nullable: false, defaultValue: (short)1),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    AuditReference = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorPolicyChangeAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorPolicyChangeAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OidcSigningKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", nullable: false),
                    PrivateKeyPem = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OidcSigningKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: true),
                    EntityId = table.Column<string>(type: "TEXT", nullable: true),
                    Severity = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxProcessedEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConsumerName = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxProcessedEvents", x => new { x.EventId, x.ConsumerName });
                });

            migrationBuilder.CreateTable(
                name: "OutboxReadReceipts",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ReadUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxReadReceipts", x => new { x.EventId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "RemoteSupportSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpenRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    InitiatingOperatorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TargetWindowsSessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetUserSidHash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TargetInventorySequence = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: true),
                    RequestedCapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    GrantedCapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LifecycleRevision = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TerminalAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TerminalReasonCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSupportSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSupportTargetSelectionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequesterIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    ClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetMode = table.Column<string>(type: "TEXT", nullable: false),
                    TargetWindowsSessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetUserSidHash = table.Column<string>(type: "TEXT", nullable: true),
                    TargetDisplayLabel = table.Column<string>(type: "TEXT", nullable: true),
                    SelectedProvider = table.Column<string>(type: "TEXT", nullable: true),
                    InventorySequence = table.Column<ulong>(type: "INTEGER", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSupportTargetSelectionEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSystem = table.Column<string>(type: "TEXT", nullable: false),
                    TargetClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    TargetTenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RundeckJobDefinitionId = table.Column<string>(type: "TEXT", nullable: false),
                    RundeckExecutionId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResultMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ResultData = table.Column<string>(type: "TEXT", nullable: true),
                    JobInputs = table.Column<string>(type: "TEXT", nullable: true),
                    Logs = table.Column<string>(type: "text[]", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Scripts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceRevision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 1L),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scripts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Secrets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Secrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    Domains = table.Column<string>(type: "text[]", nullable: false),
                    ContactPerson = table.Column<string>(type: "TEXT", nullable: true),
                    ContactEmail = table.Column<string>(type: "TEXT", nullable: true),
                    AutoUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoUpdateChannel = table.Column<string>(type: "TEXT", nullable: false),
                    AutoUpdateTargetVersion = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentClientUpdateStates",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    SuspendedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SuspensionReason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SuspensionAttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SuppressedReleaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PolicyRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ResumedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResumedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentClientUpdateStates", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_AgentClientUpdateStates_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastUsedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCredentials_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReplacedByTokenId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastUsedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RecoveryUsedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentRefreshTokens_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DevelopmentOperatorTargetGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Classification = table.Column<short>(type: "INTEGER", nullable: false),
                    AllowedOperations = table.Column<int>(type: "INTEGER", nullable: false),
                    FileFixtureRoot = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    EvidenceReference = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    GrantedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentOperatorTargetGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentOperatorTargetGrants_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorTargetProfiles",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Classification = table.Column<short>(type: "INTEGER", nullable: false),
                    TagsJson = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorTargetProfiles", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_McpOperatorTargetProfiles_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClientUpdateAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<short>(type: "INTEGER", nullable: false),
                    AdmissionNonceHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    GatewayConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GatewayConnectionEpoch = table.Column<long>(type: "INTEGER", nullable: true),
                    ConfirmationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReadmittedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConfirmedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUpdateAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientUpdateAttempts_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClientUpdateAttempts_ClientUpdateReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "ClientUpdateReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrimaryClientAgentBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EnrollmentCodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PrimaryClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    BoundAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    BoundBy = table.Column<string>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedBy = table.Column<string>(type: "TEXT", nullable: true),
                    BindingSource = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrimaryClientAgentBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrimaryClientAgentBindings_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrimaryClientAgentBindings_EnrollmentCodes_EnrollmentCodeId",
                        column: x => x.EnrollmentCodeId,
                        principalTable: "EnrollmentCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobParameters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Required = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultValue = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobParameters_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    StartedBy = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentStepOrdinal = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    InputsJson = table.Column<string>(type: "TEXT", nullable: true),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobRuns_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobSteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Runner = table.Column<string>(type: "TEXT", nullable: true),
                    Command = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptId = table.Column<long>(type: "INTEGER", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobSteps_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorAcceptedAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Environment = table.Column<short>(type: "INTEGER", nullable: false),
                    ServicePrincipal = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AuthorizedParty = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    GroupsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RolesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ScopesJson = table.Column<string>(type: "jsonb", nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OperationFamily = table.Column<int>(type: "INTEGER", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorAcceptedAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorAcceptedAudits_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetSetDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobs_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobs_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSupportAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RemoteSupportSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    AuditSequence = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    LifecycleRevision = table.Column<decimal>(type: "TEXT", precision: 20, scale: 0, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ActorKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSupportAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteSupportAuditEvents_RemoteSupportSessions_RemoteSupportSessionId",
                        column: x => x.RemoteSupportSessionId,
                        principalTable: "RemoteSupportSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorScripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScriptId = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ShellType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ManifestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DeclaredSideEffectsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorScripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorScripts_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorScripts_Scripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "Scripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScriptParameters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScriptId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Required = table.Column<bool>(type: "INTEGER", nullable: false),
                    Default = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScriptParameters_Scripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "Scripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DevelopmentMcpFileArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetGrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MarkerOwned = table.Column<bool>(type: "INTEGER", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentMcpFileArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentMcpFileArtifacts_DevelopmentOperatorTargetGrants_TargetGrantId",
                        column: x => x.TargetGrantId,
                        principalTable: "DevelopmentOperatorTargetGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DevelopmentMcpMarkerJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    ScriptId = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetGrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Marker = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentMcpMarkerJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentMcpMarkerJobs_DevelopmentOperatorTargetGrants_TargetGrantId",
                        column: x => x.TargetGrantId,
                        principalTable: "DevelopmentOperatorTargetGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DevelopmentMcpScripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScriptId = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetGrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Marker = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Shell = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExecutionMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "standard"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentMcpScripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentMcpScripts_DevelopmentOperatorTargetGrants_TargetGrantId",
                        column: x => x.TargetGrantId,
                        principalTable: "DevelopmentOperatorTargetGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DevelopmentOperatorAcceptedAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetGrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Operation = table.Column<short>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentOperatorAcceptedAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentOperatorAcceptedAudits_DevelopmentOperatorTargetGrants_TargetGrantId",
                        column: x => x.TargetGrantId,
                        principalTable: "DevelopmentOperatorTargetGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobStepRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    JobRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    JobStepId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskRequestId = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobStepRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobStepRuns_JobRuns_JobRunId",
                        column: x => x.JobRunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobStepRuns_JobSteps_JobStepId",
                        column: x => x.JobStepId,
                        principalTable: "JobSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "JobTaskActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    JobRunId = table.Column<long>(type: "INTEGER", nullable: true),
                    JobStepId = table.Column<long>(type: "INTEGER", nullable: true),
                    ClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTaskActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTaskActivities_JobRuns_JobRunId",
                        column: x => x.JobRunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobTaskActivities_JobSteps_JobStepId",
                        column: x => x.JobStepId,
                        principalTable: "JobSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ShellType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CommandHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CommandLength = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentReferencesJson = table.Column<string>(type: "jsonb", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumOutputBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<short>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OutputJson = table.Column<string>(type: "jsonb", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorCommands_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorCommands_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorCommands_McpOperatorIdempotencyRecords_IdempotencyId",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorCommands_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorFileArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReadRootFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorFileArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorFileArtifacts_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorFileArtifacts_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorFileArtifacts_McpOperatorIdempotencyRecords_IdempotencyId",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetSetDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    ResultSummary = table.Column<string>(type: "TEXT", maxLength: 49152, nullable: true),
                    ClaimReferenceHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorRequests_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorRequests_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorRequests_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorRequests_McpOperatorIdempotencyRecords_IdempotencyId",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorRequests_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorRequests_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorTerminalSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Generation = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ShellType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Columns = table.Column<int>(type: "INTEGER", nullable: false),
                    Rows = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<short>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastActivityAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IdleExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CloseRequestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CloseReason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorTerminalSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalSessions_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalSessions_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalSessions_McpOperatorIdempotencyRecords_IdempotencyId",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalSessions_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorJobAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    JobVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorJobAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobAudits_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobAudits_McpOperatorJobs_JobRecordId",
                        column: x => x.JobRecordId,
                        principalTable: "McpOperatorJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorJobRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobRunId = table.Column<long>(type: "INTEGER", precision: 20, scale: 0, nullable: false),
                    JobRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TargetSetDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorJobRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobRuns_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobRuns_McpOperatorIdempotencyRecords_IdempotencyId",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobRuns_McpOperatorJobs_JobRecordId",
                        column: x => x.JobRecordId,
                        principalTable: "McpOperatorJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorScriptVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScriptRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScriptId = table.Column<long>(type: "INTEGER", nullable: false),
                    ScriptVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ShellType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ManifestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DeclaredSideEffectsJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorScriptVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorScriptVersions_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorScriptVersions_McpOperatorScripts_ScriptRecordId",
                        column: x => x.ScriptRecordId,
                        principalTable: "McpOperatorScripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobTaskLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    JobTaskActivityId = table.Column<long>(type: "INTEGER", nullable: true),
                    ClientIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    Stream = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTaskLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTaskLogs_JobTaskActivities_JobTaskActivityId",
                        column: x => x.JobTaskActivityId,
                        principalTable: "JobTaskActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskActivityId = table.Column<long>(type: "INTEGER", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    McpInstance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetSetDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ShellType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CommandHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CommandLength = table.Column<int>(type: "INTEGER", nullable: false),
                    ScriptId = table.Column<long>(type: "INTEGER", nullable: true),
                    ScriptVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    ScriptContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumOutputBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ResultSummary = table.Column<string>(type: "TEXT", maxLength: 49152, nullable: true),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorTasks_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTasks_JobTaskActivities_TaskActivityId",
                        column: x => x.TaskActivityId,
                        principalTable: "JobTaskActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTasks_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTasks_McpOperatorIdempotencyRecords_IdempotencyId",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTasks_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorRequestAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorRequestAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorRequestAudits_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorRequestAudits_McpOperatorRequests_RequestRecordId",
                        column: x => x.RequestRecordId,
                        principalTable: "McpOperatorRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorTerminalActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DelegationRequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Outcome = table.Column<short>(type: "INTEGER", nullable: false),
                    ResultReference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorTerminalActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalActions_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalActions_McpOperatorTerminalSessions_SessionRecordId",
                        column: x => x.SessionRecordId,
                        principalTable: "McpOperatorTerminalSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorTerminalSessionAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<short>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorTerminalSessionAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalSessionAudits_McpOperatorTerminalSessions_SessionRecordId",
                        column: x => x.SessionRecordId,
                        principalTable: "McpOperatorTerminalSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorJobRunAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobRunRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorJobRunAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobRunAudits_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorJobRunAudits_McpOperatorJobRuns_JobRunRecordId",
                        column: x => x.JobRunRecordId,
                        principalTable: "McpOperatorJobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorTaskAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorTaskAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorTaskAudits_McpOperatorAcceptedAudits_AcceptedAuditId",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTaskAudits_McpOperatorTasks_TaskRecordId",
                        column: x => x.TaskRecordId,
                        principalTable: "McpOperatorTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentClientUpdateStates_TenantId_SuspendedAtUtc",
                table: "AgentClientUpdateStates",
                columns: new[] { "TenantId", "SuspendedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentials_AgentId",
                table: "AgentCredentials",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentials_RefreshTokenHash",
                table: "AgentCredentials",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentNonceLogs_AgentId_Nonce",
                table: "AgentNonceLogs",
                columns: new[] { "AgentId", "Nonce" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentNonceLogs_CreatedAtUtc",
                table: "AgentNonceLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRefreshTokens_AgentId",
                table: "AgentRefreshTokens",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRefreshTokens_AgentId_CreatedAtUtc",
                table: "AgentRefreshTokens",
                columns: new[] { "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRefreshTokens_TokenHash",
                table: "AgentRefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_GlobalSearch_DeviceInfoJson_trgm",
                table: "Agents",
                column: "DeviceInfoJson");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_GlobalSearch_Name_trgm",
                table: "Agents",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_Status",
                table: "Agents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_SupersededByAgentId",
                table: "Agents",
                column: "SupersededByAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId",
                table: "Agents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_CreatedAtUtc",
                table: "Agents",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_IsEnabled",
                table: "Agents",
                columns: new[] { "TenantId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_PublicKeyFingerprint",
                table: "Agents",
                columns: new[] { "TenantId", "PublicKeyFingerprint" },
                unique: true,
                filter: "\"PublicKeyFingerprint\" IS NOT NULL AND \"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTokenEvents_TenantId_AgentId_CreatedAtUtc",
                table: "AgentTokenEvents",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_AgentId_ReleaseId",
                table: "ClientUpdateAttempts",
                columns: new[] { "AgentId", "ReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_PublicId",
                table: "ClientUpdateAttempts",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_ReleaseId",
                table: "ClientUpdateAttempts",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_TenantId_AgentId_UpdatedAtUtc",
                table: "ClientUpdateAttempts",
                columns: new[] { "TenantId", "AgentId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateReleases_Enabled_RuntimeId_Channel_PublishedAtUtc",
                table: "ClientUpdateReleases",
                columns: new[] { "Enabled", "RuntimeId", "Channel", "PublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateReleases_PublicId",
                table: "ClientUpdateReleases",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateReleases_Revision",
                table: "ClientUpdateReleases",
                column: "Revision",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateReleases_RuntimeId_Version",
                table: "ClientUpdateReleases",
                columns: new[] { "RuntimeId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionInventoryRefreshes_ClientIdentity_Completed",
                table: "ClientWindowsSessionInventoryRefreshes",
                columns: new[] { "ClientIdentity", "Completed" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionInventoryRefreshes_RefreshRequestId",
                table: "ClientWindowsSessionInventoryRefreshes",
                column: "RefreshRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionInventoryRefreshes_TenantId_ClientIdentity_RequestedAtUtc",
                table: "ClientWindowsSessionInventoryRefreshes",
                columns: new[] { "TenantId", "ClientIdentity", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionSnapshots_ClientIdentity_InventorySequence",
                table: "ClientWindowsSessionSnapshots",
                columns: new[] { "ClientIdentity", "InventorySequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionSnapshots_ClientIdentity_WindowsSessionId",
                table: "ClientWindowsSessionSnapshots",
                columns: new[] { "ClientIdentity", "WindowsSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionSnapshots_ExpiresAtUtc",
                table: "ClientWindowsSessionSnapshots",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionSnapshots_TenantId_ClientIdentity",
                table: "ClientWindowsSessionSnapshots",
                columns: new[] { "TenantId", "ClientIdentity" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandInboxReceipts_FirstReceivedAtUtc",
                table: "CommandInboxReceipts",
                column: "FirstReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommandInboxReceipts_TenantId_CorrelationId",
                table: "CommandInboxReceipts",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "UX_CommandInbox_Idempotency",
                table: "CommandInboxReceipts",
                columns: new[] { "TenantId", "CommandId", "Version", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandIntentEvents_RecordedAtUtc",
                table: "CommandIntentEvents",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommandIntentEvents_TenantId_CorrelationId",
                table: "CommandIntentEvents",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "UX_CommandIntentHistory_Order",
                table: "CommandIntentEvents",
                columns: new[] { "TenantId", "CommandId", "Version", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandOutbox_TenantId_CorrelationId",
                table: "CommandOutbox",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandOutbox_TerminalAtUtc_CreatedAtUtc",
                table: "CommandOutbox",
                columns: new[] { "TerminalAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_CommandOutbox_Command",
                table: "CommandOutbox",
                columns: new[] { "TenantId", "CommandId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpFileArtifacts_TargetGrantId_CreatedAtUtc",
                table: "DevelopmentMcpFileArtifacts",
                columns: new[] { "TargetGrantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpFileArtifacts_TenantId_AgentId_ExpiresAtUtc",
                table: "DevelopmentMcpFileArtifacts",
                columns: new[] { "TenantId", "AgentId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpMarkerJobs_JobId",
                table: "DevelopmentMcpMarkerJobs",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpMarkerJobs_ScriptId",
                table: "DevelopmentMcpMarkerJobs",
                column: "ScriptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpMarkerJobs_TargetGrantId_CreatedAtUtc",
                table: "DevelopmentMcpMarkerJobs",
                columns: new[] { "TargetGrantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpMarkerJobs_TenantId_AgentId_DeletedAtUtc_CreatedAtUtc",
                table: "DevelopmentMcpMarkerJobs",
                columns: new[] { "TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpScripts_ScriptId",
                table: "DevelopmentMcpScripts",
                column: "ScriptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpScripts_TargetGrantId_CreatedAtUtc",
                table: "DevelopmentMcpScripts",
                columns: new[] { "TargetGrantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpScripts_TenantId_AgentId_DeletedAtUtc_CreatedAtUtc",
                table: "DevelopmentMcpScripts",
                columns: new[] { "TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentOperatorAcceptedAudits_TargetGrantId_OccurredAtUtc",
                table: "DevelopmentOperatorAcceptedAudits",
                columns: new[] { "TargetGrantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentOperatorAcceptedAudits_TenantId_AgentId_OccurredAtUtc",
                table: "DevelopmentOperatorAcceptedAudits",
                columns: new[] { "TenantId", "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentOperatorTargetGrants_AgentId_RevokedAtUtc",
                table: "DevelopmentOperatorTargetGrants",
                columns: new[] { "AgentId", "RevokedAtUtc" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentOperatorTargetGrants_TenantId_AgentId_RevokedAtUtc_ExpiresAtUtc",
                table: "DevelopmentOperatorTargetGrants",
                columns: new[] { "TenantId", "AgentId", "RevokedAtUtc", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentCodes_Code",
                table: "EnrollmentCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentCodes_TenantId_DevelopmentMcpTargetAgentId_DevelopmentMcpMarker",
                table: "EnrollmentCodes",
                columns: new[] { "TenantId", "DevelopmentMcpTargetAgentId", "DevelopmentMcpMarker" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentCodes_TenantId_ValidToUtc",
                table: "EnrollmentCodes",
                columns: new[] { "TenantId", "ValidToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobParameters_JobId",
                table: "JobParameters",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_ClientIdentity",
                table: "JobRuns",
                column: "ClientIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_CreatedAtUtc",
                table: "JobRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobId",
                table: "JobRuns",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_TenantId",
                table: "JobRuns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_TenantId_AgentId_CreatedAtUtc",
                table: "JobRuns",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ClientIdentity",
                table: "Jobs",
                column: "ClientIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedAtUtc",
                table: "Jobs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_FolderPath_Name",
                table: "Jobs",
                columns: new[] { "FolderPath", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId",
                table: "Jobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId_AgentId",
                table: "Jobs",
                columns: new[] { "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobShadowObservations_JobRunId_SourceEventId",
                table: "JobShadowObservations",
                columns: new[] { "JobRunId", "SourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobShadowObservations_RecordedAtUtc",
                table: "JobShadowObservations",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobShadowObservations_TenantId_TaskRequestId",
                table: "JobShadowObservations",
                columns: new[] { "TenantId", "TaskRequestId" });

            migrationBuilder.CreateIndex(
                name: "UX_JobShadowObservation_Source",
                table: "JobShadowObservations",
                columns: new[] { "SourceSystem", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobStepRuns_JobRunId",
                table: "JobStepRuns",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobStepRuns_JobRunId_Ordinal",
                table: "JobStepRuns",
                columns: new[] { "JobRunId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_JobStepRuns_JobStepId",
                table: "JobStepRuns",
                column: "JobStepId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSteps_JobId",
                table: "JobSteps",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSteps_JobId_Ordinal",
                table: "JobSteps",
                columns: new[] { "JobId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_CreatedAtUtc",
                table: "JobTaskActivities",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_JobRunId",
                table: "JobTaskActivities",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_JobStepId",
                table: "JobTaskActivities",
                column: "JobStepId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_RequestId",
                table: "JobTaskActivities",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_TenantId_AgentId_CreatedAtUtc",
                table: "JobTaskActivities",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskLogs_JobTaskActivityId",
                table: "JobTaskLogs",
                column: "JobTaskActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskLogs_RequestId",
                table: "JobTaskLogs",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskLogs_RequestId_Sequence_Stream",
                table: "JobTaskLogs",
                columns: new[] { "RequestId", "Sequence", "Stream" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskLogs_TimestampUtc",
                table: "JobTaskLogs",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorAcceptedAudits_PolicyId_OccurredAtUtc",
                table: "McpOperatorAcceptedAudits",
                columns: new[] { "PolicyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorAcceptedAudits_RequestId_OccurredAtUtc",
                table: "McpOperatorAcceptedAudits",
                columns: new[] { "RequestId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorAcceptedAudits_TenantId_AgentId_OccurredAtUtc",
                table: "McpOperatorAcceptedAudits",
                columns: new[] { "TenantId", "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorCommands_AcceptedAuditId",
                table: "McpOperatorCommands",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorCommands_AgentId",
                table: "McpOperatorCommands",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorCommands_CommandId",
                table: "McpOperatorCommands",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorCommands_IdempotencyId",
                table: "McpOperatorCommands",
                column: "IdempotencyId",
                unique: true,
                filter: "\"IdempotencyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorCommands_PolicyId",
                table: "McpOperatorCommands",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorCommands_TenantId_AgentId_Subject_ClientId_State",
                table: "McpOperatorCommands",
                columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorConfirmationPlans_ExpiresAtUtc_ConsumedAtUtc",
                table: "McpOperatorConfirmationPlans",
                columns: new[] { "ExpiresAtUtc", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorConfirmationPlans_TenantId_AgentId_CreatedAtUtc",
                table: "McpOperatorConfirmationPlans",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorConfirmationPlans_TokenHash",
                table: "McpOperatorConfirmationPlans",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorFileArtifacts_AcceptedAuditId",
                table: "McpOperatorFileArtifacts",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorFileArtifacts_AgentId",
                table: "McpOperatorFileArtifacts",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorFileArtifacts_IdempotencyId",
                table: "McpOperatorFileArtifacts",
                column: "IdempotencyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorFileArtifacts_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_ExpiresAtUtc",
                table: "McpOperatorFileArtifacts",
                columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorIdempotencyRecords_Environment_Subject_ClientId_TenantId_OperationFamily_Operation_IdempotencyKey",
                table: "McpOperatorIdempotencyRecords",
                columns: new[] { "Environment", "Subject", "ClientId", "TenantId", "OperationFamily", "Operation", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorIdempotencyRecords_TenantId_AgentId_CreatedAtUtc",
                table: "McpOperatorIdempotencyRecords",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobAudits_AcceptedAuditId",
                table: "McpOperatorJobAudits",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobAudits_JobId_OccurredAtUtc",
                table: "McpOperatorJobAudits",
                columns: new[] { "JobId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobAudits_JobRecordId_JobVersion",
                table: "McpOperatorJobAudits",
                columns: new[] { "JobRecordId", "JobVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobRunAudits_AcceptedAuditId",
                table: "McpOperatorJobRunAudits",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobRunAudits_JobRunRecordId_OccurredAtUtc",
                table: "McpOperatorJobRunAudits",
                columns: new[] { "JobRunRecordId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobRuns_AcceptedAuditId",
                table: "McpOperatorJobRuns",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobRuns_IdempotencyId",
                table: "McpOperatorJobRuns",
                column: "IdempotencyId",
                unique: true,
                filter: "\"IdempotencyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobRuns_JobRecordId",
                table: "McpOperatorJobRuns",
                column: "JobRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobRuns_JobRunId",
                table: "McpOperatorJobRuns",
                column: "JobRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobRuns_TenantId_AgentId_DeletedAtUtc_CreatedAtUtc",
                table: "McpOperatorJobRuns",
                columns: new[] { "TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobs_JobId",
                table: "McpOperatorJobs",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobs_PolicyId",
                table: "McpOperatorJobs",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorJobs_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_DeletedAtUtc_UpdatedAtUtc",
                table: "McpOperatorJobs",
                columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "DeletedAtUtc", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicies_Environment_PrincipalSelectorKind_PrincipalSelectorValue",
                table: "McpOperatorPolicies",
                columns: new[] { "Environment", "PrincipalSelectorKind", "PrincipalSelectorValue" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicies_Environment_TargetSelectorKind_TenantId_AgentId",
                table: "McpOperatorPolicies",
                columns: new[] { "Environment", "TargetSelectorKind", "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicies_Environment_TenantId_DisabledAtUtc_ExpiresAtUtc_Effect_Priority",
                table: "McpOperatorPolicies",
                columns: new[] { "Environment", "TenantId", "DisabledAtUtc", "ExpiresAtUtc", "Effect", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicyChangeAudits_AgentId_OccurredAtUtc",
                table: "McpOperatorPolicyChangeAudits",
                columns: new[] { "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicyChangeAudits_PolicyId_OccurredAtUtc",
                table: "McpOperatorPolicyChangeAudits",
                columns: new[] { "PolicyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicyChangeAudits_TenantId_OccurredAtUtc",
                table: "McpOperatorPolicyChangeAudits",
                columns: new[] { "TenantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequestAudits_AcceptedAuditId",
                table: "McpOperatorRequestAudits",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequestAudits_RequestRecordId_OccurredAtUtc",
                table: "McpOperatorRequestAudits",
                columns: new[] { "RequestRecordId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequests_AcceptedAuditId",
                table: "McpOperatorRequests",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequests_AgentId",
                table: "McpOperatorRequests",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequests_IdempotencyId",
                table: "McpOperatorRequests",
                column: "IdempotencyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequests_JobId_UpdatedAtUtc",
                table: "McpOperatorRequests",
                columns: new[] { "JobId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequests_PolicyId",
                table: "McpOperatorRequests",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequests_RequestId",
                table: "McpOperatorRequests",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorRequests_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_State_UpdatedAtUtc",
                table: "McpOperatorRequests",
                columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "State", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorScripts_PolicyId",
                table: "McpOperatorScripts",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorScripts_ScriptId",
                table: "McpOperatorScripts",
                column: "ScriptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorScripts_TenantId_Subject_ClientId_McpResource_McpInstance_DeletedAtUtc_UpdatedAtUtc",
                table: "McpOperatorScripts",
                columns: new[] { "TenantId", "Subject", "ClientId", "McpResource", "McpInstance", "DeletedAtUtc", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorScriptVersions_AcceptedAuditId",
                table: "McpOperatorScriptVersions",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorScriptVersions_ScriptId_OccurredAtUtc",
                table: "McpOperatorScriptVersions",
                columns: new[] { "ScriptId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorScriptVersions_ScriptRecordId_ScriptVersion",
                table: "McpOperatorScriptVersions",
                columns: new[] { "ScriptRecordId", "ScriptVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTargetProfiles_TenantId_Classification",
                table: "McpOperatorTargetProfiles",
                columns: new[] { "TenantId", "Classification" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTaskAudits_AcceptedAuditId",
                table: "McpOperatorTaskAudits",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTaskAudits_TaskRecordId_OccurredAtUtc",
                table: "McpOperatorTaskAudits",
                columns: new[] { "TaskRecordId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTasks_AcceptedAuditId",
                table: "McpOperatorTasks",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTasks_AgentId",
                table: "McpOperatorTasks",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTasks_CommandId",
                table: "McpOperatorTasks",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTasks_IdempotencyId",
                table: "McpOperatorTasks",
                column: "IdempotencyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTasks_PolicyId",
                table: "McpOperatorTasks",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTasks_TaskActivityId",
                table: "McpOperatorTasks",
                column: "TaskActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTasks_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_State_UpdatedAtUtc",
                table: "McpOperatorTasks",
                columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "State", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalActions_AcceptedAuditId",
                table: "McpOperatorTerminalActions",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalActions_SessionRecordId_CreatedAtUtc",
                table: "McpOperatorTerminalActions",
                columns: new[] { "SessionRecordId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalActions_SessionRecordId_Operation_DelegationRequestId",
                table: "McpOperatorTerminalActions",
                columns: new[] { "SessionRecordId", "Operation", "DelegationRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalSessionAudits_SessionRecordId_OccurredAtUtc",
                table: "McpOperatorTerminalSessionAudits",
                columns: new[] { "SessionRecordId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalSessions_AcceptedAuditId",
                table: "McpOperatorTerminalSessions",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalSessions_AgentId",
                table: "McpOperatorTerminalSessions",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalSessions_IdempotencyId",
                table: "McpOperatorTerminalSessions",
                column: "IdempotencyId",
                unique: true,
                filter: "\"IdempotencyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalSessions_PolicyId",
                table: "McpOperatorTerminalSessions",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalSessions_SessionId",
                table: "McpOperatorTerminalSessions",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalSessions_TenantId_AgentId_State_ExpiresAtUtc",
                table: "McpOperatorTerminalSessions",
                columns: new[] { "TenantId", "AgentId", "State", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalSessions_TenantId_AgentId_Subject_ClientId_State",
                table: "McpOperatorTerminalSessions",
                columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_OidcSigningKeys_IsActive",
                table: "OidcSigningKeys",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_OidcSigningKeys_KeyId",
                table: "OidcSigningKeys",
                column: "KeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CorrelationId",
                table: "OutboxMessages",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_EntityId",
                table: "OutboxMessages",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OccurredUtc",
                table: "OutboxMessages",
                column: "OccurredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_NextAttemptUtc_LockedUntilUtc",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextAttemptUtc", "LockedUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Type",
                table: "OutboxMessages",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxProcessedEvents_ProcessedUtc",
                table: "OutboxProcessedEvents",
                column: "ProcessedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxReadReceipts_UserId_ReadUtc",
                table: "OutboxReadReceipts",
                columns: new[] { "UserId", "ReadUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryClientAgentBindings_AgentId",
                table: "PrimaryClientAgentBindings",
                column: "AgentId",
                unique: true,
                filter: "\"AgentId\" IS NOT NULL AND \"Status\" <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryClientAgentBindings_EnrollmentCodeId",
                table: "PrimaryClientAgentBindings",
                column: "EnrollmentCodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryClientAgentBindings_TenantId_PrimaryClientIdentity",
                table: "PrimaryClientAgentBindings",
                columns: new[] { "TenantId", "PrimaryClientIdentity" },
                unique: true,
                filter: "\"Status\" <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryClientAgentBindings_TenantId_Status_CreatedAtUtc",
                table: "PrimaryClientAgentBindings",
                columns: new[] { "TenantId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportAuditEvents_RemoteSupportSessionId_AuditSequence",
                table: "RemoteSupportAuditEvents",
                columns: new[] { "RemoteSupportSessionId", "AuditSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportAuditEvents_RemoteSupportSessionId_RequestId",
                table: "RemoteSupportAuditEvents",
                columns: new[] { "RemoteSupportSessionId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportAuditEvents_TenantId_AgentId_OccurredAtUtc",
                table: "RemoteSupportAuditEvents",
                columns: new[] { "TenantId", "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportSessions_ExpiresAtUtc",
                table: "RemoteSupportSessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportSessions_TenantId_AgentId_CreatedAtUtc",
                table: "RemoteSupportSessions",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportSessions_TenantId_AgentId_OpenRequestId",
                table: "RemoteSupportSessions",
                columns: new[] { "TenantId", "AgentId", "OpenRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportSessions_TenantId_InitiatingOperatorId_UpdatedAtUtc",
                table: "RemoteSupportSessions",
                columns: new[] { "TenantId", "InitiatingOperatorId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportTargetSelectionEvents_SessionId",
                table: "RemoteSupportTargetSelectionEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportTargetSelectionEvents_TenantId_ClientIdentity_RequestedAtUtc",
                table: "RemoteSupportTargetSelectionEvents",
                columns: new[] { "TenantId", "ClientIdentity", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_CreatedAtUtc",
                table: "Requests",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_Status",
                table: "Requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_TargetClientIdentity",
                table: "Requests",
                column: "TargetClientIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_TargetTenantId_TargetAgentId",
                table: "Requests",
                columns: new[] { "TargetTenantId", "TargetAgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_UpdatedAtUtc",
                table: "Requests",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptParameters_ScriptId",
                table: "ScriptParameters",
                column: "ScriptId");

            migrationBuilder.CreateIndex(
                name: "IX_Scripts_CreatedAtUtc",
                table: "Scripts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Scripts_FolderPath_Name",
                table: "Scripts",
                columns: new[] { "FolderPath", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Secrets_ClientIdentity",
                table: "Secrets",
                column: "ClientIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_Secrets_CreatedAtUtc",
                table: "Secrets",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Secrets_TenantId",
                table: "Secrets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_CreatedAtUtc",
                table: "Tenants",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Name",
                table: "Tenants",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentClientUpdateStates");

            migrationBuilder.DropTable(
                name: "AgentCredentials");

            migrationBuilder.DropTable(
                name: "AgentNonceLogs");

            migrationBuilder.DropTable(
                name: "AgentRefreshTokens");

            migrationBuilder.DropTable(
                name: "AgentTokenEvents");

            migrationBuilder.DropTable(
                name: "ClientUpdateAttempts");

            migrationBuilder.DropTable(
                name: "ClientUpdateCatalogRevision");

            migrationBuilder.DropTable(
                name: "ClientWindowsSessionInventoryRefreshes");

            migrationBuilder.DropTable(
                name: "ClientWindowsSessionSnapshots");

            migrationBuilder.DropTable(
                name: "CommandInboxReceipts");

            migrationBuilder.DropTable(
                name: "CommandIntentEvents");

            migrationBuilder.DropTable(
                name: "CommandOutbox");

            migrationBuilder.DropTable(
                name: "DevelopmentMcpFileArtifacts");

            migrationBuilder.DropTable(
                name: "DevelopmentMcpMarkerJobs");

            migrationBuilder.DropTable(
                name: "DevelopmentMcpScripts");

            migrationBuilder.DropTable(
                name: "DevelopmentOperatorAcceptedAudits");

            migrationBuilder.DropTable(
                name: "JobParameters");

            migrationBuilder.DropTable(
                name: "JobShadowObservations");

            migrationBuilder.DropTable(
                name: "JobStepRuns");

            migrationBuilder.DropTable(
                name: "JobTaskLogs");

            migrationBuilder.DropTable(
                name: "M2MConnectivitySettings");

            migrationBuilder.DropTable(
                name: "McpOperatorCommands");

            migrationBuilder.DropTable(
                name: "McpOperatorConfirmationPlans");

            migrationBuilder.DropTable(
                name: "McpOperatorFileArtifacts");

            migrationBuilder.DropTable(
                name: "McpOperatorJobAudits");

            migrationBuilder.DropTable(
                name: "McpOperatorJobRunAudits");

            migrationBuilder.DropTable(
                name: "McpOperatorPolicyChangeAudits");

            migrationBuilder.DropTable(
                name: "McpOperatorRequestAudits");

            migrationBuilder.DropTable(
                name: "McpOperatorScriptVersions");

            migrationBuilder.DropTable(
                name: "McpOperatorTargetProfiles");

            migrationBuilder.DropTable(
                name: "McpOperatorTaskAudits");

            migrationBuilder.DropTable(
                name: "McpOperatorTerminalActions");

            migrationBuilder.DropTable(
                name: "McpOperatorTerminalSessionAudits");

            migrationBuilder.DropTable(
                name: "OidcSigningKeys");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "OutboxProcessedEvents");

            migrationBuilder.DropTable(
                name: "OutboxReadReceipts");

            migrationBuilder.DropTable(
                name: "PrimaryClientAgentBindings");

            migrationBuilder.DropTable(
                name: "RemoteSupportAuditEvents");

            migrationBuilder.DropTable(
                name: "RemoteSupportTargetSelectionEvents");

            migrationBuilder.DropTable(
                name: "ScriptParameters");

            migrationBuilder.DropTable(
                name: "Secrets");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "ClientUpdateReleases");

            migrationBuilder.DropTable(
                name: "DevelopmentOperatorTargetGrants");

            migrationBuilder.DropTable(
                name: "McpOperatorJobRuns");

            migrationBuilder.DropTable(
                name: "McpOperatorRequests");

            migrationBuilder.DropTable(
                name: "McpOperatorScripts");

            migrationBuilder.DropTable(
                name: "McpOperatorTasks");

            migrationBuilder.DropTable(
                name: "McpOperatorTerminalSessions");

            migrationBuilder.DropTable(
                name: "EnrollmentCodes");

            migrationBuilder.DropTable(
                name: "RemoteSupportSessions");

            migrationBuilder.DropTable(
                name: "McpOperatorJobs");

            migrationBuilder.DropTable(
                name: "Requests");

            migrationBuilder.DropTable(
                name: "Scripts");

            migrationBuilder.DropTable(
                name: "JobTaskActivities");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "McpOperatorAcceptedAudits");

            migrationBuilder.DropTable(
                name: "McpOperatorIdempotencyRecords");

            migrationBuilder.DropTable(
                name: "JobRuns");

            migrationBuilder.DropTable(
                name: "JobSteps");

            migrationBuilder.DropTable(
                name: "McpOperatorPolicies");

            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
