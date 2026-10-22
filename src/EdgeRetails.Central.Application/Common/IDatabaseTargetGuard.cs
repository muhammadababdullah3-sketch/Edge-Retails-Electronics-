using System.Data.Common;

namespace EdgeRetails.Central.Application.Common;

public interface IDatabaseTargetGuard
{
    void ValidateConnectionString(string connectionString);
    void ValidateRuntimeTarget(string host, int port, string database, string username);
    void VerifyDataDirMarker(string markerFilePath);
    Task VerifyServerIdentityAsync(DbConnection connection, string expectedDatabase, string expectedUser, CancellationToken cancellationToken = default);
}
