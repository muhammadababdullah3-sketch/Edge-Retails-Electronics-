using EdgeRetails.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("TrackingManufacturerIdentityPg")]
public sealed class TrackingCutoverPostgresTests
{
    [Theory]
    [InlineData("serial-collision")]
    [InlineData("serial-control")]
    [InlineData("serial-format")]
    [InlineData("serial-unicode-review")]
    [InlineData("imei-cross-slot")]
    [InlineData("imei-same-unit")]
    public async Task MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(string scenario)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var connection = await OpenTemporaryCutoverAsync();
        switch (scenario)
        {
            case "serial-collision":
                await SeedAsync(connection, " ab-c ");
                await SeedAsync(connection, "AB-C");
                break;
            case "serial-control":
                await SeedAsync(connection, "AB\u0001C");
                break;
            case "serial-format":
                await SeedAsync(connection, "AB\u200BC");
                break;
            case "serial-unicode-review":
                await SeedAsync(connection, "\u0131");
                break;
            case "imei-cross-slot":
                await SeedAsync(connection, null, "86012345678901");
                await SeedAsync(connection, null, null, "860-123-456-789-01");
                break;
            case "imei-same-unit":
                await SeedAsync(connection, null, "86012345678901", "860-123-456-789-01");
                break;
        }
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteMigrationSqlAsync(connection));
        Assert.Contains("Tracking manufacturer identity cutover blocked:", ex.MessageText);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_temp.tracking_claims", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MigrationBackfill_MatchesRuntimeForAdmittedSerialsAndPreservesRawIdentity()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var connection = await OpenTemporaryCutoverAsync();
        const string raw = "\tab-c12\t";
        await SeedAsync(connection, raw, "860-123-456-789-01");
        await SeedAsync(connection, " \t\u00A0 ");
        await ExecuteMigrationSqlAsync(connection);
        await using var query = new NpgsqlCommand("SELECT raw_value, normalized_value FROM pg_temp.tracking_claims WHERE identifier_type = 1", connection);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(raw, reader.GetString(0));
        Assert.Equal(EdgeRetails.Domain.Catalog.IdentityNormalizationRules.NormalizeSerialNumber(raw), reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<NpgsqlConnection> OpenTemporaryCutoverAsync()
    {
        var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await connection.OpenAsync();
        await using var setup = new NpgsqlCommand("""
            CREATE TEMP TABLE tracking_legacy_units (id uuid PRIMARY KEY, serial_number text,
                imei1 text, imei2 text, created_at timestamptz NOT NULL DEFAULT now());
            CREATE TEMP TABLE tracking_claims (id uuid PRIMARY KEY, inventory_unit_id uuid,
                identifier_type int, identifier_slot int, raw_value text, normalized_value text,
                normalization_version int, created_at timestamptz,
                UNIQUE (identifier_type, normalized_value), UNIQUE (inventory_unit_id, identifier_slot));
            """, connection);
        await setup.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task SeedAsync(NpgsqlConnection connection, string? serial, string? imei1 = null, string? imei2 = null)
    {
        await using var seed = new NpgsqlCommand("INSERT INTO pg_temp.tracking_legacy_units (id, serial_number, imei1, imei2) VALUES (@id,@serial,@imei1,@imei2)", connection);
        seed.Parameters.AddWithValue("id", Guid.NewGuid());
        seed.Parameters.Add("serial", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)serial ?? DBNull.Value;
        seed.Parameters.Add("imei1", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)imei1 ?? DBNull.Value;
        seed.Parameters.Add("imei2", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)imei2 ?? DBNull.Value;
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteMigrationSqlAsync(NpgsqlConnection connection)
    {
        // Execute the actual checked-in custom migration SQL, with only table
        // targets redirected to connection-owned temporary tables. No shop data.
        foreach (var operation in new TrackingManufacturerIdentityAuthorityV1().UpOperations.OfType<SqlOperation>())
        {
            var sql = operation.Sql.Replace("inventory.unit_identity_claims", "pg_temp.tracking_claims", StringComparison.Ordinal)
                .Replace("inventory.units", "pg_temp.tracking_legacy_units", StringComparison.Ordinal);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
