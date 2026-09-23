using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class InstallationStateConfiguration
    : IEntityTypeConfiguration<InstallationState>
{
    public void Configure(EntityTypeBuilder<InstallationState> builder)
    {
        builder.ToTable(
            "installation_state",
            "system",
            table => table.HasCheckConstraint(
                "ck_installation_state_singleton",
                "singleton_key = 'PRIMARY'"));

        builder.HasKey(x => x.InstallationId);
        builder.Property(x => x.InstallationId).ValueGeneratedNever();
        builder.Property<string>("SingletonKey")
            .HasColumnName("singleton_key")
            .HasMaxLength(20)
            .HasDefaultValue("PRIMARY")
            .IsRequired();
        builder.Property(x => x.SelectedModule).HasMaxLength(80);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex("SingletonKey").IsUnique();
    }
}
