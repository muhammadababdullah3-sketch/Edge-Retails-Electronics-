using EdgeRetails.Application.Production.Startup;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Production.Startup;

public static class MigrationHistoryCompatibilityEvaluator
{
    public static MigrationCompatibilityResult Evaluate(
        IReadOnlyList<string> known,
        IReadOnlyList<string> applied,
        bool hasPendingModelChanges)
    {
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);
        var appliedSet = new HashSet<string>(applied, StringComparer.Ordinal);
        var unknownApplied = applied.Where(x => !knownSet.Contains(x)).ToArray();
        var pending = known.Where(x => !appliedSet.Contains(x)).ToArray();

        if (unknownApplied.Length > 0)
        {
            return Build(false, MigrationCompatibilityState.DatabaseAhead,
                "The database contains migration(s) that this application build does not recognize.",
                known, applied, pending, unknownApplied);
        }

        var expectedAppliedPrefix = known.Take(applied.Count).ToArray();
        if (!applied.SequenceEqual(expectedAppliedPrefix, StringComparer.Ordinal))
        {
            return Build(false, MigrationCompatibilityState.HistoryDiverged,
                "The database migration history does not match the canonical application migration order.",
                known, applied, pending, unknownApplied);
        }

        if (pending.Length > 0)
        {
            return Build(false, MigrationCompatibilityState.DatabaseBehind,
                $"The database requires {pending.Length} canonical migration(s) before login.",
                known, applied, pending, unknownApplied);
        }

        if (hasPendingModelChanges)
        {
            return Build(false, MigrationCompatibilityState.ModelDrift,
                "The EF model has changes that are not represented by the canonical migration snapshot.",
                known, applied, Array.Empty<string>(), Array.Empty<string>());
        }

        return Build(true, MigrationCompatibilityState.Compatible,
            "Database migration history and EF model snapshot are compatible with this application build.",
            known, applied, Array.Empty<string>(), Array.Empty<string>());
    }

    private static MigrationCompatibilityResult Build(
        bool compatible,
        MigrationCompatibilityState state,
        string message,
        IReadOnlyList<string> known,
        IReadOnlyList<string> applied,
        IReadOnlyList<string> pending,
        IReadOnlyList<string> unknownApplied)
        => new(compatible, message)
        {
            State = state,
            KnownMigrations = known,
            AppliedMigrations = applied,
            PendingMigrations = pending,
            UnknownAppliedMigrations = unknownApplied
        };
}

public sealed class EfMigrationCompatibilityProbe<TContext> : IMigrationCompatibilityProbe where TContext : DbContext
{
    private readonly TContext _dbContext;
    public EfMigrationCompatibilityProbe(TContext dbContext) => _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<MigrationCompatibilityResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var known = _dbContext.Database.GetMigrations().ToArray();
            var applied = (await _dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            var modelDrift = _dbContext.Database.HasPendingModelChanges();
            return MigrationHistoryCompatibilityEvaluator.Evaluate(known, applied, modelDrift);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new MigrationCompatibilityResult(false, "Migration compatibility could not be verified safely.")
            {
                State = MigrationCompatibilityState.ProbeFailed
            };
        }
    }
}
