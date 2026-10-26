using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022190000_DurableNotificationWorker")]
public sealed class DurableNotificationWorker : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("ClaimToken", "outbox_messages", "uuid", schema: "central", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("ClaimExpiresAtUtc", "outbox_messages", "timestamp with time zone", schema: "central", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("NextAttemptAtUtc", "outbox_messages", "timestamp with time zone", schema: "central", nullable: true);
        migrationBuilder.CreateTable("admin_notifications", schema: "central", columns: table => new
        {
            Id = table.Column<Guid>("uuid", nullable: false),
            Title = table.Column<string>("character varying(200)", maxLength: 200, nullable: false),
            Category = table.Column<string>("character varying(80)", maxLength: 80, nullable: false),
            TargetType = table.Column<string>("character varying(80)", maxLength: 80, nullable: false),
            TargetId = table.Column<Guid>("uuid", nullable: false),
            ShopId = table.Column<Guid>("uuid", nullable: true),
            RequiredCapability = table.Column<string>("character varying(100)", maxLength: 100, nullable: false),
            OccurredAtUtc = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_admin_notifications", x => x.Id));
        migrationBuilder.CreateIndex("IX_admin_notifications_OccurredAtUtc_Id", "admin_notifications", new[] { "OccurredAtUtc", "Id" }, schema: "central");
        migrationBuilder.Sql("""
            CREATE INDEX "IX_outbox_dispatch_ready" ON central.outbox_messages ("NextAttemptAtUtc", "OccurredOnUtc")
              WHERE "Status" IN ('PENDING','FAILED');
            DO $$
            BEGIN
              IF EXISTS (SELECT FROM pg_roles WHERE rolname='central_runtime_user') THEN
                GRANT SELECT, INSERT ON central.admin_notifications TO central_runtime_user;
              END IF;
              IF EXISTS (SELECT FROM pg_roles WHERE rolname='central054_app') THEN
                GRANT SELECT, INSERT ON central.admin_notifications TO central054_app;
              END IF;
            END $$;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Durable notification and delivery history require a reviewed forward migration.");
}
