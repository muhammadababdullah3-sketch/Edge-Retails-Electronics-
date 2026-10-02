using System.Security.Cryptography;
using EdgeRetails.Infrastructure.Production.Backup;
using Microsoft.Extensions.Configuration;

namespace EdgeRetails.UnitTests;

public sealed class Phase3BackupProviderTests
{
    [Fact]
    public async Task BackupEncryptionKeyProviderRequiresExactly32BytesOfHexKeyMaterial()
    {
        var expected = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var provider = new EnvironmentBackupEncryptionKeyProvider(() => Convert.ToHexString(expected));

        var actual = await provider.GetKeyAsync();

        Assert.Equal(expected, actual);
        CryptographicOperations.ZeroMemory(actual);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvironmentBackupEncryptionKeyProvider(() => null).GetKeyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvironmentBackupEncryptionKeyProvider(() => "xyz").GetKeyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvironmentBackupEncryptionKeyProvider(() => "ABCD").GetKeyAsync());
    }

    [Fact]
    public async Task RestoreJournalKeyIsPersistentAndSeparateForEachTrustPath()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-restore-key-");
        try
        {
            var firstPath = Path.Combine(directory.FullName, "restore-journal.key");
            var secondPath = Path.Combine(directory.FullName, "other-trust-domain.key");
            var first = await new FileRestoreJournalIntegrityKeyProvider(firstPath).GetIntegrityKeyAsync();
            var afterRestart = await new FileRestoreJournalIntegrityKeyProvider(firstPath).GetIntegrityKeyAsync();
            var independent = await new FileRestoreJournalIntegrityKeyProvider(secondPath).GetIntegrityKeyAsync();

            Assert.Equal(32, first.Length);
            Assert.Equal(first, afterRestart);
            Assert.NotEqual(first, independent);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(afterRestart);
            CryptographicOperations.ZeroMemory(independent);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task MaintenanceCredentialProviderFailsClosedWhenRestoreAuthorityIsMissing()
    {
        var configuration = new ConfigurationBuilder().Build();
        var provider = new EnvironmentPostgresMaintenanceConnectionProvider(configuration, _ => null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync());

        Assert.Contains("EDGE_RETAILS_PG_MAINTENANCE_", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeConnectionCompositionUsesOnlyTheConfiguredDatabaseAndNeverRequiresRestoreCredentials()
    {
        var connection = ProductionBackupConfiguration.FromConnectionString(
            "Host=127.0.0.1;Port=5433;Database=retail;Username=runtime;Password=");

        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal(5433, connection.Port);
        Assert.Equal("retail", connection.Database);
        Assert.Equal("runtime", connection.Username);
        Assert.Equal("", connection.Password.Reveal());
        Assert.Equal("postgres", connection.MaintenanceDatabase);
    }
}
