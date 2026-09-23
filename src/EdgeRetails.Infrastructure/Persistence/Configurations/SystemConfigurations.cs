using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class DocumentNumberCounterConfiguration
    : IEntityTypeConfiguration<DocumentNumberCounter>
{
    public void Configure(EntityTypeBuilder<DocumentNumberCounter> builder)
    {
        builder.ToTable(
            "document_sequences",
            "system",
            table => table.HasCheckConstraint(
                "ck_document_sequence_nonnegative",
                "last_value >= 0"));

        builder.HasKey(x => x.Series);
        builder.Property(x => x.Series).HasMaxLength(20);
        builder.Property(x => x.LastValue);
    }
}
