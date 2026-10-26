using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.DevTools;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var contentRoot = Directory.GetCurrentDirectory();
CentralLocalEnvironmentLoader.TryApplyFromRepository(contentRoot, "Development");
CentralOperationalEnvironment.EnsureMinimumHostConfiguration();

if (args.Length == 0 || args[0] is not ("sync-portal-admin" or "provision-admin" or "reset-admin-password" or "rename-portal-admin" or "check-env"))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project src/EdgeRetails.Central.DevTools -- check-env");
    Console.Error.WriteLine("  dotnet run --project src/EdgeRetails.Central.DevTools -- sync-portal-admin");
    Console.Error.WriteLine("    (uses CENTRAL_DEV_PORTAL_USERNAME / CENTRAL_DEV_PORTAL_PASSWORD from local/central-dev.env)");
    return 1;
}

if (args[0] == "check-env")
{
    var missing = CentralOperationalEnvironment.GetMissingMinimumVariables();
    if (missing.Count > 0)
    {
        Console.Error.WriteLine("Missing: " + string.Join(", ", missing));
        return 2;
    }

    Console.WriteLine("Central minimum host environment is complete.");
    return 0;
}

if (args[0] == "rename-portal-admin")
{
    if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
    {
        Console.Error.WriteLine("rename-portal-admin requires the corrected username.");
        return 1;
    }

    var corrected = args[1].Trim().ToLowerInvariant();
    var connection = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
        ?? throw new InvalidOperationException("CENTRAL_DATABASE_CONNECTION_STRING is required.");
    var targetGuard = new DatabaseTargetGuard();
    targetGuard.ValidateConnectionString(connection);
    var renameOptions = new DbContextOptionsBuilder<CentralDbContext>();
    renameOptions.UseNpgsql(connection, npgsql =>
    {
        npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema);
    });
    await using var renameDb = new CentralDbContext(renameOptions.Options, targetGuard);
    var record = CentralLocalPortalCredentialStore.TryRead(contentRoot);
    var currentName = record?.Username?.Trim().ToLowerInvariant();
    var user = await renameDb.AdminUsers.FirstOrDefaultAsync(u => u.Username == corrected)
        ?? (currentName is null ? null : await renameDb.AdminUsers.FirstOrDefaultAsync(u => u.Username == currentName));
    if (user is null)
    {
        Console.Error.WriteLine("No local portal admin matched the current record or the corrected username.");
        return 4;
    }

    if (!string.Equals(user.Username, corrected, StringComparison.Ordinal))
    {
        user.ChangeUsername(corrected);
        await renameDb.SaveChangesAsync();
    }

    CentralLocalPortalCredentialStore.Write(contentRoot, new CentralLocalPortalCredentialRecord
    {
        Username = corrected,
        Password = record?.Password ?? CentralLocalPortalCredentials.ResolvePassword(),
        MfaSecret = record?.MfaSecret ?? user.MfaSecret ?? string.Empty
    });
    Console.WriteLine($"Portal username is now '{corrected}'. Password and MFA secret were not rotated.");
    return 0;
}

var username = CentralLocalPortalCredentials.ResolveUsername();
var password = CentralLocalPortalCredentials.ResolvePassword();
if (args[0] is "provision-admin" or "reset-admin-password")
{
    if (args.Length >= 3)
    {
        username = args[1].Trim().ToLowerInvariant();
        password = args[2];
    }
    else if (args[0] == "provision-admin")
    {
        Console.Error.WriteLine("provision-admin accepts optional <username> <password> or reads CENTRAL_DEV_PORTAL_* from env.");
        return 1;
    }
}

var connectionString = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
    ?? throw new InvalidOperationException("CENTRAL_DATABASE_CONNECTION_STRING is required.");
var guard = new DatabaseTargetGuard();
guard.ValidateConnectionString(connectionString);

var optionsBuilder = new DbContextOptionsBuilder<CentralDbContext>();
optionsBuilder.UseNpgsql(connectionString, npgsql =>
{
    npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema);
});

await using var db = new CentralDbContext(optionsBuilder.Options, guard);
return await PortalAdminProvisioner.SyncAsync(db, username, password, contentRoot);
