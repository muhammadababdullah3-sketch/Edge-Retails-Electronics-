using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022150000_BootstrapRegistrationBindings")]
public sealed class BootstrapRegistrationBindings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "LicenseId", schema: "central", table: "bootstrap_challenges", type: "uuid", nullable: false, defaultValue: Guid.Empty);
        migrationBuilder.AddColumn<string>(name: "InstallationPublicKeyPem", schema: "central", table: "bootstrap_challenges", type: "character varying(8192)", maxLength: 8192, nullable: false, defaultValue: string.Empty);
        migrationBuilder.AddColumn<string>(name: "ChallengeNonce", schema: "central", table: "bootstrap_challenges", type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: string.Empty);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "RegisteredAtUtc", schema: "central", table: "bootstrap_challenges", type: "timestamp with time zone", nullable: true);

        migrationBuilder.AddColumn<int>(name: "DeviceIdentityVersion", schema: "central", table: "installations", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(name: "DeviceIdentityHash", schema: "central", table: "installations", type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: string.Empty);
        migrationBuilder.AddColumn<int>(name: "HardwareFingerprintVersion", schema: "central", table: "installations", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(name: "HardwareFingerprintHash", schema: "central", table: "installations", type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: string.Empty);
        migrationBuilder.AddColumn<string>(name: "SignalQuality", schema: "central", table: "installations", type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "UNKNOWN");
        migrationBuilder.AddColumn<string>(name: "ApplicationVersion", schema: "central", table: "installations", type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: string.Empty);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Installation identity and bootstrap proof bindings are security boundaries and cannot be downgraded.");
}
