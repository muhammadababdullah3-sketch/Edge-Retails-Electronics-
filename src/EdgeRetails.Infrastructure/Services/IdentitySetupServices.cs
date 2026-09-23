using System.Security.Cryptography;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class Pbkdf2PinCredentialService : IPinCredentialService
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Algorithm = "PBKDF2-SHA256";

    public PinCredential Hash(string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            pin,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return new PinCredential(hash, salt, Iterations, Algorithm);
    }
    public bool Verify(string pin, PinCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);
        ArgumentNullException.ThrowIfNull(credential.Hash);
        ArgumentNullException.ThrowIfNull(credential.Salt);

        if (!string.Equals(
                credential.Algorithm,
                Algorithm,
                StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            pin,
            credential.Salt,
            credential.Iterations,
            HashAlgorithmName.SHA256,
            credential.Hash.Length);

        return CryptographicOperations.FixedTimeEquals(
            candidate,
            credential.Hash);
    }
}

public sealed class EfDatabaseReadinessService : IDatabaseReadinessService
{
    private readonly EdgeRetailsDbContext _db; public EfDatabaseReadinessService(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public async Task<DatabaseReadinessResult> CheckAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _db.Database.CanConnectAsync(cancellationToken))
            {
                return new DatabaseReadinessResult(
                    false,
                    false,
                    Array.Empty<string>(),
                    "PostgreSQL connection could not be established.");
            }

            var pending = (await _db.Database
                    .GetPendingMigrationsAsync(cancellationToken))
                .ToArray();

            return new DatabaseReadinessResult(
                true,
                pending.Length > 0,
                pending,
                null);
        }
        catch (Exception exception)
        {
            return new DatabaseReadinessResult(
                false,
                false,
                Array.Empty<string>(),
                exception.Message);
        }
    }
}

public sealed class InstallationStateReadService : IInstallationStateReadService
{
    private readonly EdgeRetailsDbContext _db;

    public InstallationStateReadService(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<EdgeRetails.Domain.SystemConfiguration.InstallationState?> GetAsync(
        CancellationToken cancellationToken) =>
        _db.InstallationStates
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
}
