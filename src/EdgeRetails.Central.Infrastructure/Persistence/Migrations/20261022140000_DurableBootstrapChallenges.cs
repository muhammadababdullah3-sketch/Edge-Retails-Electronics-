using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022140000_DurableBootstrapChallenges")]
public sealed class DurableBootstrapChallenges : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "bootstrap_challenges",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                ActivationTokenHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                InstallationPublicKeyFingerprintSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RequestNonce = table.Column<Guid>(type: "uuid", nullable: false),
                ChallengeNonceHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RedeemedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_bootstrap_challenges", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_bootstrap_challenges_RequestNonce",
            schema: "central", table: "bootstrap_challenges", column: "RequestNonce", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_bootstrap_challenges_ChallengeNonceHashSha256",
            schema: "central", table: "bootstrap_challenges", column: "ChallengeNonceHashSha256", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_bootstrap_challenges_InstallationId_ExpiresAtUtc",
            schema: "central", table: "bootstrap_challenges", columns: new[] { "InstallationId", "ExpiresAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Bootstrap replay protection is a security boundary and cannot be downgraded.");
}
