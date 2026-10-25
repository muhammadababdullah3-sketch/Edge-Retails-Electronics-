using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class PostgresFixtureProfileSafetyTests
{
    [Fact]
    public void NamedProfilesAttestDistinctRootsEndpointsAndSchemaModes()
    {
        var profiles = Enum.GetValues<PostgresFixtureKind>().Select(PostgresFixtureProfile.Resolve).ToArray();
        Assert.Equal(3, profiles.Select(p => p.OwnedRoot).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, profiles.Select(p => new NpgsqlConnectionStringBuilder(p.ConnectionString).Port).Distinct().Count());
        foreach (var profile in profiles)
        {
            profile.Attest();
        }
        var original = profiles[0];
        var swapped = PostgresFixtureProfile.FromConfiguration(original.Kind, original.ConnectionString,
            original.MaintenanceConnectionString, profiles[1].OwnedRoot, original.BackupConnectionString);
        Assert.Throws<InvalidOperationException>(swapped.Attest);
    }

    [Theory]
    [InlineData(5432)]
    [InlineData(7150)]
    [InlineData(55436)]
    public void OperationalOrUnapprovedPhase11PortsAreRejectedBeforeConnecting(int port)
    {
        var profile = PostgresFixtureProfile.Resolve();
        string AtPort(string raw) => new NpgsqlConnectionStringBuilder(raw) { Port = port }.ConnectionString;
        Assert.Throws<InvalidOperationException>(() => PostgresFixtureProfile.FromConfiguration(profile.Kind,
            AtPort(profile.ConnectionString), AtPort(profile.MaintenanceConnectionString), profile.OwnedRoot,
            AtPort(profile.BackupConnectionString)));
    }

    [Fact]
    public void MaintenanceRoleCannotBeRoutedAsApplicationWriter()
    {
        var profile = PostgresFixtureProfile.Resolve();
        var wrongRole = new NpgsqlConnectionStringBuilder(profile.ConnectionString) { Username = "er_master_admin" };
        Assert.Throws<InvalidOperationException>(() => PostgresFixtureProfile.FromConfiguration(profile.Kind,
            wrongRole.ConnectionString, profile.MaintenanceConnectionString, profile.OwnedRoot, profile.BackupConnectionString));
    }

    [Fact]
    public void BackupRoleIsReadOnlyAndWriteRoleHasNoPrivilegedMembership()
    {
        var profile = PostgresFixtureProfile.Resolve();
        profile.Attest(); // Includes transitive privileged-role membership and role-attribute verification.
        using var pg = new NpgsqlConnection(profile.BackupConnectionString);
        pg.Open();
        using var permissions = new NpgsqlCommand("""
            SELECT current_user, has_table_privilege(current_user,'inventory.movements','SELECT'),
              has_table_privilege(current_user,'inventory.movements','INSERT'),
              EXISTS(SELECT 1 FROM pg_roles r WHERE pg_has_role(current_user,r.oid,'MEMBER')
                AND (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls))
            """, pg);
        using var row = permissions.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal("er_master_runtime", row.GetString(0));
        Assert.True(row.GetBoolean(1));
        Assert.False(row.GetBoolean(2));
        Assert.False(row.GetBoolean(3));
    }
}
