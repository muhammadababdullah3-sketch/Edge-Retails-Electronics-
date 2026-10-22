using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022210000_CanonicalOrderContract")]
public sealed class CanonicalOrderContract : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("PlanId", "orders", schema: "central", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("OrderType", "orders", schema: "central", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<int>("RequestedPrimaryCapacity", "orders", schema: "central", type: "integer", nullable: true);
        migrationBuilder.AddColumn<Guid>("BaseLicenseId", "orders", schema: "central", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>("ExistingEntitlementId", "orders", schema: "central", type: "uuid", nullable: true);

        migrationBuilder.Sql("""
            UPDATE central.orders AS o
            SET "PlanId" = p."CatalogItemId",
                "OrderType" = CASE o."PurchaseTarget"
                    WHEN 'BASE_LICENSE' THEN 'NEW_LICENSE'
                    WHEN 'SUPPORT' THEN 'SUPPORT_RENEWAL'
                    WHEN 'PREMIUM_UPGRADE' THEN 'UPGRADE'
                    ELSE NULL
                END,
                "RequestedPrimaryCapacity" = 1
            FROM central.pricing_snapshots AS p
            WHERE p."Id" = o."SnapshotId";
            """);

        migrationBuilder.AlterColumn<Guid>("PlanId", "orders", schema: "central", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
        migrationBuilder.AlterColumn<string>("OrderType", "orders", schema: "central", type: "character varying(32)", maxLength: 32, nullable: false, oldClrType: typeof(string), oldType: "character varying(32)", oldMaxLength: 32, oldNullable: true);
        migrationBuilder.AlterColumn<int>("RequestedPrimaryCapacity", "orders", schema: "central", type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldNullable: true);

        migrationBuilder.CreateIndex("IX_orders_PlanId", "orders", "PlanId", schema: "central");
        migrationBuilder.CreateIndex("IX_orders_BaseLicenseId", "orders", "BaseLicenseId", schema: "central");
        migrationBuilder.CreateIndex("IX_orders_ExistingEntitlementId", "orders", "ExistingEntitlementId", schema: "central");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Canonical order payload identity is persisted; use an approved forward migration rather than dropping it during rollback.");
    }
}
