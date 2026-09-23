using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Parties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class SupplierProductConfiguration : IEntityTypeConfiguration<SupplierProduct>
{
    public void Configure(EntityTypeBuilder<SupplierProduct> builder)
    {
        builder.ToTable(
            "supplier_products",
            "catalog",
            table => table.HasCheckConstraint(
                "ck_supplier_products_next_sequence_positive",
                "next_item_sequence >= 1"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.SupplierId, x.ProductId }).IsUnique();
        builder.HasIndex(x => new { x.ProductId, x.IsActive });
        builder.HasIndex(x => new { x.SupplierId, x.IsActive });
    }
}

internal sealed class SupplierCodeSequenceConfiguration : IEntityTypeConfiguration<SupplierCodeSequence>
{
    public void Configure(EntityTypeBuilder<SupplierCodeSequence> builder)
    {
        builder.ToTable(
            "supplier_code_sequences",
            "system",
            table => table.HasCheckConstraint(
                "ck_supplier_code_sequences_next_positive",
                "next_value >= 1"));

        builder.HasKey(x => x.Prefix);
        builder.Property(x => x.Prefix).HasMaxLength(2);
    }
}
