using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>Exact outbound request, separated from transcript messages so context cannot create duplicate turns.</summary>
public sealed class AiInvestigationContextRecord
{
    public Guid InvestigationId { get; set; }
    public Guid MessageId { get; set; }
    public string Request { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public string OmissionNotesJson { get; set; } = "[]";
    public int PayloadBytes { get; set; }
    public DateTimeOffset PreparedAtUtc { get; set; }
    public DateTimeOffset? SendAttemptedAtUtc { get; set; }

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<AiInvestigationContextRecord>(entity =>
        {
            entity.ToTable("AiInvestigationContexts"); entity.HasKey(x => new { x.InvestigationId, x.MessageId });
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");
            entity.Property(x => x.OmissionNotesJson).HasColumnType("jsonb");
            entity.HasOne<AiInvestigationRecord>().WithMany().HasForeignKey(x => x.InvestigationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
