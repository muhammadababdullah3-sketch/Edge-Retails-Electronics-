using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Audit;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Persistence;

public sealed class EdgeRetailsDbContext : DbContext, IUnitOfWork
{
    public EdgeRetailsDbContext(DbContextOptions<EdgeRetailsDbContext> options)
        : base(options)
    {
    }

    public DbSet<BusinessAuditEvent> BusinessAuditEvents => Set<BusinessAuditEvent>();

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<InstallationState> InstallationStates => Set<InstallationState>();
    public DbSet<ShopProfile> ShopProfiles => Set<ShopProfile>();
    public DbSet<ReceiptTemplateSettings> ReceiptTemplateSettings => Set<ReceiptTemplateSettings>();

    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductUnit> ProductUnits => Set<ProductUnit>();
    public DbSet<ProductUnitBarcode> ProductUnitBarcodes => Set<ProductUnitBarcode>();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierCodeSequence> SupplierCodeSequences => Set<SupplierCodeSequence>();

    public DbSet<SupplierProduct> SupplierProducts => Set<SupplierProduct>();

    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<InventoryMovementEffect> InventoryMovementEffects => Set<InventoryMovementEffect>();
    public DbSet<InventoryMovementUnit> InventoryMovementUnits => Set<InventoryMovementUnit>();
    public DbSet<InventoryUnit> InventoryUnits => Set<InventoryUnit>();
    public DbSet<ProductCostState> ProductCostStates => Set<ProductCostState>();
    public DbSet<InventoryLot> InventoryLots => Set<InventoryLot>();
    public DbSet<InventoryLotBucketBalance> InventoryLotBucketBalances => Set<InventoryLotBucketBalance>();
    public DbSet<InventoryLotConsumption> InventoryLotConsumptions => Set<InventoryLotConsumption>();
    public DbSet<Stocktake> Stocktakes => Set<Stocktake>();
    public DbSet<StocktakeItem> StocktakeItems => Set<StocktakeItem>();
    public DbSet<StocktakeUnitCheck> StocktakeUnitChecks => Set<StocktakeUnitCheck>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<StockAdjustmentItem> StockAdjustmentItems => Set<StockAdjustmentItem>();

    public DbSet<WarrantyClaim> WarrantyClaims => Set<WarrantyClaim>();
    public DbSet<WarrantyClaimItem> WarrantyClaimItems => Set<WarrantyClaimItem>();
    public DbSet<WarrantyClaimItemUnit> WarrantyClaimItemUnits => Set<WarrantyClaimItemUnit>();
    public DbSet<WarrantyClaimEvent> WarrantyClaimEvents => Set<WarrantyClaimEvent>();
    public DbSet<WarrantyOperation> WarrantyOperations => Set<WarrantyOperation>();
    public DbSet<ShopStockWarrantyCase> ShopStockWarrantyCases => Set<ShopStockWarrantyCase>();

    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<SupplierAccountEntry> SupplierAccountEntries => Set<SupplierAccountEntry>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<SupplierPaymentReversal> SupplierPaymentReversals => Set<SupplierPaymentReversal>();
    public DbSet<SupplierRefund> SupplierRefunds => Set<SupplierRefund>();
    public DbSet<SupplierRefundReversal> SupplierRefundReversals => Set<SupplierRefundReversal>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<ExpenseSubcategory> ExpenseSubcategories => Set<ExpenseSubcategory>();
    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<PosDraft> PosDrafts => Set<PosDraft>();
    public DbSet<PosDraftItem> PosDraftItems => Set<PosDraftItem>();
    public DbSet<Quotation> Quotations => Set<Quotation>();
    public DbSet<QuotationItem> QuotationItems => Set<QuotationItem>();
    public DbSet<QuotationOperation> QuotationOperations => Set<QuotationOperation>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<SaleItemUnit> SaleItemUnits => Set<SaleItemUnit>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<SaleReturnItem> SaleReturnItems => Set<SaleReturnItem>();
    public DbSet<SaleReturnItemUnit> SaleReturnItemUnits => Set<SaleReturnItemUnit>();

    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<PurchaseItemUnit> PurchaseItemUnits => Set<PurchaseItemUnit>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnItem> PurchaseReturnItems => Set<PurchaseReturnItem>();
    public DbSet<PurchaseReturnItemUnit> PurchaseReturnItemUnits => Set<PurchaseReturnItemUnit>();
    public DbSet<PurchaseVoid> PurchaseVoids => Set<PurchaseVoid>();

    public DbSet<ThakaProject> ThakaProjects => Set<ThakaProject>();
    public DbSet<ThakaMaterialIssue> ThakaMaterialIssues => Set<ThakaMaterialIssue>();
    public DbSet<ThakaMaterialIssueItem> ThakaMaterialIssueItems => Set<ThakaMaterialIssueItem>();
    public DbSet<ThakaMaterialIssueUnit> ThakaMaterialIssueUnits => Set<ThakaMaterialIssueUnit>();
    public DbSet<ThakaPayment> ThakaPayments => Set<ThakaPayment>();
    public DbSet<ThakaSettlement> ThakaSettlements => Set<ThakaSettlement>();
    public DbSet<ThakaReopening> ThakaReopenings => Set<ThakaReopening>();
    public DbSet<ThakaMaterialReversal> ThakaMaterialReversals => Set<ThakaMaterialReversal>();
    public DbSet<ThakaPaymentReversal> ThakaPaymentReversals => Set<ThakaPaymentReversal>();

    internal DbSet<DocumentNumberCounter> DocumentNumberCounters =>
        Set<DocumentNumberCounter>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Terminal> Terminals => Set<Terminal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EdgeRetailsDbContext).Assembly);
        ApplySnakeCaseDatabaseNames(modelBuilder);
    }

    public override int SaveChanges()
    {
        EnforceAppendOnlyAudit();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        EnforceAppendOnlyAudit();
        return base.SaveChangesAsync(cancellationToken);
    }

    Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesAsync(cancellationToken);

    private void EnforceAppendOnlyAudit()
    {
        if (ChangeTracker.Entries<BusinessAuditEvent>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Business audit events are append-only and cannot be modified or deleted.");
        }
    }

    private static void ApplySnakeCaseDatabaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName() is { } columnName)
                {
                    property.SetColumnName(ToSnakeCase(columnName));
                }
            }

            foreach (var key in entityType.GetKeys())
            {
                if (key.GetName() is { } keyName)
                {
                    key.SetName(ToSnakeCase(keyName));
                }
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                if (foreignKey.GetConstraintName() is { } constraintName)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(constraintName));
                }
            }

            foreach (var index in entityType.GetIndexes())
            {
                if (index.GetDatabaseName() is { } indexName)
                {
                    index.SetDatabaseName(ToSnakeCase(indexName));
                }
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current) &&
                index > 0 &&
                (char.IsLower(value[index - 1]) ||
                 (index + 1 < value.Length && char.IsLower(value[index + 1]))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}

internal sealed class DocumentNumberCounter
{
    public string Series { get; set; } = string.Empty;
    public long LastValue { get; set; }
}
