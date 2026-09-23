using EdgeRetails.Domain.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class ExpenseCategoryConfiguration
    : IEntityTypeConfiguration<ExpenseCategory>
{
    public void Configure(EntityTypeBuilder<ExpenseCategory> builder)
    {
        builder.ToTable("expense_categories", "finance");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class ExpenseSubcategoryConfiguration
    : IEntityTypeConfiguration<ExpenseSubcategory>
{
    public void Configure(EntityTypeBuilder<ExpenseSubcategory> builder)
    {
        builder.ToTable("expense_subcategories", "finance");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasOne<ExpenseCategory>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict); builder.HasIndex(x => new { x.CategoryId, x.Name }).IsUnique();
    }
}

internal sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> builder)
    {
        builder.ToTable(
            "expenses",
            "finance",
            table => table.HasCheckConstraint(
                "ck_expense_amount_positive",
                "amount > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ExpenseNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Reference).HasMaxLength(160);
        builder.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.VoidReason).HasMaxLength(500);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<ExpenseCategory>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExpenseSubcategory>()
            .WithMany()
            .HasForeignKey(x => x.SubcategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>()
            .WithMany()
            .HasForeignKey(x => x.CashSessionId)
            .OnDelete(DeleteBehavior.Restrict); builder.HasIndex(x => x.ExpenseNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.ExpenseDate, x.Status });
        builder.HasIndex(x => x.CategoryId);
    }
}
