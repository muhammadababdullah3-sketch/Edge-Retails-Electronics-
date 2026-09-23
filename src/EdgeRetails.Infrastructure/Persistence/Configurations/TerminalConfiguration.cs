using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class TerminalConfiguration : IEntityTypeConfiguration<Terminal>
{
    public void Configure(EntityTypeBuilder<Terminal> builder)
    {
        builder.ToTable("terminals", "system");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.TerminalCode)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.HardwareFingerprint)
            .HasMaxLength(200);

        builder.Property(x => x.ProtocolVersion)
            .HasMaxLength(50);

        builder.Property(x => x.LastKnownIpAddress)
            .HasMaxLength(50);

        builder.Property(x => x.AuthSecretHash)
            .HasMaxLength(500);

        builder.Property(x => x.RegisteredAt)
            .IsRequired();

        builder.Property(x => x.LastSeenAt)
            .IsRequired();

        builder.HasIndex(x => x.TerminalCode)
            .IsUnique()
            .HasDatabaseName("ix_terminals_terminal_code");

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("ix_terminals_status");
    }
}
