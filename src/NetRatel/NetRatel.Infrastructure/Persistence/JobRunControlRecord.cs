using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Durable transport intent and terminal publication boundary owned by one run actor.</summary>
public sealed class JobRunControlRecord
{
    public long RunId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset? DispatchPreparedAtUtc { get; set; }
    public DateTimeOffset? DispatchEnqueuedAtUtc { get; set; }
    // Nullable for historical intent; never synthesize authority on upgrade.
    public Guid? DispatchOwnerConnectionId { get; set; }
    public long? DispatchOwnerEpoch { get; set; }
    public DateTimeOffset? NativeDeadlineUtc { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset? CancellationEnqueuedAtUtc { get; set; }
    public DateTimeOffset? TerminalReadyAtUtc { get; set; }
    public string? TerminalResultHash { get; set; }
}

public static class JobRunControlModelConfiguration
{
    public static void ConfigureJobRunControlModel(this ModelBuilder model) => model.Entity<JobRunControlRecord>(e =>
    {
        e.ToTable("JobRunControls", table => table.HasCheckConstraint(
            "CK_JobRunControls_DispatchOwner",
            "(\"DispatchOwnerConnectionId\" IS NULL AND \"DispatchOwnerEpoch\" IS NULL) OR " +
            "(\"DispatchOwnerConnectionId\" IS NOT NULL AND \"DispatchOwnerConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid " +
            "AND \"DispatchOwnerEpoch\" IS NOT NULL AND \"DispatchOwnerEpoch\" > 0)"));
        e.HasKey(x => x.RunId);
        e.Property(x => x.Revision).IsConcurrencyToken();
        e.Property(x => x.CancellationReason).HasMaxLength(256);
        e.Property(x => x.TerminalResultHash).HasMaxLength(64);
        e.HasIndex(x => new { x.TerminalReadyAtUtc, x.NativeDeadlineUtc });
        e.HasOne<JobRunRecord>().WithOne().HasForeignKey<JobRunControlRecord>(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    });
}
