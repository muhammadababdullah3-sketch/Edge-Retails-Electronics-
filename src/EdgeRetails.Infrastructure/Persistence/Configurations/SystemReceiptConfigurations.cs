using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class ShopProfileConfiguration
    : IEntityTypeConfiguration<ShopProfile>
{
    public void Configure(EntityTypeBuilder<ShopProfile> builder)
    {
        builder.ToTable(
            "shop_profile",
            "system",
            table => table.HasCheckConstraint(
                "ck_shop_profile_primary_key",
                "profile_key = 'PRIMARY'"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ProfileKey).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ShopName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Phone).HasMaxLength(80);
        builder.Property(x => x.Address).HasMaxLength(500);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.ProfileKey).IsUnique();
    }
}

internal sealed class ReceiptTemplateSettingsConfiguration
    : IEntityTypeConfiguration<ReceiptTemplateSettings>
{
    public void Configure(EntityTypeBuilder<ReceiptTemplateSettings> builder)
    {
        builder.ToTable(
            "receipt_template_settings",
            "system",
            table => table.HasCheckConstraint(
                "ck_receipt_template_primary_key",
                "template_key = 'PRIMARY' AND template_version > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TemplateKey).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Header).HasMaxLength(1000);
        builder.Property(x => x.Footer).HasMaxLength(1000);
        builder.Property(x => x.LogoBehavior).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.TemplateKey).IsUnique();
    }
}
