using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4MultiTerminalSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "terminals",
                schema: "system",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    terminal_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    hardware_fingerprint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    protocol_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_known_ip_address = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    auth_secret_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_terminals", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_terminals_status",
                schema: "system",
                table: "terminals",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_terminals_terminal_code",
                schema: "system",
                table: "terminals",
                column: "terminal_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "terminals",
                schema: "system");
        }
    }
}
