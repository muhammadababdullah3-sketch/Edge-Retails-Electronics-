using Npgsql;

namespace EdgeRetails.IntegrationTests;

public sealed class ShopHolderOperationalPostgresTests
{
    [Fact]
    public async Task Schema_Contains_Critical_Indexes_And_Precision()
    {
        await using var connection = await OpenAsync();

        const string indexSql = """
            SELECT count(*)
            FROM pg_indexes
            WHERE indexname IN (
                'ux_product_units_default_purchase',
                'ux_product_units_default_sale',
                'ux_stocktakes_single_open',
                'ix_cash_sessions_status');
            """;

        await using (var indexCommand = new NpgsqlCommand(indexSql, connection))
        {
            var indexCount = Convert.ToInt32(await indexCommand.ExecuteScalarAsync());
            Assert.Equal(4, indexCount);
        }

        var factor = await ReadPrecisionAsync(
            connection,
            "catalog",
            "product_units",
            "factor_to_base_unit");
        Assert.Equal((18, 9), factor);

        var stock = await ReadPrecisionAsync(
            connection,
            "inventory",
            "stock_balances",
            "sellable_qty");
        Assert.Equal((18, 6), stock);

        var quotation = await ReadPrecisionAsync(
            connection,
            "sales",
            "quotation_items",
            "base_quantity");
        Assert.Equal((18, 6), quotation);

        const string nullableSql = """
            SELECT is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'inventory'
              AND table_name = 'movement_units'
              AND column_name = 'from_status';
            """;
        await using var nullableCommand = new NpgsqlCommand(nullableSql, connection);
        Assert.Equal("YES", (string?)await nullableCommand.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Database_Enforces_Only_One_Open_Cash_Session()
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE finance.cash_sessions SET status = 2 WHERE status = 1;");

        await InsertCashSessionAsync(connection, transaction, Guid.CreateVersion7());

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertCashSessionAsync(
                connection,
                transaction,
                Guid.CreateVersion7()));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ix_cash_sessions_status", exception.ConstraintName);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Database_Enforces_Only_One_Open_Stocktake()
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE inventory.stocktakes SET status = 2 WHERE status = 1;");

        await InsertStocktakeAsync(connection, transaction, Guid.CreateVersion7());

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertStocktakeAsync(
                connection,
                transaction,
                Guid.CreateVersion7()));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_stocktakes_single_open", exception.ConstraintName);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Product_Units_Enforce_One_Active_Default_Sale_Unit()
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var baseUnitId = Guid.CreateVersion7();
        var boxUnitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        await InsertUnitAsync(
            connection,
            transaction,
            baseUnitId,
            "Piece-" + Guid.NewGuid().ToString("N"),
            "pc-" + Guid.NewGuid().ToString("N")[..8]);

