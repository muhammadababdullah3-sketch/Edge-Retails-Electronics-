using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Sprint8CanonicalReportingSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "reporting");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally non-destructive. The canonical reporting schema is a shared
        // deployment boundary and must not be dropped by a downgrade operation.
    }
}
