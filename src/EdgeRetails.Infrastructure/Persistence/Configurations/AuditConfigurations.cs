using EdgeRetails.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class BusinessAuditEventConfiguration
    : IEntityTypeConfiguration<BusinessAuditEvent>
{
    public void Configure(EntityTypeBuilder<BusinessAuditEvent> builder)
    {
        builder.ToTable("business_events", "audit");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Action).HasMaxLength(120).IsRequired();
        builder.Property(x => x.EntityType).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Summary).HasMaxLength(1000);

        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAt });
        builder.HasIndex(x => x.ActorId);
        builder.HasIndex(x => x.CorrelationId);
    }
}
