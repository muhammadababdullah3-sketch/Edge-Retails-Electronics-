namespace EdgeRetails.Infrastructure.Production.Licensing;

/// <summary>
/// The installed license is machine-wide. Explicit production state roots keep
/// isolated rehearsals and deliberately configured installations self-contained.
/// </summary>
public static class ProductionLicensePathPolicy
{
    public static string Resolve(string commonApplicationDataDirectory, string? explicitProductionStateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonApplicationDataDirectory);
        if (explicitProductionStateRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(explicitProductionStateRoot);
        }

        var root = explicitProductionStateRoot is null
            ? Path.Combine(commonApplicationDataDirectory, "EdgeRetails")
            : explicitProductionStateRoot;
        return Path.Combine(Path.GetFullPath(root), "license.erlic");
    }
}
