using EdgeRetails.Central.Application.Common;

namespace EdgeRetails.Central.UnitTests.Common;

public class DatabaseTargetGuardTests
{
    private readonly DatabaseTargetGuard _guard = new();

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=edgeretails_central_test;Username=central_test_admin;Password=pass")]
    [InlineData("Server=localhost;Port=5432;Database=testdb;User Id=usr")]
    [InlineData("Host=127.0.0.1;Database=testdb;Username=usr")] // Default port is 5432
    public void ValidateConnectionString_RejectsPort5432_FailClosed(string connStr)
    {
        var ex = Assert.Throws<DatabaseTargetGuardException>(() => _guard.ValidateConnectionString(connStr));
        Assert.Equal("FORBIDDEN_PORT_TARGET", ex.ViolationCode);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=7150;Database=testdb;Username=usr;Password=pass")]
    public void ValidateConnectionString_RejectsPort7150_FailClosed(string connStr)
    {
        var ex = Assert.Throws<DatabaseTargetGuardException>(() => _guard.ValidateConnectionString(connStr));
        Assert.Equal("FORBIDDEN_PORT_TARGET", ex.ViolationCode);
    }

    [Theory]
    [InlineData("Host=192.168.1.100;Port=54332;Database=testdb;Username=usr;Password=pass")]
    [InlineData("Host=db.internal.cloud;Port=54332;Database=testdb;Username=usr;Password=pass")]
    public void ValidateConnectionString_RejectsNonLoopback(string connStr)
    {
        var ex = Assert.Throws<DatabaseTargetGuardException>(() => _guard.ValidateConnectionString(connStr));
        Assert.Equal("NON_LOOPBACK_TARGET", ex.ViolationCode);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=54332;Database=edgeretails;Username=usr;Password=pass")]
    [InlineData("Host=127.0.0.1;Port=54332;Database=edgeretails_pos;Username=usr;Password=pass")]
    [InlineData("Host=127.0.0.1;Port=54332;Database=shop_pos;Username=usr;Password=pass")]
    [InlineData("Host=127.0.0.1;Port=54332;Database=postgres;Username=usr;Password=pass")]
    public void ValidateConnectionString_RejectsShopAndDefaultDatabases(string connStr)
    {
        var ex = Assert.Throws<DatabaseTargetGuardException>(() => _guard.ValidateConnectionString(connStr));
        Assert.Equal("FORBIDDEN_DATABASE_TARGET", ex.ViolationCode);
    }

    [Fact]
    public void VerifyDataDirMarker_RejectsMissingMarkerFile()
    {
        var ex = Assert.Throws<DatabaseTargetGuardException>(() =>
            _guard.VerifyDataDirMarker("C:/non/existent/marker.txt"));
        Assert.Equal("DATA_DIR_NOT_OWNED", ex.ViolationCode);
    }

    [Fact]
    public void VerifyDataDirMarker_AcceptsValidMarkerFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile, new[]
            {
                "owner=central_test_admin",
                "cluster_type=disposable_pg18",
                "port=54332",
                "database=edgeretails_central_test",
                "schema=central"
            });

            _guard.VerifyDataDirMarker(tempFile);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
