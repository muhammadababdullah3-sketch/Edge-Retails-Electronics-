using EdgeRetails.Domain.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class PhysicalLabelPrintIntentConfiguration : IEntityTypeConfiguration<PhysicalLabelPrintIntent>
{
    public void Configure(EntityTypeBuilder<PhysicalLabelPrintIntent> builder)
    {
        builder.ToTable("physical_label_print_intent", "production");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TargetWorkstationId).HasMaxLength(120);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => new { x.InventoryUnitId, x.Kind })
            .IsUnique()
            .HasFilter("kind = 0");
        builder.HasIndex(x => x.SourceClientOperationId);
        builder.HasIndex(x => x.Status);
    }
}
