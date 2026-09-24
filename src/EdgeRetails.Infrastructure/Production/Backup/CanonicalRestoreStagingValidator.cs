using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public interface IRestoreStagingCompatibilityProbe
{
    string Name { get; }
    Task ValidateAsync(RestoreStagingValidationContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production restore validator. It verifies the canonical Edge Retails PostgreSQL schema surface and
/// then requires live project compatibility/invariant probes (migration compatibility, setup/owner state,
/// inventory/finance reconciliation as appropriate) before a restore session can become Prepared.
/// </summary>
public sealed class CanonicalRestoreStagingValidator : IRestoreStagingValidator
{
    private static readonly string[] RequiredSchemas =
    {
        "system", "identity", "parties", "catalog", "inventory", "sales",
        "purchasing", "thaka", "warranty", "finance", "audit", "reporting"
    };

    private readonly string _psql;
    private readonly IPostgresProcessRunner _runner;
    private readonly IReadOnlyList<IRestoreStagingCompatibilityProbe> _probes;

    public CanonicalRestoreStagingValidator(
        string psqlPath,
        IEnumerable<IRestoreStagingCompatibilityProbe> probes,
        IPostgresProcessRunner? runner = null)
    {
        if (string.IsNullOrWhiteSpace(psqlPath) || !File.Exists(psqlPath))
        {
            throw new FileNotFoundException("Required PostgreSQL psql executable was not found.", psqlPath);
        }

        _psql = psqlPath;
        _runner = runner ?? new ProcessRunner();
        _probes = (probes ?? throw new ArgumentNullException(nameof(probes))).ToArray();
        if (_probes.Count == 0)
        {
            throw new InvalidOperationException("Production restore validation requires at least one live migration/business compatibility probe.");
        }
    }

    public async Task ValidateAsync(RestoreStagingValidationContext context, CancellationToken cancellationToken = default)
    {
        foreach (var schema in RequiredSchemas)
        {
            var sql = $"SELECT CASE WHEN EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = {SqlLiteral(schema)}) THEN '1' ELSE '0' END;";
            var result = await _runner.RunAsync(_psql, new[]
            {
                "--no-password", "--tuples-only", "--no-align", "--quiet",
                "--host", context.MaintenanceConnection.Host,
                "--port", context.MaintenanceConnection.Port.ToString(),
                "--username", context.MaintenanceConnection.Username,
                "--dbname", context.StagingDatabase,
                "--command", sql
            }, new Dictionary<string, string?> { ["PGPASSWORD"] = context.MaintenanceConnection.Password.Reveal() }, cancellationToken);

            if (result.ExitCode != 0 || result.StandardOutput.Trim() != "1")
            {
                throw new InvalidDataException($"Restored staging database is missing required Edge Retails schema '{schema}'.");
            }
        }

        foreach (var probe in _probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await probe.ValidateAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"Restore compatibility probe '{probe.Name}' failed.", ex);
            }
        }
    }

    private static string SqlLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
