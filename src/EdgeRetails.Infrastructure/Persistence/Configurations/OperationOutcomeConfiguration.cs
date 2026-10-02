using EdgeRetails.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class OperationOutcomeConfiguration : IEntityTypeConfiguration<OperationOutcome>
{
    public void Configure(EntityTypeBuilder<OperationOutcome> builder)
    {
        builder.ToTable("operation_outcomes", "system");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ClientOperationId)
            .IsRequired();

        builder.Property(x => x.OperationType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.PayloadFingerprint)
            .HasMaxLength(128);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.ResultEntityType)
            .HasMaxLength(100);

        builder.Property(x => x.ResultEntityId);

        builder.Property(x => x.DocumentNumber)
            .HasMaxLength(100);

        builder.Property(x => x.ActorUserId);

        builder.Property(x => x.TerminalId);

        builder.Property(x => x.SessionId);

        builder.Property(x => x.WasCommitted)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();

        builder.Property(x => x.CompletedAt);

        builder.Property(x => x.ErrorCode)
            .HasMaxLength(100);

        builder.Property(x => x.ErrorMessage)
            .HasMaxLength(4000);

        builder.HasIndex(x => x.ClientOperationId)
            .IsUnique()
            .HasDatabaseName("ix_operation_outcomes_client_operation_id");

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("ix_operation_outcomes_status");

        builder.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("ix_operation_outcomes_created_at");
    }
}
