using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", "system");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.EffectType)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.SourceType)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.SourceId)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.PayloadJson)
            .IsRequired();

        builder.Property(x => x.IdempotencyKey)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.AttemptCount)
            .IsRequired();

        builder.Property(x => x.NextAttemptAt);

        builder.Property(x => x.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.LastError)
            .HasMaxLength(2000);

        builder.Property(x => x.CompletedAt);

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ix_outbox_messages_idempotency_key");

        builder.HasIndex(x => new { x.Status, x.NextAttemptAt })
            .HasDatabaseName("ix_outbox_messages_status_next_attempt");
    }
}
