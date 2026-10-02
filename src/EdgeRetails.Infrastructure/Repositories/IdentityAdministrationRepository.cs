using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class IdentityAdministrationRepository(EdgeRetailsDbContext db) : IIdentityAdministrationRepository
{
    public Task<bool> DisplayNameExistsAsync(string normalizedName, CancellationToken cancellationToken)
        => db.Users.AsNoTracking().AnyAsync(user => user.DisplayName.Trim().ToUpper() == normalizedName,
            cancellationToken);

    public void AddUser(User user) => db.Users.Add(user);
}