        await InsertUnitAsync(
            connection,
            transaction,
            boxUnitId,
            "Box-" + Guid.NewGuid().ToString("N"),
            "bx-" + Guid.NewGuid().ToString("N")[..8]);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO catalog.products
                (id, name, base_unit_id, tracking_mode,
                 serial_tracking_enabled, imei_tracking_enabled,
                 default_sale_price, is_active, version)
            VALUES
                (@id, @name, @baseUnitId, 1, FALSE, FALSE, 100, TRUE, 0);
            """,
            new NpgsqlParameter("id", productId),
            new NpgsqlParameter("name", "Test Product " + Guid.NewGuid().ToString("N")),
            new NpgsqlParameter("baseUnitId", baseUnitId));

        await InsertProductUnitAsync(
            connection,
            transaction,
            Guid.CreateVersion7(),
            productId,
            baseUnitId,
            1m,
            true);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertProductUnitAsync(
                connection,
                transaction,
                Guid.CreateVersion7(),
                productId,
                boxUnitId,
                12m,
                true));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_product_units_default_sale", exception.ConstraintName);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Customer_Warranty_Claim_Does_Not_Create_Inventory()
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var unitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        var claimId = Guid.CreateVersion7();

        await InsertUnitAsync(
            connection,
            transaction,
            unitId,
            "Warranty Piece-" + Guid.NewGuid().ToString("N"),
            "wp-" + Guid.NewGuid().ToString("N")[..8]);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO catalog.products
                (id, name, base_unit_id, tracking_mode,
                 serial_tracking_enabled, imei_tracking_enabled,
                 default_sale_price, is_active, version)
            VALUES
                (@id, @name, @unitId, 1, FALSE, FALSE, 5000, TRUE, 0);
            """,
            new NpgsqlParameter("id", productId),
            new NpgsqlParameter("name", "Warranty Fan " + Guid.NewGuid().ToString("N")),
            new NpgsqlParameter("unitId", unitId));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO parties.customers
                (id, name, is_walk_in, is_active, created_at, version)
            VALUES
                (@id, @name, FALSE, TRUE, now(), 0);
            """,
            new NpgsqlParameter("id", customerId),
            new NpgsqlParameter("name", "Warranty Customer"));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO warranty.claims
                (id, claim_number, customer_id, status, current_custody,
                 received_at, created_by, created_at, version, client_operation_id)
            VALUES
                (@id, @claimNumber, @customerId, 1, 2,
                 now(), @actorId, now(), 0, @clientOpId);
            """,
            new NpgsqlParameter("id", claimId),
            new NpgsqlParameter("claimNumber", "WC-" + Guid.NewGuid().ToString("N")),
            new NpgsqlParameter("customerId", customerId),
            new NpgsqlParameter("actorId", Guid.CreateVersion7()),
            new NpgsqlParameter("clientOpId", Guid.CreateVersion7()));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO warranty.claim_items
                (id, claim_id, product_id, quantity, fault_description)
            VALUES
                (@id, @claimId, @productId, 1, @fault);
            """,
            new NpgsqlParameter("id", Guid.CreateVersion7()),
            new NpgsqlParameter("claimId", claimId),
            new NpgsqlParameter("productId", productId),
            new NpgsqlParameter("fault", "Fan stopped working"));

        await using var countCommand = new NpgsqlCommand(
            "SELECT count(*) FROM inventory.units WHERE product_id = @productId;",
            connection,
            transaction);
        countCommand.Parameters.AddWithValue("productId", productId);

        var inventoryUnitCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
        Assert.Equal(0, inventoryUnitCount);

        await transaction.RollbackAsync();
    }

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "EDGE_RETAILS_TEST_DB must point to an isolated PostgreSQL integration-test database.");
        }

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<(int Precision, int Scale)> ReadPrecisionAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        string column)
    {
        const string sql = """
            SELECT numeric_precision, numeric_scale
            FROM information_schema.columns
            WHERE table_schema = @schema
              AND table_name = @table
              AND column_name = @column;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static Task<int> InsertCashSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO finance.cash_sessions
                (id, business_date, opened_by, opened_at, opening_cash, status, version)
            VALUES
                (@id, current_date, @actorId, now(), 1000, 1, 0);
            """,
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("actorId", Guid.CreateVersion7()));

    private static Task<int> InsertStocktakeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO inventory.stocktakes
                (id, scope, status, created_by, created_at, version)
            VALUES
                (@id, 1, 1, @actorId, now(), 0);
            """,
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("actorId", Guid.CreateVersion7()));

    private static Task<int> InsertUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        string name,
        string symbol) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO catalog.units
                (id, name, symbol, display_decimal_places, is_active)
            VALUES
                (@id, @name, @symbol, 0, TRUE);
            """,
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("name", name),
            new NpgsqlParameter("symbol", symbol));

    private static Task<int> InsertProductUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        Guid productId,
        Guid unitId,
        decimal factor,
        bool defaultSale) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO catalog.product_units
                (id, product_id, unit_id, factor_to_base_unit,
                 can_purchase, can_sell, can_use_in_thaka,
                 is_default_purchase_unit, is_default_sale_unit, is_active)
            VALUES
                (@id, @productId, @unitId, @factor,
                 TRUE, TRUE, TRUE, FALSE, @defaultSale, TRUE);
            """,
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("productId", productId),
            new NpgsqlParameter("unitId", unitId),
            new NpgsqlParameter("factor", factor),
            new NpgsqlParameter("defaultSale", defaultSale));

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }
}

