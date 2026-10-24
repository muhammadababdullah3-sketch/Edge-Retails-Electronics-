using EdgeRetails.Domain.Posting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class PeriodLockConfiguration : IEntityTypeConfiguration<PeriodLock>
{
    public void Configure(EntityTypeBuilder<PeriodLock> builder)
    {
        builder.ToTable("period_locks", "posting");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.PeriodName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.PeriodStartDate).IsRequired();
        builder.Property(x => x.PeriodEndDate).IsRequired();
        builder.Property(x => x.IsLocked).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.LockedAt).IsRequired();
        builder.Property(x => x.LockedBy).IsRequired();
        builder.Property(x => x.LockReason).HasMaxLength(500);

        builder.HasIndex(x => new { x.IsLocked, x.PeriodStartDate, x.PeriodEndDate });
        builder.ToTable(t => t.HasCheckConstraint("chk_period_lock_dates", "period_end_date >= period_start_date"));
    }
}

internal sealed class PostingAuditEntryConfiguration : IEntityTypeConfiguration<PostingAuditEntry>
{
    public void Configure(EntityTypeBuilder<PostingAuditEntry> builder)
    {
        builder.ToTable("posting_audit_entries", "posting");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.OperationType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.EntityId).IsRequired();
        builder.Property(x => x.EntityNumber).HasMaxLength(120).IsRequired();
        builder.Property(x => x.ActorId).IsRequired();
        builder.Property(x => x.SupervisorId);
        builder.Property(x => x.ShopDate).IsRequired();
        builder.Property(x => x.PostingDate).IsRequired();
        builder.Property(x => x.SupplierDocumentDate);
        builder.Property(x => x.EventTimestampUtc).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.HasIndex(x => x.EntityId);
        builder.HasIndex(x => x.PostingDate);
        builder.HasIndex(x => x.ShopDate);
        builder.HasIndex(x => x.OperationType);
        builder.ToTable(t => t.HasCheckConstraint("chk_posting_audit_dates", "posting_date <= shop_date"));
    }
}
